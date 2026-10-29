namespace Schuly.Plugin.Microsoft.Infrastructure
{
    /// <summary>A read-only wrapper that throws once more than <c>max</c> bytes were read, whatever Content-Length claimed (or when there is none).</summary>
    public sealed class LengthLimitedStream(Stream inner, long max) : Stream
    {
        public sealed class TooLargeException() : IOException("The request body is larger than allowed.");

        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => Count(await inner.ReadAsync(buffer, cancellationToken));

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken));

        private int Count(int n)
        {
            _read += n;
            if (_read > max)
                throw new TooLargeException();
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
