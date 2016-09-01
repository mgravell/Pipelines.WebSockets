using Channels.Networking.Libuv;
using Channels.Text.Primitives;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Numerics;
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
                listener.OnConnection(OnConnection);
                listener.Start();
            }
        }

        private async void OnConnection(UvTcpServerConnection connection)
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
                    WriteStatus($"Protocol: {WebSocketProtocol.Name}");
                    WriteStatus("Authenticating...");
                    if (!await OnAuthenticateAsync(socket, ref request.Headers)) throw new InvalidOperationException("Authentication refused");
                    WriteStatus("Completing handshake...");
                    await WebSocketProtocol.CompleteHandshakeAsync(ref request, socket);
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
        }

        [Conditional("LOGGING")]
        internal static void WriteStatus(string message)
        {
#if LOGGING
            Console.WriteLine($"[Server:{Environment.CurrentManagedThreadId}]: {message}");
#endif
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


        internal Task OnFrameReceivedAsync(WebSocketConnection connection, ref WebSocketsFrame frame, ref ReadableBuffer buffer)
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




        static readonly char[] Comma = { ',' };

        protected virtual Task<bool> OnAuthenticateAsync(WebSocketConnection connection, ref HttpRequestHeaders headers) => TaskResult.True;
        protected virtual Task OnHandshakeCompleteAsync(WebSocketConnection connection) => TaskResult.True;

        private WebSocketConnection GetProtocol(UvTcpServerConnection connection, ref HttpRequest request)
        {
            var headers = request.Headers;
            string host = headers.GetAsciiString("Host");
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
                var parts = headers.GetAsciiString("Connection").Split(Comma);
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

            if (looksGoodEnough)
            {
                //9.   The request MUST include a header field with the name
                //|Sec-WebSocket-Version|.  The value of this header field MUST be

                if (!headers.ContainsKey("Sec-WebSocket-Version"))
                {
                    throw new NotSupportedException();
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
                            break; // looks ok
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
            socket.Origin = headers.GetAsciiString("Origin") ?? headers.GetAsciiString("Sec-WebSocket-Origin");
            socket.Protocol = headers.GetAsciiString("Sec-WebSocket-Protocol");
            socket.RequestLine = request.Path.GetAsciiString();
            return socket;
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
