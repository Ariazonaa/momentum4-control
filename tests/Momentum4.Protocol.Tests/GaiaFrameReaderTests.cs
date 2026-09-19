// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Momentum4.Protocol.Gaia;

namespace Momentum4.Protocol.Tests;

// Die Frames sind nach dem Schema aus docs/protocol.md §2 gebaut. Echte Mitschnitte liegen in tests/CapturedPackets.
public sealed class GaiaFrameReaderTests
{
    private const string BatteryResponse70 = "FF 03 00 01 04 95 07 03 46";

    [Fact]
    public void Parses_single_frame()
    {
        var result = new GaiaFrameReader().Push(Hex.Parse(BatteryResponse70));

        var frame = Assert.Single(result.Frames);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(3, frame.Version);
        Assert.Equal(0, frame.Flags);
        Assert.Equal((ushort)0x0495, frame.Vendor);
        Assert.Equal(new CommandWord(0x0703), frame.Command);
        Assert.Equal(PacketType.Response, frame.Type);
        Assert.Equal(new byte[] { 0x46 }, frame.Payload);
    }

    [Fact]
    public void Reassembles_three_byte_fragments_after_noise_like_reference_test()
    {
        // [MC]-Test: Frame übersteht 3-Byte-Reads und ein vorangestelltes "00 7E".
        var data = Hex.Parse("00 7E FF 03 00 01 04 95 14 01 02");
        var reader = new GaiaFrameReader();
        var frames = new List<GaiaFrame>();
        var diagnostics = new List<GaiaReaderDiagnostic>();

        for (var i = 0; i < data.Length; i += 3)
        {
            var result = reader.Push(data.AsSpan(i, Math.Min(3, data.Length - i)));
            frames.AddRange(result.Frames);
            diagnostics.AddRange(result.Diagnostics);
        }

        var frame = Assert.Single(frames);
        Assert.Equal(new CommandWord(0x1401), frame.Command);
        Assert.Equal(new byte[] { 0x02 }, frame.Payload);
        var discarded = Assert.Single(diagnostics);
        Assert.Equal(GaiaReaderDiagnosticKind.DiscardedBytes, discarded.Kind);
        Assert.Equal(new byte[] { 0x00, 0x7E }, discarded.Bytes);
        Assert.Equal(0, reader.PendingByteCount);
    }

    [Fact]
    public void Returns_multiple_frames_from_one_read()
    {
        var data = Hex.Parse(BatteryResponse70 + " FF 03 00 01 04 95 1A 85 00");

        var result = new GaiaFrameReader().Push(data);

        Assert.Equal(2, result.Frames.Count);
        Assert.Equal(PacketType.Notification, result.Frames[1].Type);
    }

    [Fact]
    public void Waits_for_incomplete_frame()
    {
        var reader = new GaiaFrameReader();

        Assert.Empty(reader.Push(Hex.Parse("FF 03 00 05 04 95")).Frames);
        Assert.Equal(6, reader.PendingByteCount);

        var frame = Assert.Single(reader.Push(Hex.Parse("13 06 41 42 43 44 45")).Frames);
        Assert.Equal("41 42 43 44 45", Hex.Format(frame.Payload));
    }

    [Fact]
    public void Payload_containing_ff_does_not_break_framing()
    {
        var result = new GaiaFrameReader().Push(Hex.Parse("FF 03 00 02 04 95 11 02 FF FF " + BatteryResponse70));

        Assert.Equal(2, result.Frames.Count);
        Assert.Equal("FF FF", Hex.Format(result.Frames[0].Payload));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Skips_header_with_unknown_version_and_resynchronises()
    {
        var result = new GaiaFrameReader().Push(Hex.Parse("FF 07 00 00 00 " + BatteryResponse70));

        Assert.Single(result.Frames);
        Assert.Contains(result.Diagnostics, d => d.Kind == GaiaReaderDiagnosticKind.InvalidHeader);
    }

    [Fact]
    public void Skips_header_with_unknown_flag_bits()
    {
        var result = new GaiaFrameReader().Push(Hex.Parse("FF 03 04 00 " + BatteryResponse70));

        Assert.Single(result.Frames);
        Assert.Contains(result.Diagnostics, d => d.Kind == GaiaReaderDiagnosticKind.InvalidHeader);
    }

    [Fact]
    public void Accepts_frame_with_valid_checksum_and_reports_unusual_flags()
    {
        var frame = WithChecksum("FF 03 01 01 04 95 07 03 46");

        var result = new GaiaFrameReader().Push(frame);

        var parsed = Assert.Single(result.Frames);
        Assert.Equal(1, parsed.Flags);
        Assert.Equal(new byte[] { 0x46 }, parsed.Payload);
        Assert.Contains(result.Diagnostics, d => d.Kind == GaiaReaderDiagnosticKind.UnusualFlags);
    }

    [Fact]
    public void Rejects_frame_with_wrong_checksum()
    {
        var frame = WithChecksum("FF 03 01 01 04 95 07 03 46");
        frame[^1] ^= 0x01;

        var result = new GaiaFrameReader().Push(frame);

        Assert.Empty(result.Frames);
        Assert.Contains(result.Diagnostics, d => d.Kind == GaiaReaderDiagnosticKind.ChecksumMismatch);
    }

    [Fact]
    public void Reads_two_byte_length_when_flag_is_set()
    {
        var result = new GaiaFrameReader().Push(Hex.Parse("FF 04 02 00 03 04 95 07 03 AA BB CC"));

        var frame = Assert.Single(result.Frames);
        Assert.Equal(4, frame.Version);
        Assert.Equal("AA BB CC", Hex.Format(frame.Payload));
    }

    [Fact]
    public void Rejects_two_byte_length_above_limit()
    {
        var result = new GaiaFrameReader().Push(Hex.Parse("FF 04 02 FF FF 04 95 07 03"));

        Assert.Empty(result.Frames);
        Assert.Contains(result.Diagnostics, d => d.Kind == GaiaReaderDiagnosticKind.InvalidHeader);
    }

    [Fact]
    public void Tolerates_unknown_vendor_and_command()
    {
        var frame = Assert.Single(new GaiaFrameReader().Push(Hex.Parse("FF 03 00 00 12 34 7F 7F")).Frames);

        Assert.Equal((ushort)0x1234, frame.Vendor);
        Assert.Equal(new CommandWord(0x7F7F), frame.Command);
    }

    [Fact]
    public void Discards_data_without_start_of_frame()
    {
        var reader = new GaiaFrameReader();

        var result = reader.Push(Hex.Parse("00 01 02"));

        Assert.Empty(result.Frames);
        Assert.Equal(GaiaReaderDiagnosticKind.DiscardedBytes, Assert.Single(result.Diagnostics).Kind);
        Assert.Equal(0, reader.PendingByteCount);
    }

    [Fact]
    public void Reset_drops_pending_bytes()
    {
        var reader = new GaiaFrameReader();
        reader.Push(Hex.Parse("FF 03 00 05"));

        reader.Reset();

        Assert.Equal(0, reader.PendingByteCount);
        Assert.Single(reader.Push(Hex.Parse(BatteryResponse70)).Frames);
    }

    private static byte[] WithChecksum(string hex)
    {
        var bytes = Hex.Parse(hex);
        byte xor = 0;
        foreach (var b in bytes)
        {
            xor ^= b;
        }

        return [.. bytes, xor];
    }
}
