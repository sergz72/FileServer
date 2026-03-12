using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace FileServerLibrary.Tests;

public class MemoryStorage: IKeyValueStorage
{
    protected readonly ConcurrentDictionary<KeyValueStorageKey, byte[]> Storage = new ();
    protected readonly Logger Log;

    public MemoryStorage(Logger logger, ServerConfigurationParameters parameters)
    {
        Log = logger;
    }
    

    public byte[]? Get(KeyValueStorageKey key)
    {
        return Storage.GetValueOrDefault(key);
    }

    public void Set(KeyValueStorageKey key, byte[] value)
    {
        Storage[key] = value;
    }

    public void Delete(KeyValueStorageKey key)
    {
        Storage.Remove(key, out _);
    }

    public List<KeyValueStorageKey> GetKeys()
    {
        return Storage.Keys.ToList();
    }
}