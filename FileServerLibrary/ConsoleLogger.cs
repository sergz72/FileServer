using System.Text.Json;

namespace FileServerLibrary;

public sealed class ConsoleLogger: Logger
{
    private readonly StreamWriter? _logFile;

    public ConsoleLogger(string prefix, StreamWriter? logFile, LogLevel logLevel): base(prefix, logLevel)
    {
        _logFile = logFile;
    }
    
    protected override void Log(LogLevel level, string message)
    {
        var formatted = FormatMessage(DateTime.Now, level, message);
        if (Level <= level)
            Console.WriteLine(formatted);
        _logFile?.WriteLine(formatted);
        _logFile?.Flush();
    }
}

public sealed class ConsoleLoggerCreator: ILoggerCreator
{
    private readonly StreamWriter? _logFile;
    
    public ConsoleLoggerCreator(ServerConfigurationParameters parameters)
    {
        var fileName = parameters.GetStringParameterOrNull("logFileName");
        _logFile = fileName != null ? new StreamWriter(fileName) : null;
    }
    
    public Logger CreateLogger(string prefix)
    {
        return new ConsoleLogger(prefix, _logFile, LogLevel.Debug);
    }
    
    public void Dispose()
    {
        _logFile?.Dispose();
    }
}