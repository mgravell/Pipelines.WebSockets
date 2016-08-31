namespace Channels.WebSockets
{
    internal interface IMessageWriter
    {
        void Write(ref WritableBuffer buffer);
        int GetTotalBytes();
    }
}
