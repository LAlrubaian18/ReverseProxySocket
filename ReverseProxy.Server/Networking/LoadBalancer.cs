// ReverseProxy.Server/Networking/LoadBalancer.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace ReverseProxy.Server.Networking
{
    public class LoadBalancer
    {
        private static int _roundRobinIndex = -1;
        private static readonly object _lock = new object();

        public enum Strategy
        {
            Random,
            RoundRobin,
            LeastConnections
        }

        public static ClientHandler SelectClient(
            ConcurrentDictionary<int, ClientHandler> clients,
            Strategy strategy = Strategy.LeastConnections)
        {
            var availableClients = clients.Values.Where(c => c.IsConnected).ToList();
            if (!availableClients.Any())
            {
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] No available clients found");
                return null;
            }

            switch (strategy)
            {
                case Strategy.Random:
                    return SelectRandom(availableClients);
                
                case Strategy.RoundRobin:
                    return SelectRoundRobin(availableClients);
                
                case Strategy.LeastConnections:
                    return SelectLeastConnections(availableClients);
                
                default:
                    throw new ArgumentException("Invalid load balancing strategy");
            }
        }

        private static ClientHandler SelectRandom(List<ClientHandler> clients)
        {
            var random = new Random();
            return clients[random.Next(clients.Count)];
        }

        private static ClientHandler SelectRoundRobin(List<ClientHandler> clients)
        {
            lock (_lock)
            {
                _roundRobinIndex = (_roundRobinIndex + 1) % clients.Count;
                return clients[_roundRobinIndex];
            }
        }

        private static ClientHandler SelectLeastConnections(List<ClientHandler> clients)
        {
            return clients.OrderBy(c => c.ActiveConnections).First();
        }
    }
}