using System;
using System.Collections.Generic;
using System.IO.Pipelines;

namespace Pipelines.WebSockets
{
    internal struct HttpRequest : IDisposable
    {
        public void Dispose()
        {
            Method.Dispose();
            Path.Dispose();
            HttpVersion.Dispose();
            Headers.Dispose();
            Method = Path = HttpVersion = default(PreservedBuffer);
            Headers = default(HttpRequestHeaders);
        }
        public PreservedBuffer Method { get; private set; }
        public PreservedBuffer Path { get; private set; }
        public PreservedBuffer HttpVersion { get; private set; }

        public HttpRequestHeaders Headers; // yes, naked field - internal type, so not too exposed; allows for "ref" without copy

        public HttpRequest(PreservedBuffer method, PreservedBuffer path, PreservedBuffer httpVersion, Dictionary<string, PreservedBuffer> headers)
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
        public PreservedBuffer HttpVersion => request.Method;
        public PreservedBuffer StatusCode => request.Path;
        public PreservedBuffer StatusText => request.HttpVersion;
    }
}
