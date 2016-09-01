using System;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SampleServer
{
    public class TrivialClient : IDisposable
    {
        Socket socket;
        internal void Send(string line)
        {
            try
            {
                var arr = Encoding.ASCII.GetBytes(line);
                Console.WriteLine($"[client] sending {arr.Length} bytes...");
                socket.Send(arr);
                Console.WriteLine($"[client] sent");
            }
            catch (Exception ex)
            {
                Program.WriteError(ex);
            }
        }

        internal void Connect(IPEndPoint endpoint)
        {
            SocketAsyncEventArgs args = new SocketAsyncEventArgs();
            args.RemoteEndPoint = endpoint;
            args.Completed += OnCompleted;
            if (!Socket.ConnectAsync(SocketType.Stream, ProtocolType.Tcp, args)) OnCompleted(this, args);
        }

        private void OnCompleted(object sender, SocketAsyncEventArgs e)
        {
            if(e.SocketError != SocketError.Success)
            {
                Dispose();
                return;
            }
            switch(e.LastOperation)
            {
                case SocketAsyncOperation.Connect:
                    socket = e.ConnectSocket;
                    Console.WriteLine($"[client] connected from {socket.LocalEndPoint} to {socket.RemoteEndPoint}");

                    e.RemoteEndPoint = null;
                    e.SetBuffer(new byte[1024], 0, 1024);
                    BeginRead(e);
                    break;
                case SocketAsyncOperation.Receive:
                    EndRead(e);
                    break;
            }
            
        }

        private void EndRead(SocketAsyncEventArgs e)
        {
            var len = e.BytesTransferred;
            if(len == 0)
            {
                Console.WriteLine("[client] input closed");
            }
            else
            {
                Console.Write("[client] received: ");
                Console.WriteLine(Encoding.ASCII.GetString(e.Buffer, 0, len));
                BeginRead(e);
            }
            
        }

        private void BeginRead(SocketAsyncEventArgs e)
        {
            if (!socket.ReceiveAsync(e)) EndRead(e);
        }

        public void Dispose()
        {
            try { socket?.Dispose(); } catch { }
            socket = null;
        }
    }
}
