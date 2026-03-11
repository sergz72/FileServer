using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace FileServerLibrary.Tests;

public abstract class GenericKeyValueStorageTest<T> where T: DatabaseInfo
{
    protected static readonly string[] DbNames = ["db1", "db2"];
    
    protected readonly ConcurrentDictionary<KeyValueStorageKey, KeyValue> Items = new();
    protected readonly Lock WriteLock = new();
    protected readonly Dictionary<string, int> ExpectedVersions = DbNames.ToDictionary(dbName => dbName, _ => 1);

    protected readonly int MinKey, MaxKey;
    
    protected GenericKeyValueStorage<T>? Storage;
    protected int NumOperations;
    
    private volatile bool _stop;

    public GenericKeyValueStorageTest(int minKey, int maxKey)
    {
        MinKey = minKey;
        MaxKey = maxKey;
    }
    
    protected void TestTearDown()
    {
        _stop = false;
        Items.Clear();
        
        foreach (var key in ExpectedVersions.Keys)
            ExpectedVersions[key] = 1;
        Console.WriteLine($"Total operations count: {NumOperations}");
    }
    
    protected void PerformTests(int numThreads, int time, bool versioned)
    {
        NumOperations = 0;
        var tasks = new Task[numThreads];
        for (var i = 0; i < numThreads; i++)
            tasks[i] = Task.Run(() => PerformTests(versioned));

        Thread.Sleep(time);
        _stop = true;
        Task.WaitAll(tasks);
        Storage?.Dispose();
        CompareStorages(versioned);
    }

    protected void CompareStorages(bool versioned)
    {
        var iface = Storage!.GetStorageInterface();
        var keys = iface.GetKeys();
        Assert.AreEqual(Items.Count, keys.Count);
        foreach (var key in keys)
        {
            var value = iface.Get(key)!;
            if (versioned)
            {
                var version = BitConverter.ToInt32(value, 0);
                var item = Items[key];
                Assert.AreEqual(item.Version, version);
                Assert.AreEqual(item.Value, value[4..]);
            }
            else
                Assert.AreEqual(Items[key].Value, value);
        }
    }

    protected static string GetRandomDbName() => DbNames[RandomNumberGenerator.GetInt32(0, DbNames.Length)];
    protected virtual int GetRandomKey() => RandomNumberGenerator.GetInt32(MinKey, MaxKey);
    protected virtual int AddToKey(int key, int value) => key + value;
    
    protected void PerformTests(bool versioned)
    {
        while (!_stop)
        {
            Interlocked.Increment(ref NumOperations);
            var from = GetRandomKey();
            switch (RandomNumberGenerator.GetInt32(0, versioned ? 6 : 5))
            {
                //Get
                case 0:
                    GetCheck(GetRandomDbName(), from, AddToKey(from, RandomNumberGenerator.GetInt32(0, MaxKey)));
                    break;
                //Get last
                case 1:
                    GetLastCheck(GetRandomDbName(), from, AddToKey(from, RandomNumberGenerator.GetInt32(0, MaxKey)));
                    break;
                case 2:
                    Set(GetRandomDbName(), from, AddToKey(from, RandomNumberGenerator.GetInt32(0, 10)), null, versioned);
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

    protected void AddOrUpdate(string dbName, int? key, string? propertyName, bool versioned)
    {
        if (key == null)
        {
            if (Items.IsEmpty) return;
            WriteLock.Enter();
            var idx = RandomNumberGenerator.GetInt32(0, Items.Count);
            key = Items.ElementAt(idx).Key.Key;
        }
        else
            WriteLock.Enter();

        try
        {
            var newData = RandomNumberGenerator.GetBytes(RandomNumberGenerator.GetInt32(0, 100)); 
            var dbInfo = Storage!.AddOrUpdate(dbName, (int)key, propertyName, () => newData, _ => newData);
            var kv = new KeyValue((int)key, versioned ? 1 : 0, newData);
            Items.AddOrUpdate(new KeyValueStorageKey(dbName, (int)key, propertyName), kv,
                (_, value) => versioned ? kv with { Version = value.Version + 1 } : kv);
            dbInfo.ExitWriteLock();
        }
        finally
        {
            WriteLock.Exit();
        }
    }

    protected void GetFileVersionCheck()
    {
        if (Items.IsEmpty) return;
        WriteLock.Enter();
        int version, expectedVersion;
        try
        {
            var idx = RandomNumberGenerator.GetInt32(0, Items.Count);
            var keyValue = Items.ElementAt(idx);
            (var dbInfo, version) = Storage!.GetFileVersion(keyValue.Key.DbName, keyValue.Key.Key);
            dbInfo.ExitReadLock();
            expectedVersion = keyValue.Value.Version;
        }
        finally
        {
            WriteLock.Exit();
        }
        Assert.AreEqual(expectedVersion, version);
    }

    protected virtual IEnumerable<int> GetKeys(int from, int to) => Enumerable.Range(from, to - from + 1);
    
    protected void Set(string dbName, int from, int to, string? propertyName, bool versioned)
    {
        var toSet = GetKeys(from, to)
            .Select(i => new KeyValue(i, versioned ? 1 : 0, RandomNumberGenerator.GetBytes(RandomNumberGenerator.GetInt32(0, 100))))
            .ToList();
        WriteLock.Enter();
        try
        {
            var version = ExpectedVersions[dbName];
            var dbInfo = Storage!.Set(dbName, version, toSet);
            ExpectedVersions[dbName] = version + 1;
            foreach (var kv in toSet)
                Items.AddOrUpdate(new KeyValueStorageKey(dbName, kv.Key, propertyName), kv,
                    (_, value) => versioned ? kv with { Version = value.Version + 1 } : kv);
            dbInfo.ExitWriteLock();
        }
        finally
        {
            WriteLock.Exit();
        }
    }

    protected void GetCheck(string dbName, int from, int to, string? propertyName = null)
    {
        DatabaseInfo? dbInfo = null;
        try
        {
            (dbInfo, var result) = Storage!.Get(dbName, from, to, propertyName);
            var resultList = result.ToList();
            var expected = Items
                .Where(kv =>
                    kv.Key.DbName == dbName && kv.Key.Key >= from && kv.Key.Key <= to &&
                    kv.Key.PropertyName == propertyName)
                .Select(kv => kv.Value)
                .OrderBy(kv => kv.Key)
                .ToList();
            dbInfo.ExitReadLock();
            dbInfo = null;
            if (expected.Count != resultList.Count)
                Assert.Fail("Different number of items");
            Assert.AreEqual(expected, resultList);
        }
        finally
        {
            dbInfo?.ExitReadLock();
        }
    }
    
    protected void GetLastCheck(string dbName, int from, int to, string? propertyName = null)
    {
        DatabaseInfo? dbInfo = null;
        try
        {
            (dbInfo, var result) = Storage!.GetLast(dbName, from, to, propertyName);
            var expected = Items
                .Where(kv =>
                    kv.Key.DbName == dbName && kv.Key.Key >= from && kv.Key.Key <= to &&
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