using System;
using System.IO.Pipelines;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Pipelines.WebSockets
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
        public WebSocketsFrame(byte header, bool isMasked, int maskLittleEndian, int payloadLength)
        {
            this.header = header;
            header2 = (byte)(isMasked ? 1 : 0);
            PayloadLength = payloadLength;
            MaskLittleEndian = isMasked ? maskLittleEndian : 0;
        }
        public bool IsMasked => (header2 & 1) != 0;
        private bool HasFlag(FrameFlags flag) => (header & (byte)flag) != 0;

        
        //private static readonly int vectorWidth = Vector<byte>.Count;
        //private static readonly int vectorShift = (int)Math.Log(vectorWidth, 2);
        //private static readonly int vectorOverflow = ~(~0 << vectorShift);
        //private static readonly bool isHardwareAccelerated = Vector.IsHardwareAccelerated;

        private static unsafe uint ApplyMask(Span<byte> span, uint mask)
        {
            // Vector widths
            if (Vector.IsHardwareAccelerated)
            {
                var vectors = span.NonPortableCast<byte, Vector<uint>>();
                var vectorMask = new Vector<uint>(mask);
                for(int i = 0; i < vectors.Length; i++)
                {
                    vectors[i] ^= vectorMask;
                }
                span = span.Slice(vectors.Length * Vector<uint>.Count);
            }

            // qword widths
            if((span.Length & ~7) != 0)
            {
                ulong mask8 = mask;
                mask8 = (mask8 << 32) | mask8;
                var ulongs = span.NonPortableCast<byte, ulong>();
                for (int i = 0; i < ulongs.Length; i++)
                {
                    ulongs[i] ^= mask8;
                }
                span = span.Slice(ulongs.Length << 3);
            }

            // Now there can be at most 7 bytes left; loop
            for(int i = 0; i < span.Length; i++)
            {
                var b = mask & 0xFF;
                mask = (mask >> 8) | (b << 24);
                span[i] ^= (byte)b;                
            }
            return mask;
        }

        internal unsafe static void ApplyMask(ref ReadableBuffer buffer, int maskLittleEndian)
        {
            if (maskLittleEndian == 0) return;

            uint m = (uint)maskLittleEndian;
            foreach(var span in buffer)
            {
                // note: this is an optimized xor implementation using Vector<byte>, qword hacks, etc; unsafe access
                // to the span  is warranted
                // note: in this case, we know we're talking about memory from the pool, so we know it is already pinned
                m = ApplyMask(span.Span, m);
            }
        }


        public bool IsControlFrame { get { return (header & (byte)OpCodes.Close) != 0; } }
        public int MaskLittleEndian { get; }
        public OpCodes OpCode => (OpCodes)(header & 15);
        public FrameFlags Flags => (FrameFlags)(header & ~15);
        public bool IsFinal { get { return HasFlag(FrameFlags.IsFinal); } }
        public bool Reserved1 { get { return HasFlag(FrameFlags.Reserved1); } }
        public bool Reserved2 { get { return HasFlag(FrameFlags.Reserved2); } }
        public bool Reserved3 { get { return HasFlag(FrameFlags.Reserved3); } }

        public int PayloadLength { get; }
    }
}
