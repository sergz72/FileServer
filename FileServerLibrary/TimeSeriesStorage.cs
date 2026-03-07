namespace FileServerLibrary;

internal sealed class TimeSeriesEntryValue
{
    internal KeyValue Value
    {
        get
        {
            LastAccessTime = DateTime.UtcNow.Ticks;
            return field;
        }
        private set;
    }

    internal long LastAccessTime { get; private set; }
    
    internal TimeSeriesEntryValue(KeyValue value)
    {
        Value = value;
        LastAccessTime = DateTime.UtcNow.Ticks;
    }

    public int SetData(byte[] newData, bool versioned)
    {
        var difference = newData.Length - Value.Value.Length;
        Value = new KeyValue(Value.Key, versioned ? Value.Version + 1 : 0, newData);
        LastAccessTime = DateTime.UtcNow.Ticks;
        return difference;
    }
}

internal sealed class TimeSeriesEntry
{
    internal readonly Dictionary<string, TimeSeriesEntryValue?> Values;
    internal readonly int Date;

    internal TimeSeriesEntry(int date, HashSet<string> values)
    {
        Date = date;
        Values = values.Select<string, (string, TimeSeriesEntryValue?)>(v => (v, null)).ToDictionary();
    }

    internal KeyValue? Get(string dbName, bool versioned, IKeyValueStorage storageInterface, string? propertyName, ref int sizeDifference)
    {
        var pName = propertyName ?? ""; 
        if (!Values.ContainsKey(pName)) return null;
        var data = storageInterface.Get(new KeyValueStorageKey(dbName, Date, propertyName));
        if (data == null) throw new Exception($"item {dbName} {Date} {propertyName} not found");
        var kv = KeyValue.ReadData(Date, data, versioned);
        sizeDifference += kv.Value.Length;
        Values[pName] = new TimeSeriesEntryValue(kv);
        return kv;
    }
    
    internal byte[] BuildData(bool versioned, string? propertyName)
    {
        var value = Values[propertyName ?? ""] ?? throw new Exception($"item {Date} {propertyName} not found");
        return value.Value.BuildData(versioned);
    }
}


public sealed class TimeSeriesDatabaseInfo : DatabaseInfo
{
    private readonly int _minDay;
    private readonly TimeSeriesEntry[] _entries;
    private readonly bool _versioned;
    private readonly IKeyValueStorage _storageInterface;
    private readonly Logger _storageLogger;
    private readonly bool _writeBack;
    private readonly HashSet<KeyValueStorageShortKey> _dirtyKeys;
    
    internal TimeSeriesDatabaseInfo(string dbName, IEnumerable<KeyValueStorageShortKey> existingKeys, int minDay,
        int numEntries, bool versioned, IKeyValueStorage storageInterface, Logger storageLogger, bool writeBack): base(dbName)
    {
        _writeBack = writeBack;
        _storageLogger = storageLogger;
        _versioned = versioned;
        _storageInterface = storageInterface;
        _minDay = minDay;
        _entries = new TimeSeriesEntry[numEntries];
        foreach (var g in existingKeys.GroupBy(k => k.Key))
            _entries[DateToKey(g.Key)] = new TimeSeriesEntry(g.Key, g
                .Select(k => k.PropertyName ?? "")
                .ToHashSet());
        _dirtyKeys = [];
    }

    private int DateToKey(int date)
    {
        var day = TimeSeriesStorage.DateToDayNumber(date);
        return day > _minDay ? day - _minDay : 0;
    }

    internal void WriteDirtyData()
    {
        EnterReadLock();
        try
        {
            foreach (var key in _dirtyKeys.ToList())
            {
                var value = _entries[key.Key].Values[key.PropertyName ?? ""]!;
                _storageInterface.Set(new KeyValueStorageKey(DbName, value.Value.Key, key.PropertyName), value.Value.BuildData(_versioned));
                _dirtyKeys.Remove(key);
            }
        }
        catch (Exception e)
        {
            _storageLogger.Error(e.Message);
        }
        finally
        {
            ExitReadLock();
        }
    }

