using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace FileServerLibrary.Tests;

public class MemoryStorage: IKeyValueStorage
{
    private readonly ConcurrentDictionary<KeyValueStorageKey, byte[]> _storage = new ();
    private readonly Logger _logger;

    public MemoryStorage(Logger logger, ServerConfigurationParameters parameters)
    {
        _logger = logger;
    }
    

    public byte[]? Get(KeyValueStorageKey key)
    {
        return _storage.GetValueOrDefault(key);
    }

    public void Set(KeyValueStorageKey key, byte[] value)
    {
        _storage[key] = value;
    }

    public void Delete(KeyValueStorageKey key)
    {
        _storage.Remove(key, out _);
    }

    public List<KeyValueStorageKey> GetKeys()
    {
        return _storage.Keys.ToList();
    }
}