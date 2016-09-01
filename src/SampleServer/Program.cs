using Channels.WebSockets;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Net.WebSockets;
using System.Numerics;
using Channels.Text.Primitives;
using System.Reflection;
using Channels;

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
                    Console.WriteLine($"Received: {message.GetText()} (final: {message.IsFinal})");
                }
                return base.OnTextAsync(connection, ref message);
            }
        }
        static bool logging = true;
        static void XorVector()
        {
            Random rand = new Random(12345);
            byte[] chunk = new byte[16384];
            rand.NextBytes(chunk);
            int mask = rand.Next();

            // functionality test first
            var orig = BitConverter.ToString(chunk, 0, 128);
            ApplyMaskWithoutAcceleration(chunk, 0, 128, mask);
            var masked1 = BitConverter.ToString(chunk, 0, 128);
            ApplyMaskWithoutAcceleration(chunk, 0, 128, mask);
            var masked2 = BitConverter.ToString(chunk, 0, 128);
            if (masked1 == orig) throw new InvalidOperationException("unaccelerated mask unsuccessful");
            if (masked2 != orig) throw new InvalidOperationException("unaccelerated mask unsuccessful");
            Console.WriteLine("masked and unmasked successfully without acceleration");

            if (Vector.IsHardwareAccelerated)
            {
                ApplyMaskWithAcceleration(chunk, 0, 128, mask);
                var masked3 = BitConverter.ToString(chunk, 0, 128);
                ApplyMaskWithAcceleration(chunk, 0, 128, mask);
                var masked4 = BitConverter.ToString(chunk, 0, 128);
                if (masked3 != masked1) throw new InvalidOperationException("accelerated mask unsuccessful");
                if (masked4 != orig) throw new InvalidOperationException("accelerated mask unsuccessful");
                Console.WriteLine("masked and unmasked successfully with acceleration");
            }
            else
            {
                Console.WriteLine("acceleration not available");
            }

            const int Cycles = 10000000;

            PerformanceTest(chunk, 0, Vector<byte>.Count, mask, Cycles, false); // dry run for JIT etc
            PerformanceTest(chunk, 0, Vector<byte>.Count, mask, Cycles);
            PerformanceTest(chunk, 0, 128, mask, Cycles);
            PerformanceTest(chunk, 0, 192, mask, Cycles);
            PerformanceTest(chunk, 0, 256, mask, Cycles); // around the sweet spot
            PerformanceTest(chunk, 0, 320, mask, Cycles);
            PerformanceTest(chunk, 0, 384, mask, Cycles);
            PerformanceTest(chunk, 0, 448, mask, Cycles);
            PerformanceTest(chunk, 0, 512, mask, Cycles);
            PerformanceTest(chunk, 0, 1024, mask, Cycles);
            PerformanceTest(chunk, 0, 2048, mask, Cycles);
            PerformanceTest(chunk, 0, 16384, mask, Cycles);

        }
        static void PerformanceTest(byte[] chunk, int offset, int count, int mask, int cycles, bool log = true)
        {
            PerformanceTestWithoutAcceleration(chunk, offset, count, mask, cycles, log);
            if (Vector.IsHardwareAccelerated)
            {
                PerformanceTestWithAcceleration(chunk, offset, count, mask, cycles, log);
            }
        }
        static void PerformanceTestWithoutAcceleration(byte[] chunk, int offset, int count, int mask, int cycles, bool log)
        {
            CollectGarbage();
            var watch = Stopwatch.StartNew();
            for(int i = 1; i < cycles; i++)
            {
                ApplyMaskWithoutAcceleration(chunk, offset, count, mask);
            }
            watch.Stop();
            if(log) Console.WriteLine($"{cycles}x{count} bytes, no acceleration: {watch.ElapsedMilliseconds}ms");            
        }
        static void PerformanceTestWithAcceleration(byte[] chunk, int offset, int count, int mask, int cycles, bool log)
        {
            CollectGarbage();
            var watch = Stopwatch.StartNew();
            for (int i = 1; i < cycles; i++)
            {
                ApplyMaskWithAcceleration(chunk, offset, count, mask);
            }
            watch.Stop();
            if(log) Console.WriteLine($"{cycles}x{count} bytes, with acceleration: {watch.ElapsedMilliseconds}ms");
        }

        [ThreadStatic]
        private static byte[] maskArray;

        static unsafe void ApplyMaskWithAcceleration(byte[] data, int offset, int count, int mask)
        {

            int vectorWidth = Vector<byte>.Count;
            var maskArr = maskArray ?? (maskArray = new byte[vectorWidth]);
            fixed (byte* maskPtr = maskArr)
            {
                int* iPtr = (int*)maskPtr;
                for (int i = 0; i < (maskArr.Length / 4); i++)
                {
                    *iPtr++ = mask;
                }
            }

            var maskVector = new Vector<byte>(maskArr);

            int chunks = count / vectorWidth;
            for (int i = 0; i < chunks; i++)
            {
                var dataVector = new Vector<byte>(data, offset);
                dataVector ^= maskVector;
                dataVector.CopyTo(data, offset);
                offset += vectorWidth;
            }
            // count = count % vectorWidth;
        }
        static unsafe void ApplyMaskWithoutAcceleration(byte[] data, int offset, int count, int mask)
        {
            ulong mask8 = (uint)mask;
            mask8 = (mask8 << 32) | mask8;

            int chunks = count >> 3;
            fixed (byte* ptr = data)
            {
                var ptr8 = (ulong*)(ptr + offset);
                for (int i = 0; i < chunks; i++)
                {
                    (*ptr8++) ^= mask8;
                }
            }
        }
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
                WriteAssemblyVersion(typeof(BufferSpan));
                WriteAssemblyVersion(typeof(Channels.Networking.Libuv.UvTcpListener));
                WriteAssemblyVersion(typeof(ReadableBufferExtensions));

                RunBasicEchoServer();
                // XorVector();
                // RunServer();
                CollectGarbage();
                return 0;
            } catch(Exception ex)
            {
                WriteError(ex);
                return -1;
            }
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

        public static void RunServer()
        {
            using (var server = new MyServer())
            {
                server.Start(IPAddress.Loopback, 5001);
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
                        Console.WriteLine("s: send from clients");
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
                        Console.WriteLine($"clients: {ClientCount}; server connections: {server.ConnectionCount}; awaiting input: {WebSocketConnection.AwaitingInput}");
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
                Console.WriteLine("Shutting down...");
                cancel.Cancel();                
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
                var msg = encoding.GetBytes($"Hello from client {client.Id}");
                try
                {
                    await client.Socket.SendAsync(new ArraySegment<byte>(msg, 0, msg.Length), WebSocketMessageType.Text, true, cancel);
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
                    var msg = encoding.GetBytes($"Hello ");
                    await client.Socket.SendAsync(new ArraySegment<byte>(msg, 0, msg.Length), WebSocketMessageType.Text, false, cancel);
                    msg = encoding.GetBytes($"from client {client.Id}");
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
                var msg = encoding.GetBytes($"Hello from client {client.Id}");
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
                    //socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
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
                    var message = encoding.GetString(buffer, 0, result.Count);
                    Console.WriteLine($"client {named.Id} received {result.MessageType}: {message}");
                }
            }

        }
    }
}
