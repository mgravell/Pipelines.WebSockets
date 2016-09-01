using Channels;
using Channels.Networking.Libuv;
using System;
using System.Collections.Generic;
using System.Net;

namespace SampleServer
{
    public class EchoServer : IDisposable
    {
        private UvTcpListener listener;
        private UvThread thread;
        public void Start(IPEndPoint endpoint)
        {
            if (listener == null)
            {
                thread = new UvThread();
                listener = new UvTcpListener(thread, endpoint);
                listener.OnConnection(OnConnection);
                listener.Start();
            }
        }

        private List<UvTcpConnection> connections = new List<UvTcpConnection>();

        public int CloseAllConnections(Exception error = null)
        {
            UvTcpConnection[] arr;
            lock(connections)
            {
                arr = connections.ToArray(); // lazy
            }
            int count = 0;
            foreach (var conn in arr)
            {
                Close(conn, error);
                count++;
            }
            return count;
        }
        private async void OnConnection(UvTcpConnection connection)
        {
            try
            {
                Console.WriteLine("[server] OnConnection entered");
                lock(connections)
                {
                    connections.Add(connection);
                }
                while(true)
                {
                    ReadableBuffer request;

                    Console.WriteLine("[server] awaiting input...");
                    try
                    {
                        request = await connection.Input;
                    }
                    finally
                    {
                        Console.WriteLine("[server] await completed");
                    }
                    if (request.IsEmpty && connection.Input.Completion.IsCompleted) break;

                    int len = request.Length;
                    Console.WriteLine($"[server] echoing {len} bytes...");
                    var response = connection.Output.Alloc();
                    response.Append(ref request);
                    await response.FlushAsync();
                    Console.WriteLine($"[server] echoed");
                    request.Consumed();
                }
                Close(connection);          
            }
            catch(Exception ex)
            {
                Program.WriteError(ex);
            }
            finally
            {
                lock(connections)
                {
                    connections.Remove(connection);
                }
                Console.WriteLine("[server] OnConnection exited");
            }
        }

        private void Close(UvTcpConnection connection, Exception error = null)
        {
            Console.WriteLine("[server] closing connection...");
            connection.Output.CompleteWriting(error);
            connection.Input.CompleteReading(error);
            Console.WriteLine("[server] connection closed");
        }

        public void Stop()
        {
            CloseAllConnections();
            listener?.Stop();
            thread?.Dispose();
            listener = null;
            thread = null;
        }
        public void Dispose() => Dispose(true);
        private void Dispose(bool disposing)
        {
            if (disposing) Stop();
        }
    }
}
