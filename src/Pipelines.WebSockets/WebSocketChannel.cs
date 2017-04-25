
namespace Channels.WebSockets
{
    public class WebSocketChannel : IChannel
    {
        private Channel _input, _output;
        private WebSocketConnection _webSocket;

        public WebSocketChannel(WebSocketConnection webSocket, ChannelFactory channelFactory)
        {
            _webSocket = webSocket;
            _input = channelFactory.CreateChannel();
            _output = channelFactory.CreateChannel();

            webSocket.BufferFragments = false;
            webSocket.BinaryAsync += msg => msg.WriteAsync(_input);
            webSocket.Closed += () => _input.CompleteWriter();

            SendCloseWhenOutputCompleted();
            PushFromOutputToWebSocket();
        }
        private async void SendCloseWhenOutputCompleted()
        {
            await _output.Reading;
            await _webSocket.CloseAsync();
        }

        private async void PushFromOutputToWebSocket()
        {
            while (true)
            {
                var data = await _output.ReadAsync();
                if(data.IsEmpty)
                {
                    if (_output.Reading.IsCompleted) break;
                    _output.Advance(data.End);
                }
                else
                {
                    // note we don't need to create a mask here - is created internally
                    var message = new Message(data, mask: 0, isFinal: true);
                    var send = _webSocket.SendAsync(WebSocketsFrame.OpCodes.Binary, ref message);
                    // can free up one lock on the data *before* we await...
                    _output.Advance(data.End);
                    await send;
                }
            }
        }

        public IReadableChannel Input => _input;
        public IWritableChannel Output => _output;

        public void Dispose() => _webSocket.Dispose();
    }
}
