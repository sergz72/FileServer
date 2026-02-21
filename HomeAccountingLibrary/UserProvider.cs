using FileServerLibrary;

namespace HomeAccountingLibrary;

internal enum DatabaseAccessMode
{
    ReadOnly,
    ReadWrite,
    WriteOnly
}

internal record HomeAccountingUserRecord(string Name, int Id, string KeyFileName, Dictionary<string, string> Databases);

internal class HomeAccountingUser(HomeAccountingUserRecord r): User(r.Name, r.Id, r.KeyFileName)
{
    private static DatabaseAccessMode GetAccessMode(string mode)
    {
        return mode switch
        {
            "r" => DatabaseAccessMode.ReadOnly,
            "rw" => DatabaseAccessMode.ReadWrite,
            "w" => DatabaseAccessMode.WriteOnly,
            _ => throw new Exception($"Unknown database access mode {mode}")
        };
    }

    private readonly Dictionary<string, DatabaseAccessMode> _databases = r.Databases
        .ToDictionary(x => x.Key, x => GetAccessMode(x.Value));

    internal void CheckReadAccess(string dbName)
    {
        if (!_databases.TryGetValue(dbName, out var mode) || mode == DatabaseAccessMode.WriteOnly)
            throw new Exception($"User {Name} has no read access to database {dbName}");
    }
    
    internal void CheckWriteAccess(string dbName)
    {
        if (!_databases.TryGetValue(dbName, out var mode) || mode == DatabaseAccessMode.ReadOnly)
            throw new Exception($"User {Name} has no write access to database {dbName}");
    }
}

public class HomeAccountingUserProvider: IUserProviderPlugin
{
    private readonly Dictionary<int, HomeAccountingUser> _users;

    public HomeAccountingUserProvider(ServerConfigurationParameters parameters)
    {
        _users = parameters.GetParameter<List<HomeAccountingUserRecord>>("Users")
            .ToDictionary(x => x.Id, x => new HomeAccountingUser(x));
    }
    
    public (User, int) GetUser(byte[] data)
    {
        var userId = BitConverter.ToInt32(data);
        if (!_users.TryGetValue(userId, out var user))
            throw new Exception($"User {userId} not found");
        return (user, 4);
    }
}