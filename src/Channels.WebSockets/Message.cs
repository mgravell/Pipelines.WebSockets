using Channels.Text.Primitives;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Channels.WebSockets
{
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

        private static unsafe string GetText(List<ReadableBuffer> buffers)
        {
            // try to re-use a shared decoder; note that in heavy usage, we might need to allocate another
            var decoder = (Decoder)Interlocked.Exchange<Decoder>(ref Utf8Decoder, null);
            if (decoder == null) decoder = Utf8Encoding.GetDecoder();
            else decoder.Reset();

            var length = 0;
            foreach (var buffer in buffers) length += buffer.Length;

            var capacity = length; // worst case is 1 byte per char
            var chars = new char[capacity];
            var charIndex = 0;

            int bytesUsed = 0;
            int charsUsed = 0;
            bool completed;
            foreach (var buffer in buffers)
            {
                foreach (var span in buffer)
                {
                    ArraySegment<byte> segment;
                    void* ignored;
                    if(!span.TryGetArrayElseGetPointer(out segment, out ignored))
                    {
                        throw new InvalidOperationException("Array not available for span");
                    }
                    decoder.Convert(
                        segment.Array,
                        segment.Offset,
                        segment.Count,
                        chars,
                        charIndex,
                        capacity,
                        false, // a single character could span two spans
                        out bytesUsed,
                        out charsUsed,
                        out completed);

                    charIndex += charsUsed;
                    capacity -= charsUsed;
                }
            }
            // make the decoder available for re-use
            Interlocked.CompareExchange<Decoder>(ref Utf8Decoder, decoder, null);
            return new string(chars, 0, charIndex);
        }
        private static readonly byte[] NilBytes = new byte[0];
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
                    while (iter.MoveNext())
                    {
                        tmp = iter.Current;
                        destination.Append(ref tmp);
                    }
                }
            }

        }
    }
}
