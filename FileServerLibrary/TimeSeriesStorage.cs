namespace FileServerLibrary;

internal sealed class TimeSeriesEntryValue
{
    private readonly LinkedListNode<LruItem> _node;

    internal KeyValue Value { get; private set; }
    public bool IsDirty { get; private set; }

    internal TimeSeriesEntryValue(KeyValue value, Lru<LruItem> lru, TimeSeriesEntry entry,
        string propertyName)
    {
        Value = value;
        _node = lru.Add(new LruItem(entry, this, propertyName));
    }

    public int SetData(byte[] newData, bool versioned, Lru<LruItem> lru)
    {
        UpdateLru(lru);
        var difference = newData.Length - Value.Value.Length;
        Value = new KeyValue(Value.Key, versioned ? Value.Version + 1 : 0, newData);
        return difference;
    }

    private void UpdateLru(Lru<LruItem> lru)
    {
        lru.ToTop(_node);
    }

    internal KeyValue GetValue(Lru<LruItem> lru)
    {
        UpdateLru(lru);
        return Value;
    }

    internal void SetDirty(bool dirty)
    {
        IsDirty = dirty;
    }
}

internal sealed class TimeSeriesEntry
{
    internal readonly Dictionary<string, TimeSeriesEntryValue?> Values;
    internal readonly int Date;
    private readonly Lock _lock;

    internal TimeSeriesEntry(int date, HashSet<string> values)
    {
        Date = date;
        Values = values
            .Select<string, (string, TimeSeriesEntryValue?)>(v => (v, null))
            .ToDictionary();
        _lock = new Lock();
    }

    internal KeyValue? Get(string dbName, bool versioned, IKeyValueStorage storageInterface, string? propertyName,
        Lru<LruItem> lru, ref int size)
    {
        var pName = propertyName ?? ""; 
        if (!Values.TryGetValue(pName, out var value)) return null;
        if (value != null)
            return value.GetValue(lru);
        using (_lock.EnterScope())
        {
            if (!Values.TryGetValue(pName, out value)) return null;
            if (value != null)
                return value.GetValue(lru);

            var e = GetFromStorage(dbName, versioned, storageInterface, propertyName, lru, ref size);
            return e.Value;
        }
    }

    internal TimeSeriesEntryValue GetFromStorage(string dbName, bool versioned, IKeyValueStorage storageInterface,
        string? propertyName, Lru<LruItem> lru, ref int size)
    {
        var data = storageInterface.Get(new KeyValueStorageKey(dbName, Date, propertyName));
        if (data == null) throw new Exception($"item {dbName} {Date} {propertyName} not found");
        var kv = KeyValue.ReadData(Date, data, versioned);
        size += kv.Value.Length;
        var pName = propertyName ?? "";
        var e = new TimeSeriesEntryValue(kv, lru, this, pName);
        Values[pName] = e;
        return e;
    }
}

public record TimeSeriesDatabaseParametersRecord(int MaximumMemoryUsage, int MinimumDate, int NumDaysFromNow);

public sealed class TimeSeriesDatabaseParameters
{
    internal readonly string DbName;
    public readonly int MaximumMemoryUsage;
    internal readonly bool Versioned;
    internal readonly bool WriteBack;
    internal readonly IKeyValueStorage StorageInterface;
    internal readonly Logger StorageLogger;
    internal readonly int NumEntries;
    private readonly int _minDay;

    public TimeSeriesDatabaseParameters(TimeSeriesDatabaseParametersRecord parameters, string dbName,
        TimeSeriesDatabaseParametersRecord? defaultParameters, IKeyValueStorage storageInterface, Logger storageLogger,
        bool versioned, bool writeBack)
    {
        MaximumMemoryUsage = parameters.MaximumMemoryUsage;
        var minimumDate = parameters.MinimumDate;
        var numDaysFromNow = parameters.NumDaysFromNow;
        DbName = dbName;
        Versioned = versioned;
        WriteBack = writeBack;
        StorageInterface = storageInterface;
        StorageLogger = storageLogger;
        if (defaultParameters != null)
        {
            if (MaximumMemoryUsage == 0) MaximumMemoryUsage = defaultParameters.MaximumMemoryUsage;
            if (minimumDate == 0) minimumDate = defaultParameters.MinimumDate;
            if (numDaysFromNow == 0) numDaysFromNow = defaultParameters.NumDaysFromNow;
        }
        if (MaximumMemoryUsage == 0) throw new Exception($"{dbName}: MaximumMemoryUsage is not set");
        if (minimumDate == 0) throw new Exception($"{dbName}: MinimumDate is not set");
        if (numDaysFromNow == 0) throw new Exception($"{dbName}: NumDaysFromNow is not set");
        _minDay = DateToDayNumber(minimumDate);
        NumEntries = DateOnly.FromDateTime(DateTime.UtcNow).DayNumber - _minDay + numDaysFromNow + 1;
    }
    
