using FileServerLibrary;

namespace HomeAccountingLibrary;

public class HomeAccountingDecoder: IDecoderPlugin
{
    public HomeAccountingDecoder(ServerConfigurationParameters parameters)
    {
    }
    
    public ICommand Decode(Logger logger, byte[] data)
    {
        if (data.Length == 0)
            throw new ArgumentException("Empty command");
        return data[0] switch
        {
            0 => BuildGetCommand(data),
            1 => BuildSetCommand(data),
            2 => BuildGetLastCommand(data),
            3 => BuildGetFileVersionCommand(data),
            _ => throw new ArgumentException($"Unknown command {data[0]}")
        };
    }

    private ICommand BuildGetFileVersionCommand(byte[] data)
    {
        throw new NotImplementedException();
    }

    private ICommand BuildGetLastCommand(byte[] data)
    {
        throw new NotImplementedException();
    }

    private ICommand BuildGetCommand(byte[] data)
    {
        throw new NotImplementedException();
    }
    
    private ICommand BuildSetCommand(byte[] data)
    {
        throw new NotImplementedException();
    }

    private (string, int) GetDatabaseName(byte[] data)
    {
        throw new NotImplementedException();
    }
}