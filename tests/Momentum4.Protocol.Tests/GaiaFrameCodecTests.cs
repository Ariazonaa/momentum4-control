// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Momentum4.Protocol.Gaia;

namespace Momentum4.Protocol.Tests;

public sealed class GaiaFrameCodecTests
{
    [Fact]
    public void Encodes_request_without_payload()
    {
        var frame = GaiaFrameCodec.Encode(0x0495, new CommandWord(0x0603), []);

        Assert.Equal("FF 03 00 00 04 95 06 03", Hex.Format(frame));
    }

    [Fact]
    public void Encodes_request_with_payload_like_reference_implementation()
    {
        // Testvektor aus [MC] (Sources/CoreTests): Geräteeintrag 2 lesen
        var frame = GaiaFrameCodec.Encode(0x0495, new CommandWord(0x1401), [0x02]);

        Assert.Equal("FF 03 00 01 04 95 14 01 02", Hex.Format(frame));
    }

    [Fact]
    public void Encodes_qualcomm_vendor()
    {
        // Wie ymg2006 (BluetoothSocketWrapper.cpp) als GAIA-Probe
        Assert.Equal("FF 03 00 00 00 1D 00 00", Hex.Format(GaiaFrameCodec.Encode(0x001D, new CommandWord(0x0000), [])));
    }

    [Fact]
    public void Rejects_payload_longer_than_255_bytes()
    {
        Assert.Throws<ArgumentException>(() => GaiaFrameCodec.Encode(0x0495, new CommandWord(0x1001), new byte[256]));
    }

    [Fact]
    public void Encoded_frame_round_trips_through_reader()
    {
        var encoded = GaiaFrameCodec.Encode(0x0495, new CommandWord(0x1001), [0x02, 0xEC]);

        var frame = Assert.Single(new GaiaFrameReader().Push(encoded).Frames);
        Assert.Equal((ushort)0x0495, frame.Vendor);
        Assert.Equal(new CommandWord(0x1001), frame.Command);
        Assert.Equal(new byte[] { 0x02, 0xEC }, frame.Payload);
        Assert.Equal(encoded, frame.Raw);
    }
}
