using Channels.Networking.Libuv;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Channels.WebSockets
{
    public class WebSocketConnection : IDisposable
    {


        private UvTcpConnection connection;
        internal UvTcpConnection Connection => connection;
        internal WebSocketConnection(UvTcpConnection connection)
        {
            this.connection = connection;
        }

        public string Host { get; internal set; }
        public string Origin { get; internal set; }
        public string Protocol { get; internal set; }
        public string RequestLine { get; internal set; }

        public object UserState { get; set; }
        internal async Task ProcessIncomingFramesAsync(WebSocketServer server)
        {
            while (!IsClosed)
            {
                var buffer = await connection.Input.ReadAsync();
                try
                {
                    if (buffer.IsEmpty && connection.Input.Completion.IsCompleted)
                    {
                        break; // that's all, folks
                    }
                    WebSocketsFrame frame;
                    if (WebSocketProtocol.TryReadFrameHeader(ref buffer, out frame))
                    {
                        int payloadLength = frame.PayloadLength;
                        // buffer now points to the payload 
                        if (!frame.IsMasked)
                        {
                            throw new InvalidOperationException("Client-to-server frames should be masked");
                        }
                        if (frame.IsControlFrame && !frame.IsFinal)
                        {
                            throw new InvalidOperationException("Control frames cannot be fragmented");
                        }

                        await server.OnFrameReceivedAsync(this, ref frame, ref buffer);
                        // and finally, progress past the frame
                        if (payloadLength != 0) buffer = buffer.Slice(payloadLength);
                    }

                }
                finally
                {
                    buffer.Consumed(buffer.Start);
                }
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
        public bool HasBacklog => (backlog?.Count ?? 0) != 0;

        internal List<ReadableBuffer> GetBacklog() => backlog;

        public Task SendAsync(string message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            var msg = MessageWriter.Create(message);
            return SendAsync(WebSocketsFrame.OpCodes.Text, ref msg);
        }
        public Task SendAsync(byte[] message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            var msg = MessageWriter.Create(message);
            return SendAsync(WebSocketsFrame.OpCodes.Binary, ref msg);
        }
        public Task PingAsync(string message = null)
        {
            var msg = MessageWriter.Create(message);
            return SendAsync(WebSocketsFrame.OpCodes.Ping, ref msg);
        }
        public Task CloseAsync(string message = null)
        {
            CheckCanSend(); // seems quite likely to be in doubt here...
            var msg = MessageWriter.Create(message);
            return SendAsync(WebSocketsFrame.OpCodes.Ping, ref msg);
        }
        internal Task SendAsync(WebSocketsFrame.OpCodes opCode, ref Message message)
        {
            CheckCanSend();
            return SendAsyncImpl(opCode, message);
        }
        public bool IsClosed => isClosed;
        private volatile bool isClosed;
        internal void Close(Exception error = null)
        {
            isClosed = true;
            connection.Output.CompleteWriting(error);
            connection.Input.CompleteReading(error);
        }
        private void CheckCanSend()
        {
            if (isClosed) throw new InvalidOperationException();
        }


        internal Task SendAsync<T>(WebSocketsFrame.OpCodes opCode, ref T message) where T : struct, IMessageWriter
        {
            CheckCanSend();
            return SendAsyncImpl(opCode, message);
        }
        private async Task SendAsyncImpl<T>(WebSocketsFrame.OpCodes opCode, T message) where T : struct, IMessageWriter
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
                WebSocketServer.WriteStatus($"Writing {opCode} message...");
                await WebSocketProtocol.WriteAsync(this, opCode, ref message);
                if (opCode == WebSocketsFrame.OpCodes.Close) Close();
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
        // lazily instantiated SemaphoreSlim
        object writeLock;

        public void Dispose()
        {
            ((IDisposable)writeLock)?.Dispose();
            ClearBacklog();
        }
    }


}
