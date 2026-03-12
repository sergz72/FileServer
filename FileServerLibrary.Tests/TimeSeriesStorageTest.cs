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
    
    [TearDown]
    public void TearDown()
    {
        TestTearDown();
    }

    [Test]
    public void TestGetSet()
    {
        Storage = new TimeSeriesStorage(new ConsoleLogger("test", null, LogLevel.Error), new ServerConfigurationParameters(
            new Dictionary<string, Type> {{"MemoryStorageWithInitialData", typeof(MemoryStorageWithInitialData)}},
            new Dictionary<string, JsonElement>
            {
                {"storageInterface", JsonSerializer.SerializeToElement("MemoryStorageWithInitialData")},
                {"versionedStorage", JsonSerializer.SerializeToElement(false)},
                {"storageWriteBackInterval", JsonSerializer.SerializeToElement(0)},
                {"storageDatabaseParameters", JsonSerializer.SerializeToElement(
                    new Dictionary<string, TimeSeriesDatabaseParametersRecord>()
                    {
                        {"db1", new TimeSeriesDatabaseParametersRecord(100 * 1024, MinDate, NumDaysFromNow)},
                        {"db2", new TimeSeriesDatabaseParametersRecord(100 * 1024, MinDate, NumDaysFromNow)}
                    })}
            }));
        var storage = (TimeSeriesStorage)Storage;
        Assert.That(storage.GetTotalSize("db1"), Is.EqualTo(0));
        Assert.That(storage.GetTotalSize("db2"), Is.EqualTo(0));
        var (dbInfo, result) = storage.Get("db1", 20120101, 20120101, null);
        var resultList = result.ToList();
        dbInfo.ExitReadLock();
        Assert.That(resultList.Count, Is.EqualTo(1));
        Assert.That(resultList[0], Is.EqualTo(new KeyValue(20120101, 0, MemoryStorageWithInitialData.Values[0])));
        (dbInfo, result) = storage.Get("db1", 20120101, 20120101, "aggregated");
        resultList = result.ToList();
        dbInfo.ExitReadLock();
        Assert.That(resultList.Count, Is.EqualTo(1));
        Assert.That(resultList[0], Is.EqualTo(new KeyValue(20120101, 0, MemoryStorageWithInitialData.Values[1])));
        Assert.That(storage.GetTotalSize("db1"), 
            Is.EqualTo(MemoryStorageWithInitialData.Values[0].Length + MemoryStorageWithInitialData.Values[1].Length));
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
