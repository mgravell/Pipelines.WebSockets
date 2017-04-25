using System;
using System.Collections.Generic;

namespace Channels.WebSockets
{
    internal struct HttpRequest : IDisposable
    {
        public void Dispose()
        {
            Method.Dispose();
            Path.Dispose();
            HttpVersion.Dispose();
            Headers.Dispose();
            Method = Path = HttpVersion = default(ReadableBuffer);
            Headers = default(HttpRequestHeaders);
        }
        public ReadableBuffer Method { get; private set; }
        public ReadableBuffer Path { get; private set; }
        public ReadableBuffer HttpVersion { get; private set; }

        public HttpRequestHeaders Headers; // yes, naked field - internal type, so not too exposed; allows for "ref" without copy

        public HttpRequest(ReadableBuffer method, ReadableBuffer path, ReadableBuffer httpVersion, Dictionary<string, ReadableBuffer> headers)
        {
            Method = method;
            Path = path;
            HttpVersion = httpVersion;
            Headers = new HttpRequestHeaders(headers);
        }
    }
    internal struct HttpResponse : IDisposable
    {
        private HttpRequest request;
        public void Dispose()
        {
            request.Dispose();
            request = default(HttpRequest);
        }
        internal HttpResponse(HttpRequest request)
        {
            this.request = request;
        }
        public HttpRequestHeaders Headers => request.Headers;
        // looks similar, but different order
        public ReadableBuffer HttpVersion => request.Method;
        public ReadableBuffer StatusCode => request.Path;
        public ReadableBuffer StatusText => request.HttpVersion;
    }
}
