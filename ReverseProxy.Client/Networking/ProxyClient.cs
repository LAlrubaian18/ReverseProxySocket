using ReverseProxy.Common.Models;
using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using System.Buffers;
using System.Net;

namespace ReverseProxy.Client.Networking
{
    public class ProxyClient : IDisposable
{
    private readonly int _connectionId;
    private readonly string _target;
    private readonly int _port;
    private TcpClient _targetConnection;
    private NetworkStream _targetStream;
    private readonly Channel<byte[]> _sendChannel;
    private volatile bool _isConnected;
    private volatile bool _isDisposed;
    private readonly CancellationTokenSource _clientCts;
    private readonly SemaphoreSlim _connectionLock;
    private const int BUFFER_SIZE = 65536; // 64KB
    private const int TIMEOUT_MS = 10000;  // 10 seconds timeout

    public ProxyClient(int connectionId, string target, int port)
    {
        _connectionId = connectionId;
        _target = target;
        _port = port;
        _clientCts = new CancellationTokenSource();
        _connectionLock = new SemaphoreSlim(1, 1);

        // Unbounded channel with fast options
        var options = new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = true
        };
        _sendChannel = Channel.CreateUnbounded<byte[]>(options);
    }

    public async Task<ReverseProxyConnectResponse> ConnectAsync()
    {
        if (!await _connectionLock.WaitAsync(TIMEOUT_MS))
        {
            throw new TimeoutException("Connection lock timeout");
        }

        try
        {
            _targetConnection = new TcpClient
            {
                NoDelay = true,
                ReceiveBufferSize = BUFFER_SIZE,
                SendBufferSize = BUFFER_SIZE,
                LingerState = new LingerOption(false, 0)
            };

            using var timeoutCts = new CancellationTokenSource(TIMEOUT_MS);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, _clientCts.Token);

            try
            {
                await _targetConnection.ConnectAsync(_target, _port).WaitAsync(linkedCts.Token);
            }
            catch (Exception)
            {
                _targetConnection.Dispose();
                throw;
            }

            _targetStream = _targetConnection.GetStream();
            _isConnected = true;

            // Start processing send queue
            _ = Task.Run(() => ProcessSendQueueAsync(_clientCts.Token), _clientCts.Token);

            var localEndPoint = (IPEndPoint)_targetConnection.Client.LocalEndPoint;
            return new ReverseProxyConnectResponse
            {
                ConnectionId = _connectionId,
                IsConnected = true,
                LocalAddress = localEndPoint.Address.GetAddressBytes(),
                LocalPort = localEndPoint.Port,
                HostName = _target
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Connection error for {_target}:{_port}: {ex.Message}");
            Disconnect();
            return new ReverseProxyConnectResponse
            {
                ConnectionId = _connectionId,
                IsConnected = false
            };
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task StartReceivingAsync(Func<byte[], Task> onDataReceived, CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _clientCts.Token);

        try
        {
            while (!linkedCts.Token.IsCancellationRequested && _isConnected)
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(BUFFER_SIZE);
                try
                {
                    int bytesRead;
                    try
                    {
                        var readTask = _targetStream.ReadAsync(buffer, 0, buffer.Length, linkedCts.Token);
                        bytesRead = await readTask.WaitAsync(TimeSpan.FromMilliseconds(TIMEOUT_MS));
                        
                        if (bytesRead == 0)
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                            break;
                        }
                    }
                    catch (Exception) when (!_isConnected || linkedCts.Token.IsCancellationRequested)
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                        break;
                    }

                    byte[] data = new byte[bytesRead];
                    Buffer.BlockCopy(buffer, 0, data, 0, bytesRead);
                    ArrayPool<byte>.Shared.Return(buffer);

                    try
                    {
                        await onDataReceived(data).WaitAsync(TimeSpan.FromMilliseconds(TIMEOUT_MS));
                    }
                    catch (TimeoutException)
                    {
                        Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Data processing timeout for {_connectionId}");
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Read error: {ex.Message}");
                    break;
                }
            }
        }
        finally
        {
            Disconnect();
        }
    }

    private async Task ProcessSendQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _sendChannel.Reader.WaitToReadAsync(cancellationToken))
            {
                while (_sendChannel.Reader.TryRead(out byte[] data))
                {
                    if (!_isConnected) break;

                    try
                    {
                        await _targetStream.WriteAsync(data, 0, data.Length)
                            .WaitAsync(TimeSpan.FromMilliseconds(TIMEOUT_MS));
                        await _targetStream.FlushAsync()
                            .WaitAsync(TimeSpan.FromMilliseconds(TIMEOUT_MS));
                    }
                    catch (TimeoutException)
                    {
                        Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Send timeout for {_connectionId}");
                        continue;
                    }
                    catch (Exception)
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Send queue error: {ex.Message}");
        }
        finally
        {
            Disconnect();
        }
    }

    public async Task SendDataAsync(byte[] data)
    {
        if (!_isConnected || data == null) return;

        try
        {
            if (!_sendChannel.Writer.TryWrite(data))
            {
                throw new InvalidOperationException("Send queue full");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Queue write error: {ex.Message}");
            Disconnect();
        }
    }

    public void Disconnect()
    {
        if (!_isConnected) return;
        _isConnected = false;

        try
        {
            _clientCts.Cancel();
            _sendChannel.Writer.Complete();

            try { _targetStream?.Close(); } catch { }
            try { _targetConnection?.Close(); } catch { }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Disconnect error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            Disconnect();
            _clientCts.Dispose();
            _connectionLock.Dispose();
            _targetConnection?.Dispose();
        }
        catch { }
    }
}
    
    
}