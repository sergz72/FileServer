using FileServerLibrary;

namespace HomeAccountingLibrary;

public class HomeAccountingDecoder: IDecoderPlugin
{
    public HomeAccountingDecoder(ServerConfigurationParameters parameters)
    {
    }
    
    public ICommand Decode(Logger logger, byte[] data)
    {
        throw new NotImplementedException();
    }
}