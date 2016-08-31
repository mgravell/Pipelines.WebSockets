using System;
using System.Text;
using System.Threading;

namespace Channels.WebSockets
{
    internal struct StringMessageWriter : IMessageWriter
    {
        private string value;
        private int offset, count, totalBytes;
        public StringMessageWriter(string value, int offset, int count, bool preComputeLength)
        {
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (offset + count > (value?.Length ?? 0)) throw new ArgumentOutOfRangeException(nameof(count));

            this.value = value;
            this.offset = offset;
            this.count = count;
            this.totalBytes = count == 0 ? 0 : -1;
            if (preComputeLength) GetTotalBytes();
        }
#if ENCODING_POINTER_API
        // avoid allocating Encoder instances all the time
        static Encoder sharedEncoder;
#endif
        unsafe void IMessageWriter.Write(ref WritableBuffer buffer)
        {
            int expected = GetTotalBytes();
            if (expected == 0) return;


            var memory = buffer.Memory;
            byte* dest = (byte*)memory.BufferPtr;
            int actual;
            fixed (char* chars = value)
            {
#if ENCODING_POINTER_API
                var enc = Interlocked.Exchange(ref sharedEncoder, null);
                if (enc == null) enc = encoding.GetEncoder(); // need a new one      
                actual = enc.GetBytes(chars + offset, count, dest, expected, true);
                Interlocked.CompareExchange(ref sharedEncoder, enc, null); // make the encoder available  for re-use
#else
                    actual = encoding.GetBytes(chars + offset, count, dest, expected);
#endif
            }
            if (actual != expected)
            {
                throw new InvalidOperationException($"Unexpected length in {nameof(StringMessageWriter)}.{nameof(IMessageWriter.Write)}");
            }
            buffer.CommitBytes(actual);
        }

        public unsafe int GetTotalBytes()
        {
            if (totalBytes >= 0) return totalBytes;
            fixed (char* chars = value)
            {
                return totalBytes = encoding.GetByteCount(chars + offset, count);
            }
        }
        static readonly Encoding encoding = Encoding.UTF8;
    }
}
