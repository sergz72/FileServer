using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using NUnit.Framework;

namespace FileServerLibrary.Tests;

public class MemoryStorageWithInitialData : MemoryStorage
{
    public static readonly KeyValueStorageKey[] Keys =
    {
        new KeyValueStorageKey("db1", 20120101, null),
        new KeyValueStorageKey("db1", 20120101, "aggregated"),
        new KeyValueStorageKey("db2", 20120101, null),
        new KeyValueStorageKey("db2", 20120101, "aggregated")
    };

    public static readonly byte[][] Values =
    {
        [1, 2, 3, 4, 5, 6, 7, 8, 9, 10],
        [2, 3, 4, 5, 6, 7, 8, 9, 10],
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10],
        [3, 4, 5, 6, 7, 8, 9, 10]
    };
    
    public MemoryStorageWithInitialData(Logger logger, ServerConfigurationParameters parameters) : base(logger, parameters)
    {
        Set(Keys[0], Values[0]);
        Set(Keys[1], Values[1]);
        Set(Keys[2], Values[2]);
        Set(Keys[3], Values[3]);
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
    int Test5Version,
    byte[] Test5Data,
    int Test5Version2,
    byte[] Test5Data2
);

[TestFixture]
[TestOf(typeof(TimeSeriesStorage))]
public sealed class TimeSeriesStorageTest(): GenericKeyValueStorageTest<TimeSeriesDatabaseInfo>(
    TimeSeriesDatabaseParameters.DateToDayNumber(MinDate), 
    MaxDate.DayNumber)
{
    private const int MinDate = 20120101;
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
        TestGetSet(new TimeSeriesGetSetTestData(
            Versioned: false,
            MaximumMemoryUsage: 100 * 1024,
            WriteBackInterval: 0,
            Test1Version: 0,
            Test1Data: MemoryStorageWithInitialData.Values[0],
            Test2Version: 0,
            Test2Data: MemoryStorageWithInitialData.Values[1],
            Test3Size: MemoryStorageWithInitialData.Values[0].Length + MemoryStorageWithInitialData.Values[1].Length,
            Test4Data: Test4Data,
            Test4Data2: Test4Data2,
            Test5Version: 0,
            Test5Data: MemoryStorageWithInitialData.Values[2],
            Test5Version2: 0,
            Test5Data2: Test4Data2
            ));
    }

    [Test]
    public void TestGetSetNoWriteBack()
    {
        TestGetSet(new TimeSeriesGetSetTestData(
            Versioned: true,
            MaximumMemoryUsage: 100 * 1024,
            WriteBackInterval: 0,
            Test1Version: BitConverter.ToInt32(MemoryStorageWithInitialData.Values[0]),
            Test1Data: MemoryStorageWithInitialData.Values[0][4..],
            Test2Version: BitConverter.ToInt32(MemoryStorageWithInitialData.Values[1]),
            Test2Data: MemoryStorageWithInitialData.Values[1][4..],
            Test3Size: MemoryStorageWithInitialData.Values[0].Length + MemoryStorageWithInitialData.Values[1].Length - 8,
            Test4Data: Test4Data,
            Test4Data2: Test4Data2,
            Test5Version: BitConverter.ToInt32(MemoryStorageWithInitialData.Values[2]),
            Test5Data: MemoryStorageWithInitialData.Values[2][4..],
            Test5Version2: 2,
            Test5Data2: Test4Data2
        ));
    }
    

    public void TestGetSet(TimeSeriesGetSetTestData testData)
    {
        Storage = new TimeSeriesStorage(new ConsoleLogger("test", null, LogLevel.Error), new ServerConfigurationParameters(
            new Dictionary<string, Type> {{"MemoryStorageWithInitialData", typeof(MemoryStorageWithInitialData)}},
            new Dictionary<string, JsonElement>
            {
                {"storageInterface", JsonSerializer.SerializeToElement("MemoryStorageWithInitialData")},
                {"versionedStorage", JsonSerializer.SerializeToElement(testData.Versioned)},
                {"storageWriteBackInterval", JsonSerializer.SerializeToElement(testData.WriteBackInterval)},
                {"storageDatabaseParameters", JsonSerializer.SerializeToElement(
                    new Dictionary<string, TimeSeriesDatabaseParametersRecord>()
                    {
                        {"db1", new TimeSeriesDatabaseParametersRecord(testData.MaximumMemoryUsage, MinDate, NumDaysFromNow)},
                        {"db2", new TimeSeriesDatabaseParametersRecord(testData.MaximumMemoryUsage, MinDate, NumDaysFromNow)}
                    })}
            }));
        var storage = (TimeSeriesStorage)Storage;
        Assert.That(storage.GetTotalSize("db1"), Is.EqualTo(0));
        Assert.That(storage.GetTotalSize("db2"), Is.EqualTo(0));
        // test 1
        var (dbInfo, result) = storage.Get("db1", 20120101, 20120101);
        var resultList = result.ToList();
        dbInfo.ExitReadLock();
        Assert.That(resultList.Count, Is.EqualTo(1));
        Assert.That(resultList[0], Is.EqualTo(new KeyValue(20120101, testData.Test1Version, testData.Test1Data)));
        // test 2
        (dbInfo, result) = storage.Get("db1", 20120101, 20120101, "aggregated");
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
        (dbInfo, result) = storage.Get("db2", 20120101, 20120102);
        resultList = result.ToList();
        dbInfo.ExitReadLock();
        Assert.That(resultList.Count, Is.EqualTo(2));
        Assert.That(resultList[0], Is.EqualTo(new KeyValue(20120101, testData.Test5Version, testData.Test5Data)));
        Assert.That(resultList[1], Is.EqualTo(new KeyValue(20120102, testData.Test5Version2, testData.Test5Data2)));
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
                    new Dictionary<string, TimeSeriesDatabaseParametersRecord>()
                    {
                        {"db1", new TimeSeriesDatabaseParametersRecord(100 * 1024, MinDate, NumDaysFromNow)},
                        {"db2", new TimeSeriesDatabaseParametersRecord(100 * 1024, MinDate, NumDaysFromNow)}
                    })}
            }));
        PerformTests(10, 2000, true);
    }

    [Test]
    public void TestStorageWithWriteBack()
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
