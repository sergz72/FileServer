using System.Globalization;

namespace FileServerLibrary;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

public abstract class Logger
{
    private static readonly DateTimeFormatInfo DateTimeFormat = new DateTimeFormatInfo { ShortDatePattern = "yyyy-MM-dd" };
    
    protected readonly string Prefix;
    protected readonly LogLevel Level;

    protected Logger(string prefix, LogLevel logLevel)
    {
        Prefix = prefix;
        Level = logLevel;
    }

    public void Debug(string message)
    {
        Log(LogLevel.Debug, message);
    }

    public void Info(string message)
    {
        Log(LogLevel.Info, message);
    }

    public void Warning(string message)
    {
        Log(LogLevel.Warning, message);
    }

    public void Error(string message)
    {
        Log(LogLevel.Error, message);
    }
    
    protected virtual string FormatMessage(DateTime date, LogLevel level, string message) => $"{date.ToString(DateTimeFormat)} {Prefix}: {LevelToString(level)} {message}";
    
    protected static string LevelToString(LogLevel level) => level.ToString().ToUpper();
    
    protected abstract void Log(LogLevel level, string message);
}

public interface ILoggerCreator: IPlugin, IDisposable
{
    Logger CreateLogger(string prefix);
}
