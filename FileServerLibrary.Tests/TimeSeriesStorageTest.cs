using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using NUnit.Framework;

namespace FileServerLibrary.Tests;

internal static class MemoryStorageInitialData
{
    private static readonly KeyValueStorageKey[] Keys =
    [
        new ("db1", 20120101, null),
        new ("db1", 20120101, "aggregated"),
        new ("db2", 20120101, null),
        new ("db2", 20120101, "aggregated")
    ];

    internal static readonly byte[][] Values =
    [
        [1, 2, 3, 4, 5, 6, 7, 8, 9, 10],
        [2, 3, 4, 5, 6, 7, 8, 9, 10],
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10],
        [3, 4, 5, 6, 7, 8, 9, 10]
    ];
    
    internal static void InitStorage(MemoryStorage storage)
    {
        storage.Set(Keys[0], Values[0]);
        storage.Set(Keys[1], Values[1]);
        storage.Set(Keys[2], Values[2]);
        storage.Set(Keys[3], Values[3]);
    }
}

public record TimeSeriesGetSetTestData(
    bool Versioned,
    int MaximumMemoryUsage,
    int WriteBackInterval,
    int Test1Version,
    byte[] Test1Data,
    int Test2Version,
    byte[] Test2Data,
    int Test3Size,
    byte[] Test4Data,
    byte[] Test4Data2,
    List<KeyValue> Test5Data,
    int Test6Version,
    byte[] Test6Data,
    int Test6Version2,
    byte[] Test6Data2,
    int Test6Version3,
    int Test6Version4
);

