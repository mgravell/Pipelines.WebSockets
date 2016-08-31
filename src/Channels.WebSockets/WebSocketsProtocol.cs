using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Channels.WebSockets
{
    internal abstract partial class WebSocketProtocol
    {
        public abstract string Name { get; }
        internal abstract Task CompleteHandshakeAsync(ref HttpRequest request, WebSocketConnection socket);
        internal abstract bool TryReadFrameHeader(ref ReadableBuffer buffer, out WebSocketsFrame frame);
        internal abstract Task WriteAsync<T>(WebSocketConnection connection, WebSocketsFrame.OpCodes opCode, ref T message) where T : struct, IMessageWriter;
        

        protected static unsafe void Copy(byte* source, byte* destination, uint bytes)
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
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected static unsafe int ReadBigEndianInt32(byte* buffer, int offset)
        {
            return (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected static unsafe int ReadLittleEndianInt32(byte* buffer, int offset)
        {
            return (buffer[offset]) | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24);
        }
    }
}
