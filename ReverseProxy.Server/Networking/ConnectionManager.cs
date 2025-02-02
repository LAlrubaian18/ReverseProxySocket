// ReverseProxy.Server/Networking/ConnectionManager.cs
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ReverseProxy.Server.Networking
{
    public class ConnectionManager
    {
        private static readonly Lazy<ConnectionManager> _instance = new Lazy<ConnectionManager>(() => new ConnectionManager());
        public static ConnectionManager Instance => _instance.Value;

        private readonly SemaphoreSlim _connectionSemaphore;
        private volatile int _currentConnections;
        
        public const int MAX_CONCURRENT_CONNECTIONS = 10000; // Giới hạn kết nối đồng thời
        public int CurrentConnections => _currentConnections;

        private ConnectionManager()
        {
            _connectionSemaphore = new SemaphoreSlim(MAX_CONCURRENT_CONNECTIONS, MAX_CONCURRENT_CONNECTIONS);
            _currentConnections = 0;
        }

        public async Task<bool> TryAcquireConnectionAsync(TimeSpan timeout)
        {
            if (await _connectionSemaphore.WaitAsync(timeout))
            {
                Interlocked.Increment(ref _currentConnections);
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] New connection acquired. Current connections: {_currentConnections}");
                return true;
            }
            return false;
        }

        public void ReleaseConnection()
        {
            Interlocked.Decrement(ref _currentConnections);
            _connectionSemaphore.Release();
            Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Connection released. Current connections: {_currentConnections}");
        }
    }
}