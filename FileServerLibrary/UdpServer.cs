using System.Net;
using System.Net.Sockets;

namespace FileServerLibrary;

public class UdpServer: IServerPlugin
{
    public const int MaximumMessageSize = 65507;
        
    private readonly UdpClient _client;
    private readonly Logger _logger;
    private readonly ServerParameters _parameters;

    private readonly IHandlerPlugin _handler;
    
    private int _taskCount;
    
    private volatile bool _stop, _stopped;

    public UdpServer(ServerParameters parameters)
    {
        _parameters = parameters;
        _client = new UdpClient(parameters.Port);
        _logger = parameters.LoggerCreator.CreateLogger(parameters.Name);
        _handler = parameters.Parameters.CreateInstance<IHandlerPlugin>(parameters.Handler, this, parameters);
    }
    
    public void Start()
    {
        _logger.Info($"Starting {_parameters.Name} on port {_parameters.Port}");
        while (true)
        {
            IPEndPoint? ep = null;
            var data = _client.Receive(ref ep);
            if (_stop) break;
            Interlocked.Increment(ref _taskCount);
            Task.Run(() => _handler.Handle(data, ep));
        }
        while (_taskCount > 0)
            Thread.Sleep(100);
        _client.Close();
        _logger.Info($"{_parameters.Name} stopped");
        _stopped = true;
    }

    public void Stop()
    {
        _stop = true;
        new UdpClient().Send([0], 1, new IPEndPoint(IPAddress.Loopback, _parameters.Port));
        while (!_stopped)
            Thread.Sleep(100);
    }
    
    public void DecrementTaskCount() => Interlocked.Decrement(ref _taskCount);
    public void Send(byte[] data, IPEndPoint ep) => _client.Send(data, data.Length, ep);
}