using System.Text;
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
        var idx = GetString(data, 1, out var dbName);
        throw new NotImplementedException();
    }

    private ICommand BuildGetLastCommand(byte[] data)
    {
        var idx = GetString(data, 1, out var dbName);
        throw new NotImplementedException();
    }

    private ICommand BuildGetCommand(byte[] data)
    {
        var idx = GetString(data, 1, out var dbName);
        throw new NotImplementedException();
    }
    
    private ICommand BuildSetCommand(byte[] data)
    {
        var idx = GetString(data, 1, out var dbName);
        throw new NotImplementedException();
    }

    private static int GetString(byte[] data, int idx, out string s)
    {
        if (idx + 2 >= data.Length) throw new ArgumentException("GetString: data is too short");
        var length = data[idx++];
        if (idx + length >= data.Length) throw new ArgumentException("GetString: data is too short");
        s = Encoding.UTF8.GetString(data, idx, length);
        return length + idx;
    }
}