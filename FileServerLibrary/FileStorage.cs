namespace FileServerLibrary;

public sealed class FileStorage: IStoragePlugin
{
    private readonly string _baseFolder;
    private readonly int _keyDivider;
    private readonly ReaderWriterLockSlim _lock;

    private int _dbVersion;
    
    public FileStorage(ServerConfigurationParameters parameters)
    {
        _baseFolder = parameters.GetStringParameter("baseFolder");
        _keyDivider = parameters.GetIntParameter("keyDivider");
        _lock = new ReaderWriterLockSlim();
        _dbVersion = 1;
    }

    public (int, IEnumerable<KeyValue>) Get(string dbName, int from, int to, string? propertyName = null)
    {
        _lock.EnterReadLock();
        try
        {
        }
        finally
        {
            _lock.ExitReadLock();
        }
        throw new NotImplementedException();
    }

    public (int, int) GetFileVersion(string dbName, int key, string? propertyName = null)
    {
        var path = BuildPath(dbName, key, propertyName);
        _lock.EnterReadLock();
        try
        {
            return (_dbVersion, GetFileVersion(path));
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }
    
    public (int, KeyValue?) GetLast(string dbName, int from, int to, string? propertyName = null)
    {
        _lock.EnterReadLock();
        try
        {
        }
        finally
        {
            _lock.ExitReadLock();
        }
        throw new NotImplementedException();
    }

    public void Set(string dbName, int expectedVersion, List<KeyValue> data, string? propertyName = null)
    {
        _lock.EnterWriteLock();
        try
        {
            if (expectedVersion != _dbVersion) throw new Exception("Database version mismatch");
            _dbVersion++;
            foreach (var kv in data)
            {
                var path = BuildPath(dbName, kv.Key, propertyName, true);
                var version = File.Exists(path) ? GetFileVersion(path) + 1 : 1;
                Save(path, version, kv.Value);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private static void Save(string path, int version, byte[] value)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
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
}
