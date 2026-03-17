using FileServerLibrary;

namespace HomeAccountingLibrary;

public class GetFileVersionCommand(string dbName, int key): ICommand
{
    public byte[] Execute(User? user, IStoragePlugin storage, Logger logger)
    {
        ((HomeAccountingUser)user!).CheckReadAccess(dbName);
        var (dbInfo, fileVersion) = storage.GetFileVersion(dbName, key);
        var version = dbInfo.GetVersionAndUnlock();
        var result = new byte[8];
        BitConverter.GetBytes(version).CopyTo(result, 0);
        BitConverter.GetBytes(fileVersion).CopyTo(result, 4);
        return result;
    }
}

public class GetLastCommand(string dbName, int from, int to): ICommand
{
    public byte[] Execute(User? user, IStoragePlugin storage, Logger logger)
    {
        ((HomeAccountingUser)user!).CheckReadAccess(dbName);
        var (dbInfo, result) = storage.GetLast(dbName, from, to);
        var version = dbInfo.GetVersionAndUnlock();
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(version);
        result?.ToBinary(writer);
        return stream.ToArray();
    }
}

public class GetCommand(string dbName, int from, int to): ICommand
{
    public byte[] Execute(User? user, IStoragePlugin storage, Logger logger)
    {
        ((HomeAccountingUser)user!).CheckReadAccess(dbName);
        var (dbInfo, result) = storage.Get(dbName, from, to, false);
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(dbInfo.DbVersion);
        writer.Write(0); // int
        var length = 0;
        foreach (var kv in result)
        {
            kv.ToBinary(writer);
            length++;
        }
        dbInfo.ExitReadLock();
        stream.Seek(4, SeekOrigin.Begin);
        writer.Write(length);
        return stream.ToArray();
    }
}

public class SetCommand(string dbName, int expectedVersion, List<KeyValue> data) : ICommand
{
    public byte[] Execute(User? user, IStoragePlugin storage, Logger logger)
    {
        ((HomeAccountingUser)user!).CheckWriteAccess(dbName);
        storage.Set(dbName, expectedVersion, data).ExitWriteLock();
        return [];
    }
}