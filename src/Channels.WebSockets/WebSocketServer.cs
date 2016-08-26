using System;
using System.Net;
using System.Threading;
using Microsoft.AspNetCore.Server.Kestrel.Internal.Networking;
using System.Numerics;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Collections;
using System.Text;
using System.Security.Cryptography;

namespace Channels.WebSockets
{
    public class WebSocketServer : IDisposable
    {
        private UvTcpListener listener;
        private IPAddress ip;
        private int port;
        public int Port => port;
        public IPAddress IP => ip;

        public bool AllowClientsMissingConnectionHeaders { get; set; } = true; // stoopid browsers

        public WebSocketServer() : this(IPAddress.Any, 80) { }
        public WebSocketServer(IPAddress ip, int port)
        {
            this.ip = ip;
            this.port = port;
        }


        public void Dispose() => Dispose(true);
        ~WebSocketServer() { Dispose(false); }
        protected virtual void Dispose(bool disposing)
        {
            if (disposing) GC.SuppressFinalize(this);
            Stop(disposing);
        }

        public void Start()
        {
            if (listener == null)
            {
                listener = new UvTcpListener(ip, port);
                listener.OnConnection(async connection =>
                {
                    try
                    {
                        var request = await ParseHttpRequest(connection.Input);

                        var socket = GetProtocol(connection, request);

                        if (!OnAuthenticate(socket)) throw new InvalidOperationException("Authentication refused");
                        socket.WebSocketProtocol.CompleteHandshake(request, socket);
                        OnHandshakeComplete(socket);                       

                        
                        
                    }
                    catch { } // meh, bye bye broken connection
                    finally
                    {
                        try { connection.Output.CompleteWriting(); } catch { }
                        try { connection.Input.CompleteReading(); } catch { }
                    }
                });
                listener.Start();
            }
        }
        public class WebSocketConnection
        {
            private UvTcpConnection connection;

            internal WebSocketConnection(UvTcpConnection connection)
            {
                this.connection = connection;
            }

            public string Host { get; internal set; }
            public string Origin { get; internal set; }
            public string Protocol { get; internal set; }
            public string RequestLine { get; internal set; }
            internal WebSocketProtocol WebSocketProtocol { get; set; }
        }
        static readonly char[] Comma = { ',' };

        protected virtual bool OnAuthenticate(WebSocketConnection connection) => true;
        protected virtual void OnHandshakeComplete(WebSocketConnection connection) { }

        private WebSocketConnection GetProtocol(UvTcpConnection connection, HttpRequest request)
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

                string version = headers.GetAscii("Sec-WebSocket-Version");
                if (version == null)
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
                    switch (version)
                    {

                        case "4":
                        case "5":
                        case "6":
                        case "7":
                        case "8": // these are all early drafts
                        case "13": // this is later drafts and RFC6455
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

        internal abstract class WebSocketProtocol
        {
            internal static readonly WebSocketProtocol RFC6455_13 = new WebSocketProtocol_RFC6455_13(), Hixie76_00 = new WebSocketProtocol_Hixie76_00();
            class WebSocketProtocol_RFC6455_13 : WebSocketProtocol
            {
                internal override void CompleteHandshake(HttpRequest request, WebSocketConnection socket)
                {
                    var key = request.Headers.GetAscii("Sec-WebSocket-Key");
                    if (key != null) key = key.Trim();
                    string response = ComputeReply(key);

                    var builder = new StringBuilder(
                                      "HTTP/1.1 101 Switching Protocols\r\n"
                                    + "Upgrade: websocket\r\n"
                                    + "Connection: Upgrade\r\n"
                                    + "Sec-WebSocket-Accept: ").Append(response).Append("\r\n");

                    builder.Append("\r\n");
                }
                static readonly byte[] WebSocketKeySuffixBytes = Encoding.ASCII.GetBytes("258EAFA5-E914-47DA-95CA-C5AB0DC85B11");
                internal static string ComputeReply(string webSocketKey)
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
                    if (webSocketKey  == null || webSocketKey.Length != 24) throw new ArgumentException(nameof(webSocketKey));

                    int len = 24 + WebSocketKeySuffixBytes.Length;
                    byte[] buffer = new byte[len]; // TODO optimize / lease etc
                    int offset = Encoding.ASCII.GetBytes(webSocketKey, 0, webSocketKey.Length, buffer, 0);
                    //foreach(var span in webSocketKey)
                    //{
                    //    Buffer.BlockCopy(span.Array, span.Offset, buffer, offset, span.Length);
                    //    offset += span.Length;
                    //}
                    Buffer.BlockCopy(WebSocketKeySuffixBytes, 0, buffer, offset, WebSocketKeySuffixBytes.Length);
                    using (var sha = SHA1.Create())
                    {
                        return Convert.ToBase64String(sha.ComputeHash(buffer, 0, len));
                    }
                }
            }
            class WebSocketProtocol_Hixie76_00 : WebSocketProtocol
            {
                internal override void CompleteHandshake(HttpRequest request, WebSocketConnection socket)
                {
                    throw new NotImplementedException();
                }
            }

            internal abstract void CompleteHandshake(HttpRequest request, WebSocketConnection socket);
        }

