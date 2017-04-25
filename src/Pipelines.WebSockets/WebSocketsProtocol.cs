using System;
using System.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.IO.Pipelines.Networking.Sockets;
using System.IO.Pipelines;
using System.IO.Pipelines.Text.Primitives;
using System.Buffers;
using System.Numerics;

namespace Pipelines.WebSockets
{
    internal static class WebSocketProtocol
    {
        public static string Name => "RFC6455";
        static readonly byte[]
            StandardPrefixBytes = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\n"
                            + "Upgrade: websocket\r\n"
                            + "Connection: Upgrade\r\n"
                            + "Sec-WebSocket-Accept: "),
            StandardPostfixBytes = Encoding.ASCII.GetBytes("\r\n\r\n");
        internal static Task CompleteServerHandshakeAsync(ref HttpRequest request, WebSocketConnection socket)
        {
            var key = request.Headers.GetRaw("Sec-WebSocket-Key");

            var connection = socket.Connection;

            var buffer = connection.Output.Alloc(StandardPrefixBytes.Length +
                SecResponseLength + StandardPostfixBytes.Length);

            buffer.Write(StandardPrefixBytes);
            // RFC6455 logic to prove that we know how to web-socket
            buffer.Ensure(SecResponseLength);
            int bytes = ComputeReply(key.Buffer, buffer.Buffer.Span);
            if (bytes != SecResponseLength)
            {
                throw new InvalidOperationException($"Incorrect response token length; expected {SecResponseLength}, got {bytes}");
            }
            buffer.Advance(SecResponseLength);
            buffer.Write(StandardPostfixBytes);

            return buffer.FlushAsync().AsTask();
        }

        private static readonly RandomNumberGenerator rng = RandomNumberGenerator.Create();
        private static readonly Random cheapRandom = new Random();
        public static int CreateMask()
        {
            int mask = cheapRandom.Next();
            if (mask == 0) mask = 0x2211BBFF; // just something non zero
            return mask;
        }
        static readonly byte[] maskBytesBuffer = new byte[4];
        public static void GetRandomBytes(byte[] buffer)
        {
            lock (rng) // thread safety not explicit
            {
                rng.GetBytes(buffer);
            }
        }

        internal static async Task<WebSocketConnection> ClientHandshake(IPipeConnection connection, Uri uri, string origin, string protocol)
        {
            WebSocketServer.WriteStatus(ConnectionType.Client, "Writing client handshake...");
            // write the outbound request portion of the handshake
            var output = connection.Output.Alloc();
            output.Write(GET);
            output.WriteAsciiString(uri.PathAndQuery);
            output.Write(HTTP_Host);
            output.WriteAsciiString(uri.Host);
            output.Write(UpgradeConnectionKey);

            byte[] challengeKey = new byte[WebSocketProtocol.SecResponseLength];

            GetRandomBytes(challengeKey); // we only want the first 16 for entropy, but... meh
            output.Ensure(WebSocketProtocol.SecRequestLength);
            int bytesWritten = Base64.Encode(new ReadOnlySpan<byte>(challengeKey, 0, 16), output.Buffer.Span);
            // now cheekily use that data we just wrote the the output buffer
            // as a source to compute the expected bytes, and store them back
            // into challengeKey; sneaky!
            WebSocketProtocol.ComputeReply(
                output.Buffer.Slice(0, WebSocketProtocol.SecRequestLength).Span,
                challengeKey);
            output.Advance(bytesWritten);
            output.Write(CRLF);

            if (!string.IsNullOrWhiteSpace(origin))
            {
                output.WriteAsciiString("Origin: ");
                output.WriteAsciiString(origin);
                output.Write(CRLF);
            }
            if (!string.IsNullOrWhiteSpace(protocol))
            {
                output.WriteAsciiString("Sec-WebSocket-Protocol: ");
                output.WriteAsciiString(protocol);
                output.Write(CRLF);
            }
            output.Write(WebSocketVersion);
            output.Write(CRLF); // final CRLF to end the HTTP request
            await output.FlushAsync();

            WebSocketServer.WriteStatus(ConnectionType.Client, "Parsing response to client handshake...");
            using (var resp = await WebSocketServer.ParseHttpResponse(connection.Input))
            {
                if (!resp.HttpVersion.Equals(ExpectedHttpVersion)
                    || !resp.StatusCode.Equals(ExpectedStatusCode)
                    || !resp.StatusText.Equals(ExpectedStatusText)
                    || !resp.Headers.GetRaw("Upgrade").Equals(ExpectedUpgrade)
                    || !resp.Headers.GetRaw("Connection").Equals(ExpectedConnection))
                {
                    throw new InvalidOperationException("Not a web-socket server");
                }

                var accept = resp.Headers.GetRaw("Sec-WebSocket-Accept");
                if (!accept.Equals(challengeKey))
                {
                    throw new InvalidOperationException("Sec-WebSocket-Accept mismatch");
                }

                protocol = resp.Headers.GetAsciiString("Sec-WebSocket-Protocol");
            }

            var webSocket = new WebSocketConnection(connection, ConnectionType.Client)
            {
                Host = uri.Host,
                RequestLine = uri.OriginalString,
                Origin = origin,
                Protocol = protocol
            };
            webSocket.StartProcessingIncomingFrames();
            return webSocket;
        }
        static readonly byte[] GET = Encoding.ASCII.GetBytes("GET "),
            HTTP_Host = Encoding.ASCII.GetBytes(" HTTP/1.1\r\nHost: "),
            UpgradeConnectionKey = Encoding.ASCII.GetBytes("\r\nUpgrade: websocket\r\n"
                        + "Connection: Upgrade\r\n"
                        + "Sec-WebSocket-Key: "),
            WebSocketVersion = Encoding.ASCII.GetBytes("Sec-WebSocket-Version: 13\r\n"),
            CRLF = { (byte)'\r', (byte)'\n' };

