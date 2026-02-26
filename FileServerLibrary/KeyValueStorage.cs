using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Caching.Memory;

namespace FileServerLibrary;

public interface IKeyValueStorage
{
    byte[]? Get(KeyValueStorageKey key);
    void Set(KeyValueStorageKey key, byte[] value);
    void Delete(KeyValueStorageKey key);
    List<KeyValueStorageKey> GetKeys();
}

public sealed class DatabaseInfo(HashSet<KeyValueStorageShortKey> existingKeys)
{
    private readonly ReaderWriterLockSlim _lock = new();
    
    public int DbVersion { get; private set; } = 1;
    
    internal void EnterReadLock() => _lock.EnterReadLock();
    internal void EnterWriteLock() => _lock.EnterWriteLock();
    public void ExitReadLock() => _lock.ExitReadLock();
    public void ExitWriteLock() => _lock.ExitWriteLock();
    
    internal void CheckVersionAndIncrement(int expectedVersion)
    {
        if (expectedVersion != DbVersion) throw new Exception("Database version mismatch");
        DbVersion++;
    }

    public IEnumerable<KeyValueStorageShortKey> GetKeys(int from, int to, string? propertyName = null)
    {
        return existingKeys
            .Where(key => key.Key >= from && key.Key <= to && key.PropertyName == propertyName)
            .OrderBy(key => key.Key);
    }

    public int GetVersionAndUnlock()
    {
        var version = DbVersion;
        ExitReadLock();
        return version;
    }

    public void AddKey(KeyValueStorageKey cacheKey)
    {
        existingKeys.Add(new KeyValueStorageShortKey(cacheKey.Key, cacheKey.PropertyName));
    }

    public void IncrementVersion()
    {
        DbVersion++;
    }
}

public record KeyValueStorageKey(string DbName, int Key, string? PropertyName);
public record KeyValueStorageShortKey(int Key, string? PropertyName);
internal record KeyValueStorageCacheValue(KeyValue Value, bool Dirty);

public sealed class KeyValueStorage: IStoragePlugin
{
    private readonly int _writeBackInterval;
    private readonly bool _versioned;
    private readonly ConcurrentDictionary<string, DatabaseInfo> _dbInfo;
    private readonly MemoryCache _cache;
    private readonly Logger _logger;
    private readonly Task? _writeBackTask;
    private readonly IKeyValueStorage _storageInterface;
    
    private volatile bool _stop;
    
    public KeyValueStorage(Logger logger, ServerConfigurationParameters parameters)
    {
        _logger = logger;
        _storageInterface = parameters.CreateInstance<IKeyValueStorage>(
            parameters.GetStringParameter("storageInterface"), logger, parameters);
        _dbInfo = new ConcurrentDictionary<string, DatabaseInfo>(_storageInterface.GetKeys()
            .GroupBy(keys => keys.DbName)
            .ToDictionary(group => group.Key,
                group => new DatabaseInfo(group
                    .Select(item => new KeyValueStorageShortKey(item.Key, item.PropertyName))
                    .ToHashSet())));
        _writeBackInterval = parameters.GetIntParameterOrDefault("storageWriteBackInterval", 0);
        _versioned = parameters.GetBoolParameterOrDefault("versionedStorage", false);
        var maxCacheMemory = parameters.GetIntParameterOrDefault("storageCacheMemoryLimit", 300*1024*1024);
        _cache = new MemoryCache(new MemoryCacheOptions() { SizeLimit = maxCacheMemory });
        _stop = false;
        _writeBackTask = _writeBackInterval > 0 ? Task.Run(Loop) : null;
    }

    private void Loop()
    {
        while (true)
        {
            for (var i = 0; i < _writeBackInterval; i += 100)
            {
                if (_stop)
                {
                    WriteDirtyData();
                    return;
                }
                Thread.Sleep(100);
            }
            WriteDirtyData();
        }
    }

    private DatabaseInfo GetDatabaseInfo(string dbName) => _dbInfo.GetOrAdd(dbName, _ => new DatabaseInfo([]));
    
    private void WriteDirtyData()
    {
        foreach (var key in _cache.Keys.Select(k => (KeyValueStorageKey)k))
        {
            var item = _cache.Get<KeyValueStorageCacheValue>(key);
            if (item is { Dirty: true })
            {
                Write(key, item.Value);
                _cache.Set(key, item with { Dirty = false },
                    new MemoryCacheEntryOptions { Priority = CacheItemPriority.Low });
            }
        }
    }

