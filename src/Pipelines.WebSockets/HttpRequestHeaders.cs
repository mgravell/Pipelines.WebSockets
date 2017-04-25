using System;
using System.Collections;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.IO.Pipelines.Text.Primitives;

namespace Pipelines.WebSockets
{
    public struct HttpRequestHeaders : IEnumerable<KeyValuePair<string, PreservedBuffer>>, IDisposable
    {
        private Dictionary<string, PreservedBuffer> headers;
        public void Dispose()
        {
            if (headers != null)
            {
                foreach (var pair in headers)
                    pair.Value.Dispose();
            }
            headers = null;
        }
        internal HttpRequestHeaders(Dictionary<string, PreservedBuffer> headers)
        {
            this.headers = headers;
        }
        public bool ContainsKey(string key) => headers.ContainsKey(key);
        IEnumerator<KeyValuePair<string, PreservedBuffer>> IEnumerable<KeyValuePair<string, PreservedBuffer>>.GetEnumerator() => ((IEnumerable<KeyValuePair<string, PreservedBuffer>>)headers).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)headers).GetEnumerator();
        public Dictionary<string, PreservedBuffer>.Enumerator GetEnumerator() => headers.GetEnumerator();

        public string GetAsciiString(string key)
        {
            PreservedBuffer buffer;
            if (headers.TryGetValue(key, out buffer)) return buffer.Buffer.GetAsciiString();
            return null;
        }
        internal PreservedBuffer GetRaw(string key)
        {
            PreservedBuffer buffer;
            if (headers.TryGetValue(key, out buffer)) return buffer;
            return default(PreservedBuffer);
        }

    }
}
