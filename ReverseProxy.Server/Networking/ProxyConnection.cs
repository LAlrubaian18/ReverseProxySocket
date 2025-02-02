using ReverseProxy.Common.Models;
using ReverseProxy.Server.Socks5;
using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Buffers;

namespace ReverseProxy.Server.Networking
{
    public class ProxyConnection : IDisposable
    {
        private readonly int _connectionId;
        private readonly TcpClient _proxyClient;
        private readonly NetworkStream _proxyStream;
        private readonly ConcurrentDictionary<int, ClientHandler> _clients;
        private readonly LoadBalancer.Strategy _loadBalancingStrategy;
        private readonly SemaphoreSlim _sendLock;
        private ClientHandler _selectedClient;
        private volatile bool _isConnected;
        private volatile bool _isDisposed;
        private const int BUFFER_SIZE = 32768; // 32KB

        public ProxyConnection(
            int connectionId,
            TcpClient proxyClient,
            ConcurrentDictionary<int, ClientHandler> clients,
            LoadBalancer.Strategy strategy = LoadBalancer.Strategy.LeastConnections)
        {
            _connectionId = connectionId;
            _proxyClient = proxyClient;
            _clients = clients;
            _loadBalancingStrategy = strategy;
            _sendLock = new SemaphoreSlim(1, 1);
            
            // Configure TCP client
            _proxyClient.NoDelay = true;
            _proxyClient.ReceiveBufferSize = BUFFER_SIZE;
            _proxyClient.SendBufferSize = BUFFER_SIZE;
            _proxyClient.LingerState = new LingerOption(true, 0);
            
            _proxyStream = proxyClient.GetStream();
            _isConnected = true;

            Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] New proxy connection {_connectionId}");
        }

        private ClientHandler SelectClient()
        {
            var selectedClient = LoadBalancer.SelectClient(_clients, _loadBalancingStrategy);
            
            if (selectedClient != null)
            {
                selectedClient.IncrementConnections();
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Selected client {selectedClient.Id} for proxy connection {_connectionId}");
            }
            else
            {
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] No available clients for proxy connection {_connectionId}");
            }

            return selectedClient;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                _selectedClient = SelectClient();
                if (_selectedClient == null)
                {
                    Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] No available clients for proxy connection {_connectionId}");
                    return;
                }

                _selectedClient.AddProxyConnection(_connectionId, this);

                var socks5Handler = new Socks5Handler(_proxyStream);
                var (target, port) = await socks5Handler.HandleHandshakeAsync();

                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Proxy {_connectionId} connecting to {target}:{port}");

                var connectRequest = new ReverseProxyConnect
                {
                    ConnectionId = _connectionId,
                    Target = target,
                    Port = port
                };

                await _selectedClient.SendMessageAsync(connectRequest);

                // Start processing data
                await ProcessDataAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Proxy connection {_connectionId} error: {ex.Message}");
            }
            finally
            {
                await DisconnectAsync();
            }
        }

        private async Task ProcessDataAsync(CancellationToken cancellationToken)
        {
            using var memoryOwner = MemoryPool<byte>.Shared.Rent(BUFFER_SIZE);
            Memory<byte> buffer = memoryOwner.Memory;

            while (!cancellationToken.IsCancellationRequested && _isConnected)
            {
                int bytesRead;
                try
                {
                    bytesRead = await _proxyStream.ReadAsync(buffer, cancellationToken);
                    if (bytesRead == 0) break;
                }
                catch (Exception) when (!_isConnected || cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Error reading from proxy {_connectionId}: {ex.Message}");
                    break;
                }

                try
                {
                    var data = new ReverseProxyData
                    {
                        ConnectionId = _connectionId,
                        Data = buffer.Slice(0, bytesRead).ToArray()
                    };

                    await _selectedClient.SendMessageAsync(data);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Error sending data from proxy {_connectionId}: {ex.Message}");
                    break;
                }
            }
        }

        public async Task HandleConnectResponseAsync(ReverseProxyConnectResponse response)
        {
            if (!response.IsConnected)
            {
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Proxy {_connectionId} connection rejected");
                await DisconnectAsync();
            }
        }

        public async Task HandleDataAsync(byte[] data)
        {
            if (!_isConnected || data == null) return;

            try
            {
                await _sendLock.WaitAsync();
                try
                {
                    await _proxyStream.WriteAsync(data, 0, data.Length);
                    await _proxyStream.FlushAsync();
                }
                finally
                {
                    _sendLock.Release();
                }
            }
            catch (Exception) when (!_isConnected)
            {
                // Ignore exceptions when already disconnected
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Error writing to proxy {_connectionId}: {ex.Message}");
                await DisconnectAsync();
            }
        }

        public async Task DisconnectAsync()
        {
            if (!_isConnected) return;
            _isConnected = false;

            try
            {
                if (_selectedClient != null)
                {
                    await _selectedClient.SendMessageAsync(new ReverseProxyDisconnect { ConnectionId = _connectionId });
                    _selectedClient.RemoveProxyConnection(_connectionId);
                    _selectedClient.DecrementConnections(); // Giảm số lượng kết nối của client
                    Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Proxy {_connectionId} disconnected from client {_selectedClient.Id}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Error during proxy {_connectionId} disconnect: {ex.Message}");
            }
            finally
            {
                try
                {
                    _proxyClient.Close();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Error closing proxy client {_connectionId}: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                _sendLock.Dispose();
                _proxyClient.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Error disposing proxy connection {_connectionId}: {ex.Message}");
            }
        }
    }
}