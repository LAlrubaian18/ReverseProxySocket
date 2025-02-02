// ReverseProxy.Server/Socks5/Socks5Handler.cs
using System.Net.Sockets;

namespace ReverseProxy.Server.Socks5
{
    public class Socks5Handler
    {
        private readonly NetworkStream _clientStream;

        public Socks5Handler(NetworkStream clientStream)
        {
            _clientStream = clientStream;
        }

        public async Task<(string host, int port)> HandleHandshakeAsync()
        {
            // Initial greeting
            await HandleInitialGreeting();

            // Handle connection request
            return await HandleConnectionRequest();
        }

        private async Task HandleInitialGreeting()
        {
            byte[] buffer = new byte[257];
            await _clientStream.ReadAsync(buffer, 0, 2);

            if (buffer[0] != 0x05) // SOCKS5 version
                throw new Exception("Unsupported SOCKS version");

            int numMethods = buffer[1];
            await _clientStream.ReadAsync(buffer, 0, numMethods);

            // Send authentication method response (no authentication required)
            await _clientStream.WriteAsync(new byte[] { 0x05, 0x00 }, 0, 2);
        }

        private async Task<(string host, int port)> HandleConnectionRequest()
        {
            byte[] buffer = new byte[4];
            await _clientStream.ReadAsync(buffer, 0, 4);

            if (buffer[0] != 0x05) // SOCKS5 version
                throw new Exception("Unsupported SOCKS version");

            if (buffer[1] != 0x01) // CONNECT command
                throw new Exception("Only CONNECT method is supported");

            if (buffer[2] != 0x00) // Reserved byte
                throw new Exception("Reserved byte must be 0x00");

            string host;
            switch (buffer[3]) // Address type
            {
                case 0x01: // IPv4
                    host = await HandleIPv4Address();
                    break;
                case 0x03: // Domain name
                    host = await HandleDomainName();
                    break;
                case 0x04: // IPv6
                    host = await HandleIPv6Address();
                    break;
                default:
                    throw new Exception("Unsupported address type");
            }

            // Read port (2 bytes, big endian)
            await _clientStream.ReadAsync(buffer, 0, 2);
            int port = (buffer[0] << 8) | buffer[1];

            // Send success response
            byte[] response = new byte[] {
                0x05, // SOCKS5
                0x00, // Success
                0x00, // Reserved
                0x01, // IPv4
                0x00, 0x00, 0x00, 0x00, // Address (0.0.0.0)
                0x00, 0x00 // Port (0)
            };
            await _clientStream.WriteAsync(response, 0, response.Length);

            return (host, port);
        }

        private async Task<string> HandleIPv4Address()
        {
            byte[] buffer = new byte[4];
            await _clientStream.ReadAsync(buffer, 0, 4);
            return string.Join(".", buffer);
        }

        private async Task<string> HandleDomainName()
        {
            byte[] buffer = new byte[256];
            await _clientStream.ReadAsync(buffer, 0, 1);
            int length = buffer[0];

            await _clientStream.ReadAsync(buffer, 0, length);
            return System.Text.Encoding.ASCII.GetString(buffer, 0, length);
        }

        private async Task<string> HandleIPv6Address()
        {
            byte[] buffer = new byte[16];
            await _clientStream.ReadAsync(buffer, 0, 16);
            return BitConverter.ToString(buffer).Replace("-", ":");
        }
    }
}