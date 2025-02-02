// ReverseProxy.Client/Networking/Client.cs
using ReverseProxy.Common.Models;
using ReverseProxy.Common.Utilities;
using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ReverseProxy.Client.Networking
{
    public class Client : IDisposable
    {
        private readonly TcpClient _tcpClient;
        private readonly string _serverAddress;
        private readonly int _serverPort;
        private readonly ConcurrentDictionary<int, ProxyClient> _proxyConnections;
        private NetworkStream _stream;
        private readonly CancellationTokenSource _internalCts;
        private bool _isConnected;
        private readonly SemaphoreSlim _connectionLock;
        private const int MAX_RECONNECT_ATTEMPTS = 5;
        private const int RECONNECT_DELAY_MS = 5000;

        public Client(string serverAddress, int serverPort)
        {
            _tcpClient = new TcpClient();
            _serverAddress = serverAddress;
            _serverPort = serverPort;
            _proxyConnections = new ConcurrentDictionary<int, ProxyClient>();
            _internalCts = new CancellationTokenSource();
            _connectionLock = new SemaphoreSlim(1, 1);
            _isConnected = false;
        }

        public async Task ConnectAndRunAsync(CancellationToken cancellationToken = default)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _internalCts.Token);
            
            try
            {
                await ConnectWithRetryAsync(linkedCts.Token);

                while (!linkedCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        if (!_isConnected)
                        {
                            await ConnectWithRetryAsync(linkedCts.Token);
                            continue;
                        }

                        var message = await MessageSerializer.ReceiveMessageAsync(_stream);
                        if (message == null) continue;

                        await HandleMessageAsync(message, linkedCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Error in message processing: {ex.Message}");
                        await DisconnectAsync();
                        
                        // Add delay before reconnect attempt
                        await Task.Delay(1000, linkedCts.Token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Client shutdown requested");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Fatal error: {ex.Message}");
            }
            finally
            {
                await CleanupAsync();
            }
        }

        private async Task ConnectWithRetryAsync(CancellationToken cancellationToken)
        {
            await _connectionLock.WaitAsync(cancellationToken);
            try
            {
                if (_isConnected) return;

                for (int attempt = 1; attempt <= MAX_RECONNECT_ATTEMPTS; attempt++)
                {
                    try
                    {
                        if (_tcpClient?.Connected == true)
                        {
                            _tcpClient.Close();
                        }

                        _tcpClient?.Dispose();
                        var newClient = new TcpClient();
                        
                        Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Connecting to server at {_serverAddress}:{_serverPort} (Attempt {attempt}/{MAX_RECONNECT_ATTEMPTS})");
                        
                        await newClient.ConnectAsync(_serverAddress, _serverPort);
                        _stream = newClient.GetStream();
                        _isConnected = true;
                        
                        Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Successfully connected to server");
                        return;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Connection attempt {attempt} failed: {ex.Message}");
                        
                        if (attempt < MAX_RECONNECT_ATTEMPTS)
                        {
                            await Task.Delay(RECONNECT_DELAY_MS, cancellationToken);
                        }
                    }
                }

                throw new Exception("Failed to connect to server after maximum retry attempts");
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        private async Task DisconnectAsync()
        {
            await _connectionLock.WaitAsync();
            try
            {
                _isConnected = false;
                _stream?.Dispose();
                _tcpClient?.Close();

                // Cleanup all proxy connections
                foreach (var proxyClient in _proxyConnections.Values)
                {
                    proxyClient.Disconnect();
                }
                _proxyConnections.Clear();
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        private async Task HandleMessageAsync(object message, CancellationToken cancellationToken)
        {
            if (message == null) return;

            try
            {
                switch (message)
                {
                    case ReverseProxyConnect connectRequest:
                        await HandleConnectRequestAsync(connectRequest, cancellationToken);
                        break;

                    case ReverseProxyData dataMessage:
                        await HandleDataMessageAsync(dataMessage, cancellationToken);
                        break;

                    case ReverseProxyDisconnect disconnectMessage:
                        HandleDisconnectMessage(disconnectMessage);
                        break;

                    default:
                        Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Received unknown message type");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Error handling message: {ex.Message}");
            }
        }

        private async Task HandleConnectRequestAsync(ReverseProxyConnect request, CancellationToken cancellationToken)
        {
            var proxyClient = new ProxyClient(request.ConnectionId, request.Target, request.Port);
            
            if (_proxyConnections.TryAdd(request.ConnectionId, proxyClient))
            {
                try
                {
                    var response = await proxyClient.ConnectAsync();
                    await MessageSerializer.SendMessageAsync(_stream, response);

                    if (response.IsConnected)
                    {
                        _ = proxyClient.StartReceivingAsync(
                            async (data) =>
                            {
                                if (_isConnected)
                                {
                                    var proxyData = new ReverseProxyData
                                    {
                                        ConnectionId = request.ConnectionId,
                                        Data = data
                                    };
                                    await MessageSerializer.SendMessageAsync(_stream, proxyData);
                                }
                            },
                            cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Error in proxy connection: {ex.Message}");
                    _proxyConnections.TryRemove(request.ConnectionId, out _);
                    proxyClient.Disconnect();
                }
            }
        }

        private async Task HandleDataMessageAsync(ReverseProxyData message, CancellationToken cancellationToken)
        {
            if (_proxyConnections.TryGetValue(message.ConnectionId, out var proxyClient))
            {
                try
                {
                    await proxyClient.SendDataAsync(message.Data);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Error sending data to proxy: {ex.Message}");
                    _proxyConnections.TryRemove(message.ConnectionId, out _);
                    proxyClient.Disconnect();
                }
            }
        }

        private void HandleDisconnectMessage(ReverseProxyDisconnect message)
        {
            if (_proxyConnections.TryRemove(message.ConnectionId, out var proxyClient))
            {
                proxyClient.Disconnect();
            }
        }

        private async Task CleanupAsync()
        {
            await DisconnectAsync();
            _connectionLock.Dispose();
            _internalCts.Dispose();
        }

        public void Dispose()
        {
            _internalCts.Cancel();
            _internalCts.Dispose();
            _connectionLock.Dispose();
            _tcpClient?.Dispose();
            _stream?.Dispose();

            foreach (var proxyClient in _proxyConnections.Values)
            {
                proxyClient.Disconnect();
            }
            _proxyConnections.Clear();
        }
    }
}