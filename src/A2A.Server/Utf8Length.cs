using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace A2A.Server;

/// <summary>Measures the UTF-8 JSON of a value without keeping the bytes or building a string.</summary>
internal static class Utf8Length
{
    /// <summary>The length in bytes of the UTF-8 JSON of <paramref name="value" /> as <paramref name="typeInfo" /> writes it.</summary>
    public static long Of<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        using var counter = new CountingStream();
        JsonSerializer.Serialize(counter, value, typeInfo);
        return counter.Length;
    }

    /// <summary>A write-only stream that counts the bytes written to it and discards them.</summary>
    private sealed class CountingStream : Stream
    {
        private long _length;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;

        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _length += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _length += buffer.Length;
        }

        public override void WriteByte(byte value)
        {
            _length++;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }
    }
}
