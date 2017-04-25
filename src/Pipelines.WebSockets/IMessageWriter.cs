using System.IO.Pipelines;

namespace Pipelines.WebSockets
{
    internal interface IMessageWriter
    {
        void WritePayload(WritableBuffer buffer);
        int GetPayloadLength();
    }
}
