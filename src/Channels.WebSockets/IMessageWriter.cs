namespace Channels.WebSockets
{
    internal interface IMessageWriter
    {
        void WritePayload(WritableBuffer buffer);
        int GetPayloadLength();
    }
}