    public IEnumerable<KeyValue> Get( int from, int to, string? propertyName, out int sizeDifference)
    {
        var fromKey = DateToKey(from);
        var toKey = DateToKey(to);
        EnterReadLock();
        try
        {
            var difference = 0;
            var result = Enumerable.Range(fromKey, toKey - fromKey + 1)
                .Select(i => _entries[i].Get(DbName, _versioned, _storageInterface, propertyName, ref difference))
                .Where(kv => kv != null)
                .Cast<KeyValue>();
            sizeDifference = difference;
            return result;
        }
        catch
        {
            ExitReadLock();
            throw;
        }
    }

    public KeyValue? GetLast(int from, int to, string? propertyName, out int sizeDifference)
    {
        var fromKey = DateToKey(from);
        var toKey = DateToKey(to);  
        EnterReadLock();
        try
        {
            var difference = 0;
            var result = Enumerable.Range(fromKey, toKey - fromKey + 1)
                .Reverse()
                .Select(i => _entries[i].Get(DbName, _versioned, _storageInterface, propertyName, ref difference))
                .FirstOrDefault(kv => kv != null);
            sizeDifference = difference;
            return result;
        }
        catch
        {
            ExitReadLock();
            throw;
        }
    }

    public int AddOrUpdate(int date, string? propertyName, Func<byte[]> addFunc, Func<byte[], byte[]> updateFunc)
    {
        var key = DateToKey(date);
        var pname = propertyName ?? "";
        EnterWriteLock();
        try
        {
            int difference;
            if (_entries[key].Values.TryGetValue(pname, out var value))
                difference = value!.SetData(updateFunc(value.Value.Value), _versioned);
            else
            {
                value = new TimeSeriesEntryValue(new KeyValue(date, _versioned ? 1 : 0, addFunc()));
                difference = value.Value.Value.Length;
                _entries[key].Values[pname] = value;
            }
            if (!_writeBack)
                _storageInterface.Set(new KeyValueStorageKey(DbName, date, propertyName), value.Value.BuildData(_versioned));
            else
                _dirtyKeys.Add(new KeyValueStorageShortKey(key, propertyName));
            return difference;
        }
        catch
        {
            ExitWriteLock();
            throw;
        }
    }

    public KeyValue Get(int date, string? propertyName, out int sizeDifference)
    {
        var key = DateToKey(date);
        var difference = 0;
        var result = _entries[key].Get(DbName, _versioned, _storageInterface, propertyName, ref difference)
               ?? throw new Exception($"item {DbName} {date} {propertyName} not found");
        sizeDifference = difference;
        return result;
    }

    public int Set(List<KeyValue> data, string? propertyName)
    {
        var pname = propertyName ?? "";
        EnterWriteLock();
        try
        {
            var difference = 0;
            foreach (var kv in data)
            {
                var key = DateToKey(kv.Key);
                if (_entries[key].Values.TryGetValue(pname, out var value))
                    difference += value!.SetData(kv.Value, _versioned);
                else
                {
                    value = new TimeSeriesEntryValue(_versioned ? kv with { Version = 1 } : kv);
                    difference += value.Value.Value.Length;
                    _entries[key].Values[pname] = value;
                }

                if (!_writeBack)
                    _storageInterface.Set(new KeyValueStorageKey(DbName, kv.Key, propertyName),
                        value.Value.BuildData(_versioned));
                else
                    _dirtyKeys.Add(new KeyValueStorageShortKey(key, propertyName));
            }
            return difference;
        }
        catch
        {
            ExitWriteLock();
            throw;
        }
    }

