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
        UvTcpClientConnection connection;

        internal Task SendAsync(string line)
        {
            try
            {
                if (connection == null)
                {
                    Console.WriteLine($"[client] (no connection; cannot send)");
                    return done;
                }
                else
                {
                    var buffer = connection.Input.Alloc(line.Length);
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
            Task.Run(ReadLoop).FireOrForget();
        }
        internal async Task ReadLoop()
        {
            while (true)
            {
                var buffer = await connection.Output;
                if (buffer.IsEmpty && connection.Output.Completion.IsCompleted)
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

        public void Dispose()
        {
            if(connection != null) Close(connection);
            connection = null;
            // client.Dispose(); //
            thread?.Dispose();
            thread = null;
        }

        private void Close(UvTcpClientConnection connection, Exception error = null)
        {
            Console.WriteLine("[client] closing connection...");
            connection.Output.CompleteReading(error);
            connection.Input.CompleteWriting(error);
            Console.WriteLine("[client] connection closed");
        }
    }
}
