using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace FileServerLibrary;

internal sealed class DatabaseInfo
{
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly Dictionary<int, Dictionary<string, KeyValue>> _toSave = new();
    
    internal int DbVersion { get; private set; } = 1;
    
    internal void EnterReadLock() => _lock.EnterReadLock();
    internal void EnterWriteLock() => _lock.EnterWriteLock();
    internal void ExitReadLock() => _lock.ExitReadLock();
    internal void ExitWriteLock() => _lock.ExitWriteLock();

    public bool TryGetValue(int key, string? propertyName, [MaybeNullWhen(false)] out KeyValue kv)
    {
        if (!_toSave.TryGetValue(key, out var kvMap))
        {
            kv = null;
            return false;
        }
        return kvMap.TryGetValue(propertyName ?? "", out kv);
    }

    public void CheckVersionAndIncrement(int expectedVersion)
    {
        if (expectedVersion != DbVersion) throw new Exception("Database version mismatch");
        DbVersion++;
    }

    public void Set(int key, string? propertyName, KeyValue kv)
    {
        if (_toSave.TryGetValue(key, out var kvMap))
            kvMap[propertyName ?? ""] = kv;
        else
            _toSave[key] = new Dictionary<string, KeyValue> {{propertyName ?? "", kv}};
    }

    public void Save(Logger logger, string dbName, FileStorage fileStorage)
    {
        EnterWriteLock();
        var keys = _toSave.Keys.ToList();
        foreach (var key in keys)
        {
                var kvMap = _toSave[key];
                var propertyNames = kvMap.Keys.ToList();
                foreach (var propertyName in propertyNames)
                {
                    var kv = kvMap[propertyName];
                    try
                    {
                        fileStorage.Write(dbName, kv, propertyName);
                        kvMap.Remove(propertyName);
                    }
                    catch (Exception e)
                    {
                        logger.Error(e.Message);
                    }
                }
                if (kvMap.Count == 0) _toSave.Remove(key);
        }
        ExitWriteLock();
    }
}

public sealed class FileStorage: IStoragePlugin
{
    private readonly string _baseFolder;
    private readonly int _keyDivider;
    private readonly int _writeBackInterval;
    private readonly bool _versioned;
    private readonly ConcurrentDictionary<string, DatabaseInfo> _toSave;
    private readonly Logger _logger;

    private volatile bool _stop, _stopped;
    
    public FileStorage(Logger logger, ServerConfigurationParameters parameters)
    {
        _logger = logger;
        _toSave = new ConcurrentDictionary<string, DatabaseInfo>();
        _baseFolder = parameters.GetStringParameter("baseFolder");
        _keyDivider = parameters.GetIntParameter("keyDivider");
        _writeBackInterval = parameters.GetIntParameterOrDefault("writeBackInterval", 0);
        _versioned = parameters.GetBoolParameterOrDefault("versionedFileStorage", false);
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

    private DatabaseInfo GetDatabaseInfo(string dbName) => _toSave.GetOrAdd(dbName, _ => new DatabaseInfo());
    
    private void WriteDirtyData()
    {
        foreach (var (dbName, dbInfo) in _toSave)
            dbInfo.Save(_logger, dbName, this);
    }

    public (int, IEnumerable<KeyValue>) Get(string dbName, int from, int to, string? propertyName = null)
    {
        var dbInfo = GetDatabaseInfo(dbName);
        dbInfo.EnterReadLock();
        try
        {
            throw new NotImplementedException();
        }
        finally
        {
            dbInfo.ExitReadLock();
        }
    }

    public (int, int) GetFileVersion(string dbName, int key, string? propertyName = null)
    {
        if (!_versioned) return (0, 0);
        
        var dbInfo = GetDatabaseInfo(dbName);
        dbInfo.EnterReadLock();
        try
        {
            if (dbInfo.TryGetValue(key, propertyName, out var value)) return (dbInfo.DbVersion, value.Version);
            var path = BuildPath(dbName, key, propertyName);
            return (dbInfo.DbVersion, GetFileVersion(path));
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
            throw new NotImplementedException();
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

            if (_writeBackInterval > 0)
            {
                foreach (var kv in data)
                {
                    if (dbInfo.TryGetValue(kv.Key, propertyName, out var oldValue))
                        dbInfo.Set(kv.Key, propertyName, kv with { Version = oldValue.Version + 1 });
                    else
                    {
                        var version = GetNextFileVersion(dbName, kv.Key, propertyName);
                        dbInfo.Set(kv.Key, propertyName, kv with { Version = version });
                    }
                }
                return;
            }

            Write(dbName, data, propertyName);
        }
        finally
        {
            dbInfo.ExitWriteLock();
        }
    }

    private int GetNextFileVersion(string dbName, int key, string? propertyName)
    {
        var path = BuildPath(dbName, key, propertyName);
        return GetNextFileVersion(path);
    }

    private int GetNextFileVersion(string path)
    {
        return _versioned && File.Exists(path) ? GetFileVersion(path) + 1 : 1;
    }
    
    private void Write(string dbName, List<KeyValue> data, string? propertyName)
    {
        foreach (var kv in data)
        {
            var path = BuildPath(dbName, kv.Key, propertyName, true);
            Save(path, GetNextFileVersion(path), kv.Value);
        }
    }

    internal void Write(string dbName, KeyValue kv, string? propertyName)
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

    private string BuildPath(string dbName, int key, string? propertyName, bool createFolder = false)
    {
        var folder = Path.Combine(_baseFolder, dbName, (key / _keyDivider).ToString());
        if (createFolder && !Directory.Exists(folder)) Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, key.ToString());
        return propertyName != null ? path + "." + propertyName : path;
    }
    
    private static int GetFileVersion(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        var data = new byte[4];
        if (fs.Read(data, 0, 4) != 4) throw new Exception("Can't read file version");
        return BitConverter.ToInt32(data);
    }

    public void Dispose()
    {
        if (_writeBackInterval <= 0) return;
        _stop = true;
        while (!_stopped)
            Thread.Sleep(100);
    }
}
