using Channels.Networking.Libuv;
using Channels.Text.Primitives;
using System;
using System.Net;
using System.Threading.Tasks;

namespace SampleServer
{
    public class TrivialClient : IDisposable
    {
        UvTcpClient client;
        UvThread thread;
        UvTcpConnection connection;

        internal Task SendAsync(string line)
        {
            
            try
            {
                if (connection == null)
                {
                    Console.WriteLine($"[client] (no connection; cannot send)");
                    return done;
                }
                else if (string.IsNullOrEmpty(line))
                {
                    return done;
                }
                else
                {
                    var buffer = connection.Output.Alloc();
                    Console.WriteLine($"[client] sending {line.Length} bytes...");
                    WritableBufferExtensions.WriteAsciiString(ref buffer, line);
                    return buffer.FlushAsync();
                }
            }
            catch (Exception ex)
            {
                Program.WriteError(ex);
                return done;
            }
        }
        static readonly Task done = Task.FromResult(0);

        internal async Task ConnectAsync(IPEndPoint endpoint)
        {
            thread = new UvThread();
            client = new UvTcpClient(thread, endpoint);
            connection = await client.ConnectAsync();
            ReadLoop(); // will hand over to libuv thread
        }
        internal async void ReadLoop()
        {
            Console.WriteLine("[client] read loop started");
            try
            {
                while (true)
                {
                    var buffer = await connection.Input;
                    if (buffer.IsEmpty && (connection == null || connection.Input.Completion.IsCompleted))
                    {
                        Console.WriteLine("[client] input ended");
                        break;
                    }

                    var s = buffer.GetAsciiString();
                    buffer.Consumed();

                    Console.Write("[client] received: ");
                    Console.WriteLine(s);
                }
            }
            catch(Exception ex)
            {
                Console.Write("[client] read loop exploded");
                Program.WriteError(ex);
            }
            finally
            {
                Console.WriteLine("[client] read loop ended");
            }
        }
        public void Close()
        {
            if (connection != null) Close(connection);
            connection = null;
            // client.Dispose(); //
            thread?.Dispose();
            thread = null;
        }
        public void Dispose() => Dispose(true);
        private void Dispose(bool disposing)
        {
            if (disposing) Close();
        }

        private void Close(UvTcpConnection connection, Exception error = null)
        {
            Console.WriteLine("[client] closing connection...");
            connection.Input.CompleteReading(error);
            connection.Output.CompleteWriting(error);
            Console.WriteLine("[client] connection closed");
        }
    }
}
