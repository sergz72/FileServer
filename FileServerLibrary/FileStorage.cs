using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Caching.Memory;

namespace FileServerLibrary;

internal sealed class DatabaseInfo
{
    private readonly ReaderWriterLockSlim _lock = new();
    
    internal int DbVersion { get; private set; } = 1;
    
    internal void EnterReadLock() => _lock.EnterReadLock();
    internal void EnterWriteLock() => _lock.EnterWriteLock();
    internal void ExitReadLock() => _lock.ExitReadLock();
    internal void ExitWriteLock() => _lock.ExitWriteLock();
    
    public void CheckVersionAndIncrement(int expectedVersion)
    {
        if (expectedVersion != DbVersion) throw new Exception("Database version mismatch");
        DbVersion++;
    }
}

internal record FileStorageMemoryCacheKey(string DbName, int Key, string? PropertyName);
internal record FileStorageMemoryCacheValue(KeyValue Value, bool Dirty);

public sealed class FileStorage: IStoragePlugin
{
    private readonly string _baseFolder;
    private readonly int _keyDivider;
    private readonly int _writeBackInterval;
    private readonly bool _versioned;
    private readonly ConcurrentDictionary<string, DatabaseInfo> _dbInfo;
    private readonly MemoryCache _cache;
    private readonly Logger _logger;

    private volatile bool _stop, _stopped;
    
