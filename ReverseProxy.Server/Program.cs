// ReverseProxy.Server/Program.cs
using ReverseProxy.Server.Networking;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ReverseProxy.Server
{
    class Program
    {
        static async Task Main(string[] args)
        {
            try
            {
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Starting Reverse Proxy Server...");
                Console.WriteLine("Listening for clients on port 5010");
                Console.WriteLine("Listening for SOCKS5 connections on port 8086");
                
                var server = new Server();
                using var cts = new CancellationTokenSource();

                // Handle graceful shutdown
                Console.CancelKeyPress += (sender, e) =>
                {
                    e.Cancel = true;
                    Console.WriteLine("\nShutting down server...");
                    cts.Cancel();
                };

                try
                {
                    await server.StartAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Server shutdown completed.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Server error: {ex.Message}");
                    throw;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fatal error: {ex}");
                Environment.Exit(1);
            }
        }
    }
}