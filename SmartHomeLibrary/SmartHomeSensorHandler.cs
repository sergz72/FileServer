using System.Net;
using System.Net.Sockets;
using FileServerLibrary;

namespace SmartHomeLibrary;

public class SmartHomeSensorHandler: IHandlerPlugin
{
    public void Handle(byte[] data, IPEndPoint ep)
    {
        throw new NotImplementedException();
    }

    public void Handle(TcpClient client)
    {
        throw new NotImplementedException();
    }
}