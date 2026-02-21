using System.Reflection;
using System.Text.Json;
using FileServer;
using FileServerLibrary;

State state = new();
Configuration configuration;

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

Server server;

try
{
    server = new Server(configuration.Validate());
}
catch (Exception e)
{
    Console.WriteLine(e.InnerException != null ? e.InnerException.Message : e.Message);
    return;
}

Console.CancelKeyPress += (sender, eventArgs) =>
{
    eventArgs.Cancel = true;
    server.Stop();
};

try
{
    server.Start();
}
catch (Exception e)
{
    Console.WriteLine(e.InnerException != null ? e.InnerException.Message : e.Message);
}

return;

void Usage()
{
    Console.WriteLine(
        "Usage: FileServer [-p port] [-c configFileName]");
}

internal sealed class State
{
    private ushort _port;
    private string _configFileName = "configuration.json";
    private string _nextParameter = "";
    
    internal void Process(string arg)
    {
        if (_nextParameter != "")
        {
            switch (_nextParameter)
            {
                case "p": _port = ushort.Parse(arg); break;
                case "c": _configFileName = arg; break;
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
        var jsonString = File.ReadAllText(_configFileName);
        var config = JsonSerializer.Deserialize<Configuration>(jsonString) ??
                                throw new Exception("Invalid settings file");
        var configuration = _port == 0 ? config : config with { Port = _port };
        return configuration;
    }
}

internal record Configuration(ushort Port, List<string> Plugins, Dictionary<string, JsonElement> Parameters,
    string CryptoPlugin, string DecoderPlugin, string StoragePlugin, string LoggerCreator, string UserProvider)
{
    internal ServerConfiguration Validate()
    {
        if (Port == 0) throw new Exception("Port is not set");
        if (CryptoPlugin == "") throw new Exception("Crypto plugin is not set");
        if (DecoderPlugin == "") throw new Exception("Decoder plugin is not set");
        if (StoragePlugin == "") throw new Exception("Storage plugin is not set");
        if (LoggerCreator == "") throw new Exception("Logger creator plugin is not set");
        if (UserProvider == "") throw new Exception("User provider plugin is not set");
        var plugins = Plugins.SelectMany(assemblyFileName => Load(Assembly.LoadFile(Path.GetFullPath(assemblyFileName))))
            .Concat(Load(AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "FileServerLibrary")))
            .ToDictionary();
        var parameters = new ServerConfigurationParameters(plugins, Parameters);
        var cryptoPlugin = parameters.CreateInstance<ICryptoPlugin>(CryptoPlugin, parameters);
        var decoderPlugin = parameters.CreateInstance<IDecoderPlugin>(DecoderPlugin, parameters);
        var storagePlugin = parameters.CreateInstance<IStoragePlugin>(StoragePlugin, parameters);
        var loggerCreator = parameters.CreateInstance<ILoggerCreator>(LoggerCreator, parameters);
        var userProvider = parameters.CreateInstance<IUserProviderPlugin>(UserProvider, parameters);
        return new ServerConfiguration(Port, parameters, cryptoPlugin, decoderPlugin, storagePlugin, loggerCreator, userProvider);
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
    ServerConfigurationParameters Parameters,
    ICryptoPlugin CryptoPlugin,
    IDecoderPlugin DecoderPlugin,
    IStoragePlugin StoragePlugin,
    ILoggerCreator LoggerCreator,
    IUserProviderPlugin UserProvider);
