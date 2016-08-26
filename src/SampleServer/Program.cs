using Channels.WebSockets;
using System;
using System.Net;

namespace ConsoleApplication
{
    public class Program
    {
        public static void Main()
        {
            using (var server = new WebSocketServer(IPAddress.Loopback, 5001))
            {
                server.Start();
                Console.WriteLine($"Running on {server.IP}:{server.Port}... press any key");
                Console.ReadKey();
                Console.WriteLine("Shutting down...");
            }                
        }
    }
}
