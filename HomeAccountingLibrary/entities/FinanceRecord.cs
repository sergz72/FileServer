using System.Text;

namespace HomeAccountingLibrary.entities;

internal enum FinOpPropertyCode {
    Seca,
    Netw,
    Dist,
    Type,
    Ppto,
    Amou
}

internal class FinanceRecord
{
    internal Dictionary<int, long> Totals;
    internal List<FinanceOperation> Operations { get; }

    internal FinanceRecord(List<FinanceOperation> operations, Dictionary<int, long> totals)
    {
        Totals = totals;
        Operations = operations;
    }
    
    internal FinanceRecord(): this([], new Dictionary<int, long>())
    {
    }

    internal static Dictionary<int, long> BuildTotalsFromBinary(BinaryReader reader)
    {
        var length = reader.ReadInt16();
        var totals = new Dictionary<int, long>();
        while (length-- > 0)
        {
            var accountId = (int)reader.ReadInt16();
            totals[accountId] = reader.ReadInt64();
        }
        return totals;
    }

    internal static FinanceRecord FromBinary(byte[] data)
    {
        using var reader = new BinaryReader(new MemoryStream(data));
        
        var totals = BuildTotalsFromBinary(reader);

        var length = reader.ReadInt16();
        var operations = new List<FinanceOperation>();
        while (length-- > 0)
            operations.Add(FinanceOperation.FromBinary(reader));
        if (reader.BaseStream.Position != reader.BaseStream.Length)
            throw new Exception("incorrect data");
        return new FinanceRecord(operations, totals);
    }

    internal void AddOperation(FinanceOperation operation, Dictionary<int, long> changes)
    {
        throw new NotImplementedException();
    }

    public FinanceRecord BuildNextRecord(FinanceOperation operation, Dictionary<int, long> changes)
    {
        throw new NotImplementedException();
    }

    public void ApplyChanges(Dictionary<int, long> changes)
    {
        throw new NotImplementedException();
    }

    public byte[] ToBinary()
    {
        throw new NotImplementedException();
    }
}

internal record FinanceOperation(
    long? Amount,
    long Summa,
    int SubcategoryId,
    int AccountId,
    List<FinOpProperty> Properties)
{
    internal static FinanceOperation FromBinary(BinaryReader reader)
    {
        var amount = reader.ReadInt64();
        var summa = reader.ReadInt64();
        var subcategoryId = (int)reader.ReadInt16();
        var accountId = (int)reader.ReadInt16();
        var propertiesCount = (int)reader.ReadByte();
        var properties = new List<FinOpProperty>();
        while (propertiesCount-- > 0)
            properties.Add(FinOpProperty.FromBinary(reader));
        return new FinanceOperation(amount == 0 ? null : amount, summa, subcategoryId, accountId, properties);
    }
}

internal record FinOpProperty(long? NumericValue, string? StringValue, int? DateValue, FinOpPropertyCode Code)
{
    internal static FinOpProperty FromBinary(BinaryReader reader)
    {
        var code = (FinOpPropertyCode)reader.ReadByte();
        string? stringValue = null;
        long? numericValue = null;
        switch (code)
        {
            case FinOpPropertyCode.Seca:
            case FinOpPropertyCode.Dist:
            case FinOpPropertyCode.Ppto:
            case FinOpPropertyCode.Amou:
                numericValue = reader.ReadInt64();
                break;
            default:
                var length = (int)reader.ReadByte();
                stringValue = Encoding.ASCII.GetString(reader.ReadBytes(length));
                break;
        }

        return new FinOpProperty(numericValue, stringValue, null, code);
    }
}
