using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FileServerLibrary;

public class UserHandler: IHandlerPlugin
{
    private readonly UdpServer _server;
    private readonly ServerParameters _parameters;
    
    UserHandler(UdpServer server, ServerParameters parameters)
    {
        if (parameters.CryptoPlugin == null) throw new Exception("Crypto plugin is not set");
        if (parameters.UserProvider == null) throw new Exception("User provider plugin is not set");
        _server = server;
        _parameters = parameters;
    }
        
    public void Handle(byte[] data, IPEndPoint ep)
    {
        var logger = _parameters.LoggerCreator.CreateLogger(ep.ToString());
        
        logger.Debug("New connection");

        byte[] decrypted;
        User user;
        try
        {
            var idx = _parameters.UserProvider!.GetUser(data, out user);
            var length = user.ValidateData(data, idx);
            decrypted = _parameters.CryptoPlugin!.Decrypt(user.Key, data, idx, length);
        }
        catch (Exception e)
        {
            logger.Error(e.Message);
            _server.DecrementTaskCount();
            return;
        }
        
        byte[] response;
        try
        {
            var command = _parameters.DecoderPlugin.Decode(logger, decrypted);
            var outData = command.Execute(user, _parameters.StoragePlugin, logger);
            response = BuildOkResponse(outData);
        }
        catch (Exception e)
        {
            logger.Error(e.Message);
            response = BuildErrorResponse(e);
        }

        try
        {
            var encrypted = _parameters.CryptoPlugin.Encrypt(user.Key, response, User.HashSize);
            if (encrypted.Length > UdpServer.MaximumMessageSize) throw new Exception("Message is too long");
            user.AddHash(encrypted);
            _server.Send(encrypted, ep);
        }
        catch (Exception e)
        {
            logger.Error(e.Message);
        }
        _server.DecrementTaskCount();
    }

    private static byte[] BuildOkResponse(byte[] outData)
    {
        var result = new byte[outData.Length + 1];
        result[0] = 0;
        Array.Copy(outData, 0, result, 1, outData.Length);
        return result;
    }

    private static byte[] BuildErrorResponse(Exception e)
    {
        var bytes = Encoding.UTF8.GetBytes(e.Message);
        var result = new byte[bytes.Length + 1];
        result[0] = 1;
        Array.Copy(bytes, 0, result, 1, bytes.Length);
        return result;
    }
    
    public void Handle(TcpClient client)
    {
        throw new NotImplementedException();
    }
}