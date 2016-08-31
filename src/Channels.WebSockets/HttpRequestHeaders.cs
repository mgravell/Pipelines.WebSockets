using Channels.Text.Primitives;
using System;
using System.Collections;
using System.Collections.Generic;

namespace Channels.WebSockets
{
    public struct HttpRequestHeaders : IEnumerable<KeyValuePair<string, ReadableBuffer>>, IDisposable
    {
        private Dictionary<string, ReadableBuffer> headers;
        public void Dispose()
        {
            if (headers != null)
            {
                foreach (var pair in headers)
                    pair.Value.Dispose();
            }
            headers = null;
        }
        public HttpRequestHeaders(Dictionary<string, ReadableBuffer> headers)
        {
            this.headers = headers;
        }
        public bool ContainsKey(string key) => headers.ContainsKey(key);
        IEnumerator<KeyValuePair<string, ReadableBuffer>> IEnumerable<KeyValuePair<string, ReadableBuffer>>.GetEnumerator() => ((IEnumerable<KeyValuePair<string, ReadableBuffer>>)headers).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)headers).GetEnumerator();
        public Dictionary<string, ReadableBuffer>.Enumerator GetEnumerator() => headers.GetEnumerator();

        public string GetAsciiString(string key)
        {
            ReadableBuffer buffer;
            if (headers.TryGetValue(key, out buffer)) return buffer.GetAsciiString();
            return null;
        }
        internal ReadableBuffer GetRaw(string key)
        {
            ReadableBuffer buffer;
            if (headers.TryGetValue(key, out buffer)) return buffer;
            return default(ReadableBuffer);
        }

    }
}
