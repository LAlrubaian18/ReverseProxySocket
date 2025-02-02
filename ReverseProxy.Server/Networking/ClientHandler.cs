// ReverseProxy.Server/Networking/ClientHandler.cs
using ReverseProxy.Common.Models;
using ReverseProxy.Common.Utilities;
using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ReverseProxy.Server.Networking
{
    public class ClientHandler : IDisposable
    {
        public int Id { get; }
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly ConcurrentDictionary<int, ProxyConnection> _proxyConnections;
        private int _activeConnections;
        private bool _isConnected;
        private readonly CancellationTokenSource _cancellationTokenSource;

        public int ActiveConnections => _activeConnections;
        public bool IsConnected => _isConnected;

        public ClientHandler(TcpClient client)
        {
            Id = Guid.NewGuid().GetHashCode();
            _client = client;
            _stream = client.GetStream();
            _proxyConnections = new ConcurrentDictionary<int, ProxyConnection>();
            _activeConnections = 0;
            _isConnected = true;
            _cancellationTokenSource = new CancellationTokenSource();

            Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] New client connected. ID: {Id}");
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, 
                    _cancellationTokenSource.Token);

                while (!linkedCts.Token.IsCancellationRequested)
                {
                    var message = await MessageSerializer.ReceiveMessageAsync(_stream);
                    await HandleMessageAsync(message);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Client {Id} error: {ex.Message}");
            }
            finally
            {
                _isConnected = false;
                // Cleanup all proxy connections when client disconnects
                foreach (var proxyConnection in _proxyConnections.Values)
                {
                    await proxyConnection.DisconnectAsync();
                }
                _proxyConnections.Clear();
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Client {Id} disconnected");
            }
        }

        public void AddProxyConnection(int connectionId, ProxyConnection proxyConnection)
        {
            if (_proxyConnections.TryAdd(connectionId, proxyConnection))
            {
                IncrementConnections();
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Added proxy connection {connectionId} to client {Id}");
            }
        }

        public void RemoveProxyConnection(int connectionId)
        {
            if (_proxyConnections.TryRemove(connectionId, out _))
            {
                DecrementConnections();
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Removed proxy connection {connectionId} from client {Id}");
            }
        }

        public async Task SendMessageAsync<T>(T message)
        {
            try
            {
                await MessageSerializer.SendMessageAsync(_stream, message);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Error sending message to client {Id}: {ex.Message}");
                _isConnected = false;
                _cancellationTokenSource.Cancel();
            }
        }

        public void IncrementConnections()
        {
            Interlocked.Increment(ref _activeConnections);
            Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Client {Id} active connections: {_activeConnections}");
        }

        public void DecrementConnections()
        {
            Interlocked.Decrement(ref _activeConnections);
            Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Client {Id} active connections: {_activeConnections}");
        }

        private async Task HandleMessageAsync(object message)
        {
            switch (message)
            {
                case ReverseProxyConnectResponse response:
                    if (_proxyConnections.TryGetValue(response.ConnectionId, out var connection))
                    {
                        await connection.HandleConnectResponseAsync(response);
                    }
                    break;

                case ReverseProxyData data:
                    if (_proxyConnections.TryGetValue(data.ConnectionId, out var dataConnection))
                    {
                        await dataConnection.HandleDataAsync(data.Data);
                    }
                    break;

                default:
                    Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Unknown message type received from client {Id}");
                    break;
            }
        }

        public void Dispose()
        {
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource.Dispose();
            _client.Dispose();
        }
    }
}