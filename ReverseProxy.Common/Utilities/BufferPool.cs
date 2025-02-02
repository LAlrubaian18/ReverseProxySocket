// ReverseProxy.Common/Utilities/BufferPool.cs
using System.Buffers;

namespace ReverseProxy.Common.Utilities
{
    public static class BufferPool
    {
        private static readonly ArrayPool<byte> _pool = ArrayPool<byte>.Shared;
        public const int BufferSize = 81920; // 80KB buffer

        public static byte[] Rent() => _pool.Rent(BufferSize);
        
        public static void Return(byte[] buffer)
        {
            if (buffer != null)
                _pool.Return(buffer);
        }
    }
}