using Channels;
using Channels.Networking.Libuv;
using System;
using System.Collections.Generic;
using System.Net;

namespace SampleServer
{
    public class TrivialServer : IDisposable
    {
        private UvTcpListener listener;
        private UvThread thread;
        public void Start(IPEndPoint endpoint)
        {
            if (listener == null)
            {
                thread = new UvThread();
                listener = new UvTcpListener(thread, endpoint);
                listener.OnConnection(ConnectionCallback);
                listener.Start();
            }
        }

        private List<UvTcpServerConnection> connections = new List<UvTcpServerConnection>();

        public int CloseAllConnections(Exception error = null)
        {
            UvTcpServerConnection[] arr;
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
        private async void ConnectionCallback(UvTcpServerConnection connection)
        {
            try
            {
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
                    var response = connection.Output.Alloc(len);
                    response.Append(ref request);
                    await response.FlushAsync();
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
                connections.Remove(connection);
            }
        }

        private void Close(UvTcpServerConnection connection, Exception error = null)
        {
            Console.WriteLine("[server] closing connection...");
            connection.Output.CompleteWriting(error);
            connection.Input.CompleteReading(error);
            Console.WriteLine("[server] connection closed");
        }

        public void Stop()
        {
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
