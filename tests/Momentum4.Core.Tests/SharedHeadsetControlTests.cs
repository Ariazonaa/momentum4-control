using Microsoft.Extensions.Logging.Abstractions;
using Momentum4.Core.Queue;
using Momentum4.Core.Services;
using Momentum4.Core.State;
using Momentum4.Core.Transport;
using Momentum4.Protocol;
using Momentum4.Protocol.Gaia;

namespace Momentum4.Core.Tests;

public sealed class SharedHeadsetControlTests
{
    [Fact(Timeout = 15000)]
    public async Task M5_capture_snapshot_is_typed_has_eight_bands_and_no_serial_read()
    {
        var fixture = new SharedHeadsetFixture();
        await using var service = await fixture.StartAsync();
        var snapshot = await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.Equal("M5AEBT", snapshot.Model);
        Assert.Equal("6.21.5", snapshot.Firmware.ToString());
        Assert.Equal(8, snapshot.EqGainsDb.Count);
        Assert.Equal(new[] { 60, 150, 350, 700, 1400, 2800, 6000, 12000 }, snapshot.EqFrequenciesHz);
        Assert.Contains(HeadsetControl.Equalizer, snapshot.Writable);
        Assert.DoesNotContain(HeadsetControl.ConnectPeer, snapshot.Writable);
        Assert.Equal(new[] { 0, 1 }, snapshot.Choices[HeadsetIntegerControl.AntiWind]);
        Assert.DoesNotContain(fixture.Requests, frame => frame.Vendor == 0x001D && frame.Command.Value == 3);
    }

