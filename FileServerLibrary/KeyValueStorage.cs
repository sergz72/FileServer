using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Caching.Memory;

namespace FileServerLibrary;

public sealed class KeyValueDatabaseInfo(string dbName, SortedSet<KeyValueStorageShortKey> existingKeys): DatabaseInfo(dbName)
{
    public IEnumerable<KeyValueStorageShortKey> GetKeys(int from, int to, bool reverse, string? propertyName = null)
    {
        var result =
            existingKeys.GetViewBetween(new KeyValueStorageShortKey(from, propertyName), new KeyValueStorageShortKey(to, propertyName))
                .Where(key => key.PropertyName == propertyName);
        return reverse ? result.Reverse() : result;
    }

    public void AddKey(KeyValueStorageKey cacheKey)
    {
        existingKeys.Add(new KeyValueStorageShortKey(cacheKey.Key, cacheKey.PropertyName));
    }

    public void RemoveKey(KeyValueStorageKey cacheKey)
    {
        existingKeys.Remove(new KeyValueStorageShortKey(cacheKey.Key, cacheKey.PropertyName));
    }
}

internal record KeyValueStorageCacheValue(KeyValue Value, bool Dirty);

public sealed class KeyValueStorage: GenericKeyValueStorage<KeyValueDatabaseInfo>
{
    private readonly MemoryCache _cache;
    
    public KeyValueStorage(Logger storageLogger, ServerConfigurationParameters parameters): base(storageLogger, parameters)
    {
        var maxCacheMemory = parameters.GetIntParameterOrDefault("storageCacheMemoryLimit", 300*1024*1024);
        _cache = new MemoryCache(new MemoryCacheOptions() { SizeLimit = maxCacheMemory });
    }

    protected override KeyValueDatabaseInfo CreateDatabaseInfo(string dbName,
        IEnumerable<KeyValueStorageShortKey> existingKeys)
    {
        return new KeyValueDatabaseInfo(dbName, new SortedSet<KeyValueStorageShortKey>(existingKeys));
    }
    
    protected override void WriteDirtyData()
    {
        foreach (var dbInfo in DbInfo.Values)
            dbInfo.EnterReadLock();
        try
        {
            foreach (var key in _cache.Keys.Select(k => (KeyValueStorageKey)k))
            {
                var item = _cache.Get<KeyValueStorageCacheValue>(key);
                if (item is { Dirty: true })
                {
                    Write(key, item.Value);
                    _cache.Set(key, item with { Dirty = false },
                        new MemoryCacheEntryOptions
                            { Priority = CacheItemPriority.Low, Size = item.Value.Value.Length });
                }
            }
        }
        catch (Exception e)
        {
            StorageLogger.Error(e.Message);
        }
        finally
        {
            foreach (var dbInfo in DbInfo.Values)
                dbInfo.ExitReadLock();
        }
    }
    
    public override (DatabaseInfo, IEnumerable<KeyValue>) Get(string dbName, int from, int to, bool reverse, string? propertyName = null)
    {
        var dbInfo = GetDatabaseInfo(dbName);
        dbInfo.EnterReadLock();
        try
        {
             return (dbInfo, dbInfo.GetKeys(from, to, reverse).Select(key => Get(dbName, key).Value));
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
        var data = StorageInterface.Get(cacheKey);
        if (data == null) { result = null; return false; }
        var version = Versioned ? BitConverter.ToInt32(data, 0) : 0;
        result = new KeyValueStorageCacheValue(new KeyValue(key, version, Versioned ? data[4..] : data), false);
        _cache.Set(cacheKey, result);
        return true;
    }

    private KeyValueStorageCacheValue GetCacheValue(string dbName, int key, string? propertyName)
    {
        if (TryGet(dbName, key, propertyName, out var result)) return result;
        throw new Exception($"item {dbName} {key} {propertyName} not found");
    }

    protected override KeyValue Get(KeyValueDatabaseInfo dbInfo, int key, string? propertyName) => GetCacheValue(dbInfo.DbName, key, propertyName).Value;
    
    public override (DatabaseInfo, KeyValue?) GetLast(string dbName, int from, int to, string? propertyName = null)
    {
        var dbInfo = GetDatabaseInfo(dbName);
        dbInfo.EnterReadLock();
        try
        {
            var key = dbInfo.GetKeys(from, to, true).FirstOrDefault()?.Key;
            return key == null
                ? (dbInfo, null)
                : (dbInfo, Get(dbInfo, (int)key, propertyName));
        }
        catch
        {
            dbInfo.ExitReadLock();
            throw;
        }
    }

    protected override void Set(KeyValueDatabaseInfo dbInfo, List<KeyValue> data, string? propertyName = null)
    {
        foreach (var kv in data)
        {
            var cacheKey = new KeyValueStorageKey(dbInfo.DbName, kv.Key, propertyName);
            Set(dbInfo, cacheKey, kv);
        }
    }

    private void Set(KeyValueDatabaseInfo dbInfo, KeyValueStorageKey cacheKey, KeyValue kv)
    {
        if (kv.Value.Length == 0) // delete
        {
            _cache.Remove(cacheKey);
            dbInfo.RemoveKey(cacheKey);
            StorageInterface.Delete(cacheKey);
            return;
        }
        var exists = TryGet(cacheKey, out var oldValue);
        var newKv = kv with { Version = Versioned ? (exists ? oldValue!.Value.Version + 1 : 1) : 0 };
        _cache.Set(cacheKey,
            new KeyValueStorageCacheValue(newKv, true),
            new MemoryCacheEntryOptions
            {
                Priority = WriteBackInterval <= 0 ? CacheItemPriority.Low : CacheItemPriority.NeverRemove,
                Size = newKv.Value.Length
            });
        dbInfo.AddKey(cacheKey);
        if (WriteBackInterval <= 0)
            Write(cacheKey, newKv);
    }

    public override DatabaseInfo AddOrUpdate(string dbName, int key, string? propertyName, Func<byte[]> addFunc, Func<byte[], byte[]> updateFunc)
    {
        var dbInfo = GetDatabaseInfo(dbName);
        dbInfo.EnterWriteLock();
        try
        {
            var cacheKey = new KeyValueStorageKey(dbName, key, propertyName);
            var newData = TryGet(cacheKey, out var oldValue) ? updateFunc(oldValue.Value.Value) : addFunc();
            Set(dbInfo, cacheKey, new KeyValue(key, 0, newData));
            
            return dbInfo;
        }
        catch
        {
            dbInfo.ExitWriteLock();
            throw;
        }
    }
}
