using ReverseProxy.Common.Models;
using ReverseProxy.Server.Networking;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ReverseProxy.Server
{
    public class Server : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly TcpListener _proxyListener;
        private readonly ConcurrentDictionary<int, ClientHandler> _clients;
        private int _nextConnectionId;
        private int _nextClientId;
        private volatile bool _isRunning;
        private volatile bool _isDisposed;
        private readonly CancellationTokenSource _serverCts;
        private readonly LoadBalancer.Strategy _loadBalancingStrategy;

        public Server(LoadBalancer.Strategy loadBalancingStrategy = LoadBalancer.Strategy.LeastConnections)
        {
            _listener = new TcpListener(IPAddress.Any, 5010);
            _proxyListener = new TcpListener(IPAddress.Any, 8086);

            // Configure listeners
            _listener.Server.NoDelay = true;
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            
            _proxyListener.Server.NoDelay = true;
            _proxyListener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            
            _clients = new ConcurrentDictionary<int, ClientHandler>();
            _nextConnectionId = 1;
            _nextClientId = 1;
            _serverCts = new CancellationTokenSource();
            _loadBalancingStrategy = loadBalancingStrategy;
            _isRunning = false;
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _serverCts.Token);

            try
            {
                _listener.Start();
                _proxyListener.Start();
                _isRunning = true;

                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Server started.");
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Listening for clients on port {((IPEndPoint)_listener.LocalEndpoint).Port}");
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Listening for SOCKS5 connections on port 8086");
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Load balancing strategy: {_loadBalancingStrategy}");

                var clientTask = AcceptClientsAsync(linkedCts.Token);
                var proxyTask = AcceptProxyConnectionsAsync(linkedCts.Token);

                await Task.WhenAll(clientTask, proxyTask);
            }
            finally
            {
                _isRunning = false;
                _listener.Stop();
                _proxyListener.Stop();
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Server stopped");
            }
        }

        private async Task AcceptClientsAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _isRunning)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    client.NoDelay = true;

                    _ = HandleClientAsync(client, cancellationToken);
                }
                catch (Exception) when (!_isRunning)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Error accepting client: {ex.Message}");
                }
            }
        }

        private async Task AcceptProxyConnectionsAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _isRunning)
            {
                try
                {
                    var proxyClient = await _proxyListener.AcceptTcpClientAsync();
                    proxyClient.NoDelay = true;

                    _ = HandleProxyConnectionAsync(proxyClient, cancellationToken);
                }
                catch (Exception) when (!_isRunning)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Error accepting proxy connection: {ex.Message}");
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            var clientId = Interlocked.Increment(ref _nextClientId);
            var clientHandler = new ClientHandler(client);

            try
            {
                if (_clients.TryAdd(clientId, clientHandler))
                {
                    Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] New client {clientId} connected. Total clients: {_clients.Count}");
                    await clientHandler.StartAsync(cancellationToken);
                }
                else
                {
                    Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Failed to add client {clientId}");
                    client.Close();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Error handling client {clientId}: {ex.Message}");
            }
            finally
            {
                if (_clients.TryRemove(clientId, out _))
                {
                    Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Client {clientId} disconnected. Total clients: {_clients.Count}");
                }
                clientHandler.Dispose();
            }
        }

        private async Task HandleProxyConnectionAsync(TcpClient proxyClient, CancellationToken cancellationToken)
        {
            var connectionId = Interlocked.Increment(ref _nextConnectionId);

            try
            {
                using var proxyConnection = new ProxyConnection(connectionId, proxyClient, _clients, _loadBalancingStrategy);
                await proxyConnection.StartAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Error handling proxy connection {connectionId}: {ex.Message}");
                proxyClient.Close();
            }
        }

        public void Stop()
        {
            if (!_isRunning) return;

            try
            {
                _serverCts.Cancel();

                foreach (var client in _clients.Values)
                {
                    try
                    {
                        client.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Error disposing client: {ex.Message}");
                    }
                }

                _clients.Clear();
                _listener.Stop();
                _proxyListener.Stop();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Error stopping server: {ex.Message}");
            }
            finally
            {
                _isRunning = false;
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            Stop();
            _serverCts.Dispose();
        }
    }
}