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
        var reader = new BinaryReader(new MemoryStream(data));
        var commandId = reader.ReadByte();
        return commandId switch
        {
            0 => BuildGetCommand(reader),
            1 => BuildSetCommand(reader),
            2 => BuildGetLastCommand(reader),
            3 => BuildGetFileVersionCommand(reader),
            _ => throw new ArgumentException($"Unknown command {commandId}")
        };
    }

    private ICommand BuildSetCommand(BinaryReader reader)
    {
        var dbName = GetString(reader);
        var expectedVersion = reader.ReadInt32();
        var data = KeyValue.From(reader);
        if (reader.BaseStream.Position != reader.BaseStream.Length) throw new ArgumentException("SetCommand: wrong data length");
        return new SetCommand(dbName, expectedVersion, data);
    }

    private static ICommand BuildGetCommand(BinaryReader reader)
    {
        var dbName = GetString(reader);
        if (reader.BaseStream.Position + 8 != reader.BaseStream.Length) throw new ArgumentException("GetCommand: wrong data length");
        var from = reader.ReadInt32();
        var to = reader.ReadInt32();
        return new GetCommand(dbName, from, to);
    }

    private static ICommand BuildGetFileVersionCommand(BinaryReader reader)
    {
        var dbName = GetString(reader);
        var key = reader.ReadInt32();
        if (reader.BaseStream.Position != reader.BaseStream.Length) throw new ArgumentException("GetFileVersionCommand: wrong data length");
        return new GetFileVersionCommand(dbName, key);
    }

    private static ICommand BuildGetLastCommand(BinaryReader reader)
    {
        var dbName = GetString(reader);
        if (reader.BaseStream.Position + 8 != reader.BaseStream.Length) throw new ArgumentException("GetLastCommand: wrong data length");
        var from = reader.ReadInt32();
        var to = reader.ReadInt32();
        return new GetLastCommand(dbName, from, to);
    }
    
    private static string GetString(BinaryReader reader)
    {
        var length = reader.ReadByte();
        return Encoding.UTF8.GetString(reader.ReadBytes(length));
    }
}