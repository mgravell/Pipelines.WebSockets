using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Tasks;

namespace Pipelines.WebSockets
{
    internal static class ExtensionMethods
    {
        public static void WriteAsciiString(this WritableBuffer buffer, string value)
        {
            if (value == null || value.Length == 0) return;

            WriteAsciiString(ref buffer, value.Slice());
        }
        private static void WriteAsciiString(ref WritableBuffer buffer, ReadOnlySpan<char> value)
        {
            if (value == null || value.Length == 0) return;

            while (value.Length != 0)
            {
                buffer.Ensure();

                var span = buffer.Buffer.Span;
                int bytesToWrite = Math.Min(value.Length, span.Length);

                // todo: Vector.Narrow

                for (int i = 0; i < bytesToWrite; i++)
                {
                    span[i] = (byte)value[i];
                }

                buffer.Advance(bytesToWrite);
                buffer.Commit();
                value = value.Slice(bytesToWrite);
            }
        }

        public static void WriteUtf8String(this WritableBuffer buffer, string value)
        {
            if (value == null || value.Length == 0) return;

            WriteUtf8String(ref buffer, value.Slice());
        }
        private static void WriteUtf8String(ref WritableBuffer buffer, ReadOnlySpan<char> value)
        {
            if (value == null || value.Length == 0) return;

            var encoder = TextEncoder.Utf8;
            while (value.Length != 0)
            {
                buffer.Ensure(4); // be able to write at least one character (worst case)

                var span = buffer.Buffer.Span;
                encoder.TryEncode(value, span, out int charsConsumed, out int bytesWritten);
                buffer.Advance(bytesWritten);
                buffer.Commit();
                value = value.Slice(charsConsumed);
            }
        }

        public static Task AsTask(this WritableBufferAwaitable flush)
        {
            async Task Awaited(WritableBufferAwaitable result)
            {
                if ((await result).IsCancelled) throw new ObjectDisposedException("Flush cancelled");
            }
            if (!flush.IsCompleted) return Awaited(flush);
            if (flush.GetResult().IsCancelled) throw new ObjectDisposedException("Flush cancelled");
            return Task.CompletedTask; // all sync? who knew!
        }
    }
}
