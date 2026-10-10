using System.Buffers.Binary;

namespace Momentum4.Protocol.Dongle;

public static class DongleReports
{
    public const int MaximumReportLength = 255;
    private static readonly byte[] ControlCommands = [0x12, 6, 1, 3, 5, 7, 8, 0x15, 4];

    public static byte[] ControlRequest(byte command, ReadOnlySpan<byte> payload)
    {
        if (!ControlCommands.Contains(command) || payload.Length > 60 || (command == 4 ? payload.Length != 2 : !payload.IsEmpty))
            throw new DongleProtocolException("Forbidden or oversized control command");
        var report = new byte[64]; report[0] = 0x34; report[1] = 0xfe; report[2] = command; report[3] = (byte)payload.Length;
        payload.CopyTo(report.AsSpan(4)); return report;
    }

    public static byte[]? ControlReply(ReadOnlySpan<byte> report, byte command, ReadOnlySpan<int> lengths)
    {
        if (report.Length != 64 || report[0] != 0x34 || report[3] > 60)
            throw new DongleProtocolException("Malformed control report");
        if (report[1] == 0xfc) return null;
        if (report[1] != 0xff || report[2] != command || !lengths.Contains(report[3]))
            throw new DongleProtocolException("Unexpected control reply");
        return report.Slice(4, report[3]).ToArray();
    }

    public static byte[] FeatureRequest(HidFeatureOperation operation)
    {
        if (!Enum.IsDefined(operation)) throw new DongleProtocolException("Forbidden feature operation");
        var report = new byte[63]; report[0] = 3; report[1] = 1; report[2] = (byte)operation; return report;
    }

    public static void ValidateConnection(ReadOnlySpan<byte> report)
    {
        if (report.Length != 13 || !report[..3].SequenceEqual(new byte[] { 6, 1, 0 }))
            throw new DongleProtocolException("Upgrade connection rejected");
    }

    public static byte[] UpgradeRequest(UpgradeOpcode opcode, ReadOnlySpan<byte> payload)
    {
        if (!Enum.IsDefined(opcode) || payload.Length > 250)
            throw new DongleProtocolException("Forbidden or oversized upgrade command");
        var report = new byte[255]; report[0] = 5; report[1] = (byte)(3 + payload.Length); report[2] = (byte)opcode;
        BinaryPrimitives.WriteUInt16BigEndian(report.AsSpan(3), (ushort)payload.Length); payload.CopyTo(report.AsSpan(5)); return report;
    }

    public static (byte Opcode, byte[] Payload) UpgradeReply(ReadOnlySpan<byte> report, ReadOnlySpan<byte> expected)
    {
        if (report.Length != 13 || report[0] != 6) throw new DongleProtocolException("Malformed upgrade report");
        var size = BinaryPrimitives.ReadUInt16BigEndian(report[3..]);
        if (size > 8 || report[1] != size + 3) throw new DongleProtocolException("Malformed upgrade payload length");
        if (report[2] == 0x11)
        {
            if (size != 2) throw new DongleProtocolException("Malformed device error");
            throw new DongleProtocolException($"Device error {BinaryPrimitives.ReadUInt16BigEndian(report[5..])}");
        }
        if (!expected.Contains(report[2])) throw new DongleProtocolException("Unexpected upgrade reply");
        return (report[2], report.Slice(5, size).ToArray());
    }

    public static UpgradeVersion DecodeVersion(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 6) throw new DongleProtocolException("Invalid upgrade version length");
        var major = BinaryPrimitives.ReadUInt16BigEndian(payload);
        var minor = BinaryPrimitives.ReadUInt16BigEndian(payload[2..]);
        return new UpgradeVersion(new FirmwareVersion(major, minor / 100, minor % 100), BinaryPrimitives.ReadUInt16BigEndian(payload[4..]));
    }
}
