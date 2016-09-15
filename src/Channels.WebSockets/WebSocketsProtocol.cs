using Channels.Text.Primitives;
using System;
using System.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Channels.WebSockets
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
        internal unsafe static Task CompleteHandshakeAsync(ref HttpRequest request, WebSocketConnection socket)
        {
            var key = request.Headers.GetRaw("Sec-WebSocket-Key");

            var connection = socket.Connection;

            const int ResponseTokenLength = 28;

            var buffer = connection.Output.Alloc(StandardPrefixBytes.Length +
                ResponseTokenLength + StandardPostfixBytes.Length);

            var hashBase64Buffer = stackalloc byte[ResponseTokenLength];
            var hashBase64Span = new Span<byte>(hashBase64Buffer, ResponseTokenLength);
            ComputeReply(key, hashBase64Span);
            WebSocketServer.WriteStatus($"Response token: {hashBase64Span}");

            buffer.Write(StandardPrefixBytes);
            buffer.Write(hashBase64Span);
            buffer.Write(StandardPostfixBytes);

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
        internal static void ComputeReply(ReadableBuffer key, Span<byte> destination)
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
            if (key.Length != ExpectedKeyLength) throw new ArgumentException("Invalid key length", nameof(key));

            byte[] arr = new byte[ExpectedKeyLength + WebSocketKeySuffixBytes.Length];
            key.CopyTo(arr);
            Buffer.BlockCopy( // append the magic number from RFC6455
                WebSocketKeySuffixBytes, 0,
                arr, ExpectedKeyLength,
                WebSocketKeySuffixBytes.Length);

            // compute the hash
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(arr, 0,
                    ExpectedKeyLength + WebSocketKeySuffixBytes.Length);

                Base64.Encode(hash, destination);
            }
        }



        internal static void WriteFrameHeader(ref WritableBuffer output, WebSocketsFrame.FrameFlags flags, WebSocketsFrame.OpCodes opCode, int payloadLength, int mask)
        {
            output.Ensure(MaxHeaderLength);

            int index = 0;
            var span = output.Memory;
            
            span[index++] = (byte)(((int)flags & 240) | ((int)opCode & 15));
            if (payloadLength > ushort.MaxValue)
            { // write as a 64-bit length
                span[index++] = (byte)((mask != 0 ? 128 : 0) | 127);
                span.Slice(index).Write(payloadLength);
                index += 8;
            }
            else if (payloadLength > 125)
            { // write as a 16-bit length
                span[index++] = (byte)((mask != 0 ? 128 : 0) | 126);
                span.Slice(index).Write((ushort)payloadLength);
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
            output.CommitBytes(index);
        }
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

            var span = buffer.FirstSpan;
            if (buffer.IsSingleSpan || span.Length >= MaxHeaderLength)
            {
                return TryReadFrameHeader(span.Length, span, ref buffer, out frame);
            }
            else
            {
                // header is at most 14 bytes; can afford the stack for that - but note that if we aim for 16 bytes instead,
                // we will usually benefit from using 2 qword copies (handled internally); very very small messages ('a') might
                // have to use the slower version, but... meh
                byte* header = stackalloc byte[16];
                var slice = buffer.Slice(0, Math.Min(16, bytesAvailable));
                slice.CopyTo(new Span<byte>(header, slice.Length));
                // note that we're using the "slice" above to preview the header, but we
                // still want to pass the original buffer down below, so that we can
                // check the overall length (payload etc)
                return TryReadFrameHeader(slice.Length, new Span<byte>(header, slice.Length), ref buffer, out frame);
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
                    int big = ReadBigEndianInt32(header, 2), little = ReadBigEndianInt32(header, 6);
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
                masked ? ReadLittleEndianInt32(header, maskOffset) : 0,
                payloadLength);
            buffer = buffer.Slice(headerLength); // header is fully consumed now
            return true;
        }
        internal static Task WriteAsync<T>(WebSocketConnection connection, WebSocketsFrame.OpCodes opCode, ref T message)
            where T : struct, IMessageWriter
        {
            int payloadLength = message.GetTotalBytes();
            var buffer = connection.Connection.Output.Alloc(MaxHeaderLength + payloadLength);
            WriteFrameHeader(ref buffer, WebSocketsFrame.FrameFlags.IsFinal, opCode, payloadLength, 0);
            if (payloadLength != 0) message.Write(ref buffer);
            return buffer.FlushAsync();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ReadBigEndianInt32(Span<byte> span, int offset)
        {
            return (span[offset++] << 24) | (span[offset++] << 16) | (span[offset++] << 8) | span[offset];
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ReadLittleEndianInt32(Span<byte> span, int offset)
        {
            return (span[offset++]) | (span[offset++] << 8) | (span[offset++] << 16) | (span[offset] << 24);
        }
    }
}
