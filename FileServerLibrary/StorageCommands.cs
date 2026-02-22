namespace FileServerLibrary;

public class GetFileVersionCommand(string dbName, int key): ICommand
{
    public byte[] Execute(User user, IStoragePlugin storage, Logger logger)
    {
        var version = storage.GetFileVersion(dbName, key);
        return BitConverter.GetBytes(version);
    }
}

public class GetLastCommand(string dbName, int key): ICommand
{
    public byte[] Execute(User user, IStoragePlugin storage, Logger logger)
    {
        return storage.GetLast(dbName, key, out var factKey) ?? [];
    }
}