using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace FileServerLibrary.Tests;

[TestFixture]
[TestOf(typeof(KeyValueStorage))]
public class KeyValueStorageTest
{
    private const string BaseFolder = "/mnt/ramdisk";
    private const int MaxKey = 99999999;
    private static readonly string[] DbNames = ["db1", "db2"];
    
    private readonly ConcurrentDictionary<KeyValueStorageKey, KeyValue> _items = new();
    private readonly Lock _writeLock = new();
    private readonly Dictionary<string, int> _expectedVersions = DbNames.ToDictionary(dbName => dbName, _ => 1);
    
    private KeyValueStorage? _storage;
    private int _numOperations;
    
    private volatile bool _stop;
    
    [TearDown]
    public void TearDown()
    {
        _stop = false;
        _items.Clear();
        
        foreach (var key in _expectedVersions.Keys)
            _expectedVersions[key] = 1;
        Console.WriteLine($"Total operations count: {_numOperations}");
    }
    
    [Test]
    public void TestFileStorageWithoutWriteBackAndVersioning()
    {
        _storage = new KeyValueStorage(new ConsoleLogger("test", null, LogLevel.Error), new ServerConfigurationParameters(
            new Dictionary<string, Type> {{"MemoryStorage", typeof(MemoryStorage)}},
            new Dictionary<string, JsonElement>
            {
                {"storageInterface", JsonSerializer.SerializeToElement("MemoryStorage")},
                {"storageBaseFolder", JsonSerializer.SerializeToElement(BaseFolder)},
                {"versionedStorage", JsonSerializer.SerializeToElement(false)},
                {"storageWriteBackInterval", JsonSerializer.SerializeToElement(0)},
                {"storageKeyDivider", JsonSerializer.SerializeToElement(10000)}
            }));
        PerformTests(10, 2000, false);
    }

    [Test]
    public void TestFileStorageWithoutWriteBack()
    {
        _storage = new KeyValueStorage(new ConsoleLogger("test", null, LogLevel.Error), new ServerConfigurationParameters(
            new Dictionary<string, Type> {{"MemoryStorage", typeof(MemoryStorage)}},
            new Dictionary<string, JsonElement>
            {
                {"storageInterface", JsonSerializer.SerializeToElement("MemoryStorage")},
                {"storageBaseFolder", JsonSerializer.SerializeToElement(BaseFolder)},
                {"versionedStorage", JsonSerializer.SerializeToElement(true)},
                {"storageWriteBackInterval", JsonSerializer.SerializeToElement(0)},
                {"storageKeyDivider", JsonSerializer.SerializeToElement(10000)}
            }));
        PerformTests(10, 2000, true);
    }

    [Test]
    public void TestFileStorageWithWriteBack()
    {
        _storage = new KeyValueStorage(new ConsoleLogger("test", null, LogLevel.Error), new ServerConfigurationParameters(
            new Dictionary<string, Type> {{"MemoryStorage", typeof(MemoryStorage)}},
            new Dictionary<string, JsonElement>
            {
                {"storageInterface", JsonSerializer.SerializeToElement("MemoryStorage")},
                {"storageBaseFolder", JsonSerializer.SerializeToElement(BaseFolder)},
                {"versionedStorage", JsonSerializer.SerializeToElement(false)},
                {"storageWriteBackInterval", JsonSerializer.SerializeToElement(1000)},
                {"storageKeyDivider", JsonSerializer.SerializeToElement(10000)}
            }));
        PerformTests(10, 6000, false);
    }
    
    private void PerformTests(int numThreads, int time, bool versioned)
    {
        _numOperations = 0;
        var tasks = new Task[numThreads];
        for (var i = 0; i < numThreads; i++)
            tasks[i] = Task.Run(() => PerformTests(versioned));

        Thread.Sleep(time);
        _stop = true;
        Task.WaitAll(tasks);
        _storage?.Dispose();
        CompareStorages(versioned);
    }

    private void CompareStorages(bool versioned)
    {
        var iface = _storage!.GetStorageInterface();
        var keys = iface.GetKeys();
        Assert.AreEqual(_items.Count, keys.Count);
        foreach (var key in keys)
        {
            var value = iface.Get(key)!;
            if (versioned)
            {
                var version = BitConverter.ToInt32(value, 0);
                var item = _items[key];
                Assert.AreEqual(item.Version, version);
                Assert.AreEqual(item.Value, value[4..]);
            }
            else
                Assert.AreEqual(_items[key].Value, value);
        }
    }

    private static string GetRandomDbName() => DbNames[RandomNumberGenerator.GetInt32(0, DbNames.Length)];
    private static int GetRandom(int max = MaxKey) => RandomNumberGenerator.GetInt32(0, max);
    
