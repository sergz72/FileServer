using System.Net;
using System.Net.Sockets;
using System.Text;
using FileServerLibrary;

namespace FileServer;

internal class Server(ServerConfiguration configuration)
{
    private const int MaximumMessageSize = 65507;
        
    private readonly UdpClient _client = new (configuration.Port);
    private readonly Logger _logger = configuration.LoggerCreator.CreateLogger("Server");
    
    private int _taskCount;
    
    private volatile bool _stop, _stopped;
    
    internal void Start()
    {
        _logger.Info($"Starting server on port {configuration.Port}");
        while (true)
        {
            IPEndPoint? ep = null;
            var data = _client.Receive(ref ep);
            if (_stop) break;
            Interlocked.Increment(ref _taskCount);
            Task.Run(() => Handle(data, ep));
        }
        while (_taskCount > 0)
            Thread.Sleep(100);
        _client.Close();
        _logger.Info("Server stopped");
        _stopped = true;
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

    private void Handle(byte[] data, IPEndPoint ep)
    {
        var logger = configuration.LoggerCreator.CreateLogger(ep.ToString());
        
        logger.Debug("New connection");

        byte[] decrypted;
        User user;
        try
        {
            (user, var idx) = configuration.UserProvider.GetUser(data);
            decrypted = configuration.CryptoPlugin.Decrypt(user.Key, data, idx);
        }
        catch (Exception e)
        {
            logger.Error(e.Message);
            Interlocked.Decrement(ref _taskCount);
            return;
        }
        
        byte[] response;
        try
        {
            var command = configuration.DecoderPlugin.Decode(logger, decrypted);
            var outData = command.Execute(user, configuration.StoragePlugin, logger);
            response = BuildOkResponse(outData);
        }
        catch (Exception e)
        {
            logger.Error(e.Message);
            response = BuildErrorResponse(e);
        }

        try
        {
            var encrypted = configuration.CryptoPlugin.Encrypt(user.Key, response);
            if (encrypted.Length > MaximumMessageSize) throw new Exception("Message is too long");
            _client.Send(encrypted, encrypted.Length, ep);
        }
        catch (Exception e)
        {
            logger.Error(e.Message);
        }
        Interlocked.Decrement(ref _taskCount);
    }

    internal void Stop()
    {
        _stop = true;
        new UdpClient().Send([0], 1, new IPEndPoint(IPAddress.Loopback, configuration.Port));
        while (!_stopped)
            Thread.Sleep(100);
        configuration.LoggerCreator.Dispose();
    }
}