    public int Cleanup(int amountToFree)
    {
        if (amountToFree <= 0) return 0;
        EnterWriteLock();
        try
        {
            var freed = 0;
            foreach (var (entry, value) in _entries
                         .SelectMany(e => e.Values.Select(v => (e, v))
                         .Where(ev => ev.v.Value != null)
                         .OrderByDescending(ev => ev.v.Value!.LastAccessTime)))
            {
                freed += value.Value!.Value.Value.Length;
                entry.Values[value.Key] = null;
                if (freed >= amountToFree) break;
            }
            return freed;
        }
        finally
        {
            ExitWriteLock();
        }
    }
}


public sealed class TimeSeriesStorage: GenericKeyValueStorage<TimeSeriesDatabaseInfo>
{
    private readonly int _minDay, _numDays, _maximumMemoryUsage;
    private int _totalSize;

    internal static int DateToDayNumber(int date) =>
        new DateOnly(date / 10000, (date / 100) % 100, date % 100).DayNumber;
    
    public TimeSeriesStorage(Logger logger, ServerConfigurationParameters parameters): base(logger, parameters)
    {
        _totalSize = 0;
        _minDay = DateToDayNumber(parameters.GetIntParameter("storageMinimumDate"));
        _numDays = parameters.GetIntParameter("storageNumDays");
        _maximumMemoryUsage = parameters.GetIntParameterOrDefault("storageCacheMemoryLimit", 300*1024*1024);
    }

    protected override TimeSeriesDatabaseInfo CreateDatabaseInfo(string dbName, IEnumerable<KeyValueStorageShortKey> existingKeys)
    {
        return new TimeSeriesDatabaseInfo(dbName, existingKeys, _minDay, _numDays, Versioned,
            StorageInterface, StorageLogger, WriteBackInterval > 0);
    }

    protected override void WriteDirtyData()
    {
        foreach (var dbInfo in DbInfo.Values)
            dbInfo.WriteDirtyData();
    }

    public override (DatabaseInfo, IEnumerable<KeyValue>) Get(string dbName, int from, int to, string? propertyName = null)
    {
        var dbInfo = GetDatabaseInfo(dbName);
        _totalSize += dbInfo.Cleanup(_totalSize - _maximumMemoryUsage);
        var kvList = dbInfo.Get(from, to, propertyName, out var sizeDifference);
        _totalSize += sizeDifference;
        return (dbInfo, kvList);
    }

    public override (DatabaseInfo, KeyValue?) GetLast(string dbName, int from, int to, string? propertyName = null)
    {
        var dbInfo = GetDatabaseInfo(dbName);
        _totalSize += dbInfo.Cleanup(_totalSize - _maximumMemoryUsage);
        var kv = dbInfo.GetLast(from, to, propertyName, out var sizeDifference);
        _totalSize += sizeDifference;
        return (dbInfo, kv);
    }

    public override DatabaseInfo AddOrUpdate(string dbName, int key, string? propertyName, Func<byte[]> addFunc, Func<byte[], byte[]> updateFunc)
    {
        var dbInfo = GetDatabaseInfo(dbName);
        _totalSize += dbInfo.Cleanup(_totalSize - _maximumMemoryUsage);
        _totalSize += dbInfo.AddOrUpdate(key, propertyName, addFunc, updateFunc);
        return dbInfo;
    }

    protected override void Set(TimeSeriesDatabaseInfo dbInfo, List<KeyValue> data, string? propertyName = null)
    {
        _totalSize += dbInfo.Cleanup(_totalSize - _maximumMemoryUsage);
        _totalSize += dbInfo.Set(data, propertyName);
    }

    protected override KeyValue Get(TimeSeriesDatabaseInfo dbInfo, int key, string? propertyName)
    {
        _totalSize += dbInfo.Cleanup(_totalSize - _maximumMemoryUsage);
        var kv = dbInfo.Get(key, propertyName, out var sizeDifference);
        _totalSize += sizeDifference;
        return kv;
    }
}