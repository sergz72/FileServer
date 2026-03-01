using System;
using NUnit.Framework;

namespace FileServerLibrary.Tests;

[TestFixture]
[TestOf(typeof(Logger))]
public class LoggerTests
{
    private class TestLogger(DateTime date, string prefix, LogLevel logLevel) : Logger(prefix, logLevel)
    {
        internal string Message { get; private set;} = "";
        
        protected override void Log(LogLevel level, string message)
        { 
            Message = FormatMessage(date, level, message);
        }
    }
    
    [Test]
    public void LoggerTest()
    {
        var date = new DateTime(2021, 2, 3, 4, 5, 6);
        var logger = new TestLogger(date, "test_prefix", LogLevel.Debug);
        logger.Debug("test message");
        Assert.That(logger.Message, Is.EqualTo("2021-02-03 04:05:06 test_prefix: DEBUG test message"));
    }
}