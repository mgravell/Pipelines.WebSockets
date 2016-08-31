using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Channels.WebSockets
{
    internal abstract partial class WebSocketProtocol
    {
        internal static readonly WebSocketProtocol RFC6455 = new RFC6455_13();

        private sealed class RFC6455_13 : WebSocketProtocol
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
            internal const int MaxHeaderLength = 14;
            internal unsafe override bool TryReadFrameHeader(ref ReadableBuffer buffer, out WebSocketsFrame frame)
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
                    return TryReadFrameHeader(span.Length, (byte*)span.BufferPtr, ref buffer, out frame);
                }
                else
                {
                    // header is at most 14 bytes; can afford the stack for that - but note that if we aim for 16 bytes instead,
                    // we will usually benefit from using 2 qword copies (handled internally); very very small messages ('a') might
                    // have to use the slower version, but... meh
                    byte* header = stackalloc byte[16];
                    int inspectableBytes = SlowCopyFirst(buffer, header, 16);
                    return TryReadFrameHeader(inspectableBytes, header, ref buffer, out frame);
                }
            }
            internal unsafe bool TryReadFrameHeader(int inspectableBytes, byte* header, ref ReadableBuffer buffer, out WebSocketsFrame frame)
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
                var buffer = connection.Connection.Output.Alloc(MaxHeaderLength + payloadLength);
                WriteFrameHeader(ref buffer, WebSocketsFrame.FrameFlags.IsFinal, opCode, payloadLength, 0);
                if (payloadLength != 0) message.Write(ref buffer);
                return buffer.FlushAsync();
            }
        }
    }
}
