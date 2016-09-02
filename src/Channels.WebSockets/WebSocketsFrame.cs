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

        
        private static readonly int vectorWidth = Vector<byte>.Count;
        private static readonly int vectorShift = (int)Math.Log(vectorWidth, 2);
        private static readonly int vectorOverflow = ~(~0 << vectorShift);
        private static bool useVector = Vector.IsHardwareAccelerated;

        public static void SetHardwareAcceleration(bool enabled)
        {
            useVector = enabled & Vector.IsHardwareAccelerated;
        }
        public static unsafe uint ApplyMask(byte[] data, int offset, int count, uint mask)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0 || (offset + count) > data.Length) throw new ArgumentOutOfRangeException(nameof(offset));

            if (count == 0 || mask == 0) return mask;
            fixed (byte* ptr = data)
            {
                return ApplyMask(ptr + offset, count, mask);
            }
        }
        private static unsafe uint ApplyMask(byte* ptr, int count, uint mask)
        {
            // Vector widths
            int chunks = count >> vectorShift;
            if (chunks != 0 && useVector)
            {                    
                var maskVector = Vector.AsVectorByte(new Vector<uint>(mask));
                for (int i = 0; i < chunks; i++)
                {
                    var maskedVector = Unsafe.Read<Vector<byte>>(ptr) ^ maskVector;
                    Unsafe.Write(ptr, maskedVector);
                    ptr += vectorWidth;
                }
                count &= vectorOverflow;
            }

            // qword widths
            chunks = count >> 3;
            if (chunks != 0)
            {
                ulong mask8 = mask;
                mask8 = (mask8 << 32) | mask8;

                for (int i = 0; i < chunks; i++)
                {
                    *((ulong*)ptr) ^= mask8;
                    ptr += sizeof(ulong);
                }
            }

            // Now there can be at most 7 bytes left
            switch (count & 7)
            {
                case 0:
                    // No-op: We already finished masking all the bytes.
                    break;
                case 1:
                    *(ptr) = (byte)(*(ptr) ^ (byte)(mask));
                    mask = (mask >> 8) | (mask << 24);
                    break;
                case 2:
                    *(ushort*)(ptr) = (ushort)(*(ushort*)(ptr) ^ (ushort)(mask));
                    mask = (mask >> 16) | (mask << 16);
                    break;
                case 3:
                    *(ushort*)(ptr) = (ushort)(*(ushort*)(ptr) ^ (ushort)(mask));
                    *(ptr + 2) = (byte)(*(ptr + 2) ^ (byte)(mask >> 16));
                    mask = (mask >> 24) | (mask << 8);
                    break;
                case 4:
                    *(uint*)(ptr) = *(uint*)(ptr) ^ mask;
                    break;
                case 5:
                    *(uint*)(ptr) = *(uint*)(ptr) ^ mask;
                    *(ptr + 4) = (byte)(*(ptr + 4) ^ (byte)(mask));
                    mask = (mask >> 8) | (mask << 24);
                    break;
                case 6:
                    *(uint*)(ptr) = *(uint*)(ptr) ^ mask;
                    *(ushort*)(ptr + 4) = (ushort)(*(ushort*)(ptr + 4) ^ (ushort)(mask));
                    mask = (mask >> 16) | (mask << 16);
                    break;
                case 7:
                    *(uint*)(ptr) = *(uint*)(ptr) ^ mask;
                    *(ushort*)(ptr + 4) = (ushort)(*(ushort*)(ptr + 4) ^ (ushort)(mask));
                    *(ptr + 6) = (byte)(*(ptr + 6) ^ (byte)(mask >> 16));
                    mask = (mask >> 24) | (mask << 8);
                    break;
            }
            return mask;
        }

        internal unsafe static void ApplyMask(ref ReadableBuffer buffer, int mask)
        {
            if (mask == 0) return;

            uint m = (uint)mask;
            foreach(var span in buffer)
            {
                m = ApplyMask((byte*)span.BufferPtr, span.Length, m);
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
