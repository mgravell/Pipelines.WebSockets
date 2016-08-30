using Channels.WebSockets;
using System;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleApplication
{
    public class Program
    {
        public static void Main()
        {
            using (var server = new WebSocketServer(IPAddress.Loopback, 5001))
            {
                server.Text += (conn, msg) =>
                {
                    Console.WriteLine($"Received: {msg}");
                };
                server.Start();
                Console.WriteLine($"Running on {server.IP}:{server.Port}... press any key to test");
                Console.ReadKey();
                CancellationTokenSource cancel = new CancellationTokenSource();
                Task.Run(() => Execute(cancel.Token));

                Console.WriteLine($"Press any key to exit");
                Console.ReadKey();
                cancel.Cancel();
                Console.WriteLine("Shutting down...");
            }
        }

        private static void WriteStatus(string message)
        {
            Console.WriteLine($"[Client:{Environment.CurrentManagedThreadId}]: {message}");
        }
        private static async Task Execute(CancellationToken token)
        {
            try
            {
                using (var socket = new ClientWebSocket())
                {
                    var uri = new Uri("ws://127.0.0.1:5001");
                    WriteStatus($"connecting to {uri}...");
                    await socket.ConnectAsync(uri, token);
                    WriteStatus("connected");
                    var buffer = new byte[2048];
                    int count = 0;

                    do
                    {
                        string msg = "hello from client to server " + ++count;
                        int len = Encoding.ASCII.GetBytes(msg, 0, msg.Length, buffer, 0);
                        WriteStatus("sending...");
                        await socket.SendAsync(new ArraySegment<byte>(buffer, 0, len), WebSocketMessageType.Text, true, token);
                        WriteStatus("sent...");
                        //WriteStatus("receiving...");
                        //var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, 0, buffer.Length), token);
                        //WriteStatus("received");
                        //WriteStatus(result.MessageType.ToString());
                        //WriteStatus(Encoding.ASCII.GetString(buffer, 0, result.Count));

                        await Task.Delay(5000, token);
                    } while (!token.IsCancellationRequested);
                }
            }
            catch (Exception ex)
            {
                WriteStatus(ex.GetType().Name);
                WriteStatus(ex.Message);
            }
        }
    }
}
