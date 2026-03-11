using System.Diagnostics;

namespace FileServerLibrary;

internal sealed class TimeSeriesEntryValue
{
    private readonly LinkedListNode<LruItem> _node;

    internal KeyValue Value { get; private set; }
    
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
}

internal sealed class TimeSeriesEntry
{
    internal Dictionary<string, TimeSeriesEntryValue?> Values { get; private set; }
    internal readonly int Date;

    internal TimeSeriesEntry(int date)
    {
        Date = date;
        Values = [];
    }

    internal KeyValue? Get(string dbName, bool versioned, IKeyValueStorage storageInterface, string? propertyName,
        Lru<LruItem> lru, ref int sizeDifference)
    {
        var pName = propertyName ?? ""; 
        if (!Values.TryGetValue(pName, out var value)) return null;
        if (value != null)
            return value.GetValue(lru);
        var data = storageInterface.Get(new KeyValueStorageKey(dbName, Date, propertyName));
        if (data == null) throw new Exception($"item {dbName} {Date} {propertyName} not found");
        var kv = KeyValue.ReadData(Date, data, versioned);
        sizeDifference += kv.Value.Length;
        Values[pName] = new TimeSeriesEntryValue(kv, lru, this, pName);
        return kv;
    }

    public void SetValues(HashSet<string> values)
    {
        Values = values
            .Select<string, (string, TimeSeriesEntryValue?)>(v => (v, null))
            .ToDictionary();
    }
}

public record TimeSeriesDatabaseParametersRecord(int MaximumMemoryUsage, int MinimumDate, int NumDaysFromNow);

public sealed class TimeSeriesDatabaseParameters
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
    
    internal readonly int MinDay;

    public TimeSeriesDatabaseParameters(TimeSeriesDatabaseParametersRecord parameters, string dbName,
        TimeSeriesDatabaseParametersRecord? defaultParameters, IKeyValueStorage storageInterface, Logger storageLogger,
        bool versioned, bool writeBack)
    {
        MaximumMemoryUsage = parameters.MaximumMemoryUsage;
        MinimumDate = parameters.MinimumDate;
        NumDaysFromNow = parameters.NumDaysFromNow;
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
        MinDay = DateToDayNumber(MinimumDate);
        NumEntries = DateOnly.FromDateTime(DateTime.UtcNow).DayNumber - MinDay + NumDaysFromNow + 1;
    }
    
    internal int DateToKey(int date)
    {
        var day = DateToDayNumber(date);
        return day > MinDay ? day - MinDay : 0;
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
    private readonly TimeSeriesEntry[] _entries;
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
        _entries = new TimeSeriesEntry[_parameters.NumEntries];
        var date = DateOnly.FromDayNumber(_parameters.MinDay);
        for (var i = 0; i < _parameters.NumEntries; i++)
        {
            _entries[i] = new TimeSeriesEntry(BuildDate(date));
            date = date.AddDays(1);
        }
        foreach (var g in existingKeys.GroupBy(k => k.Key))
            _entries[_parameters.DateToKey(g.Key)].SetValues(g
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
                var entry = _entries[key.Key];
                var propertyName = key.PropertyName ?? "";
                var value = entry.Values[propertyName]!;
                var kv = value.GetValue(_lru);
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
        Cleanup(true);
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
        Cleanup(true);
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
        var key = _parameters.DateToKey(date);
        var pName = propertyName ?? "";
        EnterWriteLock();
        try
        {
            Cleanup(false);
            int difference;
            var entry = _entries[key];
            if (entry.Values.TryGetValue(pName, out var value))
                difference = value!.SetData(updateFunc(value.Value.Value), _parameters.Versioned, _lru);
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
        Cleanup(false);
        var pName = propertyName ?? "";
        foreach (var kv in data)
        {
            var key = _parameters.DateToKey(kv.Key);
            var entry = _entries[key];
            if (entry.Values.TryGetValue(pName, out var value))
                _totalSize += value!.SetData(kv.Value, _parameters.Versioned, _lru);
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

    public override void Cleanup(bool enterWriteLock)
    {
        if (_totalSize <= _parameters.MaximumMemoryUsage) return;
        if (enterWriteLock) EnterWriteLock();
        _lru.Lock();
        try
        {
            var toRemove = new List<LinkedListNode<LruItem>>();
            _lru.ReverseForEach(item =>
            {
                if (_totalSize <= _parameters.MaximumMemoryUsage) return true;
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
            _lru.Unlock();
            if (enterWriteLock) ExitWriteLock();
        }
    }
}

public sealed class TimeSeriesStorage: GenericKeyValueStorage<TimeSeriesDatabaseInfo>
{
    private readonly Dictionary<string, TimeSeriesDatabaseParameters> _databaseParameters;
    
    public TimeSeriesStorage(Logger logger, ServerConfigurationParameters parameters): base(logger, parameters)
    {
        var databaseParameters = parameters.GetParameter<Dictionary<string, TimeSeriesDatabaseParametersRecord>>("storageDatabaseParameters");
        var defaultParameters = databaseParameters.GetValueOrDefault("default");
        _databaseParameters = databaseParameters
            .Where(kv => kv.Key != "default")
            .ToDictionary(kv => kv.Key,
                kv => new TimeSeriesDatabaseParameters(kv.Value, kv.Key, defaultParameters, StorageInterface,
                    StorageLogger, Versioned, WriteBackInterval > 0));
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
}