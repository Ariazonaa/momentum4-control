using System.Buffers.Binary;

namespace Momentum4.Core.Tests;

internal static class DongleFirmwareFixture
{
    internal static byte[] Image(int payloadSize = 600, uint[]? compatible = null)
    {
        using var stream = new MemoryStream();
        compatible ??= [0x0003ffff];
        stream.Write("APPUHDR5"u8); Word(stream, (uint)(22 + compatible.Length * 4)); stream.Write("QCC518Xx"u8); Word(stream, 0x00040000);
        Span<byte> count = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(count, (ushort)compatible.Length); stream.Write(count);
        foreach (var item in compatible) Word(stream, item); stream.Write([0, 2, 0, 1, 0, 1, 0, 1]);
        stream.Write("PARTDATA"u8); Word(stream, (uint)payloadSize + 4); stream.Write([0, 0, 0, 1]); stream.Write(Enumerable.Range(0, payloadSize).Select(index => (byte)(index % 251)).ToArray());
        stream.Write("APPUPFTR"u8); Word(stream, 256); stream.Write(new byte[256]); return stream.ToArray();
    }
    internal static byte[] DataRequest(uint length, uint offset = 0)
    { var payload = new byte[8]; BinaryPrimitives.WriteUInt32BigEndian(payload, length); BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), offset); return payload; }
    private static void Word(Stream stream, uint value)
    { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); stream.Write(bytes); }
}
