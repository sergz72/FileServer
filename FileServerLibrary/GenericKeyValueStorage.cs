using System.Collections.Concurrent;

namespace FileServerLibrary;

public interface IKeyValueStorage
{
    byte[]? Get(KeyValueStorageKey key);
    void Set(KeyValueStorageKey key, byte[] value);
    void Delete(KeyValueStorageKey key);
    List<KeyValueStorageKey> GetKeys();
}

public record KeyValueStorageKey(string DbName, int Key, string? PropertyName);
public record KeyValueStorageShortKey(int Key, string? PropertyName);

public abstract class GenericKeyValueStorage<T>: IStoragePlugin where T: DatabaseInfo
{
    protected readonly int WriteBackInterval;
    protected readonly bool Versioned;
    protected readonly ConcurrentDictionary<string, T> DbInfo;
    protected readonly Logger StorageLogger;
    protected readonly Task? WriteBackTask;
    protected readonly IKeyValueStorage StorageInterface;
    
    private volatile bool _stop;
    
    public GenericKeyValueStorage(Logger storageLogger, ServerConfigurationParameters parameters)
    {
        StorageLogger = storageLogger;
        StorageInterface = parameters.CreateInstance<IKeyValueStorage>(
            parameters.GetStringParameter("storageInterface"), storageLogger, parameters);
        Versioned = parameters.GetBoolParameterOrDefault("versionedStorage", false);
        WriteBackInterval = parameters.GetIntParameterOrDefault("storageWriteBackInterval", 0);
        DbInfo = new ConcurrentDictionary<string, T>(StorageInterface.GetKeys()
            .GroupBy(keys => keys.DbName)
            .ToDictionary(group => group.Key,
                group => CreateDatabaseInfo(group.Key, group
                    .Select(item => new KeyValueStorageShortKey(item.Key, item.PropertyName)))));
        _stop = false;
        WriteBackTask = WriteBackInterval > 0 ? Task.Run(Loop) : null;
    }

    protected abstract T CreateDatabaseInfo(string dbName, IEnumerable<KeyValueStorageShortKey> existingKeys);
    
    public IKeyValueStorage GetStorageInterface() => StorageInterface;
    
    private void Loop()
    {
        while (true)
        {
            for (var i = 0; i < WriteBackInterval; i += 100)
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

    protected T GetDatabaseInfo(string dbName) => DbInfo.GetOrAdd(dbName, CreateDatabaseInfo(dbName, []));

    protected abstract void WriteDirtyData();

    protected void Write(KeyValueStorageKey key, KeyValue item)
    {
        if (!Versioned)
            StorageInterface.Set(key, item.Value);
        else
        {
            var data = new byte[item.Value.Length + 4];
            var version = BitConverter.GetBytes(item.Version);
            version.CopyTo(data, 0);
            item.Value.CopyTo(data, 4);
            StorageInterface.Set(key, data);
        }
    }
    
    public abstract (DatabaseInfo, IEnumerable<KeyValue>) Get(string dbName, int from, int to, string? propertyName = null);
    
    protected abstract KeyValue Get(T dbinfo, int key, string? propertyName);
    
    public (DatabaseInfo, int) GetFileVersion(string dbName, int key, string? propertyName = null)
    {
        var dbInfo = GetDatabaseInfo(dbName);
        dbInfo.EnterReadLock();
        try
        {
            return !Versioned
                ? (dbInfo, 0)
                : (dbInfo, Get(dbInfo, key, propertyName).Version);
        }
        catch
        {
            dbInfo.ExitReadLock();
            throw;
        }
    }

    public abstract (DatabaseInfo, KeyValue?) GetLast(string dbName, int from, int to, string? propertyName = null);

    protected abstract void Set(T dbInfo, List<KeyValue> data, string? propertyName = null);
    
    public DatabaseInfo Set(string dbName, int expectedVersion, List<KeyValue> data, string? propertyName = null)
    {
        var dbInfo = GetDatabaseInfo(dbName);
        dbInfo.EnterWriteLock();
        try
        {
            if (Versioned)
                dbInfo.CheckVersionAndIncrement(expectedVersion);
            else
                dbInfo.IncrementVersion();

            Set(dbInfo, data, propertyName);

            return dbInfo;
        }
        catch
        {
            dbInfo.ExitWriteLock();
            throw;
        }
    }

    public abstract DatabaseInfo AddOrUpdate(string dbName, int key, string? propertyName, Func<byte[]> addFunc,
        Func<byte[], byte[]> updateFunc);

    public void Dispose()
    {
        _stop = true;
        WriteBackTask?.Wait();
    }
}
