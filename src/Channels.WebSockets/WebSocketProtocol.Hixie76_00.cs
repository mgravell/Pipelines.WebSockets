using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Channels.WebSockets
{
    internal abstract partial class WebSocketProtocol
    {
        internal static readonly WebSocketProtocol Hixie76 = new Hixie76_00();

        private  sealed class Hixie76_00 : WebSocketProtocol
        {
            public override string Name => "Hixie76";
            internal override Task CompleteHandshakeAsync(ref HttpRequest request, WebSocketConnection socket)
            {
                throw new NotImplementedException();
            }
            internal override bool TryReadFrameHeader(ref ReadableBuffer buffer, out WebSocketsFrame frame)
            {
                throw new NotImplementedException();
            }
            internal override Task WriteAsync<T>(WebSocketConnection connection, WebSocketsFrame.OpCodes opCode, ref T message)
            {
                throw new NotImplementedException();
            }
        }
    }
}
