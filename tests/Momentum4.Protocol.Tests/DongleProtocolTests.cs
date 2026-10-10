using System.Buffers.Binary;
using Momentum4.Protocol.Dongle;

namespace Momentum4.Protocol.Tests;

public sealed class DongleProtocolTests
{
    [Fact]
    public void VersionWireEncodingAndOrdering()
    {
        Assert.Equal(0x0003044cu, FirmwareVersion.Parse("3.11.0").WireWord);
        Assert.Equal("3.11.0", new FirmwareVersion(3, 11, 0).ToString());
        Assert.True(new FirmwareVersion(3, 11, 99) < new FirmwareVersion(3, 12, 0));
        foreach (var invalid in new[] { "3.11", "-1.2.3", "3.2.100", "3.700.0", "3.1.0junk" })
            Assert.Throws<FormatException>(() => FirmwareVersion.Parse(invalid));
    }

    [Fact]
    public void DescriptorsValidateScopeLengthOwnershipAndBounds()
    {
        HidDescriptor.Validate(Convert.FromHexString("06a2ff0901a10185347508953f09028102953f09019102c0"), HidInterface.Control);
        var valid = Convert.FromHexString("0600ff0901a10185037508953e0902b102850595fe090291028506950c09028102c0");
        HidDescriptor.Validate(valid, HidInterface.Upgrade);
        foreach (var invalid in new[] { valid[..^1], Replace(valid, [0x95, 0xfe], [0x95, 0xff]), Replace(valid, [9, 2, 0xb1], [9, 3, 0xb1]), new byte[] { 0xfe, 0xff }, valid.Concat(valid).ToArray(), valid.Concat(new byte[4097 - valid.Length]).ToArray(), new byte[] { 0xb4 }, Enumerable.Repeat((byte)0xa4, 17).ToArray() })
            Assert.Throws<DongleProtocolException>(() => HidDescriptor.Validate(invalid, HidInterface.Upgrade));
        Assert.Throws<DongleProtocolException>(() => HidDescriptor.Validate(valid, (HidInterface)9));
    }

    [Fact]
    public void CapturedDescriptorsIgnoreUnrelatedConsumerAndOtherCollections()
    {
        var control = Convert.FromHexString("050c0901a10185011500250109cd09b509b609b77501950481021500250109b009b109b309b47501950481221500250109e909ea75019502810209e2750195018106750195058101c006a1ff0901a10185030901150026ff007508953f910009027508950f8100c006a2ff0901a10185340901150026ff007508953f910009027508953f8100c00699ff0901a101859a150026ff007508953f0903b102859b15002501750195010904810695078101c0050b0905a1011500250175018502092f95018106092009219502812205090907090195018102950c81010508850909099501912295079101852009209501912295079101851709179501912295079101851809189501912295079101c0");
        var upgrade = Convert.FromHexString("0600ff0901a101150026ff0075080902963e0085019102090296fe008505910209029510850281020902960c00850681020902953e8503b1020902953e8504b102c00903a101090296be0185079102090296be01850881020902950b85098102c00903a101090295ff8520b102c0");
        HidDescriptor.Validate(control, HidInterface.Control); HidDescriptor.Validate(upgrade, HidInterface.Upgrade);
        Assert.Throws<DongleProtocolException>(() => HidDescriptor.Validate(Replace(control, [0x85, 0x34, 9, 1], [0x85, 0x34, 9, 3]), HidInterface.Control));
        Assert.Throws<DongleProtocolException>(() => HidDescriptor.Validate(Replace(upgrade, [0x96, 0x0c, 0, 0x85, 6], [0x96, 0x0d, 0, 0x85, 6]), HidInterface.Upgrade));
        Assert.Equal(Convert.FromHexString("030102").Concat(new byte[60]), DongleReports.FeatureRequest(HidFeatureOperation.Connect));
        Assert.Equal(Convert.FromHexString("030107").Concat(new byte[60]), DongleReports.FeatureRequest(HidFeatureOperation.Disconnect));
        Assert.Throws<DongleProtocolException>(() => DongleReports.FeatureRequest((HidFeatureOperation)3));
    }

    [Fact]
    public void ExactReportsAndUpgradeAllowlist()
    {
        Assert.Equal(Convert.FromHexString("34fe04020400").Concat(new byte[58]), DongleReports.ControlRequest(4, [4, 0]));
        Assert.Equal(Convert.FromHexString("0503190000").Concat(new byte[250]), DongleReports.UpgradeRequest(UpgradeOpcode.GetVersion, []));
        Assert.Equal(255, DongleReports.UpgradeRequest(UpgradeOpcode.Data, new byte[250]).Length);
        Assert.Throws<DongleProtocolException>(() => DongleReports.UpgradeRequest(UpgradeOpcode.Data, new byte[251]));
        foreach (var invalid in new[] { 0x0b, 0x0d, 0x1f })
            Assert.Throws<DongleProtocolException>(() => DongleReports.UpgradeRequest((UpgradeOpcode)invalid, []));
        Assert.Throws<DongleProtocolException>(() => DongleReports.ControlRequest(0x99, []));
    }

