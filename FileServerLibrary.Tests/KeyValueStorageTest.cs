using System;
using System.Collections.Generic;
using System.Text.Json;
using NUnit.Framework;

namespace FileServerLibrary.Tests;

[TestFixture]
[TestOf(typeof(KeyValueStorage))]
public sealed class KeyValueStorageTest(): GenericKeyValueStorageTest<KeyValueDatabaseInfo>(0, 99999999)
{
    [TearDown]
    public void TearDown()
    {
        TestTearDown();
    }
    
    [Test]
    public void TestFileStorageWithoutWriteBackAndVersioning()
    {
        Storage = new KeyValueStorage(new ConsoleLogger("test", null, LogLevel.Error), new ServerConfigurationParameters(
            new Dictionary<string, Type> {{"MemoryStorage", typeof(MemoryStorage)}},
            new Dictionary<string, JsonElement>
            {
                {"storageInterface", JsonSerializer.SerializeToElement("MemoryStorage")},
                {"versionedStorage", JsonSerializer.SerializeToElement(false)},
                {"storageWriteBackInterval", JsonSerializer.SerializeToElement(0)}
            }));
        PerformTests(10, 2000, false);
    }

    [Test]
    public void TestFileStorageWithoutWriteBack()
    {
        Storage = new KeyValueStorage(new ConsoleLogger("test", null, LogLevel.Error), new ServerConfigurationParameters(
            new Dictionary<string, Type> {{"MemoryStorage", typeof(MemoryStorage)}},
            new Dictionary<string, JsonElement>
            {
                {"storageInterface", JsonSerializer.SerializeToElement("MemoryStorage")},
                {"versionedStorage", JsonSerializer.SerializeToElement(true)},
                {"storageWriteBackInterval", JsonSerializer.SerializeToElement(0)}
            }));
        PerformTests(10, 2000, true);
    }

    [Test]
    public void TestFileStorageWithWriteBack()
    {
        Storage = new KeyValueStorage(new ConsoleLogger("test", null, LogLevel.Error), new ServerConfigurationParameters(
            new Dictionary<string, Type> {{"MemoryStorage", typeof(MemoryStorage)}},
            new Dictionary<string, JsonElement>
            {
                {"storageInterface", JsonSerializer.SerializeToElement("MemoryStorage")},
                {"versionedStorage", JsonSerializer.SerializeToElement(false)},
                {"storageWriteBackInterval", JsonSerializer.SerializeToElement(1000)}
            }));
        PerformTests(10, 6000, false);
    }
}