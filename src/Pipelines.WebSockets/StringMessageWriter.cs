using System.Text;
using System.IO.Pipelines;

namespace Pipelines.WebSockets
{
    internal struct StringMessageWriter : IMessageWriter
    {
        private string value;
        private int totalBytes;
        public StringMessageWriter(string value, bool preComputeLength)
        {
            this.value = value;
            this.totalBytes = value.Length == 0 ? 0 : -1;
            if (preComputeLength) GetPayloadLength();
        }
        void IMessageWriter.WritePayload(WritableBuffer buffer)
            => buffer.WriteUtf8String(value);

        public int GetPayloadLength()
        {
            // lazily calculate
            return totalBytes >= 0 ? totalBytes : (totalBytes = encoding.GetByteCount(value));
        }
        static readonly Encoding encoding = Encoding.UTF8;
    }
}
