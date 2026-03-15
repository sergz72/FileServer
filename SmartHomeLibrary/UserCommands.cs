using FileServerLibrary;

namespace SmartHomeLibrary;

public record SensorDataQuery(
    string DbName,
    int MaxPoints,
    string DataType,
    int DateOrOffset,
    int OffsetUnit,
    int Period,
    int PeriodUnit
);

public class GetLastCommand(string dbName): ICommand
{
    public byte[] Execute(User? user, IStoragePlugin storage, Logger logger)
    {
        return [];
    }
}

public class GetCommand(SensorDataQuery query): ICommand
{
    public byte[] Execute(User? user, IStoragePlugin storage, Logger logger)
    {
        return [];
    }
}