        static readonly byte[]
            ExpectedHttpVersion = Encoding.ASCII.GetBytes("HTTP/1.1"),
            ExpectedStatusCode = Encoding.ASCII.GetBytes("101"),
            ExpectedStatusText = Encoding.ASCII.GetBytes("Switching Protocols"),
            ExpectedUpgrade = Encoding.ASCII.GetBytes("websocket"),
            ExpectedConnection = Encoding.ASCII.GetBytes("Upgrade");


        static readonly byte[] WebSocketKeySuffixBytes = Encoding.ASCII.GetBytes("258EAFA5-E914-47DA-95CA-C5AB0DC85B11");

        internal const int SecRequestLength = 24;
        internal const int SecResponseLength = 28;
        internal unsafe static int ComputeReply(ReadableBuffer key, Span<byte> destination)
        {
            if(key.IsSingleSpan)
            {
                return ComputeReply(key.First.Span, destination);
            }
            if (key.Length != SecRequestLength) throw new ArgumentException("Invalid key length", nameof(key));
            byte* ptr = stackalloc byte[SecRequestLength];
            var span = new Span<byte>(ptr, SecRequestLength);
            key.CopyTo(span);
            return ComputeReply(destination, destination);
        }
        internal static int ComputeReply(ReadOnlySpan<byte> key, Span<byte> destination)
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

            if (key.Length != SecRequestLength) throw new ArgumentException("Invalid key length", nameof(key));

            byte[] arr = new byte[SecRequestLength + WebSocketKeySuffixBytes.Length];
            key.TryCopyTo(arr);
            Buffer.BlockCopy( // append the magic number from RFC6455
                WebSocketKeySuffixBytes, 0,
                arr, SecRequestLength,
                WebSocketKeySuffixBytes.Length);

            // compute the hash
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(arr, 0,
                    SecRequestLength + WebSocketKeySuffixBytes.Length);

