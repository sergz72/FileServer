using System.Text;
using FileServerLibrary;
using HomeAccountingLibrary.commands;
using HomeAccountingLibrary.entities;

namespace HomeAccountingLibrary;

public class HomeAccountingDecoder: IDecoderPlugin
{
    internal const int MaxDate = 99999999;
    
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
            0 => BuildGetDictsVersionCommand(reader),
            1 => BuildGetLastCommand(reader),
            2 => BuildAddOperationCommand(reader),
            _ => throw new ArgumentException($"Unknown command {commandId}")
        };
    }

    private static ICommand BuildGetDictsVersionCommand(BinaryReader reader)
    {
        var dbName = GetString(reader);
        if (reader.BaseStream.Position != reader.BaseStream.Length) throw new ArgumentException("GetDictsVersionCommand: wrong data length");
        return new GetFileVersionCommand(dbName, 0);
    }

    private ICommand BuildGetLastCommand(BinaryReader reader)
    {
        var dbName = GetString(reader);
        if (reader.BaseStream.Position + 4 != reader.BaseStream.Length) throw new ArgumentException("GetLastCommand: wrong data length");
        var key = reader.ReadInt32();
        return new GetLastCommand(dbName, key);
    }

    private ICommand BuildAddOperationCommand(BinaryReader reader)
    {
        var dbName = GetString(reader);
        var key = reader.ReadInt32();
        var operation = FinanceOperation.FromBinary(reader);
        var changes = FinanceRecord.BuildTotalsFromBinary(reader);
        if (reader.BaseStream.Position != reader.BaseStream.Length) throw new ArgumentException("AddOperationCommand: wrong data length");
        return new AddOperationCommand(dbName, key, operation, changes);
    }

    private static string GetString(BinaryReader reader)
    {
        var length = reader.ReadByte();
        return Encoding.UTF8.GetString(reader.ReadBytes(length));
    }
}