using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace Momentum4.Protocol.Dongle;

public readonly record struct FirmwarePartition(ushort Kind, ushort Number, int Offset, int Size);

public sealed class FirmwareImage
{
    public const int MaximumSize = 16 * 1024 * 1024;
    public const int MaximumEntries = 256;
    private readonly byte[] data;
    private readonly byte[] resumeId;
    public FirmwareVersion Version { get; }
    public ushort ConfigVersion { get; }
    public ReadOnlyCollection<uint> CompatibleVersions { get; }
    public ReadOnlyCollection<ushort> CompatibleConfigs { get; }
    public ReadOnlyCollection<FirmwarePartition> Partitions { get; }
    public string Sha256 { get; }
    public byte[] ResumeId => resumeId.ToArray();
    public int Size => data.Length;
    public byte[] CopyData() => data.ToArray();

    private FirmwareImage(byte[] data, FirmwareVersion version, ushort config, uint[] versions, ushort[] configs, FirmwarePartition[] partitions)
    {
        this.data = data; Version = version; ConfigVersion = config; CompatibleVersions = Array.AsReadOnly(versions); CompatibleConfigs = Array.AsReadOnly(configs); Partitions = Array.AsReadOnly(partitions);
        Sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        var digest = MD5.HashData(data);
        uint crc = 0xffffffff;
        foreach (var item in digest)
        {
            crc ^= item;
            for (var bit = 0; bit < 8; bit++) crc = crc >> 1 ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        resumeId = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(resumeId, ~crc);
    }

    public static FirmwareImage Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 12 or > MaximumSize) throw new DongleProtocolException("Firmware exceeds image bounds");
        var outer = new Reader(bytes);
        if (!outer.Take(8).SequenceEqual("APPUHDR5"u8)) throw new DongleProtocolException("Unsupported firmware header");
        var header = new Reader(outer.TakeSize());
        if (!header.Take(8).SequenceEqual("QCC518Xx"u8)) throw new DongleProtocolException("Unsupported firmware variant");
        var firmwareWord = header.UInt32();
        var version = new FirmwareVersion((int)(firmwareWord >> 16), (int)(firmwareWord & 65535) / 100, (int)(firmwareWord & 65535) % 100);
        var versions = new uint[header.Count()];
        for (var index = 0; index < versions.Length; index++) versions[index] = header.UInt32();
        var config = header.UInt16(); var configs = new ushort[header.Count()];
        for (var index = 0; index < configs.Length; index++) configs[index] = header.UInt16();
        var extension = header.Take(header.Remaining);
        if (!extension.IsEmpty && !extension.SequenceEqual(new byte[] { 0, 1 })) throw new DongleProtocolException("Unsupported firmware header extension");
        if (versions.Distinct().Count() != versions.Length || configs.Distinct().Count() != configs.Length) throw new DongleProtocolException("Duplicate firmware compatibility entries");
        var partitions = new List<FirmwarePartition>(); var identities = new HashSet<(ushort, ushort)>(); var footer = false;
        while (outer.Remaining > 0)
        {
            if (partitions.Count >= MaximumEntries) throw new DongleProtocolException("Too many firmware sections");
            var tag = outer.Take(8); var size = outer.UInt32();
            if (tag.SequenceEqual("APPUPFTR"u8))
            {
                if (size != 256 || partitions.Count == 0) throw new DongleProtocolException("Unsupported or early firmware footer");
                outer.Take((int)size);
                if (outer.Remaining != 0) throw new DongleProtocolException("Data after firmware footer");
                footer = true; break;
            }
            if (!tag.SequenceEqual("PARTDATA"u8) || size <= 4 || size > (uint)outer.Remaining) throw new DongleProtocolException("Invalid firmware partition");
            var start = outer.Offset;
            var section = new Reader(outer.Take((int)size));
            var kind = section.UInt16(); var number = section.UInt16();
            if (!identities.Add((kind, number))) throw new DongleProtocolException("Duplicate firmware partition");
            partitions.Add(new FirmwarePartition(kind, number, start + 4, (int)size - 4));
        }
        if (!footer) throw new DongleProtocolException("Missing firmware footer");
        return new FirmwareImage(bytes.ToArray(), version, config, versions, configs, partitions.ToArray());
    }

    public void RequireCompatible(UpgradeVersion current)
    {
        if (!CompatibleConfigs.Contains(current.Config)) throw new DongleProtocolException("Current configuration is incompatible");
        var word = current.Firmware.WireWord;
        if (!CompatibleVersions.Any(item => item >> 16 == word >> 16 && ((item & 65535) == (word & 65535) || (item & 65535) == 65535)))
            throw new DongleProtocolException("Current firmware is incompatible");
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> data = data;
        public int Offset { get; private set; }
        public int Remaining => data.Length - Offset;
        public ReadOnlySpan<byte> Take(int size)
        {
            if (size < 0 || size > Remaining) throw new DongleProtocolException("Truncated firmware section");
            var result = data.Slice(Offset, size); Offset += size; return result;
        }
        public ushort UInt16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
        public uint UInt32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));
        public ReadOnlySpan<byte> TakeSize()
        {
            var size = UInt32();
            if (size > (uint)Remaining) throw new DongleProtocolException("Truncated firmware section");
            return Take((int)size);
        }
        public int Count()
        {
            var count = UInt16();
            if (count is < 1 or > MaximumEntries) throw new DongleProtocolException("Invalid firmware compatibility count");
            return count;
        }
    }
}