    internal int DateToKey(int date)
    {
        var day = DateToDayNumber(date);
        return day > _minDay ? day - _minDay : 0;
    }
    
    public static int DateToDayNumber(int date) =>
        new DateOnly(date / 10000, (date / 100) % 100, date % 100).DayNumber;
}

internal record LruItem(TimeSeriesEntry Entry, TimeSeriesEntryValue Value, string PropertyName);

public sealed class Lru<T>
{
    private readonly LinkedList<T> _lru = [];
    private readonly Lock _lock = new();

    public LinkedListNode<T> Add(T item)
    {
        Lock();
        var node = _lru.AddFirst(item);
        Unlock();
        return node;
    }

    public void ToTop(LinkedListNode<T> node)
    {
        Lock();
        _lru.Remove(node);
        _lru.AddFirst(node);
        Unlock();
    }

    public void Lock()
    {
        _lock.Enter();
    }

    public void Unlock()
    {
        _lock.Exit();
    }

    public void Remove(LinkedListNode<T> item)
    {
        _lru.Remove(item);
    }

    public void ReverseForEach(Func<LinkedListNode<T>, bool> func)
    {
        var item = _lru.Last;
        while (item != null)
        {
            if (func(item)) return;
            item = item.Previous;
        }   
    }
}

public sealed class TimeSeriesDatabaseInfo : DatabaseInfo
{
    private readonly TimeSeriesEntry?[] _entries;
    private readonly HashSet<KeyValueStorageShortKey> _dirtyKeys;
    private readonly TimeSeriesDatabaseParameters _parameters;
    private readonly Lru<LruItem> _lru;
    
    private int _totalSize;
    
    public static int BuildDate(DateOnly date)
    {
        return date.Year * 10000 + date.Month * 100 + date.Day;
    }
    
    
    internal TimeSeriesDatabaseInfo(TimeSeriesDatabaseParameters timeSeriesDatabaseParameters,
        IEnumerable<KeyValueStorageShortKey> existingKeys): base(timeSeriesDatabaseParameters.DbName)
    {
        _parameters = timeSeriesDatabaseParameters;
        _entries = new TimeSeriesEntry?[_parameters.NumEntries];
        foreach (var g in existingKeys.GroupBy(k => k.Key))
            _entries[_parameters.DateToKey(g.Key)] = new TimeSeriesEntry(g.Key, g
                .Select(k => k.PropertyName ?? "")
                .ToHashSet());
        _dirtyKeys = [];
        _totalSize = 0;
        _lru = new Lru<LruItem>();
    }
    
