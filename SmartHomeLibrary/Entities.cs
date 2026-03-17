namespace SmartHomeLibrary;

public record Sensor(int Id, string DataType, string Location, string LocationType);

public record RawSensorData(int SensorId, int Date, int Time, Dictionary<string, int> Values);

public record SensorDataValues(int Time, Dictionary<string, int> Values);

public record SensorData(int Date, List<SensorDataValues> Values);

public record LastSensorData(int Date, SensorDataValues Values);

public record AggregatedSensorDataValues(int Min, int Avg, int Max);

public record AggregatedSensorData(int Date, Dictionary<string, AggregatedSensorDataValues> Values);

public enum TimeUnit {
    Day,
    Month,
    Year
}

public record DateOffset (int Offset, TimeUnit Unit);

public record SmartHomeQuery(
    short MaxPoints,
    string DataType,
    int? StartDate,
    DateOffset? StartDateOffset,
    DateOffset? Period);

public record SensorDataResponse(bool Aggregated, Dictionary<int, List<SensorData>>? Data,
    Dictionary<int, List<AggregatedSensorData>>? AggregatedData);