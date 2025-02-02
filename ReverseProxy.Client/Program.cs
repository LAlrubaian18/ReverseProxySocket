// ReverseProxy.Client/Program.cs
using ReverseProxy.Client.Networking;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ReverseProxy.Client
{
    class Program
    {
        static async Task Main(string[] args)
        {
            try
            {
                string serverAddress = GetServerAddress(args);
                int serverPort = GetServerPort(args);

                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Starting Reverse Proxy Client...");
                Console.WriteLine($"Connecting to server at {serverAddress}:{serverPort}");

                var client = new Networking.Client(serverAddress, serverPort);
                using var cts = new CancellationTokenSource();

                // Handle graceful shutdown
                Console.CancelKeyPress += (sender, e) =>
                {
                    e.Cancel = true;
                    Console.WriteLine("\nShutting down client...");
                    cts.Cancel();
                };

                try
                {
                    await client.ConnectAndRunAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Client shutdown completed.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Client error: {ex.Message}");
                    throw;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fatal error: {ex}");
                Environment.Exit(1);
            }
        }

        private static string GetServerAddress(string[] args)
        {
            if (args.Length > 0)
                return args[0];

            Console.Write("Enter server address (default: localhost): ");
            string input = Console.ReadLine();
            return string.IsNullOrWhiteSpace(input) ? "localhost" : input;
        }

        private static int GetServerPort(string[] args)
        {
            if (args.Length > 1 && int.TryParse(args[1], out int port))
                return port;

            Console.Write("Enter server port (default: 5010): ");
            string input = Console.ReadLine();
            return string.IsNullOrWhiteSpace(input) ? 5010 : int.Parse(input);
        }
    }
}