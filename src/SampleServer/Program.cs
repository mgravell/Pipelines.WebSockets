using Channels.WebSockets;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
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
                if (logging)
                {
                    Console.WriteLine($"Received: {message.GetText()} (final: {message.IsFinal})");
                }
                return base.OnTextAsync(connection, ref message);
            }
        }
        static bool logging = true;
        public static void Main()
        {
            using (var server = new MyServer())
            {
                server.Start(IPAddress.Loopback, 5001);
                Console.WriteLine($"Running on {server.IP}:{server.Port}...");
                CancellationTokenSource cancel = new CancellationTokenSource();

                bool keepGoing = true, writeLegend = true;
                while (keepGoing)
                {
                    if (writeLegend)
                    {
                        Console.WriteLine("c: start client");
                        Console.WriteLine("c ###: start ### clients");
                        Console.WriteLine("b: broadbast from server");
                        Console.WriteLine("s: send from clients");
                        Console.WriteLine("p: ping");
                        Console.WriteLine("l: toggle logging");
                        Console.WriteLine("cls: clear console");
                        Console.WriteLine("xc: close at client");
                        Console.WriteLine("xs: close at server");
                        Console.WriteLine("q: quit");
                        Console.WriteLine($"clients: {ClientCount}; server connections: {server.ConnectionCount}");
                        writeLegend = false;
                    }

                    var line = Console.ReadLine();
                    switch (line)
                    {
                        case null:
                        case "cls":
                            Console.Clear();
                            break;
                        case "q":
                            keepGoing = false;
                            break;
                        case "l":
                            logging = !logging;
                            Console.WriteLine("logging is now " + (logging ? "on" : "off"));
                            break;
                        case "xc":
                            CloseAllClients(cancel.Token).ContinueWith(t =>
                            {
                                try
                                {
                                    Console.WriteLine($"Closed {t.Result} clients");
                                }
                                catch (Exception e)
                                {
                                    WriteError(e);
                                }
                            });
                            break;
                        case "xs":
                            server.CloseAllAsync("nuked from orbit").ContinueWith(t =>
                            {
                                try
                                {
                                    Console.WriteLine($"Closed {t.Result} connections at the server");
                                }
                                catch (Exception e)
                                {
                                    WriteError(e);
                                }
                            });
                            break;
                        case "b":
                            server.BroadcastAsync("hello to all clients").ContinueWith(t =>
                            {
                                try
                                {
                                    Console.WriteLine($"Broadcast to {t.Result} clients");
                                }
                                catch (Exception e)
                                {
                                    WriteError(e);
                                }
                            });
                            break;
                        case "s":
                            SendFromClients(cancel.Token).ContinueWith(t =>
                            {
                                try
                                {
                                    Console.WriteLine($"Sent from {t.Result} clients");
                                }
                                catch (Exception e)
                                {
                                    WriteError(e);
                                }
                            });
                            break;
                        case "c":
                            StartClients(cancel.Token);
                            break;
                        case "p":
                            server.PingAsync("ping!").ContinueWith(t =>
                            {
                                try
                                {
                                    Console.WriteLine($"Pinged {t.Result} clients");
                                }
                                catch (Exception e)
                                {
                                    WriteError(e);
                                }
                            });
                            break;
                        default:
                            var match = Regex.Match(line, "c ([0-9]+)");
                            int i;
                            if (match.Success && int.TryParse(match.Groups[1].Value, out i) && i.ToString() == match.Groups[1].Value && i >= 1)
                            {
                                StartClients(cancel.Token, i);
                            }
                            else
                            {
                                writeLegend = true;
                            }
                            break;
                    }
                }
                Console.WriteLine($"Press any key to exit");
                Console.ReadKey();
                cancel.Cancel();
                Console.WriteLine("Shutting down...");
            }
        }

        private static void StartClients(CancellationToken cancel, int count = 1)
        {
            if (count <= 0) return;
            int countBefore = ClientCount;
            for (int i = 0; i < count; i++) Task.Run(() => Execute(true, cancel));
            // not thread-pool so probably aren't there yet
            Console.WriteLine($"{count} client(s) started; expected: {countBefore + count}");
        }

        private static void WriteError(Exception e)
        {
            while (e is AggregateException && e.InnerException != null)
            {
                e = e.InnerException;
            }
            Console.WriteLine($"{e.GetType().Name}: {e.Message}");
            Console.WriteLine(e.StackTrace);
        }

        static void FireOrForget(this Task task) => task.ContinueWith(t => GC.KeepAlive(t.Exception), TaskContinuationOptions.OnlyOnFaulted);

        [Conditional("LOGGING")]
        private static void WriteStatus(string message)
        {
#if LOGGING
            Console.WriteLine($"[Client:{Environment.CurrentManagedThreadId}]: {message}");
#endif
        }
        static int clientNumber;
        // lazy client-side connection manager
        static readonly List<ClientWebSocketWithIdentity> clients = new List<ClientWebSocketWithIdentity>();
        public static int ClientCount
        {
            get
            {
                lock (clients) { return clients.Count; }
            }
        }
        private static async Task<int> SendFromClients(CancellationToken cancel)
        {
            ClientWebSocketWithIdentity[] arr;
            lock (clients)
            {
                arr = clients.ToArray();
            }
            int count = 0;
            foreach (var client in arr)
            {
                var msg = Encoding.UTF8.GetBytes($"Hello from client {client.Id}");
                try
                {
                    await client.Socket.SendAsync(new ArraySegment<byte>(msg, 0, msg.Length), WebSocketMessageType.Text, true, cancel);
                    count++;
                }
                catch { }
            }
            return count;
        }
        private static async Task<int> CloseAllClients(CancellationToken cancel)
        {
            ClientWebSocketWithIdentity[] arr;
            lock (clients)
            {
                arr = clients.ToArray();
            }
            int count = 0;
            foreach (var client in arr)
            {
                var msg = Encoding.UTF8.GetBytes($"Hello from client {client.Id}");
                try
                {
                    await client.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cancel);
                    count++;
                }
                catch { }
            }
            return count;
        }
        struct ClientWebSocketWithIdentity : IEquatable<ClientWebSocketWithIdentity>
        {
            public readonly ClientWebSocket Socket;
            public readonly int Id;
            public ClientWebSocketWithIdentity(ClientWebSocket socket, int id)
            {
                Socket = socket;
                Id = id;
            }
            public override bool Equals(object obj) => obj is ClientWebSocketWithIdentity && Equals((ClientWebSocketWithIdentity)obj);
            public bool Equals(ClientWebSocketWithIdentity obj) => obj.Id == this.Id && obj.Socket == this.Socket;
            public override int GetHashCode() => Id;
            public override string ToString() => $"{Id}: {Socket}";
        }
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
                    int clientNumber = Interlocked.Increment(ref Program.clientNumber);
                    var named = new ClientWebSocketWithIdentity(socket, clientNumber);
                    lock (clients)
                    {
                        clients.Add(named);
                    }
                    try
                    {
                        await ReceiveLoop(named, token);
                    }
                    finally
                    {
                        lock (clients)
                        {
                            clients.Remove(named);
                        }
                    }

                    //int count = 0, clientNumber = Interlocked.Increment(ref Program.clientNumber);
                    // if (listen) Task.Run(() => ReceiveLoop(socket, token)).FireOrForget();
                    //do
                    //{
                    //    string msg = $"hello from client {clientNumber} to server, message {++count}";
                    //    int len = Encoding.ASCII.GetBytes(msg, 0, msg.Length, buffer, 0);

                    //    WriteStatus("sending...");
                    //    await socket.SendAsync(new ArraySegment<byte>(buffer, 0, len), WebSocketMessageType.Text, true, token);
                    //    WriteStatus("sent...");

                    //    await Task.Delay(5000, token);
                    //} while (!token.IsCancellationRequested);
                }
            }
            catch (Exception ex)
            {
                WriteStatus(ex.GetType().Name);
                WriteStatus(ex.Message);
            }
        }

        private static async Task ReceiveLoop(ClientWebSocketWithIdentity named, CancellationToken token)
        {
            var socket = named.Socket;
            var buffer = new byte[2048];
            while (!token.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, 0, buffer.Length), token);
                if (logging)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    Console.WriteLine($"client {named.Id} received {result.MessageType}: {message}");
                }
            }

        }
    }
}
