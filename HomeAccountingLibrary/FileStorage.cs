using FileServerLibrary;

namespace HomeAccountingLibrary;

public class HomeAccountingFileStorage: IStoragePlugin
{
    public HomeAccountingFileStorage(ServerConfigurationParameters parameters)
    {
    }
    
    public byte[] Read(long key, string propertyName)
    {
        throw new NotImplementedException();
    }

    public void Write(long key, string propertyName, byte[] data)
    {
        throw new NotImplementedException();
    }
}