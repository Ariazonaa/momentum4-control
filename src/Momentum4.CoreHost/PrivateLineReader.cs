using System.Buffers;

namespace Momentum4.CoreHost;

public sealed class PrivateLineReader(Stream input)
{
    private readonly byte[] _buffer = new byte[4096];
    private int _offset;
    private int _count;

    public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
    {
        var line = new ArrayBufferWriter<byte>();
        while (true)
        {
            if (_offset == _count)
            {
                _count = await input.ReadAsync(_buffer, cancellationToken);
                _offset = 0;
                if (_count == 0)
                {
                    if (line.WrittenCount != 0) throw new PrivateProtocolException();
                    return null;
                }
            }
            var newline = Array.IndexOf(_buffer, (byte)10, _offset, _count - _offset);
            var length = (newline < 0 ? _count : newline) - _offset;
            if (length > PrivateProtocol.RequestLimit - line.WrittenCount) throw new PrivateProtocolException();
            line.Write(_buffer.AsSpan(_offset, length));
            _offset += length;
            if (newline >= 0)
            {
                _offset++;
                if (line.WrittenCount == 0) throw new PrivateProtocolException();
                return line.WrittenSpan.ToArray();
            }
        }
    }
}

internal sealed class BoundedReplyStream : MemoryStream
{
    private void Guard(int count) { if (count < 0 || Position + count > PrivateProtocol.ReplyLimit) throw new PrivateProtocolException(); }
    public override void Write(byte[] buffer, int offset, int count) { Guard(count); base.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { Guard(buffer.Length); base.Write(buffer); }
    public override void WriteByte(byte value) { Guard(1); base.WriteByte(value); }
}
