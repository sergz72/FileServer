using Microsoft.VisualBasic.CompilerServices;

namespace FileServerLibrary;

internal sealed class TimeSeriesEntry
{
    internal bool IsDirty { get; private set; }
    internal readonly Dictionary<string, KeyValue?> Values;
    internal readonly int Date;

    internal TimeSeriesEntry(int date, Dictionary<string, KeyValue?> values)
    {
        Date = date;
        Values = values;
    }

    internal KeyValue? Get(string dbName, bool versioned, IKeyValueStorage storageInterface, string? propertyName = null)
    {
        var pName = propertyName ?? ""; 
        if (!Values.ContainsKey(pName)) return null;
        var data = storageInterface.Get(new KeyValueStorageKey(dbName, Date, propertyName));
        if (data == null) throw new Exception($"item {dbName} {Date} {propertyName} not found");
        var version = versioned ? BitConverter.ToInt32(data, 0) : 0;
        var kv = new KeyValue(Date, version, versioned ? data[4..] : data);
        Values[pName] = kv;
        return kv;
    }
}


public sealed class TimeSeriesDatabaseInfo : DatabaseInfo
{
    private readonly int _minDay;
    private readonly TimeSeriesEntry[] _entries;
    private readonly bool _versioned;
    private readonly IKeyValueStorage _storageInterface;
    
    internal TimeSeriesDatabaseInfo(string dbName, IEnumerable<KeyValueStorageShortKey> existingKeys, int minDay,
        int numEntries, bool versioned, IKeyValueStorage storageInterface): base(dbName)
    {
        _versioned = versioned;
        _storageInterface = storageInterface;
        _minDay = minDay;
        _entries = new TimeSeriesEntry[numEntries];
        foreach (var g in existingKeys.GroupBy(k => k.Key))
            _entries[DateToKey(g.Key)] = new TimeSeriesEntry(g.Key, g
                .Select<KeyValueStorageShortKey, (string, KeyValue?)>(k => (k.PropertyName ?? "", null))
                .ToDictionary());
    }

    private int DateToKey(int date)
    {
        return TimeSeriesStorage.DateToDayNumber(date) - _minDay;
    }

    internal void WriteDirtyData()
    {
        throw new NotImplementedException();
    }

    public IEnumerable<KeyValue> Get( int from, int to, string? propertyName)
    {
        var fromKey = DateToKey(from);
        var toKey = DateToKey(to);
        EnterReadLock();
        try
        {
            return Enumerable.Range(fromKey, toKey - fromKey + 1)
                .Select(i => _entries[i].Get(DbName, _versioned, _storageInterface, propertyName))
                .Where(kv => kv != null)
                .Cast<KeyValue>();
        }
        catch
        {
            ExitReadLock();
            throw;
        }
    }

    public KeyValue? GetLast(int from, int to, string? propertyName)
    {
        var fromKey = DateToKey(from);
        var toKey = DateToKey(to);  
        EnterReadLock();
        try
        {
            return Enumerable.Range(fromKey, toKey - fromKey + 1)
                .Reverse()
                .Select(i => _entries[i].Get(DbName, _versioned, _storageInterface, propertyName))
                .FirstOrDefault(kv => kv != null);
        }
        catch
        {
            ExitReadLock();
            throw;
        }
    }

    public void AddOrUpdate(int key, string? propertyName, Func<byte[]> addFunc, Func<byte[], byte[]> updateFunc)
    {
        throw new NotImplementedException();
    }

    public KeyValue Get(int date, string? propertyName)
    {
        var key = DateToKey(date);
        return _entries[key].Get(DbName, _versioned, _storageInterface, propertyName)
               ?? throw new Exception($"item {DbName} {date} {propertyName} not found");
    }

    public void Set(List<KeyValue> data, string? propertyName)
    {
        throw new NotImplementedException();
    }
}


public sealed class TimeSeriesStorage: GenericKeyValueStorage<TimeSeriesDatabaseInfo>
{
    private readonly int _minDay, _numDays;

    internal static int DateToDayNumber(int date) =>
        new DateOnly(date / 10000, (date / 100) % 100, date % 100).DayNumber;
    
    public TimeSeriesStorage(Logger logger, ServerConfigurationParameters parameters): base(logger, parameters)
    {
        _minDay = DateToDayNumber(parameters.GetIntParameter("storageMinimumDate"));
        _numDays = parameters.GetIntParameter("storageNumDays");
    }

    protected override TimeSeriesDatabaseInfo CreateDatabaseInfo(string dbName, IEnumerable<KeyValueStorageShortKey> existingKeys)
    {
        return new TimeSeriesDatabaseInfo(dbName, existingKeys, _minDay, _numDays, Versioned, StorageInterface);
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