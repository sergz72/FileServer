using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using FileServerLibrary;

State state = new();
Configuration configuration;
bool stop = false;

try
{
    Array.ForEach(args, arg => state.Process(arg));
    configuration = state.Finish();
}
catch (Exception e)
{
    Console.WriteLine(e.InnerException != null ? e.InnerException.Message : e.Message);
    Usage();
    return;
}

ValidationResult serversAndLoggerCreator;

try
{
    serversAndLoggerCreator = configuration.Validate();
}
catch (Exception e)
{
    Console.WriteLine(e.InnerException != null ? e.InnerException.Message : e.Message);
    return;
}

Console.CancelKeyPress += (sender, eventArgs) =>
{
    eventArgs.Cancel = true;
    foreach (var server in serversAndLoggerCreator.Servers)
        server.Stop();
    stop = true;
};

try
{
    foreach (var server in serversAndLoggerCreator.Servers)
        Task.Run(() => server.Start());
    var logger = serversAndLoggerCreator.LoggerCreator.CreateLogger("Main");
    for (var i = 0; ; i++)
    {
        Thread.Sleep(100);
        if (i % state.MemoryUsageCheckInterval == 0)
        {
            var memoryUsageMb = Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024;
            logger.Info($"Total memory usage (Mb): {memoryUsageMb}");
        }
        if (stop)
            break;
    }
}
catch (Exception e)
{
    Console.WriteLine(e.InnerException != null ? e.InnerException.Message : e.Message);
    foreach (var server in serversAndLoggerCreator.Servers)
        server.Stop();
}

return;

void Usage()
{
    Console.WriteLine(
        "Usage: FileServer [-m memory_check_interval_in_seconds] [-c configFileName]");
}

internal sealed class State
{
    private string _configFileName = "configuration.json";
    private string _nextParameter = "";
    internal int MemoryUsageCheckInterval { get; private set; } = 100;
    
    internal void Process(string arg)
    {
        if (_nextParameter != "")
        {
            switch (_nextParameter)
            {
                case "c": _configFileName = arg; break;
                case "m": MemoryUsageCheckInterval = int.Parse(arg) * 10; break;
                default: throw new Exception($"Unknown parameter {_nextParameter}");
            }
            _nextParameter = "";
            return;
        }
        if (arg.StartsWith('-'))
            _nextParameter = arg[1..];
        else
            throw new Exception($"Unknown parameter {arg}");
    }

    internal Configuration Finish()
    {
        if (_nextParameter != "") throw new Exception($"Missing value for parameter {_nextParameter}");
        if (MemoryUsageCheckInterval <= 0) throw new Exception("Invalid memory usage check interval");
        var jsonString = File.ReadAllText(_configFileName);
        return JsonSerializer.Deserialize<Configuration>(jsonString) ??
                                throw new Exception("Invalid settings file");
    }
}

internal record ValidationResult(List<IServerPlugin> Servers, ILoggerCreator LoggerCreator);

internal record Configuration(List<string> Plugins, Dictionary<string, JsonElement> Parameters,
    List<ServerConfiguration> Servers, string StoragePlugin, string LoggerCreator)
{
    internal ValidationResult Validate()
    {
        if (Servers.Count == 0) throw new Exception("No servers defined");
        if (StoragePlugin == "") throw new Exception("Storage plugin is not set");
        if (LoggerCreator == "") throw new Exception("Logger creator plugin is not set");
        var plugins = Plugins.SelectMany(assemblyFileName => Load(Assembly.LoadFile(Path.GetFullPath(assemblyFileName))))
            .Concat(Load(AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "FileServerLibrary")))
            .ToDictionary();
        var parameters = new ServerConfigurationParameters(plugins, Parameters);
        var loggerCreator = parameters.CreateInstance<ILoggerCreator>(LoggerCreator, parameters);
        var storagePlugin = parameters.CreateInstance<IStoragePlugin>(StoragePlugin, loggerCreator.CreateLogger("Storage"), parameters);
        return new ValidationResult(Servers.Select(server => server.Validate(parameters, storagePlugin, loggerCreator)).ToList(), loggerCreator);
    }
    
    private static List<(string, Type)> Load(Assembly assembly)
    {
        return assembly.GetExportedTypes()
            .Where(t => typeof(IPlugin).IsAssignableFrom(t))
            .Select(t => (t.Name, t))
            .ToList();
    }
}

internal record ServerConfiguration(
    ushort Port,
    string Name,
    string Plugin,
    string Handler,
    string CryptoPlugin,
    string DecoderPlugin,
    string UserProvider
)
{
    public IServerPlugin Validate(ServerConfigurationParameters parameters, IStoragePlugin storagePlugin, ILoggerCreator loggerCreator)
    {
        if (Port == 0) throw new Exception("Port is not set");
        if (Name == "") throw new Exception("Name is not set");
        if (Plugin == "") throw new Exception("Plugin is not set");
        if (Handler == "") throw new Exception("Handler is not set");
        if (DecoderPlugin == "") throw new Exception("Decoder plugin is not set");
        var cryptoPlugin = CryptoPlugin == "" ? null : parameters.CreateInstance<ICryptoPlugin>(CryptoPlugin, parameters);
        var decoderPlugin = parameters.CreateInstance<IDecoderPlugin>(DecoderPlugin, parameters);
        var userProvider = UserProvider == "" ? null : parameters.CreateInstance<IUserProviderPlugin>(UserProvider, parameters);
        var serverParameters = new ServerParameters(Port, Name, Handler, parameters, cryptoPlugin, decoderPlugin, userProvider,
                                                        storagePlugin, loggerCreator);
        return parameters.CreateInstance<IServerPlugin>(Plugin, serverParameters);
    }
}

