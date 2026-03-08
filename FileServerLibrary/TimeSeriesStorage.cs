namespace FileServerLibrary;

internal sealed class TimeSeriesEntryValue
{
    internal KeyValue Value { get; private set; }
    private long _lastAccessTime;
    
    internal TimeSeriesEntryValue(KeyValue value, SortedDictionary<long, LruItem> lru, TimeSeriesEntry entry,
        string propertyName)
    {
        Value = value;
        _lastAccessTime = DateTime.UtcNow.Ticks;
        lru.Add(_lastAccessTime, new LruItem(entry, this, propertyName));
    }

    public int SetData(byte[] newData, bool versioned, SortedDictionary<long, LruItem> lru, TimeSeriesEntry entry,
        string propertyName)
    {
        UpdateLru(lru, entry, propertyName);
        var difference = newData.Length - Value.Value.Length;
        Value = new KeyValue(Value.Key, versioned ? Value.Version + 1 : 0, newData);
        return difference;
    }

    private void UpdateLru(SortedDictionary<long, LruItem> lru, TimeSeriesEntry entry, string propertyName)
    {
        lru.Remove(_lastAccessTime);
        _lastAccessTime = DateTime.UtcNow.Ticks;
        lru.Add(_lastAccessTime, new LruItem(entry, this, propertyName));
    }

    internal KeyValue GetValue(SortedDictionary<long, LruItem> lru, TimeSeriesEntry entry, string propertyName)
    {
        UpdateLru(lru, entry, propertyName);
        return Value;
    }
}

internal sealed class TimeSeriesEntry
{
    internal readonly Dictionary<string, TimeSeriesEntryValue?> Values;
    internal readonly int Date;

    internal TimeSeriesEntry(int date, HashSet<string> values)
    {
        Date = date;
        Values = values
            .Select<string, (string, TimeSeriesEntryValue?)>(v => (v, null))
            .ToDictionary();
    }

    internal KeyValue? Get(string dbName, bool versioned, IKeyValueStorage storageInterface, string? propertyName,
        SortedDictionary<long, LruItem> lru, ref int sizeDifference)
    {
        var pName = propertyName ?? ""; 
        if (!Values.TryGetValue(pName, out var value)) return null;
        if (value != null)
            return value.GetValue(lru, this, pName);
        var data = storageInterface.Get(new KeyValueStorageKey(dbName, Date, propertyName));
        if (data == null) throw new Exception($"item {dbName} {Date} {propertyName} not found");
        var kv = KeyValue.ReadData(Date, data, versioned);
        sizeDifference += kv.Value.Length;
        Values[pName] = new TimeSeriesEntryValue(kv, lru, this, pName);
        return kv;
    }
}

internal sealed class DatabaseParameters
{
    internal string DbName { get; private set; } = "";
    public int MaximumMemoryUsage { get; set; }
    public int MinimumDate { get; set; }
    public int NumDaysFromNow { get; set; }
    internal bool Versioned { get; private set; }
    internal bool WriteBack { get; private set; }
    internal IKeyValueStorage StorageInterface { get; private set; } = null!;
    internal Logger StorageLogger { get; private set; } = null!;
    internal int NumEntries { get; private set; }
    
    private int _minDay;

    public DatabaseParameters Apply(string dbName, DatabaseParameters? defaultParameters,
        IKeyValueStorage storageInterface, Logger storageLogger, bool versioned, bool writeBack)
    {
        DbName = dbName;
        Versioned = versioned;
        WriteBack = writeBack;
        StorageInterface = storageInterface;
        StorageLogger = storageLogger;
        if (defaultParameters != null)
        {
            if (MaximumMemoryUsage == 0) MaximumMemoryUsage = defaultParameters.MaximumMemoryUsage;
            if (MinimumDate == 0) MinimumDate = defaultParameters.MinimumDate;
            if (NumDaysFromNow == 0) NumDaysFromNow = defaultParameters.NumDaysFromNow;
        }
        if (MaximumMemoryUsage == 0) throw new Exception($"{dbName}: MaximumMemoryUsage is not set");
        if (MinimumDate == 0) throw new Exception($"{dbName}: MinimumDate is not set");
        if (NumDaysFromNow == 0) throw new Exception($"{dbName}: NumDaysFromNow is not set");
        _minDay = DateToDayNumber(MinimumDate);
        NumEntries = DateOnly.FromDateTime(DateTime.UtcNow).DayNumber - _minDay + NumDaysFromNow;
        return this;
    }
    
    internal int DateToKey(int date)
    {
        var day = DateToDayNumber(date);
        return day > _minDay ? day - _minDay : 0;
    }
    
    internal static int DateToDayNumber(int date) =>
        new DateOnly(date / 10000, (date / 100) % 100, date % 100).DayNumber;
}

