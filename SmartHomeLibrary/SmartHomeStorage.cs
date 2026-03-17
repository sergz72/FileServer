using FileServerLibrary;

namespace SmartHomeLibrary;

public sealed class SmartHomeStorage: TimeSeriesStorage
{
    private readonly Dictionary<int, Sensor> _sensors;
    private readonly Dictionary<int, LastSensorData> _lastSensorData;
    
    public SmartHomeStorage(Logger logger, ServerConfigurationParameters parameters, IKeyValueStorage? storageInterface) :
        base(logger, parameters, storageInterface)
    {
        _sensors = LoadSensors();
        _lastSensorData = BuildLastSensorData();
    }
    
    public SmartHomeStorage(Logger logger, ServerConfigurationParameters parameters) : base(logger, parameters)
    {
        _sensors = LoadSensors();
        _lastSensorData = BuildLastSensorData();
    }

    public void ProcessSensorData(List<RawSensorData> sensorData)
    {
        throw new NotImplementedException();
    }

    public SensorDataResponse GetSensorData(SmartHomeQuery query)
    {
        throw new NotImplementedException();
    }
    
    public Dictionary<int, LastSensorData> GetLastSensorData()
    {
        return _lastSensorData;
    }
    
    public Dictionary<int, Sensor> GetSensors()
    {
        return _sensors;
    }
    
    private Dictionary<int, Sensor> LoadSensors()
    {
        throw new NotImplementedException();
    }
    
    private Dictionary<int, LastSensorData> BuildLastSensorData()
    {
        throw new NotImplementedException();
    }
}