    public FileStorage(Logger logger, ServerConfigurationParameters parameters)
    {
        _logger = logger;
        _dbInfo = new ConcurrentDictionary<string, DatabaseInfo>();
        _baseFolder = parameters.GetStringParameter("storageBaseFolder");
        _keyDivider = parameters.GetIntParameter("storageKeyDivider");
        _writeBackInterval = parameters.GetIntParameterOrDefault("storageWriteBackInterval", 0);
        _versioned = parameters.GetBoolParameterOrDefault("versionedStorage", false);
        var maxCacheMemory = parameters.GetIntParameterOrDefault("storageCacheMemoryLimit", 300*1024*1024);
        _cache = new MemoryCache(new MemoryCacheOptions() { SizeLimit = maxCacheMemory });
        _stop = false;
        _stopped = false;
        if (_writeBackInterval > 0)
            Task.Run(Loop);
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
                    _stopped = true;
                    return;
                }
                Thread.Sleep(100);
            }
            WriteDirtyData();
        }
    }

    private DatabaseInfo GetDatabaseInfo(string dbName) => _dbInfo.GetOrAdd(dbName, _ => new DatabaseInfo());
    
    private void WriteDirtyData()
    {
        foreach (var key in _cache.Keys.Select(k => (FileStorageMemoryCacheKey)k))
        {
            var item = _cache.Get<FileStorageMemoryCacheValue>(key);
            if (item is { Dirty: true })
            {
                Write(key.DbName, item.Value, key.PropertyName);
                _cache.Set(key, item with { Dirty = false },
                    new MemoryCacheEntryOptions { Priority = CacheItemPriority.Low });
            }
        }
    }

    public (int, IEnumerable<KeyValue>) Get(string dbName, int from, int to, string? propertyName = null)
    {
        var dbInfo = GetDatabaseInfo(dbName);
        dbInfo.EnterReadLock();
        try
        {
            var cachedKeys = _cache.Keys
                .Select(key => (FileStorageMemoryCacheKey)key)
                .Where(key =>
                    key.DbName == dbName && key.PropertyName == propertyName && key.Key >= from && key.Key <= to)
                .Select(key => key.Key)
                .ToList();
            var fromFolder = from / _keyDivider;
            var toFolder = to / _keyDivider;
            var allKeys = Enumerable.Range(fromFolder, toFolder - fromFolder + 1)
                        .SelectMany(key => GetKeys(dbName, key, propertyName))
                        .Where(key => key >= from && key <= to)
                        .ToHashSet();
             allKeys.UnionWith(cachedKeys);
             return (dbInfo.DbVersion, allKeys.OrderBy(key => key).Select(key => Get(dbName, key, propertyName).Value));
        }
        finally
        {
            dbInfo.ExitReadLock();
        }
    }

    private IEnumerable<int> GetKeys(string dbName, int key, string? propertyName = null)
    {
        var path = GetFolderName(dbName, key);
        return Directory.GetFiles(path)
            .Where(fname => propertyName == null || fname.EndsWith("." + propertyName))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(fname => fname != null)
            .Select(fname => int.Parse(fname!));
    }

    private bool TryGet(FileStorageMemoryCacheKey cacheKey, [MaybeNullWhen(false)] out FileStorageMemoryCacheValue result, bool onlyVersion = false)
    {
        return TryGet(cacheKey.DbName, cacheKey.Key, cacheKey.PropertyName, out result, onlyVersion);
    }
    

    private bool TryGet(string dbName, int key, string? propertyName, [MaybeNullWhen(false)] out FileStorageMemoryCacheValue result, bool onlyVersion = false)
    {
        var cacheKey = new FileStorageMemoryCacheKey(dbName, key, propertyName);
        if (_cache.TryGetValue(cacheKey, out var value)) { result = (FileStorageMemoryCacheValue)value!; return true; }
        var path = BuildPath(dbName, key, propertyName);
        if (!File.Exists(path)) { result = null; return false;}
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var br = new BinaryReader(fs);
        var version = _versioned ? br.ReadInt32() : 0;
        var data = new byte[onlyVersion ? 0 : fs.Length - 4];
        if (!onlyVersion && br.Read(data, 0, data.Length) != data.Length) throw new Exception("Can't read file data");
        result = new FileStorageMemoryCacheValue(new KeyValue(key, version, data), false);
        if (!onlyVersion)
            _cache.Set(cacheKey, result);
        return true;
    }

    private FileStorageMemoryCacheValue Get(string dbName, int key, string? propertyName, bool onlyVersion = false)
    {
        if (TryGet(dbName, key, propertyName, out var result, onlyVersion)) return result;
        throw new Exception($"item {dbName} {key} {propertyName} not found");
    }
    
    public (int, int) GetFileVersion(string dbName, int key, string? propertyName = null)
    {
        var dbInfo = GetDatabaseInfo(dbName);
        dbInfo.EnterReadLock();
        try
        {
            return !_versioned ? (dbInfo.DbVersion, 0) : (dbInfo.DbVersion, Get(dbName, key, propertyName, true).Value.Version);
        }
        finally
        {
            dbInfo.ExitReadLock();
        }
    }
    
    public (int, KeyValue?) GetLast(string dbName, int from, int to, string? propertyName = null)
    {
        var dbInfo = GetDatabaseInfo(dbName);
        dbInfo.EnterReadLock();
        try
        {
            var lastCachedKey = _cache.Keys
                .Select(key => (FileStorageMemoryCacheKey)key)
                .Where(key =>
                    key.DbName == dbName && key.PropertyName == propertyName && key.Key >= from && key.Key <= to)
                .Select(key => key.Key)
                .OrderByDescending(key => key)
                .FirstOrDefault(-1);
            var fromFolder = from / _keyDivider;
            var toFolder = to / _keyDivider;
            var lastFsKey = Enumerable.Range(fromFolder, toFolder - fromFolder + 1)
                .Reverse()
                .SelectMany(key => GetKeys(dbName, key, propertyName)
                    .Where(key2 => key2 >= from && key2 <= to)
                    .OrderByDescending(k => k))
                .FirstOrDefault(-1);
            var key = Math.Max(lastCachedKey, lastFsKey);
            return key == -1 ? (dbInfo.DbVersion, null) : (dbInfo.DbVersion, Get(dbName, key, propertyName).Value);
        }
        finally
        {
            dbInfo.ExitReadLock();
        }
    }

    public void Set(string dbName, int expectedVersion, List<KeyValue> data, string? propertyName = null)
    {
        var dbInfo = GetDatabaseInfo(dbName);
        dbInfo.EnterWriteLock();
        try
        {
            if (_versioned)
                dbInfo.CheckVersionAndIncrement(expectedVersion);

            foreach (var kv in data)
            {
                var cacheKey = new FileStorageMemoryCacheKey(dbName, kv.Key, propertyName);
                var exists = TryGet(cacheKey, out var oldValue, true);
                _cache.Set(cacheKey, new FileStorageMemoryCacheValue(kv with { Version = exists ? oldValue!.Value.Version + 1 : 1 }, true),
                    new MemoryCacheEntryOptions {Priority = _writeBackInterval <= 0 ? CacheItemPriority.Low : CacheItemPriority.NeverRemove});
                if (_writeBackInterval <= 0)
                    Write(dbName, kv, propertyName);
            }

        }
        finally
        {
            dbInfo.ExitWriteLock();
        }
    }
    
    private void Write(string dbName, KeyValue kv, string? propertyName)
    {
        var path = BuildPath(dbName, kv.Key, propertyName, true);
        Save(path, kv.Version, kv.Value);
    }
    
    private void Save(string path, int version, byte[] value)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        if (_versioned)
            fs.Write(BitConverter.GetBytes(version), 0, 4);
        fs.Write(value, 0, value.Length);
    }
    
    private string GetFolderName(string dbName, int key) => Path.Combine(_baseFolder, dbName, key.ToString());

    private string BuildPath(string dbName, int key, string? propertyName, bool createFolder = false)
    {
        var folder = GetFolderName(dbName, key / _keyDivider);
        if (createFolder && !Directory.Exists(folder)) Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, key.ToString());
        return propertyName != null ? path + "." + propertyName : path;
    }
    
    public void Dispose()
    {
        if (_writeBackInterval <= 0) return;
        _stop = true;
        while (!_stopped)
            Thread.Sleep(100);
    }
}