internal record LruItem(TimeSeriesEntry Entry, TimeSeriesEntryValue Value, string PropertyName);

public sealed class TimeSeriesDatabaseInfo : DatabaseInfo
{
    private readonly TimeSeriesEntry[] _entries;
    private readonly HashSet<KeyValueStorageShortKey> _dirtyKeys;
    private readonly DatabaseParameters _parameters;
    private readonly SortedDictionary<long, LruItem> _lru;
    
    private int _totalSize;
    
    internal TimeSeriesDatabaseInfo(DatabaseParameters databaseParameters,
        IEnumerable<KeyValueStorageShortKey> existingKeys): base(databaseParameters.DbName)
    {
        _parameters = databaseParameters;
        _entries = new TimeSeriesEntry[_parameters.NumEntries];
        foreach (var g in existingKeys.GroupBy(k => k.Key))
            _entries[_parameters.DateToKey(g.Key)] = new TimeSeriesEntry(g.Key, g
                .Select(k => k.PropertyName ?? "")
                .ToHashSet());
        _dirtyKeys = [];
        _totalSize = 0;
        _lru = new SortedDictionary<long, LruItem>();
    }
    
    internal void WriteDirtyData()
    {
        EnterReadLock();
        try
        {
            foreach (var key in _dirtyKeys.ToList())
            {
                var entry = _entries[key.Key];
                var propertyName = key.PropertyName ?? "";
                var value = entry.Values[propertyName]!;
                var kv = value.GetValue(_lru, entry, propertyName);
                _parameters.StorageInterface.Set(new KeyValueStorageKey(DbName, kv.Key, key.PropertyName),
                    kv.BuildData(_parameters.Versioned));
                _dirtyKeys.Remove(key);
            }
        }
        catch (Exception e)
        {
            _parameters.StorageLogger.Error(e.Message);
        }
        finally
        {
            ExitReadLock();
        }
    }

    public IEnumerable<KeyValue> Get( int from, int to, string? propertyName)
    {
        Cleanup();
        var fromKey = _parameters.DateToKey(from);
        var toKey = _parameters.DateToKey(to);
        EnterReadLock();
        try
        {
            var difference = 0;
            var result = Enumerable.Range(fromKey, toKey - fromKey + 1)
                .Select(i => _entries[i].Get(DbName, _parameters.Versioned, _parameters.StorageInterface,
                    propertyName, _lru, ref difference))
                .Where(kv => kv != null)
                .Cast<KeyValue>();
            Interlocked.Add(ref _totalSize, difference);
            return result;
        }
        catch
        {
            ExitReadLock();
            throw;
        }
    }

    public KeyValue? GetLast(int from, int to, string? propertyName)
    {
        Cleanup();
        var fromKey = _parameters.DateToKey(from);
        var toKey = _parameters.DateToKey(to);  
        EnterReadLock();
        try
        {
            var difference = 0;
            var result = Enumerable.Range(fromKey, toKey - fromKey + 1)
                .Reverse()
                .Select(i => _entries[i].Get(DbName, _parameters.Versioned, _parameters.StorageInterface,
                    propertyName, _lru, ref difference))
                .FirstOrDefault(kv => kv != null);
            Interlocked.Add(ref _totalSize, difference);
            return result;
        }
        catch
        {
            ExitReadLock();
            throw;
        }
    }

    public void AddOrUpdate(int date, string? propertyName, Func<byte[]> addFunc, Func<byte[], byte[]> updateFunc)
    {
        Cleanup();
        var key = _parameters.DateToKey(date);
        var pName = propertyName ?? "";
        EnterWriteLock();
        try
        {
            int difference;
            var entry = _entries[key];
            if (entry.Values.TryGetValue(pName, out var value))
                difference = value!.SetData(updateFunc(value.Value.Value), _parameters.Versioned, _lru, entry, pName);
            else
            {
                value = new TimeSeriesEntryValue(new KeyValue(date, _parameters.Versioned ? 1 : 0, addFunc()),
                    _lru, entry, pName);
                difference = value.Value.Value.Length;
                _entries[key].Values[pName] = value;
            }
            if (!_parameters.WriteBack)
                _parameters.StorageInterface.Set(new KeyValueStorageKey(DbName, date, propertyName),
                    value.Value.BuildData(_parameters.Versioned));
            else
                _dirtyKeys.Add(new KeyValueStorageShortKey(key, propertyName));
            _totalSize += difference;
        }
        catch
        {
            ExitWriteLock();
            throw;
        }
    }