    private void PerformTests(bool versioned)
    {
        while (!_stop)
        {
            Interlocked.Increment(ref _numOperations);
            var from = GetRandom();
            switch (RandomNumberGenerator.GetInt32(0, versioned ? 6 : 5))
            {
                //Get
                case 0:
                    GetCheck(GetRandomDbName(), from, from + GetRandom());
                    break;
                //Get last
                case 1:
                    GetLastCheck(GetRandomDbName(), from, from + GetRandom());
                    break;
                case 2:
                    Set(GetRandomDbName(), from, from + GetRandom(10), null, versioned);
                    break;
                case 3:
                    AddOrUpdate(GetRandomDbName(), from, null, versioned);
                    break;
                case 4:
                    AddOrUpdate(GetRandomDbName(), null, null, versioned);
                    break;
                default:
                    GetFileVersionCheck();
                    break;
            }
        }
    }

    private void AddOrUpdate(string dbName, int? key, string? propertyName, bool versioned)
    {
        if (key == null)
        {
            if (_items.IsEmpty) return;
            _writeLock.Enter();
            var idx = GetRandom(_items.Count);
            key = _items.ElementAt(idx).Key.Key;
        }
        else
            _writeLock.Enter();

        try
        {
            var newData = RandomNumberGenerator.GetBytes(GetRandom(100)); 
            var dbInfo = _storage!.AddOrUpdate(dbName, (int)key, propertyName, () => newData, _ => newData);
            var kv = new KeyValue((int)key, versioned ? 1 : 0, newData);
            _items.AddOrUpdate(new KeyValueStorageKey(dbName, (int)key, propertyName), kv,
                (_, value) => versioned ? kv with { Version = value.Version + 1 } : kv);
            dbInfo.ExitWriteLock();
        }
        finally
        {
            _writeLock.Exit();
        }
    }

    private void GetFileVersionCheck()
    {
        if (_items.IsEmpty) return;
        _writeLock.Enter();
        int version, expectedVersion;
        try
        {
            var idx = GetRandom(_items.Count);
            var keyValue = _items.ElementAt(idx);
            (var dbInfo, version) = _storage!.GetFileVersion(keyValue.Key.DbName, keyValue.Key.Key);
            dbInfo.ExitReadLock();
            expectedVersion = keyValue.Value.Version;
        }
        finally
        {
            _writeLock.Exit();
        }
        Assert.AreEqual(expectedVersion, version);
    }

    private void Set(string dbName, int from, int to, string? propertyName, bool versioned)
    {
        var toSet = Enumerable.Range(from, to - from + 1)
            .Select(i => new KeyValue(i, versioned ? 1 : 0, RandomNumberGenerator.GetBytes(GetRandom(100))))
            .ToList();
        _writeLock.Enter();
        try
        {
            var version = _expectedVersions[dbName];
            var dbInfo = _storage!.Set(dbName, version, toSet);
            _expectedVersions[dbName] = version + 1;
            foreach (var kv in toSet)
                _items.AddOrUpdate(new KeyValueStorageKey(dbName, kv.Key, propertyName), kv,
                    (_, value) => versioned ? kv with { Version = value.Version + 1 } : kv);
            dbInfo.ExitWriteLock();
        }
        finally
        {
            _writeLock.Exit();
        }
    }

    private void GetCheck(string dbName, int from, int to, string? propertyName = null)
    {
        DatabaseInfo? dbInfo = null;
        try
        {
            (dbInfo, var result) = _storage!.Get(dbName, from, to, propertyName);
            var resultList = result.ToList();
            var expected = _items
                .Where(kv =>
                    kv.Key.DbName == dbName && kv.Key.Key >= from && kv.Key.Key < to &&
                    kv.Key.PropertyName == propertyName)
                .Select(kv => kv.Value)
                .OrderBy(kv => kv.Key)
                .ToList();
            dbInfo.ExitReadLock();
            dbInfo = null;
            Assert.AreEqual(expected, resultList);
        }
        finally
        {
            dbInfo?.ExitReadLock();
        }
    }
    
    private void GetLastCheck(string dbName, int from, int to, string? propertyName = null)
    {
        DatabaseInfo? dbInfo = null;
        try
        {
            (dbInfo, var result) = _storage!.GetLast(dbName, from, to, propertyName);
            var expected = _items
                .Where(kv =>
                    kv.Key.DbName == dbName && kv.Key.Key >= from && kv.Key.Key < to &&
                    kv.Key.PropertyName == propertyName)
                .Select(kv => kv.Value)
                .OrderByDescending(kv => kv.Key)
                .FirstOrDefault();
            dbInfo.ExitReadLock();
            dbInfo = null;
            Assert.AreEqual(expected, result);
        }
        finally
        {
            dbInfo?.ExitReadLock();
        }
    }
}