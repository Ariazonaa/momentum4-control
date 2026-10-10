using System.Runtime.InteropServices;

namespace Momentum4.CoreHost;

public sealed partial class LinuxProtocolOutput : Stream
{
    public LinuxProtocolOutput()
    {
        var flags = Native.Fcntl(1, 3, 0);
        if (flags < 0 || Native.Fcntl(1, 4, flags | 2048) < 0) throw new IOException("Cannot configure protocol output");
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var written = TryWrite(buffer[offset..]);
            if (written > 0) { offset += written; continue; }
            var error = Marshal.GetLastPInvokeError();
            if (written < 0 && error == 4) continue;
            if (written < 0 && error == 11) { await Task.Delay(10, cancellationToken); continue; }
            throw new IOException("Protocol output closed");
        }
    }

    private static unsafe int TryWrite(ReadOnlyMemory<byte> buffer)
    {
        using var pinned = buffer.Pin();
        return checked((int)Native.Write(1, pinned.Pointer, (nuint)buffer.Length));
    }

    private static partial class Native
    {
        [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
        internal static partial int Fcntl(int descriptor, int command, int argument);
        [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
        internal static unsafe partial nint Write(int descriptor, void* buffer, nuint size);
    }
}
