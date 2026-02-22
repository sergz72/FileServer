using FileServerLibrary;
using HomeAccountingLibrary.entities;

namespace HomeAccountingLibrary.commands;

// changes contain all account ids, that are active for given date
internal class AddOperationCommand(string dbName, int key, FinanceOperation operation, Dictionary<int, long> changes) : ICommand
{
    public byte[] Execute(User user, IStoragePlugin storage, Logger logger)
    {
        var data = storage.GetLast(dbName, key, out var factKey);
        var record = data == null ? new FinanceRecord([operation], changes) : FinanceRecord.FromBinary(data);
        if (data != null)
        {
            if (factKey != key)
                record = record.BuildNextRecord(operation, changes);
            else
                record.AddOperation(operation, changes);
        }
        var toSave = new Dictionary<int, byte[]> { {key, record.ToBinary()} };
        foreach (var (k, d) in storage.Get(dbName, key + 1, HomeAccountingDecoder.MaxDate))
        {
            record = FinanceRecord.FromBinary(d);
            record.ApplyChanges(changes);
            toSave[k] = record.ToBinary();
        }
        storage.Set(dbName, toSave);
        return [];
    }
}