        internal struct HttpRequest
        {
            public readonly ReadableBuffer Method, Path, HttpVersion;
            public readonly HttpRequestHeaders Headers;

            public HttpRequest(ReadableBuffer method, ReadableBuffer path, ReadableBuffer httpVersion, Dictionary<string, ReadableBuffer> headers)
            {
                Method = method;
                Path = path;
                HttpVersion = httpVersion;
                Headers = new HttpRequestHeaders(headers);
            }
        }
        internal struct HttpRequestHeaders : IEnumerable<KeyValuePair<string,ReadableBuffer>>
        {
            private Dictionary<string, ReadableBuffer> headers;

            public HttpRequestHeaders(Dictionary<string, ReadableBuffer> headers)
            {
                this.headers = headers;
            }
            public bool ContainsKey(string key) => headers.ContainsKey(key);
            IEnumerator<KeyValuePair<string, ReadableBuffer>> IEnumerable<KeyValuePair<string, ReadableBuffer>>.GetEnumerator() => ((IEnumerable<KeyValuePair<string,ReadableBuffer>>)headers).GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)headers).GetEnumerator();
            public Dictionary<string,ReadableBuffer>.Enumerator GetEnumerator() => headers.GetEnumerator();

            public string GetAscii(string key)
            {
                ReadableBuffer buffer;
                if (headers.TryGetValue(key, out buffer)) return buffer.GetAsciiString();
                return null;
            }
            public ReadableBuffer GetRaw(string key)
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
            ParsingState _state = ParsingState.StartLine;
            ReadableBuffer Method = default(ReadableBuffer), Path = default(ReadableBuffer), HttpVersion = default(ReadableBuffer);
            Dictionary<string, ReadableBuffer> Headers = new Dictionary<string, ReadableBuffer>();
            while (true)
            {
                await _input;

                var buffer = _input.BeginRead();
                var consumed = buffer.Start;
                bool needMoreData = true;

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
                        Method = method.Clone();

                        // Skip ' '
                        startLine = startLine.Slice(delim).Slice(1);

                        delim = startLine.IndexOf(ref _vectorSpaces);
                        if (delim.IsEnd)
                        {
                            throw new Exception();
                        }

                        var path = startLine.Slice(0, delim);
                        Path = path.Clone();

                        // Skip ' '
                        startLine = startLine.Slice(delim).Slice(1);

                        delim = startLine.IndexOf(ref _vectorCRs);
                        if (delim.IsEnd)
                        {
                            throw new Exception();
                        }

                        var httpVersion = startLine.Slice(0, delim);
                        HttpVersion = httpVersion.Clone();

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

                        Headers[ToHeaderKey(ref headerName)] = headerValue.Clone();

                        // Move the consumed
                        consumed = buffer.Start;
                    }
                }
                finally
                {
                    _input.EndRead(consumed);
                }

                if (needMoreData)
                {
                    continue;
                }
                return new HttpRequest(Method, Path, HttpVersion, Headers);
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

        public void Stop() => Stop(true);

        private void Stop(bool wait)
        {
            listener?.Stop(wait);
            listener = null;
        }
    }
}
