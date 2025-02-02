// ReverseProxy.Server/Configuration/ServerConfig.cs
public class ServerConfig
{
    public static class Limits
    {
        public const int MaxConcurrentConnections = 100000;        // Tổng số kết nối đồng thời tối đa
        public const int MaxConnectionsPerClient = 200000;          // Số kết nối tối đa cho mỗi client
        public const int BufferSize = 32768;                     // 32KB buffer size
        public const int ConnectionTimeout = 30000;              // 30 giây timeout cho mỗi kết nối
        public const int MaxPendingConnections = 10000;            // Số lượng kết nối đang chờ tối đa
    }
}