                return Base64.Encode(hash, destination);
            }
        }
        

        internal static void WriteFrameHeader(ref WritableBuffer output, WebSocketsFrame.FrameFlags flags, WebSocketsFrame.OpCodes opCode, int payloadLength, int mask)
        {
            output.Ensure(MaxHeaderLength);

            int index = 0;
            var span = output.Buffer.Span;
            
            span[index++] = (byte)(((int)flags & 240) | ((int)opCode & 15));
            if (payloadLength > ushort.MaxValue)
            { // write as a 64-bit length
                span[index++] = (byte)((mask != 0 ? 128 : 0) | 127);
                span.Slice(index).Write((uint)0);
                span.Slice(index + 4).Write(ToNetworkByteOrder((uint)payloadLength));
                index += 8;
            }
            else if (payloadLength > 125)
            { // write as a 16-bit length
                span[index++] = (byte)((mask != 0 ? 128 : 0) | 126);
                span.Slice(index).Write(ToNetworkByteOrder((ushort)payloadLength));
                index += 2;
            }
            else
            { // write in the header
                span[index++] = (byte)((mask != 0 ? 128 : 0) | payloadLength);
            }
            if (mask != 0)
            {
                span.Slice(index).Write(mask);
                index += 4;
            }
            output.Advance(index);
        }

        // note assumes little endian architecture
        private static ulong ToNetworkByteOrder(uint value)
            => (value >> 24)
            | ((value >> 8) & 0xFF00)
            | ((value << 8) & 0xFF0000)
            | ((value << 24) & 0xFF000000);

        private static ushort ToNetworkByteOrder(ushort value)
            => (ushort)(value >> 8 | value << 8);

        internal const int MaxHeaderLength = 14;
        // the `unsafe` here is so that in the "multiple spans, header crosses spans", we can use stackalloc to
        // collate the header bytes in one place, and pass that down for analysis
        internal unsafe static bool TryReadFrameHeader(ref ReadableBuffer buffer, out WebSocketsFrame frame)
        {
            int bytesAvailable = buffer.Length;
            if (bytesAvailable < 2)
            {
                frame = default(WebSocketsFrame);
                return false; // can't read that; frame takes at minimum two bytes
            }

            var firstSpan = buffer.First;
            if (buffer.IsSingleSpan || firstSpan.Length >= MaxHeaderLength)
            {
                return TryReadFrameHeader(firstSpan.Length, firstSpan.Span, ref buffer, out frame);
            }
            else
            {
                // header is at most 14 bytes; can afford the stack for that - but note that if we aim for 16 bytes instead,
                // we will usually benefit from using 2 qword copies
                byte* header = stackalloc byte[16];
                var slice = buffer.Slice(0, Math.Min(16, bytesAvailable));
                var headerSpan = new Span<byte>(header, slice.Length);
                slice.CopyTo(headerSpan);

                // note that we're using the "slice" above to preview the header, but we
                // still want to pass the *original* buffer down below, so that we can
                // check the overall length (payload etc)
                return TryReadFrameHeader(slice.Length, headerSpan, ref buffer, out frame);
            }
        }
        internal static bool TryReadFrameHeader(int inspectableBytes, Span<byte> header, ref ReadableBuffer buffer, out WebSocketsFrame frame)
        {
            bool masked = (header[1] & 128) != 0;
            int tmp = header[1] & 127;
            int headerLength, maskOffset, payloadLength;
            switch (tmp)
            {
                case 126:
                    headerLength = masked ? 8 : 4;
                    if (inspectableBytes < headerLength)
                    {
                        frame = default(WebSocketsFrame);
                        return false;
                    }
                    payloadLength = (header[2] << 8) | header[3];
                    maskOffset = 4;
                    break;
                case 127:
                    headerLength = masked ? 14 : 10;
                    if (inspectableBytes < headerLength)
                    {
                        frame = default(WebSocketsFrame);
                        return false;
                    }
                    int big = header.Slice(2).ReadBigEndian<int>(), little = header.Slice(6).ReadBigEndian<int>();
                    if (big != 0 || little < 0) throw new ArgumentOutOfRangeException(); // seriously, we're not going > 2GB
                    payloadLength = little;
                    maskOffset = 10;
                    break;
                default:
                    headerLength = masked ? 6 : 2;
                    if (inspectableBytes < headerLength)
                    {
                        frame = default(WebSocketsFrame);
                        return false;
                    }
                    payloadLength = tmp;
                    maskOffset = 2;
                    break;
            }
            if (buffer.Length < headerLength + payloadLength)
            {
                frame = default(WebSocketsFrame);
                return false; // frame (header+body) isn't intact
            }


            frame = new WebSocketsFrame(header[0], masked,
                masked ? header.Slice(maskOffset).ReadLittleEndian<int>() : 0,
                payloadLength);
            buffer = buffer.Slice(headerLength); // header is fully consumed now
            return true;
        }
        internal static Task WriteAsync<T>(WebSocketConnection connection,
            WebSocketsFrame.OpCodes opCode,
            WebSocketsFrame.FrameFlags flags, ref T message)
            where T : struct, IMessageWriter
        {
            int payloadLength = message.GetPayloadLength();
            var buffer = connection.Connection.Output.Alloc(MaxHeaderLength + payloadLength);
            int mask = connection.ConnectionType == ConnectionType.Client ? CreateMask() : 0;
            WriteFrameHeader(ref buffer, WebSocketsFrame.FrameFlags.IsFinal, opCode, payloadLength, mask);
            if (payloadLength != 0)
            {
                if (mask == 0) { message.WritePayload(buffer); }
                else
                {
                    var payloadStart = buffer.AsReadableBuffer().End;
                    message.WritePayload(buffer);
                    var payload = buffer.AsReadableBuffer().Slice(payloadStart); // note that this is a different AsReadableBuffer; call twice is good
                    WebSocketsFrame.ApplyMask(ref payload, mask);
                }
            }
            return buffer.FlushAsync().AsTask();
        }
    }
}