    public KeyValue Get(int date, string? propertyName)
    {
        Cleanup();
        var key = _parameters.DateToKey(date);
        var difference = 0;
        var result = _entries[key].Get(DbName, _parameters.Versioned, _parameters.StorageInterface,
                         propertyName, _lru, ref difference)
               ?? throw new Exception($"item {DbName} {date} {propertyName} not found");
        Interlocked.Add(ref _totalSize, difference);
        return result;
    }

    public void Set(List<KeyValue> data, string? propertyName)
    {
        Cleanup();
        var pName = propertyName ?? "";
        EnterWriteLock();
        try
        {
            foreach (var kv in data)
            {
                var key = _parameters.DateToKey(kv.Key);
                var entry = _entries[key];
                if (entry.Values.TryGetValue(pName, out var value))
                    _totalSize += value!.SetData(kv.Value, _parameters.Versioned, _lru, entry, pName);
                else
                {
                    value = new TimeSeriesEntryValue(_parameters.Versioned ? kv with { Version = 1 } : kv, _lru, entry,
                        pName);
                    _totalSize += value.Value.Value.Length;
                    entry.Values[pName] = value;
                }

                if (!_parameters.WriteBack)
                    _parameters.StorageInterface.Set(new KeyValueStorageKey(DbName, kv.Key, propertyName),
                        value.Value.BuildData(_parameters.Versioned));
                else
                    _dirtyKeys.Add(new KeyValueStorageShortKey(key, propertyName));
            }
        }
        catch
        {
            ExitWriteLock();
            throw;
        }
    }

    private void Cleanup()
    {
        if (_totalSize <= _parameters.MaximumMemoryUsage) return;
        EnterWriteLock();
        try
        {
            var toRemove = new List<long>();
            foreach (var item in _lru)
            {
                if (_totalSize <= _parameters.MaximumMemoryUsage) break;
                _totalSize -= item.Value.Value.Value.Value.Length;
                item.Value.Entry.Values[item.Value.PropertyName] = null;
                toRemove.Add(item.Key);
            }
            foreach (var key in toRemove)
                _lru.Remove(key);
        }
        finally
        {
            ExitWriteLock();
        }
    }
}

public sealed class TimeSeriesStorage: GenericKeyValueStorage<TimeSeriesDatabaseInfo>
{
    private readonly Dictionary<string, DatabaseParameters> _databaseParameters;
    private readonly DatabaseParameters? _defaultParameters;
    
    public TimeSeriesStorage(Logger logger, ServerConfigurationParameters parameters): base(logger, parameters)
    {
        _databaseParameters = parameters.GetParameter<Dictionary<string, DatabaseParameters>>("storageDatabaseParameters");
        _databaseParameters.TryGetValue("default", out _defaultParameters);
        foreach (var kv in _databaseParameters.Where(kv => kv.Key != "default"))
            kv.Value.Apply(kv.Key, _defaultParameters, StorageInterface, StorageLogger, Versioned, WriteBackInterval > 0);
    }

    protected override TimeSeriesDatabaseInfo CreateDatabaseInfo(string dbName, IEnumerable<KeyValueStorageShortKey> existingKeys)
    {
        var parameters = _databaseParameters.TryGetValue(dbName, out var dbParameters)
            ? dbParameters
            : new DatabaseParameters().Apply(dbName, _defaultParameters, StorageInterface, StorageLogger, Versioned, WriteBackInterval > 0);
        return new TimeSeriesDatabaseInfo(parameters, existingKeys);
    }

    protected override void WriteDirtyData()
    {
        foreach (var dbInfo in DbInfo.Values)
            dbInfo.WriteDirtyData();
    }

    public override (DatabaseInfo, IEnumerable<KeyValue>) Get(string dbName, int from, int to, string? propertyName = null)
    {
        var dbInfo = GetDatabaseInfo(dbName);
        return (dbInfo, dbInfo.Get(from, to, propertyName));
    }

    public override (DatabaseInfo, KeyValue?) GetLast(string dbName, int from, int to, string? propertyName = null)
    {
        var dbInfo = GetDatabaseInfo(dbName);
        return (dbInfo, dbInfo.GetLast(from, to, propertyName));
    }

    public override DatabaseInfo AddOrUpdate(string dbName, int key, string? propertyName, Func<byte[]> addFunc, Func<byte[], byte[]> updateFunc)
    {
        var dbInfo = GetDatabaseInfo(dbName);
        dbInfo.AddOrUpdate(key, propertyName, addFunc, updateFunc);
        return dbInfo;
    }

    protected override void Set(TimeSeriesDatabaseInfo dbInfo, List<KeyValue> data, string? propertyName = null)
    {
        dbInfo.Set(data, propertyName);
    }

    protected override KeyValue Get(TimeSeriesDatabaseInfo dbInfo, int key, string? propertyName)
    {
        return dbInfo.Get(key, propertyName);
    }
}