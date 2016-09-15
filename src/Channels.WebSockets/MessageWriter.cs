namespace Channels.WebSockets
{
    internal static class MessageWriter
    {
        internal static ByteArrayMessageWriter Create(byte[] message)
        {
            return (message == null || message.Length == 0) ? default(ByteArrayMessageWriter) : new ByteArrayMessageWriter(message, 0, message.Length);
        }
        internal static StringMessageWriter Create(string message, bool computeLength = false)
        {
            return string.IsNullOrEmpty(message) ? default(StringMessageWriter) : new StringMessageWriter(message, computeLength);
        }
    }
}
