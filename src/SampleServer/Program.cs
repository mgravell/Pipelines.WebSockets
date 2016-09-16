using Channels;
using Channels.Networking.Libuv;
using Channels.Networking.Sockets;
using Channels.Text.Primitives;
using Channels.WebSockets;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SampleServer
{
    public static class Program
    {
        class MyServer : WebSocketServer
        {
            protected override Task OnTextAsync(WebSocketConnection connection, ref Message message)
            {
                if (logging)
                {
                    Console.WriteLine($"Received {message.GetPayloadLength()} bytes: {message.GetText()} (final: {message.IsFinal})");
                }
                return base.OnTextAsync(connection, ref message);
            }
        }
        static bool logging = true;
       
        static int Main()
        {
            try
            {
                TaskScheduler.UnobservedTaskException += (sender, args) =>
                {
                    Console.WriteLine($"{nameof(TaskScheduler)}.{nameof(TaskScheduler.UnobservedTaskException)}");
                    args.SetObserved();
                    WriteError(args.Exception);
                };
#if NET451
                AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
                {
                    Console.WriteLine($"{nameof(AppDomain)}.{nameof(AppDomain.UnhandledException)}");
                    WriteError(args.ExceptionObject as Exception);
                };
#endif
                WriteAssemblyVersion(typeof(ReadableBuffer));
                WriteAssemblyVersion(typeof(UvTcpListener));
                WriteAssemblyVersion(typeof(SocketListener));
                WriteAssemblyVersion(typeof(ReadableBufferExtensions));

                // TestOpenAndCloseListener();
                // RunBasicEchoServer();
                // RunWebSocketServer(ChannelProvider.Libuv);
                RunWebSocketServer(ChannelProvider.ManagedSockets);
                CollectGarbage();
                return 0;
            } catch(Exception ex)
            {
                WriteError(ex);
                return -1;
            }
        }

        private static void TestOpenAndCloseListener()
        {
            var thread = new UvThread();
            var ep = new IPEndPoint(IPAddress.Loopback, 5003);
            var listener = new UvTcpListener(thread, ep);
            listener.OnConnection(_ => Console.WriteLine("Hi and bye"));
            listener.Start();
            Console.WriteLine("Listening...");
            Thread.Sleep(1000);
            Console.WriteLine("Stopping listener...");
            listener.Stop();
            Thread.Sleep(1000);
            Console.WriteLine("Disposing thread...");
            thread.Dispose();
        }

        private static void RunBasicEchoServer()
        {
            using (var server = new EchoServer())
            using (var client = new TrivialClient())
            {
                var endpoint = new IPEndPoint(IPAddress.Loopback, 5002);
                Console.WriteLine($"Starting server on {endpoint}...");
                server.Start(endpoint);
                Console.WriteLine($"Server running");

                Thread.Sleep(1000); // let things spin up

                Console.WriteLine($"Opening client to {endpoint}...");
                client.ConnectAsync(endpoint).FireOrForget();
                Console.WriteLine("Client connected");

                Console.WriteLine("Write data to echo, or 'quit' to exit,");
                Console.WriteLine("'killc' to kill from the client, 'kills' to kill from the server, 'kill' for both");
                while (true)
                {
                    var line = Console.ReadLine();
                    if (line == null || line == "quit") break;
                    switch(line)
                    {
                        case "kill":
                            server.CloseAllConnections();
                            client.Close();
                            break;
                        case "killc":
                            client.Close();
                            break;
                        case "kills":
                            server.CloseAllConnections();
                            break;
                        default:
                            client.SendAsync(line).FireOrForget();
                            break;
                    }
                }
                server.Stop();
            }
        }

        private static void WriteAssemblyVersion(Type type)
        {
#if NET451
            var assembly = type.Assembly;
            var assemblyName = assembly.GetName();
            var attrib = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(assembly, typeof(AssemblyInformationalVersionAttribute));
            if(attrib != null)
            {
                Console.WriteLine($"{assemblyName.Name}: {attrib.InformationalVersion}");
            }
            else
            {
                
                Console.WriteLine($"{assemblyName.Name}: {assemblyName.Version}");
            }
            
#endif
        }

        private static void CollectGarbage()
        {
            for (int i = 0; i < 5; i++) // try to force any finalizer bugs
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced);
                GC.WaitForPendingFinalizers();
            }
        }
        public enum ChannelProvider
        {
            Libuv,
            ManagedSockets
        }
        public static void RunWebSocketServer(ChannelProvider provider)
        {
            using (var server = new MyServer())
            {
                switch(provider)
                {
                    case ChannelProvider.Libuv:
                        server.StartLibuv(IPAddress.Loopback, 5001);
                        break;
                    case ChannelProvider.ManagedSockets:
                        server.StartManagedSockets(IPAddress.Loopback, 5001);
                        break;
                }
                
                Console.WriteLine($"Running on {server.IP}:{server.Port}...");
                CancellationTokenSource cancel = new CancellationTokenSource();

                bool keepGoing = true, writeLegend = true, writeStatus = true;
                while (keepGoing)
                {
                    if (writeLegend)
                    {
                        Console.WriteLine("c: start client");
                        Console.WriteLine("c ###: start ### clients");
                        Console.WriteLine("b: broadbast from server");
                        Console.WriteLine("b ###: broadcast ### bytes from server");
                        Console.WriteLine("s: send from clients");
                        Console.WriteLine("s ###: send ### bytes from clients");
                        Console.WriteLine("p: ping");
                        Console.WriteLine("l: toggle logging");
                        Console.WriteLine("cls: clear console");
                        Console.WriteLine("xc: close at client");
                        Console.WriteLine("xs: close at server");
                        Console.WriteLine($"bf: toggle {nameof(server.BufferFragments)}");
                        Console.WriteLine("frag: send fragmented message from clients");
                        Console.WriteLine("q: quit");
                        Console.WriteLine("stat: write status");
                        Console.WriteLine("?: help");
                        writeLegend = false;
                    }
                    if(writeStatus)
                    {
                        Console.WriteLine($"clients: {ClientCount}; server connections: {server.ConnectionCount}");
                        writeStatus = false;
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
                        case "stat":
                            writeStatus = true;
                            break;
                        case "?":
                            writeLegend = true;
                            break;
                        case "l":
                            logging = !logging;
                            Console.WriteLine("logging is now " + (logging ? "on" : "off"));
                            break;
                        case "bf":
                            server.BufferFragments = !server.BufferFragments;
                            Console.WriteLine($"{nameof(server.BufferFragments)} is now " + (server.BufferFragments ? "on" : "off"));
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
                        case "frag":
                            SendFragmentedFromClients(cancel.Token).ContinueWith(t =>
                            {
                                try
                                {
                                    Console.WriteLine($"Sent fragmented from {t.Result} clients");
                                }
                                catch (Exception e)
                                {
                                    WriteError(e);
                                }
                            });
                            break;
                        case "cc":
                            StartChannelClients();
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
                            Match match;
                            int i;
                            if ((match = Regex.Match(line, "c ([0-9]+)")).Success && int.TryParse(match.Groups[1].Value, out i) && i.ToString() == match.Groups[1].Value && i >= 1)
                            {
                                StartClients(cancel.Token, i);
                            }
                            else if ((match = Regex.Match(line, "s ([0-9]+)")).Success && int.TryParse(match.Groups[1].Value, out i) && i.ToString() == match.Groups[1].Value && i >= 1)
                            {
                                SendFromClients(cancel.Token, new string('#', i)).FireOrForget();
                            }
                            else if ((match = Regex.Match(line, "b ([0-9]+)")).Success && int.TryParse(match.Groups[1].Value, out i) && i.ToString() == match.Groups[1].Value && i >= 1)
                            {
                                server.BroadcastAsync(new string('#', i)).ContinueWith(t =>
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
                            }
                            else
                            {
                                writeLegend = true;
                            }
                            break;
                    }
                }
                Console.WriteLine("Shutting down...");
                cancel.Cancel();                
            }
        }

        private static void StartChannelClients(int count = 1)
        {
            if (count <= 0) return;
            int countBefore = ClientCount;
            for (int i = 0; i < count; i++) Task.Run(() => ExecuteChannel(true));
            // not thread-pool so probably aren't there yet
            Console.WriteLine($"{count} client(s) started; expected: {countBefore + count}");
        }

        private static void StartClients(CancellationToken cancel, int count = 1)
        {
            if (count <= 0) return;
            int countBefore = ClientCount;
            for (int i = 0; i < count; i++) Task.Run(() => Execute(true, cancel));
            // not thread-pool so probably aren't there yet
            Console.WriteLine($"{count} client(s) started; expected: {countBefore + count}");
        }

        internal static void WriteError(Exception e)
        {
            while (e is AggregateException && e.InnerException != null)
            {
                e = e.InnerException;
            }
            if (e != null)
            {
                Console.WriteLine($"{e.GetType().Name}: {e.Message}");
                Console.WriteLine(e.StackTrace);
            }
        }

        internal static void FireOrForget(this Task task) => task.ContinueWith(t => GC.KeepAlive(t.Exception), TaskContinuationOptions.OnlyOnFaulted);

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
        private static readonly Encoding encoding = Encoding.UTF8;
        private static async Task<int> SendFromClients(CancellationToken cancel, string message = null)
        {
            ClientWebSocketWithIdentity[] arr;
            lock (clients)
            {
                arr = clients.ToArray();
            }
            int count = 0;
            foreach (var client in arr)
            {
                try
                {
                    await client.SendAsync(message ?? $"Hello from client {client.Id}", cancel);
                    count++;
                }
                catch { }
                
                
            }
            return count;
        }

        private static async Task<int> SendFragmentedFromClients(CancellationToken cancel)
        {
            ClientWebSocketWithIdentity[] arr;
            lock (clients)
            {
                arr = clients.ToArray();
            }
            int count = 0;
            foreach (var client in arr)
            {
                try
                {
                    await client.SendAsync($"Hello ", cancel, false);
                    await client.SendAsync($"from client {client.Id}", cancel, true);
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
                var msg = encoding.GetBytes($"Hello from client {client.Id}");
                try
                {
                    await client.CloseAsync(cancel);
                    count++;
                }
                catch { }
            }
            return count;
        }
        struct ClientWebSocketWithIdentity : IEquatable<ClientWebSocketWithIdentity>
        {
            public readonly object Socket;
            public readonly int Id;
            public ClientWebSocketWithIdentity(WebSocketConnection socket, int id)
            {
                Socket = socket;
                Id = id;
            }
            public ClientWebSocketWithIdentity(ClientWebSocket socket, int id)
            {
                Socket = socket;
                Id = id;
            }
            public override bool Equals(object obj) => obj is ClientWebSocketWithIdentity && Equals((ClientWebSocketWithIdentity)obj);
            public bool Equals(ClientWebSocketWithIdentity obj) => obj.Id == this.Id && obj.Socket == this.Socket;
            public override int GetHashCode() => Id;
            public override string ToString() => $"{Id}: {Socket}";

            internal Task SendAsync(string message, CancellationToken cancel, bool final = true)
            {
                if (Socket is ClientWebSocket)
                {
                    var msg = encoding.GetBytes(message);
                    return ((ClientWebSocket)Socket).SendAsync(new ArraySegment<byte>(msg, 0, msg.Length), WebSocketMessageType.Text, final, cancel);
                }
                else if (Socket is WebSocketConnection)
                {
                    return ((WebSocketConnection)Socket).SendAsync(message,
                        final ? WebSocketsFrame.FrameFlags.IsFinal : 0);
                }
                else return null;
            }

            internal Task CloseAsync(CancellationToken cancel)
            {
                if (Socket is ClientWebSocket)
                {
                    return ((ClientWebSocket)Socket).CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cancel);
                }
                else if (Socket is WebSocketConnection)
                {
                    return ((WebSocketConnection)Socket).CloseAsync("bye");
                }
                else return null;
            }
        }
        private static async Task ExecuteChannel(bool listen)
        {
            try
            {
                const string uri = "ws://127.0.0.1:5001";
                WriteStatus($"connecting to {uri}...");

                var socket = await WebSocketConnection.ConnectAsync(uri);
                
                WriteStatus("connected");
                int clientNumber = Interlocked.Increment(ref Program.clientNumber);
                var named = new ClientWebSocketWithIdentity(socket, clientNumber);
                lock (clients)
                {
                    clients.Add(named);
                }
                ClientChannelReceiveMessages(named);
            }
            catch (Exception ex)
            {
                WriteStatus(ex.GetType().Name);
                WriteStatus(ex.Message);
            }
        }

        private static async Task Execute(bool listen, CancellationToken token)
        {
            try
            {
                using (var socket = new ClientWebSocket())
                {
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
                        await ClientReceiveLoop(named, token);
                    }
                    finally
                    {
                        lock (clients)
                        {
                            clients.Remove(named);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                WriteStatus(ex.GetType().Name);
                WriteStatus(ex.Message);
            }
        }

        private static void ClientChannelReceiveMessages(ClientWebSocketWithIdentity named)
        {
            var socket = (WebSocketConnection)named.Socket;
            socket.TextAsync += msg =>
            {
                if (logging)
                {
                    var message = msg.GetText();
                    Console.WriteLine($"client {named.Id} received text, {msg.GetPayloadLength()} bytes, final: {msg.IsFinal}: {message}");
                }
                return null;
            };
        }
        private static async Task ClientReceiveLoop(ClientWebSocketWithIdentity named, CancellationToken token)
        {
            var socket = (ClientWebSocket)named.Socket;
            var buffer = new byte[2048];
            while (!token.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, 0, buffer.Length), token);
                if (logging)
                {
                    var message = encoding.GetString(buffer, 0, result.Count);
                    Console.WriteLine($"client {named.Id} received {result.MessageType}, {result.Count} bytes, final: {result.EndOfMessage}: {message}");
                }
            }

        }
    }
}
