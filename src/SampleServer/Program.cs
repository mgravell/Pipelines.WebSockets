using Channels.WebSockets;
using System;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleApplication
{
    public static class Program
    {
        class MyServer : WebSocketServer
        {
            protected override Task OnTextAsync(WebSocketConnection connection, ref Message message)
            {
                Console.WriteLine($"Received: {message.GetText()} (final: {message.IsFinal})");
                return base.OnTextAsync(connection, ref message);
            }
            protected override Task OnPongAsync(WebSocketConnection connection, ref Message message)
            {
                Console.WriteLine("Pong: " + message.GetText());
                return base.OnPongAsync(connection, ref message);
            }
        }
        public static void Main()
        {
            using (var server = new MyServer())
            {
                server.Start(IPAddress.Loopback, 5001);
                Console.WriteLine($"Running on {server.IP}:{server.Port}...");
                CancellationTokenSource cancel = new CancellationTokenSource();
                Console.WriteLine("cl: start client (listening)");
                Console.WriteLine("cd: start client (deaf)");
                Console.WriteLine("b: broadbast");
                Console.WriteLine("p: ping");
                Console.WriteLine("x: exit");
                bool keepGoing = true;
                while (keepGoing)
                {
                    var line = Console.ReadLine();
                    switch (line)
                    {
                        case null:
                        case "x":
                            keepGoing = false;
                            break;
                        case "b":
                            server.BroadcastAsync("hello to all clients").ContinueWith(t =>
                            {
                                try {
                                    Console.WriteLine($"Broadcast to {t.Result} clients");
                                } catch (Exception e) {
                                    WriteError(e);
                                }
                            });
                            break;
                        case "c":
                            Task.Run(() => Execute(true, cancel.Token));
                            break;
                        case "p":
                            server.PingAsync("ping!").ContinueWith(t =>
                            {
                                try {
                                    Console.WriteLine($"Pinged {t.Result} clients");
                                } catch(Exception e) {
                                    WriteError(e);
                                }
                            });                                
                            break;
                    }
                }
                Console.WriteLine($"Press any key to exit");
                Console.ReadKey();
                cancel.Cancel();
                Console.WriteLine("Shutting down...");
            }
        }

        private static void WriteError(Exception e)
        {
            while(e is AggregateException && e.InnerException != null)
            {
                e = e.InnerException;
            }
            Console.WriteLine($"{e.GetType().Name}: {e.Message}");
            Console.WriteLine(e.StackTrace);
        }

        static void FireOrForget(this Task task) => task.ContinueWith(t => GC.KeepAlive(t.Exception), TaskContinuationOptions.OnlyOnFaulted);

        private static void WriteStatus(string message)
        {
            Console.WriteLine($"[Client:{Environment.CurrentManagedThreadId}]: {message}");
        }
        static int clientNumber;
        private static async Task Execute(bool listen, CancellationToken token)
        {
            try
            {
                using (var socket = new ClientWebSocket())
                {
                    socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
                    var uri = new Uri("ws://127.0.0.1:5001");
                    WriteStatus($"connecting to {uri}...");
                    await socket.ConnectAsync(uri, token);
                    
                    WriteStatus("connected");
                    var buffer = new byte[2048];
                    int count = 0, clientNumber = Interlocked.Increment(ref Program.clientNumber);

                    if (listen) Task.Run(() => ReceiveLoop(socket, token)).FireOrForget();

                    do
                    {
                        string msg = $"hello from client {clientNumber} to server, message {++count}";
                        int len = Encoding.ASCII.GetBytes(msg, 0, msg.Length, buffer, 0);
                        
                        WriteStatus("sending...");
                        await socket.SendAsync(new ArraySegment<byte>(buffer, 0, len), WebSocketMessageType.Text, true, token);
                        WriteStatus("sent...");

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

        private static async Task ReceiveLoop(ClientWebSocket socket, CancellationToken token)
        {
            var buffer = new byte[2048];
            while (!token.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, 0, buffer.Length), token);
                var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                WriteStatus($"received {result.MessageType}: {message}");
            }
            
        }
    }
}