    [Fact]
    public void ReportsRejectMarkerOpcodeLengthAndDeviceErrors()
    {
        var good = UpgradeReply(0x1a, [0, 3, 4, 0x4c, 0, 1]);
        var parsed = DongleReports.UpgradeReply(good, [0x1a]);
        Assert.Equal(new byte[] { 0, 3, 4, 0x4c, 0, 1 }, parsed.Payload);
        foreach (var bad in new[] { Array.Empty<byte>(), new byte[13], good[..^1], Replace(good, [6, 9], [6, 8]), UpgradeReply(0x17, []), UpgradeReply(0x11, [0, 7]), UpgradeReply(0x11, []) })
            Assert.Throws<DongleProtocolException>(() => DongleReports.UpgradeReply(bad, [0x1a]));
        var control = ControlReply(3, [15]);
        Assert.Equal(new byte[] { 15 }, DongleReports.ControlReply(control, 3, [1, 2]));
        Assert.Null(DongleReports.ControlReply(ControlReply(3, [], 0xfc), 3, [1, 2]));
        foreach (var bad in new[] { new byte[64], control[..^1], ControlReply(4, [15]), ControlReply(3, []), ControlReply(3, new byte[3]), ControlReply(3, [15], 0xfe) })
            Assert.Throws<DongleProtocolException>(() => DongleReports.ControlReply(bad, 3, [1, 2]));
    }

    [Fact]
    public void ImagePreservesImmutableBytesCompatibilityAndDigests()
    {
        var bytes = Image();
        var image = FirmwareImage.Parse(bytes);
        Assert.Equal(new FirmwareVersion(4, 0, 0), image.Version);
        Assert.Equal((ushort)2, image.ConfigVersion);
        Assert.Equal(new uint[] { 0x0003ffff }, image.CompatibleVersions);
        Assert.Equal(new ushort[] { 1 }, image.CompatibleConfigs);
        Assert.Single(image.Partitions);
        Assert.Equal(bytes, image.CopyData());
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(), image.Sha256);
        var clone = image.CopyData();
        clone[0] = 0;
        bytes[0] = 0;
        Assert.Equal((byte)'A', image.CopyData()[0]);
    }

    [Fact]
    public void ImageRejectsEveryTruncatedPrefixAndTrailingData()
    {
        var good = Image(payload: [1]);
        for (var end = 0; end < good.Length; end++)
            Assert.Throws<DongleProtocolException>(() => FirmwareImage.Parse(good.AsSpan(0, end)));
        Assert.Throws<DongleProtocolException>(() => FirmwareImage.Parse(good.Concat(new byte[] { 0 }).ToArray()));
    }

    [Fact]
    public void ImageRejectsVariantDuplicatesCountsTagsExtensionsAndDeclaredBounds()
    {
        var good = Image();
        var headerEnd = 12 + (int)BinaryPrimitives.ReadUInt32BigEndian(good.AsSpan(8));
        var partition = good[headerEnd..^268];
        var malformed = new[] {
            Replace(good, "QCC518Xx"u8.ToArray(), "OTHERxxx"u8.ToArray()),
            good[..headerEnd].Concat(partition).Concat(partition).Concat(good[^268..]).ToArray(),
            good[..headerEnd].Concat(good[^268..]).ToArray(),
            Replace(good, "APPUPFTR"u8.ToArray(), "UNKNOWN!"u8.ToArray()),
            Image(compatible: [0x0003ffff, 0x0003ffff]), Image(configs: [1, 1]), Image(compatible: []), Image(configs: []),
            Image(compatible: Enumerable.Range(0, 257).Select(index => (uint)index).ToArray()),
            Replace(good, [0, 1, (byte)'P', (byte)'A'], [0, 2, (byte)'P', (byte)'A']),
            good[..8].Concat(new byte[] { 255,255,255,255 }).Concat(good[12..]).ToArray(), Image(payload: new byte[16 * 1024 * 1024]) };
        foreach (var bad in malformed)
            Assert.Throws<DongleProtocolException>(() => FirmwareImage.Parse(bad));
    }

    public static byte[] Image(uint[]? compatible = null, ushort[]? configs = null, byte[]? payload = null)
    {
        compatible ??= [0x0003ffff]; configs ??= [1]; payload ??= new byte[600];
        using var header = new MemoryStream();
        header.Write("QCC518Xx"u8); Word(header, 0x00040000, 4); Word(header, (uint)compatible.Length, 2);
        foreach (var item in compatible) Word(header, item, 4);
        Word(header, 2, 2); Word(header, (uint)configs.Length, 2);
        foreach (var item in configs) Word(header, item, 2);
        header.Write([0, 1]);
        using var result = new MemoryStream();
        result.Write("APPUHDR5"u8); Word(result, (uint)header.Length, 4); result.Write(header.ToArray());
        result.Write("PARTDATA"u8); Word(result, (uint)payload.Length + 4, 4); result.Write([0, 0, 0, 1]); result.Write(payload);
        result.Write("APPUPFTR"u8); Word(result, 256, 4); result.Write(new byte[256]);
        return result.ToArray();
    }

    public static byte[] UpgradeReply(byte opcode, byte[] payload)
    {
        var report = new byte[13]; report[0] = 6; report[1] = (byte)(3 + payload.Length); report[2] = opcode;
        BinaryPrimitives.WriteUInt16BigEndian(report.AsSpan(3), (ushort)payload.Length); payload.CopyTo(report, 5); return report;
    }

    public static byte[] ControlReply(byte command, byte[] payload, byte marker = 0xff)
    {
        var report = new byte[64]; report[0] = 0x34; report[1] = marker; report[2] = command; report[3] = (byte)payload.Length;
        payload.CopyTo(report, 4); return report;
    }

    private static void Word(Stream stream, uint value, int size)
    {
        Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); stream.Write(bytes[(4 - size)..]);
    }

    private static byte[] Replace(byte[] data, byte[] from, byte[] to)
    {
        var output = data.ToArray(); var index = output.AsSpan().IndexOf(from); Assert.True(index >= 0); to.CopyTo(output, index); return output;
    }
}
