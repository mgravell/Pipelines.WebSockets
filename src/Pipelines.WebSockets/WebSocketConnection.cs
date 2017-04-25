using Channels.Networking.Sockets;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Channels.WebSockets
{
    public class WebSocketConnection : IDisposable
    {

        private readonly ConnectionType connectionType;

        public ConnectionType ConnectionType => connectionType;
        public static async Task<WebSocketConnection> ConnectAsync(
            string location, string protocol = null, string origin = null,
            Action<HttpRequestHeaders> addHeaders = null,
            ChannelFactory channelFactory = null)
        {
            WebSocketServer.WriteStatus(ConnectionType.Client, $"Connecting to {location}...");
            Uri uri;
            if (!Uri.TryCreate(location, UriKind.Absolute, out uri)
                || uri.Scheme != "ws")
            {
                throw new ArgumentException(nameof(location));
            }
            IPAddress ip;
            if (!IPAddress.TryParse(uri.Host, out ip))
            {
                throw new NotImplementedException("host must be an IP address at the moment, sorry");
            }
            WebSocketServer.WriteStatus(ConnectionType.Client, $"Opening socket to {ip}:{uri.Port}...");
            var socket = await SocketConnection.ConnectAsync(new IPEndPoint(ip, uri.Port), channelFactory);

            return await WebSocketProtocol.ClientHandshake(socket, uri, origin, protocol);
        }


        private IChannel connection;
        internal IChannel Connection => connection;

        internal WebSocketConnection(IChannel connection, ConnectionType connectionType)
        {
            this.connection = connection;
            this.connectionType = connectionType;
        }

        public string Host { get; internal set; }
        public string Origin { get; internal set; }
        public string Protocol { get; internal set; }
        public string RequestLine { get; internal set; }

        public object UserState { get; set; }
        internal async Task ProcessIncomingFramesAsync(WebSocketServer server)
        {
            while (true)
            {
                var buffer = await connection.Input.ReadAsync();
                try
                {
                    if (buffer.IsEmpty && connection.Input.Reading.IsCompleted)
                    {
                        break; // that's all, folks
                    }
                    WebSocketsFrame frame;
                    if (WebSocketProtocol.TryReadFrameHeader(ref buffer, out frame))
                    {
                        int payloadLength = frame.PayloadLength;
                        // buffer now points to the payload 

                        if (connectionType == ConnectionType.Server)
                        {
                            if (!frame.IsMasked)
                            {
                                throw new InvalidOperationException("Client-to-server frames should be masked");
                            }
                        }
                        else
                        {
                            if (frame.IsMasked)
                            {
                                throw new InvalidOperationException("Server-to-client frames should not be masked");
                            }
                        }


                        if (frame.IsControlFrame && !frame.IsFinal)
                        {
                            throw new InvalidOperationException("Control frames cannot be fragmented");
                        }
                        await OnFrameReceivedAsync(ref frame, ref buffer, server);
                        // and finally, progress past the frame
                        if (payloadLength != 0) buffer = buffer.Slice(payloadLength);
                    }

                }
                finally
                {
                    connection.Input.Advance(buffer.Start, buffer.End);
                }
            }
        }
        internal Task OnFrameReceivedAsync(ref WebSocketsFrame frame, ref ReadableBuffer buffer, WebSocketServer server)
        {
            WebSocketServer.WriteStatus(ConnectionType, frame.ToString());


            // note that this call updates the connection state; must be called, even
            // if we don't get as far as the 'switch' 
            var opCode = GetEffectiveOpCode(ref frame);

            Message msg;
            if (!frame.IsControlFrame)
            {
                if (frame.IsFinal)
                {
                    if (this.HasBacklog)
                    {
                        try
                        {
                            // add our data to the existing backlog
                            this.AddBacklog(ref buffer, ref frame);

                            // use the backlog buffer to execute the method; note that
                            // we un-masked *while adding*; don't need mask here
                            msg = new Message(this.GetBacklog());
                            if (server != null)
                            {
                                return OnServerAndConnectionFrameReceivedImplAsync(opCode, msg, server);
                            }
                            else
                            {
                                return OnConectionFrameReceivedImplAsync(opCode, ref msg);
                            }
                        }
                        finally
                        {
                            // and release the backlog
                            this.ClearBacklog();
                        }
                    }
                }
                else if (BufferFragments)
                {
                    // need to buffer this data against the connection
                    this.AddBacklog(ref buffer, ref frame);
                    return TaskResult.True;
                }
            }
            msg = new Message(buffer.Slice(0, frame.PayloadLength), frame.Mask, frame.IsFinal);
            if (server != null)
            {
                return OnServerAndConnectionFrameReceivedImplAsync(opCode, msg, server);
            }
            else
            {
                return OnConectionFrameReceivedImplAsync(opCode, ref msg);
            }

        }
        private async Task OnServerAndConnectionFrameReceivedImplAsync(WebSocketsFrame.OpCodes opCode, Message message, WebSocketServer server)
        {
            await OnConectionFrameReceivedImplAsync(opCode, ref message);
            await OnServerFrameReceivedImplAsync(opCode, ref message, server);
        }

        private Task OnServerFrameReceivedImplAsync(WebSocketsFrame.OpCodes opCode, ref Message message, WebSocketServer server)
        {
            switch (opCode)
            {
                case WebSocketsFrame.OpCodes.Binary:
                    return server.OnBinaryAsync(this, ref message);
                case WebSocketsFrame.OpCodes.Text:
                    return server.OnTextAsync(this, ref message);
            }
            return TaskResult.True;
        }
        private Task OnConectionFrameReceivedImplAsync(WebSocketsFrame.OpCodes opCode, ref Message message)
        {
            WebSocketServer.WriteStatus(connectionType, $"Processing {opCode}, {message.GetPayloadLength()} bytes...");
            switch (opCode)
            {
                case WebSocketsFrame.OpCodes.Binary:
                    return OnBinaryAsync(ref message);
                case WebSocketsFrame.OpCodes.Text:
                    return OnTextAsync(ref message);
                case WebSocketsFrame.OpCodes.Close:
                    connection.Input.Complete();
                    // respond to a close in-kind (2-handed close)
                    try { Closed?.Invoke(); } catch { }

                    if (connection.Output.Writing.IsCompleted) return TaskResult.True; // already closed

                    return SendAsync(WebSocketsFrame.OpCodes.Close, ref message);
                case WebSocketsFrame.OpCodes.Ping:
                    // by default, respond to a ping with a matching pong
                    return SendAsync(WebSocketsFrame.OpCodes.Pong, ref message); // right back at you
                case WebSocketsFrame.OpCodes.Pong:
                    return TaskResult.True;
                default:
                    return TaskResult.True;
            }
        }
        WebSocketsFrame.OpCodes lastOpCode;
        internal WebSocketsFrame.OpCodes GetEffectiveOpCode(ref WebSocketsFrame frame)
        {
            if (frame.IsControlFrame)
            {
                // doesn't change state
                return frame.OpCode;
            }

            var frameOpCode = frame.OpCode;

            // re-use the previous opcode if we are a continuation
            var result = frameOpCode == WebSocketsFrame.OpCodes.Continuation ? lastOpCode : frameOpCode;

            // if final, clear the opcode; otherwise: use what we thought of
            lastOpCode = frame.IsFinal ? WebSocketsFrame.OpCodes.Continuation : result;
            return result;
        }


        volatile List<ReadableBuffer> backlog;
        // TODO: not sure how to do this; basically I want to lease a writable, expandable area
        // for a duration, and be able to access it for reading, and release
        internal void AddBacklog(ref ReadableBuffer buffer, ref WebSocketsFrame frame)
        {
            var length = frame.PayloadLength;
            if (length == 0) return; // nothing to store!

            var slicedBuffer = buffer.Slice(0, length);
            // unscramble the data
            if (frame.Mask != 0) WebSocketsFrame.ApplyMask(ref slicedBuffer, frame.Mask);

            var backlog = this.backlog;
            if (backlog == null)
            {
                var newBacklog = new List<ReadableBuffer>();
                backlog = Interlocked.CompareExchange(ref this.backlog, newBacklog, null) ?? newBacklog;
            }
            backlog.Add(slicedBuffer.Preserve());
        }
        internal void ClearBacklog()
        {
            var backlog = this.backlog;
            if (backlog != null)
            {
                foreach (var buffer in backlog)
                    buffer.Dispose();
                backlog.Clear();
            }
        }

        internal async void StartProcessingIncomingFrames()
        {
            WebSocketServer.WriteStatus(ConnectionType.Client, "Processing incoming frames...");
            try
            {
                await ProcessIncomingFramesAsync(null);
            }
            catch (Exception ex)
            {
                WebSocketServer.WriteStatus(ConnectionType, ex.Message);
            }
        }

        public bool HasBacklog => (backlog?.Count ?? 0) != 0;

        internal List<ReadableBuffer> GetBacklog() => backlog;

        public Task SendAsync(string message, WebSocketsFrame.FrameFlags flags = WebSocketsFrame.FrameFlags.IsFinal)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            var msg = MessageWriter.Create(message);
            return SendAsync(WebSocketsFrame.OpCodes.Text, ref msg, flags);
        }
        public Task SendAsync(byte[] message, WebSocketsFrame.FrameFlags flags = WebSocketsFrame.FrameFlags.IsFinal)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            var msg = MessageWriter.Create(message);
            return SendAsync(WebSocketsFrame.OpCodes.Binary, ref msg, flags);
        }
        public Task PingAsync(string message = null)
        {
            var msg = MessageWriter.Create(message);
            return SendAsync(WebSocketsFrame.OpCodes.Ping, ref msg);
        }
        public Task CloseAsync(string message = null)
        {
            if (connection.Output.Writing.IsCompleted) return TaskResult.True;

            var msg = MessageWriter.Create(message);
            var task = SendAsync(WebSocketsFrame.OpCodes.Close, ref msg);
            return task;
        }
        internal Task SendAsync(WebSocketsFrame.OpCodes opCode, ref Message message, WebSocketsFrame.FrameFlags flags = WebSocketsFrame.FrameFlags.IsFinal)
        {
            if(connection.Output.Writing.IsCompleted)
            {
                throw new InvalidOperationException("Connection has been closed");
            }
            return SendAsyncImpl(opCode, message, flags);
        }

        public bool BufferFragments { get; set; } // TODO: flags bool
        public bool IsClosed => connection.Output.Writing.IsCompleted || connection.Input.Reading.IsCompleted;

        public event Action Closed;

        internal Task SendAsync<T>(WebSocketsFrame.OpCodes opCode, ref T message, WebSocketsFrame.FrameFlags flags = WebSocketsFrame.FrameFlags.IsFinal) where T : struct, IMessageWriter
        {
            return SendAsyncImpl(opCode, message, flags);
        }
        private async Task SendAsyncImpl<T>(WebSocketsFrame.OpCodes opCode, T message, WebSocketsFrame.FrameFlags flags) where T : struct, IMessageWriter
        {
            //TODO: come up with a better way of getting ordered access to the socket
            var writeLock = GetWriteSemaphore();
            bool haveLock = writeLock.Wait(0);

            if (!haveLock)
            {   // try to acquire asynchronously instead, then
                await writeLock.WaitAsync();
            }
            try
            {
                WebSocketServer.WriteStatus(ConnectionType, $"Writing {opCode} message ({message.GetPayloadLength()} bytes)...");
                await WebSocketProtocol.WriteAsync(this, opCode, flags, ref message);
                if (opCode == WebSocketsFrame.OpCodes.Close) connection.Output.Complete();
            }
            finally
            {
                writeLock.Release();
            }
        }
        private SemaphoreSlim GetWriteSemaphore()
        {
            var tmp = writeLock;
            if (tmp != null) return (SemaphoreSlim)tmp; // simple path

            IDisposable newSemaphore = null;
            try
            {
#if VOLATILE_READ
                while ((tmp = Thread.VolatileRead(ref writeLock)) == null)
#else
                while ((tmp = Interlocked.CompareExchange(ref writeLock, null, null)) == null)
#endif
                {
                    if (newSemaphore == null) newSemaphore = new SemaphoreSlim(1, 1);
                    if (Interlocked.CompareExchange(ref writeLock, newSemaphore, null) == null)
                    {
                        tmp = newSemaphore; // success, we swapped it in
                        newSemaphore = null; // to avoid it being disposed
                        break; // no need to read again
                    }
                }
            }
            finally
            {
                newSemaphore?.Dispose();
            }
            return (SemaphoreSlim)tmp;
        }

        public event Func<Message, Task> BinaryAsync, TextAsync;
        internal Task OnBinaryAsync(ref Message message)
            => BinaryAsync?.Invoke(message) ?? TaskResult.True;

        internal Task OnTextAsync(ref Message message)
            => TextAsync?.Invoke(message) ?? TaskResult.True;

        // lazily instantiated SemaphoreSlim
        object writeLock;


        public void Dispose()
        {
            BinaryAsync = TextAsync = null;
            ((IDisposable)writeLock)?.Dispose();
            ClearBacklog();
            connection.Input.Complete();
            connection.Output.Complete();
            connection.Dispose();
        }
    }


}
