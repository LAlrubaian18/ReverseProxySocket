using System.Collections.Concurrent;

namespace ReverseProxy.Server.Networking;

// ReverseProxy.Server/Networking/ConnectionPoolManager.cs
public class ConnectionPoolManager
{
    private static readonly Lazy<ConnectionPoolManager> _instance = 
        new Lazy<ConnectionPoolManager>(() => new ConnectionPoolManager());
    public static ConnectionPoolManager Instance => _instance.Value;

    private readonly ConcurrentDictionary<int, WeakReference<ProxyConnection>> _connections;
    private readonly Timer _cleanupTimer;
    private readonly SemaphoreSlim _poolLock;
    private const int MAX_CONNECTIONS = 10000;
    private volatile bool _isDisposed;

    private ConnectionPoolManager()
    {
        _connections = new ConcurrentDictionary<int, WeakReference<ProxyConnection>>();
        _poolLock = new SemaphoreSlim(1, 1);
        _cleanupTimer = new Timer(CleanupDeadConnections, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public async Task<bool> TryAddConnection(int connectionId, ProxyConnection connection)
    {
        if (_connections.Count >= MAX_CONNECTIONS) return false;

        await _poolLock.WaitAsync();
        try
        {
            _connections[connectionId] = new WeakReference<ProxyConnection>(connection);
            return true;
        }
        finally
        {
            _poolLock.Release();
        }
    }

    public void RemoveConnection(int connectionId)
    {
        _connections.TryRemove(connectionId, out _);
    }

    private async void CleanupDeadConnections(object state)
    {
        await _poolLock.WaitAsync();
        try
        {
            var deadConnections = _connections
                .Where(kvp => !kvp.Value.TryGetTarget(out _))
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var id in deadConnections)
            {
                _connections.TryRemove(id, out _);
            }

            if (deadConnections.Any())
            {
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Cleaned up {deadConnections.Count} dead connections");
            }
        }
        finally
        {
            _poolLock.Release();
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _cleanupTimer?.Dispose();
        _poolLock?.Dispose();
        _connections.Clear();
    }
}