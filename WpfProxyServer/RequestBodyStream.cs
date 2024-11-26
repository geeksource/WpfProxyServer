using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WpfProxyServer
{
    public class RequestBodyStream : Stream
    {
        private readonly Stream _innerStream;
        private readonly MemoryStream _memoryStream = new MemoryStream();

        public RequestBodyStream(Stream innerStream)
        {
            _innerStream = innerStream;
        }

        public override bool CanRead => _innerStream.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _innerStream.CanWrite;
        public override long Length => _memoryStream.Length;
        public override long Position { get => _memoryStream.Position; set => _memoryStream.Position = value; }

        public override void Flush()
        {
            _innerStream.Flush();
            _memoryStream.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int bytesRead = _innerStream.Read(buffer, offset, count);
            _memoryStream.Write(buffer, offset, bytesRead);
            return bytesRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _innerStream.Write(buffer, offset, count);
        }

        public byte[] GetBufferedBody()
        {
            return _memoryStream.ToArray();
        }
    }

}