    private void Write(KeyValueStorageKey key, KeyValue item)
    {
        if (!_versioned)
            _storageInterface.Set(key, item.Value);
        else
        {
            var data = new byte[item.Value.Length + 4];
            var version = BitConverter.GetBytes(item.Version);
            version.CopyTo(data, 0);
            item.Value.CopyTo(data, 4);
            _storageInterface.Set(key, data);
        }
    }

    public (DatabaseInfo, IEnumerable<KeyValue>) Get(string dbName, int from, int to, string? propertyName = null)
    {
        var dbInfo = GetDatabaseInfo(dbName);
        dbInfo.EnterReadLock();
        try
        {
             return (dbInfo, dbInfo.GetKeys(from, to).Select(key => Get(dbName, key).Value));
        }
        catch
        {
            dbInfo.ExitReadLock();
            throw;
        }
    }

    private KeyValueStorageCacheValue Get(string dbName, KeyValueStorageShortKey key)
    {
        if (TryGet(new KeyValueStorageKey(dbName, key.Key, key.PropertyName), out var result)) return result;
        throw new Exception($"item {dbName} {key.Key} {key.PropertyName} not found");
    }

    private bool TryGet(KeyValueStorageKey cacheKey, [MaybeNullWhen(false)] out KeyValueStorageCacheValue result)
    {
        return TryGet(cacheKey.DbName, cacheKey.Key, cacheKey.PropertyName, out result);
    }
    

    private bool TryGet(string dbName, int key, string? propertyName, [MaybeNullWhen(false)] out KeyValueStorageCacheValue result)
    {
        var cacheKey = new KeyValueStorageKey(dbName, key, propertyName);
        if (_cache.TryGetValue(cacheKey, out var value)) { result = (KeyValueStorageCacheValue)value!; return true; }
        var data = _storageInterface.Get(cacheKey);
        if (data == null) { result = null; return false; }
        var version = _versioned ? BitConverter.ToInt32(data, 0) : 0;
        result = new KeyValueStorageCacheValue(new KeyValue(key, version, _versioned ? data[4..] : data), false);
        _cache.Set(cacheKey, result);
        return true;
    }

    private KeyValueStorageCacheValue Get(string dbName, int key, string? propertyName)
    {
        if (TryGet(dbName, key, propertyName, out var result)) return result;
        throw new Exception($"item {dbName} {key} {propertyName} not found");
    }
    
    public (DatabaseInfo, int) GetFileVersion(string dbName, int key, string? propertyName = null)
    {
        var dbInfo = GetDatabaseInfo(dbName);
        dbInfo.EnterReadLock();
        try
        {
            return !_versioned
                ? (dbInfo, 0)
                : (dbInfo, Get(dbName, key, propertyName).Value.Version);
        }
        catch
        {
            dbInfo.ExitReadLock();
            throw;
        }
    }
    
    public (DatabaseInfo, KeyValue?) GetLast(string dbName, int from, int to, string? propertyName = null)
    {
        var dbInfo = GetDatabaseInfo(dbName);
        dbInfo.EnterReadLock();
        try
        {
            var key = dbInfo.GetKeys(from, to).LastOrDefault()?.Key;
            return key == null
                ? (dbInfo, null)
                : (dbInfo, Get(dbName, (int)key, propertyName).Value);
        }
        catch
        {
            dbInfo.ExitReadLock();
            throw;
        }
    }

    public DatabaseInfo Set(string dbName, int expectedVersion, List<KeyValue> data, string? propertyName = null)
    {
        var dbInfo = GetDatabaseInfo(dbName);
        dbInfo.EnterWriteLock();
        try
        {
            if (_versioned)
                dbInfo.CheckVersionAndIncrement(expectedVersion);
            else
                dbInfo.IncrementVersion();

            foreach (var kv in data)
            {
                var cacheKey = new KeyValueStorageKey(dbName, kv.Key, propertyName);
                var exists = TryGet(cacheKey, out var oldValue);
                var newKv = kv with { Version = _versioned ? (exists ? oldValue!.Value.Version + 1 : 1) : 0 };
                _cache.Set(cacheKey,
                    new KeyValueStorageCacheValue(newKv, true),
                    new MemoryCacheEntryOptions
                    {
                        Priority = _writeBackInterval <= 0 ? CacheItemPriority.Low : CacheItemPriority.NeverRemove,
                        Size = newKv.Value.Length
                    });
                dbInfo.AddKey(cacheKey);
                if (_writeBackInterval <= 0)
                    Write(cacheKey, newKv);
            }

            return dbInfo;
        }
        catch
        {
            dbInfo.ExitWriteLock();
            throw;
        }
    }
    
    public void Dispose()
    {
        _stop = true;
        _writeBackTask?.Wait();
    }
}
