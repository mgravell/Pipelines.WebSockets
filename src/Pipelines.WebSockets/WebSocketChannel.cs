using System;
using System.IO.Pipelines;
using System.Threading.Tasks;

namespace Pipelines.WebSockets
{
    public class WebSocketChannel : IPipeConnection
    {
        private IPipe _input, _output;
        private WebSocketConnection _webSocket;

        public WebSocketChannel(WebSocketConnection webSocket, PipeFactory pipeFactory)
        {

            _webSocket = webSocket;
            _input = pipeFactory.Create();
            _output = pipeFactory.Create();

            webSocket.BufferFragments = false;
            webSocket.BinaryAsync += msg => msg.WriteAsync(_input.Writer).AsTask();
            webSocket.Closed += () => _input.Writer.Complete();
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
                var read = await _output.Reader.ReadAsync();
                if (read.IsCompleted) break;
                var data = read.Buffer;
                if(data.IsEmpty)
                {
                    _output.Reader.Advance(data.End);
                }
                else
                {
                    // note we don't need to create a mask here - is created internally
                    var message = new Message(data, mask: 0, isFinal: true);
                    var send = _webSocket.SendAsync(WebSocketsFrame.OpCodes.Binary, ref message);
                    // can free up one lock on the data *before* we await...
                    _output.Reader.Advance(data.End);
                    await send;
                }
            }
        }

        public IPipeReader Input => _input.Reader;
        public IPipeWriter Output => _output.Writer;

        public void Dispose() => _webSocket.Dispose();
    }
}
