using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
    
    private KeyValueStorage? _storage;
    private Dictionary<string, int> _expectedVersions = DbNames.ToDictionary(dbName => dbName, _ => 1);
    private int _numOperations;
    
    private volatile bool _stop = false;
    
    [TearDown]
    public void TearDown()
    {
        _stop = false;
        _items.Clear();
        _storage?.Dispose();
        
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
        PerformTests(10, 1000, false);
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
        PerformTests(10, 1000, true);
    }
    
    private void PerformTests(int numThreads, int time, bool versioned)
    {
        _numOperations = 0;
        var tasks = new Task[numThreads];
        for (var i = 0; i < numThreads; i++)
        {
            var iLocal = i;
            tasks[i] = Task.Run(() => PerformTests(versioned));
        }

        Thread.Sleep(time);
        _stop = true;
        Task.WaitAll(tasks);
    }

    private static string GetRandomDbName() => DbNames[RandomNumberGenerator.GetInt32(0, DbNames.Length)];
    private static int GetRandom(int max = MaxKey) => RandomNumberGenerator.GetInt32(0, max);
    
    private void PerformTests(bool versioned)
    {
        while (!_stop)
        {
            Interlocked.Increment(ref _numOperations);
            var from = GetRandom();
            switch (RandomNumberGenerator.GetInt32(0, 3))
            {
                //Get
                case 0:
                    GetCheck(GetRandomDbName(), from, from + GetRandom());
                    break;
                //Get last
                case 1:
                    GetLastCheck(GetRandomDbName(), from, from + GetRandom());
                    break;
                default:
                    Set(GetRandomDbName(), from, from + GetRandom(10), null, versioned);
                    break;
            }
        }
    }

    private void Set(string dbName, int from, int to, string? propertyName, bool versioned)
    {
        var toSet = Enumerable.Range(from, to - from + 1)
            .Select(i => new KeyValue(i, versioned ? 1 : 0, new byte[GetRandom(100) + 1]))
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