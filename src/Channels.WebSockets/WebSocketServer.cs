using Channels.Networking.Libuv;
using Channels.Text.Primitives;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Channels.WebSockets
{
    public abstract class WebSocketServer : IDisposable
    {
        private UvTcpListener listener;
        private UvThread thread;
        private IPAddress ip;
        private int port;
        public int Port => port;
        public IPAddress IP => ip;

        public bool BufferFragments { get; set; }
        public bool AllowClientsMissingConnectionHeaders { get; set; } = true; // stoopid browsers

        public void Dispose() => Dispose(true);
        ~WebSocketServer() { Dispose(false); }
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                GC.SuppressFinalize(this);
                Stop();
            }
        }
        public int ConnectionCount => connections.Count;

        public Task<int> CloseAllAsync(string message = null, Func<WebSocketConnection, bool> predicate = null)
        {
            if (connections.IsEmpty) return TaskResult.Zero; // avoid any processing
            return BroadcastAsync(WebSocketsFrame.OpCodes.Close, MessageWriter.Create(message, true), predicate);
        }
        public Task<int> BroadcastAsync(string message, Func<WebSocketConnection, bool> predicate = null)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            if (connections.IsEmpty) return TaskResult.Zero; // avoid any processing
            return BroadcastAsync(WebSocketsFrame.OpCodes.Text, MessageWriter.Create(message, true), predicate);
        }
        public Task<int> PingAsync(string message = null, Func<WebSocketConnection, bool> predicate = null)
        {
            if (connections.IsEmpty) return TaskResult.Zero; // avoid any processing
            return BroadcastAsync(WebSocketsFrame.OpCodes.Ping, MessageWriter.Create(message, true), predicate);
        }
        public Task<int> BroadcastAsync(byte[] message, Func<WebSocketConnection, bool> predicate = null)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            if (connections.IsEmpty) return TaskResult.Zero; // avoid any processing
            return BroadcastAsync(WebSocketsFrame.OpCodes.Binary, MessageWriter.Create(message), predicate);
        }
        private async Task<int> BroadcastAsync<T>(WebSocketsFrame.OpCodes opCode, T message, Func<WebSocketConnection, bool> predicate) where T : struct, IMessageWriter
        {
            int count = 0;
            foreach (var pair in connections)
            {
                var conn = pair.Key;
                if (!conn.IsClosed && (predicate == null || predicate(conn)))
                {
                    try
                    {
                        await conn.SendAsync<T>(opCode, ref message);
                        count++;
                    }
                    catch { } // not really all that bothered - they just won't get counted
                }
            }
            return count;
        }
        // todo: pick a more appropriate container for connection management; this insane choice is just arbitrary
        private ConcurrentDictionary<WebSocketConnection, WebSocketConnection> connections = new ConcurrentDictionary<WebSocketConnection, WebSocketConnection>();

        public void Start(IPAddress ip, int port)
        {
            if (listener == null)
            {
                thread = new UvThread();
                listener = new UvTcpListener(thread, new IPEndPoint(ip, port));
                listener.OnConnection(async connection =>
                {
                    WebSocketConnection socket = null;
                    try
                    {
                        WriteStatus("Connected");

                        WriteStatus("Parsing http request...");
                        var request = await ParseHttpRequest(connection.Input);
                        try
                        {
                            WriteStatus("Identifying protocol...");
                            socket = GetProtocol(connection, ref request);
                            WriteStatus($"Protocol: {socket.WebSocketProtocol.Name}");
                            WriteStatus("Authenticating...");
                            if (!await OnAuthenticateAsync(socket, ref request.Headers)) throw new InvalidOperationException("Authentication refused");
                            WriteStatus("Completing handshake...");
                            await socket.WebSocketProtocol.CompleteHandshakeAsync(ref request, socket);
                        }
                        finally
                        {
                            request.Dispose(); // can't use "ref request" or "ref headers" otherwise
                        }
                        WriteStatus("Handshake complete hook...");
                        await OnHandshakeCompleteAsync(socket);

                        connections.TryAdd(socket, socket);
                        WriteStatus("Processing incoming frames...");
                        await socket.ProcessIncomingFramesAsync(this);
                        WriteStatus("Exiting...");
                        socket.Close();
                    }
                    catch (Exception ex)
                    {// meh, bye bye broken connection
                        try { socket?.Close(ex); } catch { }
                        WriteStatus(ex.StackTrace);
                        WriteStatus(ex.GetType().Name);
                        WriteStatus(ex.Message);
                    }
                    finally
                    {
                        WebSocketConnection tmp;
                        if (socket != null) connections.TryRemove(socket, out tmp);
                        try { connection.Output.CompleteWriting(); } catch { }
                        try { connection.Input.CompleteReading(); } catch { }
                    }
                });
                listener.Start();
            }
        }

        [Conditional("LOGGING")]
        internal static void WriteStatus(string message)
        {
#if LOGGING
            Console.WriteLine($"[Server:{Environment.CurrentManagedThreadId}]: {message}");
#endif
        }
        internal interface IMessageWriter
        {
            void Write(ref WritableBuffer buffer);
            int GetTotalBytes();
        }

        struct StringMessageWriter : IMessageWriter
        {
            private string value;
            private int offset, count, totalBytes;
            public StringMessageWriter(string value, int offset, int count, bool preComputeLength)
            {
                if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
                if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
                if (offset + count > (value?.Length ?? 0)) throw new ArgumentOutOfRangeException(nameof(count));

                this.value = value;
                this.offset = offset;
                this.count = count;
                this.totalBytes = count == 0 ? 0 : -1;
                if (preComputeLength) GetTotalBytes();
            }
#if ENCODING_POINTER_API
            // avoid allocating Encoder instances all the time
            static Encoder sharedEncoder;
#endif
            unsafe void IMessageWriter.Write(ref WritableBuffer buffer)
            {
                int expected = GetTotalBytes();
                if (expected == 0) return;
                

                var memory = buffer.Memory;
                byte* dest = (byte*)memory.BufferPtr;
                int actual;
                fixed (char* chars = value)
                {
#if ENCODING_POINTER_API
                    var enc = Interlocked.Exchange(ref sharedEncoder, null);
                    if (enc == null) enc = encoding.GetEncoder(); // need a new one      
                    actual = enc.GetBytes(chars + offset, count, dest, expected, true);
                    Interlocked.CompareExchange(ref sharedEncoder, enc, null); // make the encoder available  for re-use
#else
                    actual = encoding.GetBytes(chars + offset, count, dest, expected);
#endif
                }
                if (actual != expected)
                {
                    throw new InvalidOperationException($"Unexpected length in {nameof(StringMessageWriter)}.{nameof(IMessageWriter.Write)}");
                }
                buffer.CommitBytes(actual);

                
            }

            public unsafe int GetTotalBytes()
            {
                if (totalBytes >= 0) return totalBytes;
                fixed (char* chars = value)
                {
                    return totalBytes = encoding.GetByteCount(chars + offset, count);
                }
            }
            static readonly Encoding encoding = Encoding.UTF8;
        }

        static class MessageWriter
        {
            internal static ByteArrayMessageWriter Create(byte[] message)
            {
                return (message == null || message.Length == 0) ? default(ByteArrayMessageWriter) : new ByteArrayMessageWriter(message, 0, message.Length);
            }
            internal static StringMessageWriter Create(string message, bool computeLength = false)
            {
                return string.IsNullOrEmpty(message) ? default(StringMessageWriter) : new StringMessageWriter(message, 0, message.Length, computeLength);
            }
        }
        struct ByteArrayMessageWriter : IMessageWriter
        {
            private byte[] value;
            private int offset, count;
            public ByteArrayMessageWriter(byte[] value, int offset, int count)
            {
                if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
                if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
                if (offset + count > (value?.Length ?? 0)) throw new ArgumentOutOfRangeException(nameof(count));

                this.value = value;
                this.offset = offset;
                this.count = count;
            }

            unsafe void IMessageWriter.Write(ref WritableBuffer buffer)
            {
                if (count != 0) buffer.Write(value, offset, count);
            }

            unsafe int IMessageWriter.GetTotalBytes() => count;
        }

        public class WebSocketConnection : IDisposable
        {


            private UvTcpServerConnection connection;
            internal UvTcpServerConnection Connection => connection;
            internal WebSocketConnection(UvTcpServerConnection connection)
            {
                this.connection = connection;
            }

            public string Host { get; internal set; }
            public string Origin { get; internal set; }
            public string Protocol { get; internal set; }
            public string RequestLine { get; internal set; }
            internal WebSocketProtocol WebSocketProtocol { get; set; }

            internal async Task ProcessIncomingFramesAsync(WebSocketServer server)
            {
                while(!IsClosed)
                {
                    ReadableBuffer buffer = await connection.Input;
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
                if(backlog == null)
                {
                    var newBacklog = new List<ReadableBuffer>();
                    backlog = Interlocked.CompareExchange(ref this.backlog, newBacklog, null) ?? newBacklog;
                }
                backlog.Add(slicedBuffer.Preserve());
            }
            internal void ClearBacklog()
            {
                var backlog = this.backlog;
                if(backlog != null)
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
                try
                {
                    connection.Output.CompleteWriting(error);
                    connection.Input.CompleteReading(error);
                }
                finally { }
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
                    WriteStatus($"Writing {opCode} message...");
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


        private Task OnFrameReceivedAsync(WebSocketConnection connection, ref WebSocketsFrame frame, ref ReadableBuffer buffer)
        {
            WriteStatus(frame.ToString());


            // note that this call updates the connection state; must be called, even
            // if we don't get as far as the 'switch' 
            var opCode = connection.GetEffectiveOpCode(ref frame);

            Message msg;
            if (!frame.IsControlFrame)
            {
                if (frame.IsFinal)
                {
                    if (connection.HasBacklog)
                    {
                        try
                        {
                            // add our data to the existing backlog
                            connection.AddBacklog(ref buffer, ref frame);

                            // use the backlog buffer to execute the method; note that
                            // we un-masked *while adding*; don't need mask here
                            msg = new Message(connection.GetBacklog());
                            return OnFrameReceivedImplAsync(connection, opCode, ref msg);                            
                        }
                        finally
                        {
                            // and release the backlog
                            connection.ClearBacklog();
                        }
                    }
                }
                else if (BufferFragments)
                {
                    // need to buffer this data against the connection
                    connection.AddBacklog(ref buffer, ref frame);
                    return TaskResult.True;
                }
            }
            msg = new Message(buffer.Slice(0, frame.PayloadLength), frame.Mask, frame.IsFinal);
            return OnFrameReceivedImplAsync(connection, opCode, ref msg);
        }
        private Task OnFrameReceivedImplAsync(WebSocketConnection connection, WebSocketsFrame.OpCodes opCode, ref Message message)
        {
            switch (opCode)
            {
                case WebSocketsFrame.OpCodes.Binary:
                    return OnBinaryAsync(connection, ref message);
                case WebSocketsFrame.OpCodes.Text:
                    return OnTextAsync(connection, ref message);
                case WebSocketsFrame.OpCodes.Close:
                    return OnCloseAsync(connection, ref message);
                case WebSocketsFrame.OpCodes.Ping:
                    return OnPingAsync(connection, ref message);
                case WebSocketsFrame.OpCodes.Pong:
                    return OnPongAsync(connection, ref message);
                default:
                    return TaskResult.True;
            }
        }
        protected virtual Task OnCloseAsync(WebSocketConnection connection, ref Message message)
        {
            // respond to a close in-kind (2-handed close)
            return connection.SendAsync(WebSocketsFrame.OpCodes.Close, ref message);
        }
        protected virtual Task OnPongAsync(WebSocketConnection connection, ref Message message) => TaskResult.True;
        protected virtual Task OnPingAsync(WebSocketConnection connection, ref Message message)
        {
            // by default, respond to a ping with a matching pong
            return connection.SendAsync(WebSocketsFrame.OpCodes.Pong, ref message); // right back at you
        }
        protected virtual Task OnBinaryAsync(WebSocketConnection connection, ref Message message) => TaskResult.True;
        protected virtual Task OnTextAsync(WebSocketConnection connection, ref Message message) => TaskResult.True;

        public struct Message : IMessageWriter
        {
            private ReadableBuffer buffer;
            private List<ReadableBuffer> buffers;
            private int mask;
            private string text;
            public bool IsFinal { get; }
            internal Message(ReadableBuffer buffer, int mask, bool isFinal)
            {
                this.buffer = buffer;
                this.mask = mask;
                text = null;
                IsFinal = isFinal;
                buffers = null;
            }
            internal Message(List<ReadableBuffer> buffers)
            {
                mask = 0;
                text = null;
                IsFinal = true;
                if (buffers.Count == 1) // can simplify
                {
                    buffer = buffers[0];
                    this.buffers = null;
                }
                else
                {
                    buffer = default(ReadableBuffer);
                    this.buffers = buffers;
                }
            }
            private void ApplyMask()
            {
                if (mask != 0)
                {
                    WebSocketsFrame.ApplyMask(ref buffer, mask);
                    mask = 0;
                }
            }
            public override string ToString() => GetText();
            public string GetText()
            {
                if (text != null) return text;

                var buffers = this.buffers;
                if (buffers == null)
                {
                    if (buffer.Length == 0) return text = "";

                    ApplyMask();
                    return text = buffer.GetUtf8String();
                }
                return text = GetText(buffers);
            }

            private static readonly Encoding Utf8Encoding = Encoding.UTF8;
            private static Decoder Utf8Decoder;

            private static string GetText(List<ReadableBuffer> buffers)
            {
                // try to re-use a shared decoder; note that in heavy usage, we might need to allocate another
                var decoder = (Decoder)Interlocked.Exchange<Decoder>(ref Utf8Decoder, null);
                if (decoder == null) decoder = Utf8Encoding.GetDecoder();
                else decoder.Reset();

                var length = 0;
                foreach (var buffer in buffers) length += buffer.Length;

                var charLength = length; // worst case is 1 byte per char
                var chars = new char[charLength];
                var charIndex = 0;

                int bytesUsed = 0;
                int charsUsed = 0;
                bool completed;
                foreach (var buffer in buffers)
                {
                    foreach (var span in buffer)
                    {
                        decoder.Convert(
                            span.Array,
                            span.Offset,
                            span.Length,
                            chars,
                            charIndex,
                            charLength - charIndex,
                            false, // a single character could span two spans
                            out bytesUsed,
                            out charsUsed,
                            out completed);

                        charIndex += charsUsed;
                    }
                }
                // make the decoder available for re-use
                Interlocked.CompareExchange<Decoder>(ref Utf8Decoder, decoder, null);
                return new string(chars, 0, charIndex);
            }

            public byte[] GetBytes()
            {
                int len = GetTotalBytes();
                if (len == 0) return NilBytes;

                ApplyMask();
                return buffer.ToArray();
            }
            public int GetTotalBytes()
            {
                var buffers = this.buffers;
                if (buffers == null) return buffer.Length;
                int count = 0;
                foreach (var buffer in buffers) count += buffer.Length;
                return count;
            }

            void IMessageWriter.Write(ref WritableBuffer destination)
            {
                var buffers = this.buffers;
                if (buffers == null)
                {
                    ApplyMask();
                    destination.Append(ref buffer);
                }
                else
                {
                    // all this because C# doesn't let you use "ref" with an iterator variable
                    using (var iter = buffers.GetEnumerator())
                    {
                        ReadableBuffer tmp;
                        while(iter.MoveNext())
                        {
                            tmp = iter.Current;
                            destination.Append(ref tmp);
                        }
                    }
                }
                
            }
        }
        private static readonly byte[] NilBytes = new byte[0];



        static readonly char[] Comma = { ',' };
        protected static class TaskResult
        {
            public static readonly Task<bool>
                True = Task.FromResult(true),
                False = Task.FromResult(false);
            public static readonly Task<int> Zero = Task.FromResult(0);
        }

        protected virtual Task<bool> OnAuthenticateAsync(WebSocketConnection connection, ref HttpRequestHeaders headers) => TaskResult.True;
        protected virtual Task OnHandshakeCompleteAsync(WebSocketConnection connection) => TaskResult.True;

        private WebSocketConnection GetProtocol(UvTcpServerConnection connection, ref HttpRequest request)
        {
            var headers = request.Headers;
            string host = headers.GetAscii("Host");
            if (string.IsNullOrEmpty(host))
            {
                //4.   The request MUST contain a |Host| header field whose value
                //contains /host/ plus optionally ":" followed by /port/ (when not
                //using the default port).
                throw new InvalidOperationException("host required");
            }

            bool looksGoodEnough = false;
            // mozilla sends "keep-alive, Upgrade"; let's make it more forgiving
            var connectionParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (headers.ContainsKey("Connection"))
            {
                // so for mozilla, this will be the set {"keep-alive", "Upgrade"}
                var parts = headers.GetAscii("Connection").Split(Comma);
                foreach (var part in parts) connectionParts.Add(part.Trim());
            }
            if (connectionParts.Contains("Upgrade") && IsCaseInsensitiveAsciiMatch(headers.GetRaw("Upgrade"), "websocket"))
            {
                //5.   The request MUST contain an |Upgrade| header field whose value
                //MUST include the "websocket" keyword.
                //6.   The request MUST contain a |Connection| header field whose value
                //MUST include the "Upgrade" token.
                looksGoodEnough = true;
            }

            if (!looksGoodEnough && AllowClientsMissingConnectionHeaders)
            {
                if ((headers.ContainsKey("Sec-WebSocket-Version") && headers.ContainsKey("Sec-WebSocket-Key"))
                    || (headers.ContainsKey("Sec-WebSocket-Key1") && headers.ContainsKey("Sec-WebSocket-Key2")))
                {
                    looksGoodEnough = true;
                }
            }

            WebSocketProtocol protocol;
            if (looksGoodEnough)
            {
                //9.   The request MUST include a header field with the name
                //|Sec-WebSocket-Version|.  The value of this header field MUST be

                if (!headers.ContainsKey("Sec-WebSocket-Version"))
                {
                    if (headers.ContainsKey("Sec-WebSocket-Key1") && headers.ContainsKey("Sec-WebSocket-Key2"))
                    { // smells like hixie-76/hybi-00
                        protocol = WebSocketProtocol.Hixie76_00;
                    }
                    else
                    {
                        throw new NotSupportedException();
                    }
                }
                else
                {
                    var version = headers.GetRaw("Sec-WebSocket-Version").GetUInt32();
                    switch (version)
                    {

                        case 4:
                        case 5:
                        case 6:
                        case 7:
                        case 8: // these are all early drafts
                        case 13: // this is later drafts and RFC6455
                            protocol = WebSocketProtocol.RFC6455_13;
                            break;
                        default:
                            // should issues a 400 "upgrade required" and specify Sec-WebSocket-Version - see 4.4
                            throw new InvalidOperationException(string.Format("Sec-WebSocket-Version {0} is not supported", version));
                    }
                }
            }
            else
            {
                throw new InvalidOperationException("Request was not a web-socket upgrade request");
            }
            //The "Request-URI" of the GET method [RFC2616] is used to identify the
            //endpoint of the WebSocket connection, both to allow multiple domains
            //to be served from one IP address and to allow multiple WebSocket
            //endpoints to be served by a single server.
            var socket = new WebSocketConnection(connection);
            socket.Host = host;
            // Some early drafts used the latter, so we'll allow it as a fallback
            // in particular, two drafts of version "8" used (separately) **both**,
            // so we can't rely on the version for this (hybi-10 vs hybi-11).
            // To make it even worse, hybi-00 used Origin, so it is all over the place!
            socket.Origin = headers.GetAscii("Origin") ?? headers.GetAscii("Sec-WebSocket-Origin");
            socket.Protocol = headers.GetAscii("Sec-WebSocket-Protocol");
            socket.RequestLine = request.Path.GetAsciiString();
            socket.WebSocketProtocol = protocol;
            return socket;
        }
        public struct WebSocketsFrame
        {
            public override string ToString()
            {
                return OpCode.ToString() + ": " + PayloadLength.ToString() + " bytes (" + Flags.ToString() + ")";
            }
            private readonly byte header;
            private readonly byte header2;
            [Flags]
            public enum FrameFlags : byte
            {
                IsFinal = 128,
                Reserved1 = 64,
                Reserved2 = 32,
                Reserved3 = 16,
                None = 0
            }
            public enum OpCodes : byte
            {
                Continuation = 0,
                Text = 1,
                Binary = 2,
                // 3-7 reserved for non-control op-codes
                Close = 8,
                Ping = 9,
                Pong = 10,
                // 11-15 reserved for control op-codes
            }
            public WebSocketsFrame(byte header, bool isMasked, int mask, int payloadLength)
            {
                this.header = header;
                header2 = (byte)(isMasked ? 1 : 0);
                PayloadLength = payloadLength;
                Mask = isMasked ? mask : 0;
            }
            public bool IsMasked => (header2 & 1) != 0;
            private bool HasFlag(FrameFlags flag) => (header & (byte)flag) != 0;

            internal unsafe static void ApplyMask(ref ReadableBuffer buffer, int mask)
            {
                if (mask == 0) return;
                ulong mask8 = (uint)mask;
                mask8 = (mask8 << 32) | mask8;

                foreach (var span in buffer)
                {
                    int len = span.Length;

                    if ((len & ~7) != 0) // >= 8
                    {
                        var ptr = (ulong*)span.BufferPtr;
                        do
                        {
                            (*ptr++) ^= mask8;
                            len -= 8;
                        } while ((len & ~7) != 0); // >= 8
                    }
                    // TODO: worth doing an int32 mask here if >= 4?
                    if (len != 0)
                    {
                        var ptr = ((byte*)span.BufferPtr) + (buffer.Length & ~7); // forwards everything except the last chunk
                        do
                        {
                            var b = (byte)(mask8 & 255);
                            (*ptr++) ^= b;
                            // rotate the mask (need to preserve LHS in case we have another span)
                            mask8 = (mask8 >> 8) | (((ulong)b) << 56);
                            len--;
                        } while (len != 0);
                    }
                }
            }


            public bool IsControlFrame { get { return (header & (byte)OpCodes.Close) != 0; } }
            public int Mask { get; }
            public OpCodes OpCode => (OpCodes)(header & 15);
            public FrameFlags Flags => (FrameFlags)(header & ~15);
            public bool IsFinal { get { return HasFlag(FrameFlags.IsFinal); } }
            public bool Reserved1 { get { return HasFlag(FrameFlags.Reserved1); } }
            public bool Reserved2 { get { return HasFlag(FrameFlags.Reserved2); } }
            public bool Reserved3 { get { return HasFlag(FrameFlags.Reserved3); } }

            public int PayloadLength { get; }
            internal const int MaxHeaderLength = 14;
        }
        internal abstract class WebSocketProtocol
        {
            internal static readonly WebSocketProtocol RFC6455_13 = new WebSocketProtocol_RFC6455_13(), Hixie76_00 = new WebSocketProtocol_Hixie76_00();

            public abstract string Name { get; }

            class WebSocketProtocol_RFC6455_13 : WebSocketProtocol
            {
                public override string Name => "RFC6455";
                static readonly byte[]
                    StandardPrefixBytes = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\n"
                                    + "Upgrade: websocket\r\n"
                                    + "Connection: Upgrade\r\n"
                                    + "Sec-WebSocket-Accept: "),
                    StandardPostfixBytes = Encoding.ASCII.GetBytes("\r\n\r\n");
                internal override Task CompleteHandshakeAsync(ref HttpRequest request, WebSocketConnection socket)
                {
                    var key = request.Headers.GetRaw("Sec-WebSocket-Key");

                    var connection = socket.Connection;

                    const int ResponseTokenLength = 28;

                    var buffer = connection.Output.Alloc(StandardPrefixBytes.Length +
                        ResponseTokenLength + StandardPostfixBytes.Length);
                    string hashBase64 = ComputeReply(key, buffer.Memory);
                    if (hashBase64.Length != ResponseTokenLength) throw new InvalidOperationException("Unexpected response key length");
                    WebSocketServer.WriteStatus($"Response token: {hashBase64}");

                    buffer.Write(StandardPrefixBytes, 0, StandardPrefixBytes.Length);
                    buffer.CommitBytes(Encoding.ASCII.GetBytes(hashBase64, 0, hashBase64.Length, buffer.Memory.Array, buffer.Memory.Offset));
                    buffer.Write(StandardPostfixBytes, 0, StandardPostfixBytes.Length);

                    return buffer.FlushAsync();
                }
                static readonly byte[] WebSocketKeySuffixBytes = Encoding.ASCII.GetBytes("258EAFA5-E914-47DA-95CA-C5AB0DC85B11");

                static bool IsBase64(byte value)
                {
                    return (value >= (byte)'0' && value <= (byte)'9')
                        || (value >= (byte)'a' && value <= (byte)'z')
                        || (value >= (byte)'A' && value <= (byte)'Z')
                        || (value == (byte)'/')
                        || (value == (byte)'+')
                        || (value == (byte)'=');
                }
                internal static string ComputeReply(ReadableBuffer key, BufferSpan buffer)
                {
                    //To prove that the handshake was received, the server has to take two
                    //pieces of information and combine them to form a response.  The first
                    //piece of information comes from the |Sec-WebSocket-Key| header field
                    //in the client handshake:

                    //     Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==

                    //For this header field, the server has to take the value (as present
                    //in the header field, e.g., the base64-encoded [RFC4648] version minus
                    //any leading and trailing whitespace) and concatenate this with the
                    //Globally Unique Identifier (GUID, [RFC4122]) "258EAFA5-E914-47DA-
                    //95CA-C5AB0DC85B11" in string form, which is unlikely to be used by
                    //network endpoints that do not understand the WebSocket Protocol.  A
                    //SHA-1 hash (160 bits) [FIPS.180-3], base64-encoded (see Section 4 of
                    //[RFC4648]), of this concatenation is then returned in the server's
                    //handshake.

                    const int ExpectedKeyLength = 24;

                    int len = key.Length, start = 0, end = len, baseOffset = buffer.Offset;
                    if (len < ExpectedKeyLength) throw new ArgumentException("Undersized key", nameof(key));
                    byte[] arr = buffer.Array;
                    // note that it might be padded; if so we'll need to trim - allow some slack
                    if ((len + WebSocketKeySuffixBytes.Length) > buffer.Length) throw new ArgumentException("Oversized key", nameof(key));
                    // in-place "trim" to find the base-64 piece
                    key.CopyTo(arr, baseOffset);
                    for (int i = 0; i < len; i++)
                    {
                        if (IsBase64(arr[baseOffset + i])) break;
                        start++;
                    }
                    for (int i = len - 1; i >= 0; i--)
                    {
                        if (IsBase64(arr[baseOffset + i])) break;
                        end--;
                    }

                    if ((end - start) != ExpectedKeyLength) throw new ArgumentException(nameof(key));

                    // append the suffix
                    Buffer.BlockCopy(WebSocketKeySuffixBytes, 0, arr, baseOffset + end, WebSocketKeySuffixBytes.Length);

                    // compute the hash
                    using (var sha = SHA1.Create())
                    {
                        var hash = sha.ComputeHash(arr, baseOffset + start,
                            ExpectedKeyLength + WebSocketKeySuffixBytes.Length);
                        return Convert.ToBase64String(hash);
                    }
                }
                protected internal static unsafe int ReadBigEndianInt32(byte* buffer, int offset)
                {
                    return (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];
                }
                protected internal static unsafe int ReadLittleEndianInt32(byte* buffer, int offset)
                {
                    return (buffer[offset]) | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24);
                }


                internal static unsafe void WriteFrameHeader(ref WritableBuffer output, WebSocketsFrame.FrameFlags flags, WebSocketsFrame.OpCodes opCode, int payloadLength, int mask)
                {
                    byte* buffer = (byte*)output.Memory.BufferPtr;

                    int bytesWritten;
                    *buffer++ = (byte)(((int)flags & 240) | ((int)opCode & 15));
                    if (payloadLength > ushort.MaxValue)
                    { // write as a 64-bit length
                        *buffer++ = (byte)((mask != 0 ? 128 : 0) | 127);
                        *buffer++ = 0;
                        *buffer++ = 0;
                        *buffer++ = 0;
                        *buffer++ = 0;
                        *buffer++ = (byte)(payloadLength >> 24);
                        *buffer++ = (byte)(payloadLength >> 16);
                        *buffer++ = (byte)(payloadLength >> 8);
                        *buffer++ = (byte)(payloadLength);
                        bytesWritten = 10;
                    }
                    else if (payloadLength > 125)
                    { // write as a 16-bit length
                        *buffer++ = (byte)((mask != 0 ? 128 : 0) | 126);
                        *buffer++ = (byte)(payloadLength >> 8);
                        *buffer++ = (byte)(payloadLength);
                        bytesWritten = 4;
                    }
                    else
                    { // write in the header
                        *buffer++ = (byte)((mask != 0 ? 128 : 0) | payloadLength);
                        bytesWritten = 2;
                    }
                    if (mask != 0)
                    {
                        *buffer++ = (byte)(mask >> 24);
                        *buffer++ = (byte)(mask >> 16);
                        *buffer++ = (byte)(mask >> 8);
                        *buffer++ = (byte)(mask);
                        bytesWritten += 4;
                    }
                    output.CommitBytes(bytesWritten);
                }
                internal unsafe override bool TryReadFrameHeader(ref ReadableBuffer buffer, out WebSocketsFrame frame)
                {
                    int bytesAvailable = buffer.Length;
                    if (bytesAvailable < 2)
                    {
                        frame = default(WebSocketsFrame);
                        return false; // can't read that; frame takes at minimum two bytes
                    }

                    var span = buffer.FirstSpan;
                    if (span.Length >= 14)
                    {
                        return TryReadFrameHeader(bytesAvailable, (byte*)span.BufferPtr, ref buffer, out frame);
                    }
                    else
                    {
                        return TryReadFrameHeaderMultiSpan(ref buffer, out frame);
                    }
                }
                internal unsafe bool TryReadFrameHeaderMultiSpan(ref ReadableBuffer buffer, out WebSocketsFrame frame)
                {
                    // header is at most 14 bytes; can afford the stack for that - but note that if we aim for 16 bytes instead,
                    // we will usually benefit from using 2 qword copies (handled internally); very very small messages ('a') might
                    // have to use the slower version, but... meh
                    byte* header = stackalloc byte[16];
                    int bytesAvailable = SlowCopyFirst(buffer, header, 16);
                    return TryReadFrameHeader(bytesAvailable, header, ref buffer, out frame);
                }
                internal unsafe bool TryReadFrameHeader(int bytesAvailable, byte* header, ref ReadableBuffer buffer, out WebSocketsFrame frame)
                {
                    bool masked = (header[1] & 128) != 0;
                    int tmp = header[1] & 127;
                    int headerLength, maskOffset, payloadLength;
                    switch (tmp)
                    {
                        case 126:
                            headerLength = masked ? 8 : 4;
                            if (bytesAvailable < headerLength)
                            {
                                frame = default(WebSocketsFrame);
                                return false;
                            }
                            payloadLength = (header[2] << 8) | header[3];
                            maskOffset = 4;
                            break;
                        case 127:
                            headerLength = masked ? 14 : 10;
                            if (bytesAvailable < headerLength)
                            {
                                frame = default(WebSocketsFrame);
                                return false;
                            }
                            int big = ReadBigEndianInt32(header, 2), little = ReadBigEndianInt32(header, 6);
                            if (big != 0 || little < 0) throw new ArgumentOutOfRangeException(); // seriously, we're not going > 2GB
                            payloadLength = little;
                            maskOffset = 10;
                            break;
                        default:
                            headerLength = masked ? 6 : 2;
                            if (bytesAvailable < headerLength)
                            {
                                frame = default(WebSocketsFrame);
                                return false;
                            }
                            payloadLength = tmp;
                            maskOffset = 2;
                            break;
                    }
                    if (bytesAvailable < headerLength + payloadLength)
                    {
                        frame = default(WebSocketsFrame);
                        return false; // body isn't intact
                    }


                    frame = new WebSocketsFrame(header[0], masked,
                        masked ? ReadLittleEndianInt32(header, maskOffset) : 0,
                        payloadLength);
                    buffer = buffer.Slice(headerLength); // header is fully consumed now
                    return true;
                }

                private unsafe int SlowCopyFirst(ReadableBuffer buffer, byte* destination, uint bytes)
                {
                    if (bytes == 0) return 0;
                    if (buffer.IsSingleSpan)
                    {
                        var span = buffer.FirstSpan;
                        uint batch = Math.Min((uint)span.Length, bytes);
                        Copy((byte*)span.BufferPtr, destination, batch);
                        return (int)batch;
                    }
                    else
                    {
                        uint copied = 0;
                        foreach (var span in buffer)
                        {
                            uint batch = Math.Min((uint)span.Length, bytes);
                            Copy((byte*)span.BufferPtr, destination, batch);
                            destination += batch;
                            copied += batch;
                            bytes -= batch;
                            if (bytes == 0) break;
                        }
                        return (int)copied;
                    }
                }
                internal override Task WriteAsync<T>(WebSocketConnection connection, WebSocketsFrame.OpCodes opCode, ref T message)
                {
                    int payloadLength = message.GetTotalBytes();
                    var buffer = connection.Connection.Output.Alloc(WebSocketsFrame.MaxHeaderLength + payloadLength);
                    WriteFrameHeader(ref buffer, WebSocketsFrame.FrameFlags.IsFinal, opCode, payloadLength, 0);
                    if (payloadLength != 0) message.Write(ref buffer);
                    return buffer.FlushAsync();
                }
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static unsafe void Copy(byte* source, byte* destination, uint bytes)
            {
                if ((bytes & ~7) != 0) // >= 8
                {
                    ulong* source8 = (ulong*)source, destination8 = (ulong*)destination;
                    do
                    {
                        *destination8++ = *source8++;
                        bytes -= 8;
                    } while ((bytes & ~7) != 0); // >= 8
                    source = (byte*)source8;
                    destination = (byte*)destination8;
                }
                while (bytes-- != 0)
                {
                    *destination++ = *source++;
                }
            }
            class WebSocketProtocol_Hixie76_00 : WebSocketProtocol
            {
                public override string Name => "Hixie76";
                internal override Task CompleteHandshakeAsync(ref HttpRequest request, WebSocketConnection socket)
                {
                    throw new NotImplementedException();
                }
                internal override bool TryReadFrameHeader(ref ReadableBuffer buffer, out WebSocketsFrame frame)
                {
                    throw new NotImplementedException();
                }
                internal override Task WriteAsync<T>(WebSocketConnection connection, WebSocketsFrame.OpCodes opCode, ref T message)
                {
                    throw new NotImplementedException();
                }
            }

            internal abstract Task CompleteHandshakeAsync(ref HttpRequest request, WebSocketConnection socket);

            internal abstract bool TryReadFrameHeader(ref ReadableBuffer buffer, out WebSocketsFrame frame);

            internal abstract Task WriteAsync<T>(WebSocketConnection connection, WebSocketsFrame.OpCodes opCode, ref T message) where T : struct, IMessageWriter;
        }

        internal struct HttpRequest : IDisposable
        {
            public void Dispose()
            {
                Method.Dispose();
                Path.Dispose();
                HttpVersion.Dispose();
                Headers.Dispose();
                Method = Path = HttpVersion = default(ReadableBuffer);
                Headers = default(HttpRequestHeaders);
            }
            public ReadableBuffer Method { get; private set; }
            public ReadableBuffer Path { get; private set; }
            public ReadableBuffer HttpVersion { get; private set; }

            public HttpRequestHeaders Headers; // yes, naked field - internal type, so not too exposed; allows for "ref" without copy

            public HttpRequest(ReadableBuffer method, ReadableBuffer path, ReadableBuffer httpVersion, Dictionary<string, ReadableBuffer> headers)
            {
                Method = method;
                Path = path;
                HttpVersion = httpVersion;
                Headers = new HttpRequestHeaders(headers);
            }
        }
        public struct HttpRequestHeaders : IEnumerable<KeyValuePair<string, ReadableBuffer>>, IDisposable
        {
            private Dictionary<string, ReadableBuffer> headers;
            public void Dispose()
            {
                if (headers != null)
                {
                    foreach (var pair in headers)
                        pair.Value.Dispose();
                }
                headers = null;
            }
            public HttpRequestHeaders(Dictionary<string, ReadableBuffer> headers)
            {
                this.headers = headers;
            }
            public bool ContainsKey(string key) => headers.ContainsKey(key);
            IEnumerator<KeyValuePair<string, ReadableBuffer>> IEnumerable<KeyValuePair<string, ReadableBuffer>>.GetEnumerator() => ((IEnumerable<KeyValuePair<string, ReadableBuffer>>)headers).GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)headers).GetEnumerator();
            public Dictionary<string, ReadableBuffer>.Enumerator GetEnumerator() => headers.GetEnumerator();

            public string GetAscii(string key)
            {
                ReadableBuffer buffer;
                if (headers.TryGetValue(key, out buffer)) return buffer.GetAsciiString();
                return null;
            }
            internal ReadableBuffer GetRaw(string key)
            {
                ReadableBuffer buffer;
                if (headers.TryGetValue(key, out buffer)) return buffer;
                return default(ReadableBuffer);
            }

        }
        private enum ParsingState
        {
            StartLine,
            Headers
        }

        private static Vector<byte>
            _vectorCRs = new Vector<byte>((byte)'\r'),
            _vectorLFs = new Vector<byte>((byte)'\n'),
            _vectorSpaces = new Vector<byte>((byte)' '),
            _vectorColons = new Vector<byte>((byte)':');
        private static async Task<HttpRequest> ParseHttpRequest(IReadableChannel _input)
        {
            ReadableBuffer Method = default(ReadableBuffer), Path = default(ReadableBuffer), HttpVersion = default(ReadableBuffer);
            Dictionary<string, ReadableBuffer> Headers = new Dictionary<string, ReadableBuffer>();
            try
            {
                ParsingState _state = ParsingState.StartLine;
                bool needMoreData = true;
                while (needMoreData)
                {
                    var buffer = await _input;

                    var consumed = buffer.Start;
                    needMoreData = true;

                    try
                    {
                        if (buffer.IsEmpty && _input.Completion.IsCompleted)
                        {
                            throw new EndOfStreamException();
                        }

                        if (_state == ParsingState.StartLine)
                        {
                            // Find \n
                            var delim = buffer.IndexOf(ref _vectorLFs);
                            if (delim.IsEnd)
                            {
                                continue;
                            }

                            // Grab the entire start line
                            var startLine = buffer.Slice(0, delim);

                            // Move the buffer to the rest
                            buffer = buffer.Slice(delim).Slice(1);

                            delim = startLine.IndexOf(ref _vectorSpaces);
                            if (delim.IsEnd)
                            {
                                throw new Exception();
                            }

                            var method = startLine.Slice(0, delim);
                            Method = method.Preserve();

                            // Skip ' '
                            startLine = startLine.Slice(delim).Slice(1);

                            delim = startLine.IndexOf(ref _vectorSpaces);
                            if (delim.IsEnd)
                            {
                                throw new Exception();
                            }

                            var path = startLine.Slice(0, delim);
                            Path = path.Preserve();

                            // Skip ' '
                            startLine = startLine.Slice(delim).Slice(1);

                            delim = startLine.IndexOf(ref _vectorCRs);
                            if (delim.IsEnd)
                            {
                                throw new Exception();
                            }

                            var httpVersion = startLine.Slice(0, delim);
                            HttpVersion = httpVersion.Preserve();

                            _state = ParsingState.Headers;
                            consumed = startLine.End;
                        }

                        // Parse headers
                        // key: value\r\n

                        while (!buffer.IsEmpty)
                        {
                            var ch = buffer.Peek();

                            if (ch == -1)
                            {
                                break;
                            }

                            if (ch == '\r')
                            {
                                // Check for final CRLF.
                                buffer = buffer.Slice(1);
                                ch = buffer.Peek();
                                buffer = buffer.Slice(1);

                                if (ch == -1)
                                {
                                    break;
                                }
                                else if (ch == '\n')
                                {
                                    consumed = buffer.Start;
                                    needMoreData = false;
                                    break;
                                }

                                // Headers don't end in CRLF line.
                                throw new Exception();
                            }

                            var headerName = default(ReadableBuffer);
                            var headerValue = default(ReadableBuffer);

                            // End of the header
                            // \n
                            var delim = buffer.IndexOf(ref _vectorLFs);
                            if (delim.IsEnd)
                            {
                                break;
                            }

                            var headerPair = buffer.Slice(0, delim);
                            buffer = buffer.Slice(delim).Slice(1);

                            // :
                            delim = headerPair.IndexOf(ref _vectorColons);
                            if (delim.IsEnd)
                            {
                                throw new Exception();
                            }
                            headerName = headerPair.Slice(0, delim).TrimStart();
                            headerPair = headerPair.Slice(delim).Slice(1);

                            // \r
                            delim = headerPair.IndexOf(ref _vectorCRs);
                            if (delim.IsEnd)
                            {
                                // Bad request
                                throw new Exception();
                            }

                            headerValue = headerPair.Slice(0, delim).TrimStart();

                            Headers[ToHeaderKey(ref headerName)] = headerValue.Preserve();

                            // Move the consumed
                            consumed = buffer.Start;
                        }
                    }
                    finally
                    {
                        buffer.Consumed(consumed);
                    }
                }
                var result = new HttpRequest(Method, Path, HttpVersion, Headers);
                Method = Path = HttpVersion = default(ReadableBuffer);
                Headers = null;
                return result;
            }
            finally
            {
                Method.Dispose();
                Path.Dispose();
                HttpVersion.Dispose();
                if (Headers != null)
                {
                    foreach (var pair in Headers)
                        pair.Value.Dispose();
                }
            }
        }

        static readonly string[] CommonHeaders = new string[]
        {
            "Accept",
            "Accept-Encoding",
            "Accept-Language",
            "Cache-Control",
            "Connection",
            "Cookie",
            "Host",
            "Origin",
            "Pragma",
            "Sec-WebSocket-Extensions",
            "Sec-WebSocket-Key",
            "Sec-WebSocket-Key1",
            "Sec-WebSocket-Key2",
            "Sec-WebSocket-Origin",
            "Sec-WebSocket-Version",
            "Upgrade",
            "Upgrade-Insecure-Requests",
            "User-Agent"
        }, CommonHeadersLowerCaseInvariant = CommonHeaders.Select(s => s.ToLowerInvariant()).ToArray();
        private static string ToHeaderKey(ref ReadableBuffer headerName)
        {
            var lowerCaseHeaders = CommonHeadersLowerCaseInvariant;
            for (int i = 0; i < lowerCaseHeaders.Length; i++)
            {
                if (IsCaseInsensitiveAsciiMatch(headerName, lowerCaseHeaders[i])) return CommonHeaders[i];
            }

            return headerName.GetAsciiString();
        }

        private static unsafe bool IsCaseInsensitiveAsciiMatch(ReadableBuffer bufferUnknownCase, string valueLowerCase)
        {
            if (bufferUnknownCase.Length != valueLowerCase.Length) return false;
            int index = 0;
            fixed (char* valuePtr = valueLowerCase)
                foreach (var span in bufferUnknownCase)
                {
                    byte* bufferPtr = (byte*)span.BufferPtr;
                    for (int i = 0; i < span.Length; i++)
                    {
                        char x = (char)(*bufferPtr++), y = valuePtr[index++];
                        if (x != y && char.ToLowerInvariant(x) != y) return false;
                    }
                }
            return true;
        }

        public void Stop()
        {
            listener?.Stop();
            thread?.Dispose();
            listener = null;
            thread = null;
        }
    }
}
