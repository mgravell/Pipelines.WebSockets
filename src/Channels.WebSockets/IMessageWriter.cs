namespace Channels.WebSockets
{
    internal interface IMessageWriter
    {
        void WritePayload(ref WritableBuffer buffer);
        int GetPayloadLength();
    }
}