[TestFixture]
[TestOf(typeof(TimeSeriesStorage))]
public sealed class TimeSeriesStorageTest(): GenericKeyValueStorageTest<TimeSeriesDatabaseInfo>(
    TimeSeriesDatabaseParameters.DateToDayNumber(MinDate), 
    MaxDate.DayNumber)
{
    private const int MinDate = 20111231;
    private static readonly DateOnly MaxDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(NumDaysFromNow);
    private static readonly int MaxIntDate = TimeSeriesDatabaseInfo.BuildDate(MaxDate);
    private const int NumDaysFromNow = 3650;
    
    private static readonly byte[] Test4Data = [5, 6, 7, 8, 9, 0];
    private static readonly byte[] Test4Data2 = [9, 5, 6, 7, 8, 9, 0];
    
    [TearDown]
    public void TearDown()
    {
        TestTearDown();
    }

    [Test]
    public void TestGetSetNotVersionedNoWriteBack()
    {
        TestGetSetNotVersioned(0);
    }

    [Test]
    public void TestGetSetNotVersionedWriteBack()
    {
        TestGetSetNotVersioned(100);
    }
    
    private void TestGetSetNotVersioned(int writeBackInterval)
    {
        TestGetSet(new TimeSeriesGetSetTestData(
            Versioned: false,
            MaximumMemoryUsage: 100 * 1024,
            WriteBackInterval: writeBackInterval,
            Test1Version: 0,
            Test1Data: MemoryStorageInitialData.Values[0],
            Test2Version: 0,
            Test2Data: MemoryStorageInitialData.Values[1],
            Test3Size: MemoryStorageInitialData.Values[0].Length + MemoryStorageInitialData.Values[1].Length,
            Test4Data: Test4Data,
            Test4Data2: Test4Data2,
            Test5Data: [new KeyValue(20120103, 0, [9, 8, 0, 5, 6, 9, 0]), new KeyValue(20120104, 0, [1, 2, 5, 4, 6, 7, 2, 4, 5])],
            Test6Version: 0,
            Test6Data: MemoryStorageInitialData.Values[2],
            Test6Version2: 0,
            Test6Data2: Test4Data2,
            Test6Version3: 0,
            Test6Version4: 0
            ));
    }

    [Test]
    public void TestGetSetNoWriteBack()
    {
        TestGetSet(new TimeSeriesGetSetTestData(
            Versioned: true,
            MaximumMemoryUsage: 100 * 1024,
            WriteBackInterval: 0,
            Test1Version: BitConverter.ToInt32(MemoryStorageInitialData.Values[0]),
            Test1Data: MemoryStorageInitialData.Values[0][4..],
            Test2Version: BitConverter.ToInt32(MemoryStorageInitialData.Values[1]),
            Test2Data: MemoryStorageInitialData.Values[1][4..],
            Test3Size: MemoryStorageInitialData.Values[0].Length + MemoryStorageInitialData.Values[1].Length - 8,
            Test4Data: Test4Data,
            Test4Data2: Test4Data2,
            Test5Data: [new KeyValue(20120103, 0, [9, 8, 0, 5, 6, 9, 0]), new KeyValue(20120104, 0, [1, 2, 5, 4, 6, 7, 2, 4, 5])],
            Test6Version: BitConverter.ToInt32(MemoryStorageInitialData.Values[2]),
            Test6Data: MemoryStorageInitialData.Values[2][4..],
            Test6Version2: 2,
            Test6Data2: Test4Data2,
            Test6Version3: 1,
            Test6Version4: 1
        ));
    }

    private static TimeSeriesStorage CreateGetSetStorage(Logger logger, TimeSeriesGetSetTestData testData,
        IKeyValueStorage storageInterface)
    {
        return new TimeSeriesStorage(logger, new ServerConfigurationParameters(
            new Dictionary<string, Type>(),
            new Dictionary<string, JsonElement>
            {
                { "versionedStorage", JsonSerializer.SerializeToElement(testData.Versioned) },
                { "storageWriteBackInterval", JsonSerializer.SerializeToElement(testData.WriteBackInterval) },
                {
                    "storageDatabaseParameters", JsonSerializer.SerializeToElement(
                        new Dictionary<string, TimeSeriesDatabaseParametersRecord>
                        {
                            {
                                "db1",
                                new TimeSeriesDatabaseParametersRecord(testData.MaximumMemoryUsage, MinDate,
                                    NumDaysFromNow)
                            },
                            {
                                "db2",
                                new TimeSeriesDatabaseParametersRecord(testData.MaximumMemoryUsage, MinDate,
                                    NumDaysFromNow)
                            }
                        })
                }
            }), storageInterface);
    }

    private void TestGetSet(TimeSeriesGetSetTestData testData)
    {
        var logger = new ConsoleLogger("test", null, LogLevel.Error);
        var storageInterface = new MemoryStorage(logger, new ServerConfigurationParameters(
            new Dictionary<string, Type>(),
            new Dictionary<string, JsonElement>()));
        MemoryStorageInitialData.InitStorage(storageInterface);
        var storage = CreateGetSetStorage(logger, testData, storageInterface);
        Assert.That(storage.GetTotalSize("db1"), Is.EqualTo(0));
        Assert.That(storage.GetTotalSize("db2"), Is.EqualTo(0));
        // test 1
        var (dbInfo, result) = storage.Get("db1", 20120101, 20120101, false);
        var resultList = result.ToList();
        dbInfo.ExitReadLock();
        Assert.That(resultList.Count, Is.EqualTo(1));
        Assert.That(resultList[0], Is.EqualTo(new KeyValue(20120101, testData.Test1Version, testData.Test1Data)));
        // test 2
        (dbInfo, result) = storage.Get("db1", 20120101, 20120101, false, "aggregated");
        resultList = result.ToList();
        dbInfo.ExitReadLock();
        Assert.That(resultList.Count, Is.EqualTo(1));
        Assert.That(resultList[0], Is.EqualTo(new KeyValue(20120101, testData.Test2Version, testData.Test2Data)));
        // test 3
        Assert.That(storage.GetTotalSize("db1"), 
            Is.EqualTo(testData.Test3Size));
        // test 4
        dbInfo = storage.AddOrUpdate("db2", 20120102, null, () => testData.Test4Data, _ => throw new Exception("Should not be called"));
        dbInfo.ExitWriteLock();
        Assert.That(storage.GetTotalSize("db2"), 
            Is.EqualTo(testData.Test4Data.Length));
        dbInfo = storage.AddOrUpdate("db2", 20120102, null, () => throw new Exception("Should not be called"), _ => testData.Test4Data2);
        dbInfo.ExitWriteLock();
        Assert.That(storage.GetTotalSize("db2"), 
            Is.EqualTo(testData.Test4Data2.Length));
        // test 5
        dbInfo = storage.Set("db2", 1, testData.Test5Data);
        dbInfo.ExitWriteLock();
        Assert.That(storage.GetTotalSize("db2"), 
            Is.EqualTo(testData.Test4Data2.Length + testData.Test5Data[0].Value.Length + testData.Test5Data[1].Value.Length));
        Test6(storage, testData);
        storage.Dispose();
        storage.FreeMemory();
        Assert.That(storage.GetTotalSize("db1"), Is.EqualTo(0));
        Assert.That(storage.GetTotalSize("db2"), Is.EqualTo(0));
        storage = CreateGetSetStorage(logger, testData, storageInterface);
        Test6(storage, testData);
    }

    private void Test6(TimeSeriesStorage storage, TimeSeriesGetSetTestData testData)
    {
        // test 6
        var (dbInfo, result) = storage.Get("db2", 20120101, 20120104, false);
        var resultList = result.ToList();
        dbInfo.ExitReadLock();
        Assert.That(resultList.Count, Is.EqualTo(4));
        Assert.That(resultList[0], Is.EqualTo(new KeyValue(20120101, testData.Test6Version, testData.Test6Data)));
        Assert.That(resultList[1], Is.EqualTo(new KeyValue(20120102, testData.Test6Version2, testData.Test6Data2)));
        Assert.That(resultList[2], Is.EqualTo(new KeyValue(20120103, testData.Test6Version3, testData.Test5Data[0].Value)));
        Assert.That(resultList[3], Is.EqualTo(new KeyValue(20120104, testData.Test6Version4, testData.Test5Data[1].Value)));

        (dbInfo, result) = storage.Get("db2", 20120101, 20120104, true);
        resultList = result.ToList();
        dbInfo.ExitReadLock();
        Assert.That(resultList.Count, Is.EqualTo(4));
        Assert.That(resultList[3], Is.EqualTo(new KeyValue(20120101, testData.Test6Version, testData.Test6Data)));
        Assert.That(resultList[2], Is.EqualTo(new KeyValue(20120102, testData.Test6Version2, testData.Test6Data2)));
        Assert.That(resultList[1], Is.EqualTo(new KeyValue(20120103, testData.Test6Version3, testData.Test5Data[0].Value)));
        Assert.That(resultList[0], Is.EqualTo(new KeyValue(20120104, testData.Test6Version4, testData.Test5Data[1].Value)));
    }

    [Test]
    public void TestStorageWithoutWriteBackAndVersioning()
    {
        Storage = new TimeSeriesStorage(new ConsoleLogger("test", null, LogLevel.Error), new ServerConfigurationParameters(
            new Dictionary<string, Type> {{"MemoryStorage", typeof(MemoryStorage)}},
            new Dictionary<string, JsonElement>
            {
                {"storageInterface", JsonSerializer.SerializeToElement("MemoryStorage")},
                {"versionedStorage", JsonSerializer.SerializeToElement(false)},
                {"storageWriteBackInterval", JsonSerializer.SerializeToElement(0)},
                {"storageDatabaseParameters", JsonSerializer.SerializeToElement(
                    new Dictionary<string, TimeSeriesDatabaseParametersRecord>()
                    {
                        {"db1", new TimeSeriesDatabaseParametersRecord(100 * 1024, MinDate, NumDaysFromNow)},
                        {"db2", new TimeSeriesDatabaseParametersRecord(100 * 1024, MinDate, NumDaysFromNow)}
                    })}
            }));
        PerformTests(10, 2000, false);
        var storage = (TimeSeriesStorage) Storage;
        storage.FreeMemory();
        Assert.That(storage.GetTotalSize("db1"), Is.EqualTo(0));
        Assert.That(storage.GetTotalSize("db2"), Is.EqualTo(0));
    }

    [Test]
    public void TestStorageWithoutWriteBack()
    {
        Storage = new TimeSeriesStorage(new ConsoleLogger("test", null, LogLevel.Error), new ServerConfigurationParameters(
            new Dictionary<string, Type> {{"MemoryStorage", typeof(MemoryStorage)}},
            new Dictionary<string, JsonElement>
            {
                {"storageInterface", JsonSerializer.SerializeToElement("MemoryStorage")},
                {"versionedStorage", JsonSerializer.SerializeToElement(true)},
                {"storageWriteBackInterval", JsonSerializer.SerializeToElement(0)},
                {"storageDatabaseParameters", JsonSerializer.SerializeToElement(
                    new Dictionary<string, TimeSeriesDatabaseParametersRecord>
                    {
                        {"db1", new TimeSeriesDatabaseParametersRecord(100 * 1024, MinDate, NumDaysFromNow)},
                        {"db2", new TimeSeriesDatabaseParametersRecord(100 * 1024, MinDate, NumDaysFromNow)}
                    })}
            }));
        PerformTests(10, 2000, true);
        var storage = (TimeSeriesStorage) Storage;
        storage.FreeMemory();
        Assert.That(storage.GetTotalSize("db1"), Is.EqualTo(0));
        Assert.That(storage.GetTotalSize("db2"), Is.EqualTo(0));
    }

    [Test]
    public void TestStorageWithoutVersioningAndWithWriteBack()
    {
        Storage = new TimeSeriesStorage(new ConsoleLogger("test", null, LogLevel.Error), new ServerConfigurationParameters(
            new Dictionary<string, Type> {{"MemoryStorage", typeof(MemoryStorage)}},
            new Dictionary<string, JsonElement>
            {
                {"storageInterface", JsonSerializer.SerializeToElement("MemoryStorage")},
                {"versionedStorage", JsonSerializer.SerializeToElement(false)},
                {"storageWriteBackInterval", JsonSerializer.SerializeToElement(1000)},
                {"storageDatabaseParameters", JsonSerializer.SerializeToElement(
                    new Dictionary<string, TimeSeriesDatabaseParametersRecord>()
                    {
                        {"db1", new TimeSeriesDatabaseParametersRecord(100 * 1024, MinDate, NumDaysFromNow)},
                        {"db2", new TimeSeriesDatabaseParametersRecord(100 * 1024, MinDate, NumDaysFromNow)}
                    })}
            }));
        PerformTests(10, 6000, false);
        var storage = (TimeSeriesStorage) Storage;
        storage.FreeMemory();
        Assert.That(storage.GetTotalSize("db1"), Is.EqualTo(0));
        Assert.That(storage.GetTotalSize("db2"), Is.EqualTo(0));
    }

    protected override int GetRandomKey()
    {
        var days = RandomNumberGenerator.GetInt32(MinKey, MaxKey);
        var date = DateOnly.FromDayNumber(days);
        return TimeSeriesDatabaseInfo.BuildDate(date);
    }

    protected override int AddToKey(int date, int value)
    {
        var newDate = new DateOnly(date / 10000, (date / 100) % 100, date % 100).AddDays(value);
        if (newDate > MaxDate)
            return MaxIntDate;
        return TimeSeriesDatabaseInfo.BuildDate(newDate);
    }

    protected override IEnumerable<int> GetKeys(int from, int to)
    {
        var toDate = new DateOnly(to / 10000, (to / 100) % 100, to % 100);
        var fromDate = new DateOnly(from / 10000, (from / 100) % 100, from % 100);
        while (fromDate <= toDate)
        {
            yield return TimeSeriesDatabaseInfo.BuildDate(fromDate);
            fromDate = fromDate.AddDays(1);
        }
    }
}
