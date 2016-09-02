using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Channels.WebSockets
{
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

        private static readonly int vectorWidth = Vector<byte>.Count, vectorShift = (int)Math.Log(vectorWidth, 2), vectorOverflow = ~(~0 << vectorShift);

        // COMPLETELY UNTESTED
        /// <returns>The rotated mask</returns>
        public static unsafe uint ApplyMask(byte[] data, int offset, int count, uint mask)
        {
            if (data == null) throw new ArgumentNullException();
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (offset + count > data.Length) throw new ArgumentOutOfRangeException(nameof(count));
            if (mask == 0) return 0; // nothing to do

            fixed (byte* basePtr = data)
            {
                var ptr = basePtr + offset;
                int chunks = count >> vectorShift;

                if (chunks != 0)
                {
                    var maskVector = Vector.AsVectorByte(new Vector<uint>(mask));
                    // Vector widths
                    for (int i = 0; i < chunks; i++)
                    {
                        var maskedVector = Unsafe.Read<Vector<byte>>(ptr) ^ maskVector;
                        Unsafe.Write(ptr, maskedVector);
                        ptr += vectorWidth;
                    }
                    count &= vectorOverflow;
                }

                chunks = count >> 3;
                // Remaing ulong widths
                if (chunks != 0)
                {
                    ulong mask8 = (uint)mask;
                    mask8 = (mask8 << 32) | mask8;

                    var ptr8 = (ulong*)ptr;
                    for (int i = 0; i < chunks; i++)
                    {
                        *ptr8++ ^= mask8;
                    }
                    ptr = (byte*)ptr8;
                }

                // Now there can be at most 7 bytes left
                switch (count & 7)
                {
                    case 0:
                        // No-op: We already finished masking all the bytes.
                        break;
                    case 1:
                        *ptr = (byte)(*ptr ^ (byte)(mask & 0xff));
                        mask = (mask >> 8) | (mask << 24);
                        break;
                    case 2:
                        *(ushort*)(ptr) = (ushort)(*(short*)(ptr) ^ (ushort)(mask & 0xffff));
                        mask = (mask >> 16) | (mask << 16);
                        break;
                    case 3:
                        *(ushort*)(ptr) = (ushort)(*(ushort*)(ptr) ^ (ushort)(mask & 0xffff));
                        *(ptr + 2) = (byte)(*(ptr + 2) ^ (byte)((mask & 0xff0000) >> 16));
                        mask = (mask >> 24) | (mask << 8);
                        break;
                    case 4:
                        *(uint*)(ptr) = *(uint*)(ptr) ^ mask;
                        break;
                    case 5:
                        *(uint*)(ptr) = *(uint*)(ptr) ^ mask;
                        *(ptr + 4) = (byte)(*(ptr + 4) ^ (byte)(mask & 0xff));
                        mask = (mask >> 8) | (mask << 24);
                        break;
                    case 6:
                        *(uint*)(ptr) = *(uint*)(ptr) ^ mask;
                        *(ushort*)(ptr + 4) = (ushort)(*(ushort*)(ptr + 4) ^ (ushort)(mask & 0xffff));
                        mask = (mask >> 16) | (mask << 16);
                        break;
                    case 7:
                        *(uint*)(ptr) = *(uint*)(ptr) ^ mask;
                        *(ushort*)(ptr + 4) = (ushort)(*(ushort*)(ptr + 4) ^ (ushort)(mask & 0xffff));
                        *(ptr + 6) = (byte)(*(ptr + 6) ^ (byte)((mask & 0xff0000) >> 16));
                        mask = (mask >> 24) | (mask << 8);
                        break;
                }
            }
            return mask;
        }

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
    }
}