    internal void WriteDirtyData()
    {
        EnterReadLock();
        try
        {
            foreach (var key in _dirtyKeys.ToList())
            {
                var entry = _entries[key.Key]!;
                var propertyName = key.PropertyName ?? "";
                var value = entry.Values[propertyName]!;
                var kv = value.GetValue(_lru);
                _parameters.StorageInterface.Set(new KeyValueStorageKey(DbName, kv.Key, key.PropertyName),
                    kv.BuildData(_parameters.Versioned));
                value.SetDirty(false);
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

    internal IEnumerable<KeyValue> Get(int from, int to, string? propertyName)
    {
        Cleanup(true);
        var fromKey = _parameters.DateToKey(from);
        var toKey = _parameters.DateToKey(to);
        EnterReadLock();
        try
        {
            var result = Enumerable.Range(fromKey, toKey - fromKey + 1)
                .Select(i => _entries[i]?.Get(DbName, _parameters.Versioned, _parameters.StorageInterface,
                    propertyName, _lru, ref _totalSize))
                .Where(kv => kv != null)
                .Cast<KeyValue>();
            return result;
        }
        catch
        {
            ExitReadLock();
            throw;
        }
    }

    internal KeyValue? GetLast(int from, int to, string? propertyName)
    {
        Cleanup(true);
        var fromKey = _parameters.DateToKey(from);
        var toKey = _parameters.DateToKey(to);  
        EnterReadLock();
        try
        {
            var result = Enumerable.Range(fromKey, toKey - fromKey + 1)
                .Reverse()
                .Select(i => _entries[i]?.Get(DbName, _parameters.Versioned, _parameters.StorageInterface,
                    propertyName, _lru, ref _totalSize))
                .FirstOrDefault(kv => kv != null);
            return result;
        }
        catch
        {
            ExitReadLock();
            throw;
        }
    }

    private void Set(int date, string? propertyName, Func<byte[]> addFunc, Func<byte[], byte[]> updateFunc)
    {
        var pName = propertyName ?? "";
        var key = _parameters.DateToKey(date);
        var entry = _entries[key];
        if (entry != null && entry.Values.TryGetValue(pName, out var value))
        {
            value ??= entry.GetFromStorage(DbName, _parameters.Versioned, _parameters.StorageInterface, propertyName,
                _lru, ref _totalSize);
            _totalSize += value.SetData(updateFunc(value.Value.Value), _parameters.Versioned, _lru);
        }
        else
        {
            entry = new TimeSeriesEntry(date, []);
            _entries[key] = entry;
            value = new TimeSeriesEntryValue(new KeyValue(date, _parameters.Versioned ? 1 : 0, addFunc()),
                _lru, entry, pName);
            _totalSize += value.Value.Value.Length;
            entry.Values[pName] = value;
        }
        if (!_parameters.WriteBack)
            _parameters.StorageInterface.Set(new KeyValueStorageKey(DbName, date, propertyName),
                value.Value.BuildData(_parameters.Versioned));
        else
        {
            value.SetDirty(true);
            _dirtyKeys.Add(new KeyValueStorageShortKey(key, propertyName));
        }
    }

    internal void AddOrUpdate(int date, string? propertyName, Func<byte[]> addFunc, Func<byte[], byte[]> updateFunc)
    {
        EnterWriteLock();
        try
        {
            Cleanup(false);
            Set(date, propertyName, addFunc, updateFunc);
        }
        catch
        {
            ExitWriteLock();
            throw;
        }
    }

    internal KeyValue Get(int date, string? propertyName)
    {
        var key = _parameters.DateToKey(date);
        var result = _entries[key]?.Get(DbName, _parameters.Versioned, _parameters.StorageInterface,
                         propertyName, _lru, ref _totalSize)
               ?? throw new Exception($"item {DbName} {date} {propertyName} not found");
        return result;
    }

    internal void Set(List<KeyValue> data, string? propertyName)
    {
        Cleanup(false);
        foreach (var kv in data)
            Set(kv.Key, propertyName, () => kv.Value, _ => kv.Value);
    }

    public override void Cleanup(bool enterWriteLock)
    {
        Cleanup(enterWriteLock, _parameters.MaximumMemoryUsage);
    }

    private void Cleanup(bool enterWriteLock, int max)
    {
        if (_totalSize <= max) return;
        if (enterWriteLock) EnterWriteLock();
        try
        {
            var toRemove = new List<LinkedListNode<LruItem>>();
            _lru.ReverseForEach(item =>
            {
                if (_totalSize <= max) return true;
                if (item.Value.Value.IsDirty) return false;
                _totalSize -= item.Value.Value.Value.Value.Length;
                item.Value.Entry.Values[item.Value.PropertyName] = null;
                toRemove.Add(item);
                return false;
            });
            foreach (var item in toRemove)
                _lru.Remove(item);
        }
        finally
        {
            if (enterWriteLock) ExitWriteLock();
        }
    }

    public int GetTotalSize()
    {
        return _totalSize;
    }

    internal void FreeMemory()
    {
        Cleanup(false, 0);
    }
}

public sealed class TimeSeriesStorage: GenericKeyValueStorage<TimeSeriesDatabaseInfo>
{
    private Dictionary<string, TimeSeriesDatabaseParameters> _databaseParameters = null!;

    public TimeSeriesStorage(Logger logger, ServerConfigurationParameters parameters, IKeyValueStorage? storageInterface): 
        base(logger, parameters, storageInterface, InitOthers)
    {
    }
    
    public TimeSeriesStorage(Logger logger, ServerConfigurationParameters parameters):
        base(logger, parameters, null, InitOthers)
    {
    }
        
    private static void InitOthers(GenericKeyValueStorage<TimeSeriesDatabaseInfo> instance, Logger logger, ServerConfigurationParameters parameters)
    {
        var i = (TimeSeriesStorage)instance;
        var databaseParameters = parameters.GetParameter<Dictionary<string, TimeSeriesDatabaseParametersRecord>>("storageDatabaseParameters");
        var defaultParameters = databaseParameters.GetValueOrDefault("default");
        i._databaseParameters = databaseParameters
            .Where(kv => kv.Key != "default")
            .ToDictionary(kv => kv.Key,
                kv => new TimeSeriesDatabaseParameters(kv.Value, kv.Key, defaultParameters, i.StorageInterface,
                    i.StorageLogger, i.Versioned, i.WriteBackInterval > 0));
    }
    
    protected override TimeSeriesDatabaseInfo CreateDatabaseInfo(string dbName, IEnumerable<KeyValueStorageShortKey> existingKeys)
    {
        var parameters = _databaseParameters.TryGetValue(dbName, out var dbParameters)
            ? dbParameters
            : throw new Exception($"database {dbName} not found");
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

    public int GetTotalSize(string dbName)
    {
        return DbInfo[dbName].GetTotalSize();
    }
    
    public void FreeMemory()
    {
        foreach (var dbInfo in DbInfo.Values)
            dbInfo.FreeMemory();
    }
}