    [Theory(Timeout = 15000)]
    [InlineData("Foreign", 6, 21, 5)]
    [InlineData("M5AEBT", 6, 21, 6)]
    [InlineData("M5AEBT BAD!", 6, 21, 5)]
    public async Task Unknown_identity_and_firmware_are_readonly(string model, int major, int minor, int patch)
    {
        var fixture = new SharedHeadsetFixture { Model = model, Firmware = [(byte)(major >> 8), (byte)major, (byte)(minor >> 8), (byte)minor, (byte)(patch >> 8), (byte)patch] };
        await using var service = await fixture.StartAsync();
        var snapshot = await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.Empty(snapshot.Writable);
        await Assert.ThrowsAsync<FeatureNotSupportedException>(() => service.SetBooleanControlAsync(HeadsetBooleanControl.SmartPause, true, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<FeatureNotSupportedException>(() => service.SetBehaviorAsync(BehaviorSetting.SmartPause, true, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Writes);
    }

    [Fact(Timeout = 15000)]
    public async Task Optional_rejection_revokes_only_affected_observation_and_capability()
    {
        var fixture = new SharedHeadsetFixture();
        await using var service = await fixture.StartAsync();
        Assert.True((await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken)).Settings.BatteryProtection);
        fixture.Rejected.Add(0x060b);
        var snapshot = await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.Null(snapshot.Settings.BatteryProtection);
        Assert.DoesNotContain(HeadsetControl.BatteryProtection, snapshot.Writable);
        Assert.Contains(HeadsetControl.SmartPause, snapshot.Writable);
        await Assert.ThrowsAsync<FeatureNotSupportedException>(() => service.SetBooleanControlAsync(HeadsetBooleanControl.BatteryProtection, false, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Writes);
    }

    [Fact(Timeout = 15000)]
    public async Task Old_valid_then_malformed_revokes_session()
    {
        var fixture = new SharedHeadsetFixture();
        await using var service = await fixture.StartAsync();
        Assert.True((await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken)).VerifiedSession);
        fixture.Overrides[0x060b] = [2];
        await Assert.ThrowsAsync<ProtocolFormatException>(() => service.GetControlSnapshotAsync(TestContext.Current.CancellationToken));
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(ConnectionState.Disconnected, service.Store.Current.Connection);
        Assert.Equal(1, fixture.Transport.ConnectCount);
        Assert.Empty(fixture.Writes);
    }

    [Fact(Timeout = 15000)]
    public async Task M5_antiwind_writes_single_pair_and_rejects_unverified_value()
    {
        var fixture = new SharedHeadsetFixture();
        fixture.Headset.TransparentHearing = false;
        await using var service = await fixture.StartAsync();
        var result = await service.SetIntegerControlAsync(HeadsetIntegerControl.AntiWind, 0, TestContext.Current.CancellationToken);
        Assert.Equal(0, result.Settings.AntiWind);
        Assert.Equal(new byte[] { 1, 0 }, Assert.Single(fixture.Writes).Payload);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SetIntegerControlAsync(HeadsetIntegerControl.AntiWind, 2, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SetAntiWindAsync(Momentum4.Protocol.Features.AntiWindMode.Automatic, TestContext.Current.CancellationToken));
        Assert.Single(fixture.Writes);
    }

    [Fact(Timeout = 15000)]
    public async Task Raw_anc_strength_preserves_adaptive_mode_while_windows_level_selects_custom()
    {
        var fixture = new SharedHeadsetFixture();
        fixture.Headset.AncTable = [1, 1, 2, 0, 3, 1];
        fixture.Headset.TransparentHearing = false;
        await using var service = await fixture.StartAsync();
        var result = await service.SetIntegerControlAsync(HeadsetIntegerControl.AncStrength, 30, TestContext.Current.CancellationToken);
        Assert.True(result.Settings.AdaptiveAnc);
        Assert.Equal(30, result.Settings.AncStrength);
        Assert.Equal(0x1a02, Assert.Single(fixture.Writes).Command.Value);
        var windows = await service.SetNoiseControlLevelAsync(40, TestContext.Current.CancellationToken);
        Assert.Equal(NoiseControlMode.Custom, windows.Mode);
        Assert.Equal(40, windows.Level);
    }

    [Fact(Timeout = 15000)]
    public async Task Transparency_level_prepares_100_then_writes_level_and_preserves_siblings()
    {
        var fixture = new SharedHeadsetFixture();
        fixture.Headset.TransparentHearing = false;
        fixture.Headset.AncLevel = 0;
        await using var service = await fixture.StartAsync();
        var result = await service.SetIntegerControlAsync(HeadsetIntegerControl.TransparencyLevel, 65, TestContext.Current.CancellationToken);
        Assert.Equal(65, result.Settings.TransparencyLevel);
        Assert.Equal(65, result.Settings.AncStrength);
        Assert.False(result.Settings.Transparency);
        Assert.True(result.Settings.AncEnabled);
        Assert.Equal(1, result.Settings.AntiWind);
        Assert.False(result.Settings.AdaptiveAnc);
        Assert.Equal(new ushort[] { 0x1804, 0x1802 }, fixture.Writes.Select(frame => frame.Command.Value));
        var prepare = fixture.Requests.FindIndex(frame => frame.Command.Value == 0x1804);
        var level = fixture.Requests.FindIndex(frame => frame.Command.Value == 0x1802);
        Assert.Contains(fixture.Requests.Skip(prepare + 1).Take(level - prepare - 1), frame => frame.Command.Value == 0x1803);
    }

    [Fact(Timeout = 15000)]
    public async Task Spatial_conflicts_missing_capabilities_and_sibling_change_fail_closed()
    {
        var fixture = new SharedHeadsetFixture();
        fixture.Headset.SoundMode = 3;
        await using var service = await fixture.StartAsync();
        await Assert.ThrowsAsync<FeatureNotSupportedException>(() => service.SetBooleanControlAsync(HeadsetBooleanControl.ImmersiveAudio, true, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Writes);
        fixture.Headset.SoundMode = 1;
        await Assert.ThrowsAsync<FeatureNotSupportedException>(() => service.SetBooleanControlAsync(HeadsetBooleanControl.HeadTracking, true, TestContext.Current.CancellationToken));
        fixture.SpatialSiblingMutation = true;
        await Assert.ThrowsAnyAsync<SettingNotAppliedException>(() => service.SetBooleanControlAsync(HeadsetBooleanControl.ImmersiveAudio, true, TestContext.Current.CancellationToken));
        Assert.Single(fixture.Writes);
    }

    [Fact(Timeout = 15000)]
    public async Task Eq_validates_identity_order_and_all_gains_before_first_write_and_never_changes_mode()
    {
        var fixture = new SharedHeadsetFixture();
        fixture.Headset.SoundMode = 2;
        await using var service = await fixture.StartAsync();
        var snapshot = await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        var topology = new HeadsetEqTopology(snapshot.Model, snapshot.EqFrequenciesHz);
        await Assert.ThrowsAsync<FeatureNotSupportedException>(() => service.SetControlEqualizerAsync(new decimal[8], topology with { Model = "M4AEBT" }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<FeatureNotSupportedException>(() => service.SetControlEqualizerAsync(new decimal[8], topology with { FrequenciesHz = snapshot.EqFrequenciesHz.Reverse().ToArray() }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SetControlEqualizerAsync(new decimal[] { 1, 2, 3, 4, 5, 6, 0, 6.1m }, topology, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Writes);
        var result = await service.SetControlEqualizerAsync(new decimal[] { 1, 2, 3, 4, 5, 6, 0, 1 }, topology, TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Settings.SoundMode);
        Assert.All(fixture.Writes, frame => Assert.Equal(0x1001, frame.Command.Value));
    }

    [Fact(Timeout = 15000)]
    public async Task Eq_partial_write_is_truthful_and_never_replayed_or_rolled_back()
    {
        var fixture = new SharedHeadsetFixture();
        await using var service = await fixture.StartAsync();
        var snapshot = await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        fixture.Headset.RejectEqBand = 2;
        await Assert.ThrowsAsync<HeadsetPartialWriteException>(() => service.SetControlEqualizerAsync(Enumerable.Repeat(1m, 8).ToArray(), new HeadsetEqTopology(snapshot.Model, snapshot.EqFrequenciesHz), TestContext.Current.CancellationToken));
        Assert.Equal(new byte[] { 0, 1, 2 }, fixture.Writes.Select(frame => frame.Payload[0]));
        Assert.Equal(new byte[] { 10, 10, 0, 0, 0, 0, 0, 0 }, fixture.Headset.EqGains);
    }

    [Theory(Timeout = 15000)]
    [InlineData("12345678901234567")]
    [InlineData("ééééééééé")]
    [InlineData("bad\nname")]
    public async Task M5_rename_rejects_invalid_utf8_byte_length_or_unprintable_name(string name)
    {
        var fixture = new SharedHeadsetFixture();
        await using var service = await fixture.StartAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => service.RenameControlAsync(name, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Writes);
        Assert.Equal("Renamed", (await service.RenameControlAsync("Renamed", TestContext.Current.CancellationToken)).DeviceName);
        Assert.Single(fixture.Writes);
    }
}

internal sealed class SharedHeadsetFixture
{
    public SimulatedHeadset Headset { get; } = new() { EqGains = new byte[8] };
    public FakeTransport Transport { get; }
    public string Model { get; set; } = "M5AEBT";
    public byte[] Firmware { get; set; } = [0, 6, 0, 21, 0, 5];
    public HashSet<ushort> Rejected { get; } = [];
    public Dictionary<ushort, byte[]> Overrides { get; } = [];
    public List<GaiaFrame> Requests { get; } = [];
    public List<GaiaFrame> Writes { get; } = [];
    public bool SpatialSiblingMutation { get; set; }
    public Action<GaiaFrame>? OnRequest { get; set; }
    public Microsoft.Extensions.Time.Testing.FakeTimeProvider? Time { get; set; }

    public TimeSpan PeerConnectTimeout { get; set; } = TimeSpan.FromSeconds(8);
    public Dictionary<ushort, byte> ExtraValues { get; } = new() { [0x0806] = 3, [0x060b] = 1, [0x3605] = 0, [0x3603] = 0 };
    public int[] Frequencies { get; } = [60, 150, 350, 700, 1400, 2800, 6000, 12000];

    public SharedHeadsetFixture()
    {
        Transport = new FakeTransport { Responder = Respond };
        Transport.SetState(TransportState.Disconnected);
    }

    public async Task<Momentum4Service> StartAsync(bool polling = false)
    {
        var service = new Momentum4Service(Transport, CommandPolicy.Production,
            new Momentum4ServiceOptions { RegisterNotifications = false, ReadSerialNumber = false, EnablePolling = polling, Reconnect = false, PeerConnectTimeout = PeerConnectTimeout }, NullLoggerFactory.Instance, Time);
        service.Start();
        for (var attempt = 0; attempt < 1000 && service.Store.Current.Connection != ConnectionState.Connected; attempt++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.Equal(ConnectionState.Connected, service.Store.Current.Connection);
        return service;
    }

    private IEnumerable<byte[]> Respond(byte[] bytes)
    {
        var frame = new GaiaFrameReader().Push(bytes).Frames.Single();
        Requests.Add(frame);
        OnRequest?.Invoke(frame);
        var id = frame.Command.Value;
        if (Rejected.Contains(id))
        {
            return [GaiaFrameCodec.Encode(frame.Vendor, frame.Command.AsError(), new byte[] { 1 })];
        }

        if (Overrides.TryGetValue(id, out var payload))
        {
            return [GaiaFrameCodec.Encode(frame.Vendor, frame.Command.AsResponse(), payload)];
        }

        if (Momentum4.Protocol.Catalog.CommandCatalog.Find(frame.Vendor, frame.Command)?.Safety is Momentum4.Protocol.Catalog.SafetyClass.Setting or Momentum4.Protocol.Catalog.SafetyClass.Action)
        {
            Writes.Add(frame);
        }

        payload = id switch
        {
            0x1206 => System.Text.Encoding.UTF8.GetBytes(Model),
            0x1201 => Firmware,
            0x1000 => [8, 196, 60],
            0x100b => [frame.Payload[0], (byte)(Frequencies[frame.Payload[0]] >> 8), (byte)Frequencies[frame.Payload[0]]],
            0x0602 => [0],
            0x2001 => [0],
            0x1803 => [Headset.AncLevel],
            _ => [],
        };
        if (payload.Length > 0)
        {
            return [GaiaFrameCodec.Encode(frame.Vendor, frame.Command.AsResponse(), payload)];
        }

        if (ExtraValues.TryGetValue(id, out var value))
        {
            return [GaiaFrameCodec.Encode(frame.Vendor, frame.Command.AsResponse(), new byte[] { value })];
        }

        var getter = id switch { 0x0805 => (ushort)0x0806, 0x060a => (ushort)0x060b, 0x3604 => (ushort)0x3605, 0x3602 => (ushort)0x3603, _ => (ushort)0 };
        if (getter != 0)
        {
            ExtraValues[getter] = frame.Payload[0];
            if (SpatialSiblingMutation && id == 0x3604)
            {
                ExtraValues[0x3603] = 1;
            }

            return [GaiaFrameCodec.Encode(frame.Vendor, frame.Command.AsResponse(), [])];
        }

        if (id == 0x1802)
        {
            Headset.AncLevel = frame.Payload[0];
            Headset.TransparentHearing = frame.Payload[0] == 100;
            return [GaiaFrameCodec.Encode(frame.Vendor, frame.Command.AsResponse(), [])];
        }

        return Headset.Respond(bytes);
    }
}
