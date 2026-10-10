using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Momentum4.Core.Queue;
using Momentum4.Core.Services;
using Momentum4.Core.State;
using Momentum4.Core.Transport;
using Momentum4.Protocol;
using Momentum4.Protocol.Catalog;
using Momentum4.Protocol.Gaia;

namespace Momentum4.Core.Tests;

public sealed class SharedHeadsetCaptureTests
{
    [Fact(Timeout = 15000)]
    public async Task Actual_m5_captures_decode_eight_bands_and_metadata_without_private_reads()
    {
        var replay = new HeadsetCaptureReplay();
        await using var service = await replay.StartAsync();
        var snapshot = await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.Equal("M5AEBT Black", snapshot.Model);
        Assert.Equal("6.21.5", snapshot.Firmware.ToString());
        Assert.Equal(Enumerable.Repeat(0m, 8), snapshot.EqGainsDb);
        Assert.Equal(new[] { 90, 325, 1500, 2500, 4500, 8000, 13000, 16000 }, snapshot.EqFrequenciesHz);
        Assert.Equal(-6m, snapshot.EqMinimumDb);
        Assert.Equal(6m, snapshot.EqMaximumDb);
        Assert.Equal(900, snapshot.Settings.AutoPowerOff);
        Assert.Equal(0, snapshot.Settings.Sidetone);
        Assert.Equal(3, snapshot.WearState);
        Assert.Null(snapshot.DeviceName);
        Assert.False(snapshot.PeersAvailable);
        Assert.Contains(HeadsetControl.Equalizer, snapshot.Writable);
        Assert.Empty(replay.Writes);
    }

    [Theory(Timeout = 15000)]
    [InlineData(HeadsetBooleanControl.TouchControls, false)]
    [InlineData(HeadsetBooleanControl.BassBoost, false)]
    [InlineData(HeadsetBooleanControl.SmartPause, true)]
    [InlineData(HeadsetBooleanControl.ComfortCall, true)]
    [InlineData(HeadsetBooleanControl.AdaptiveAnc, true)]
    [InlineData(HeadsetBooleanControl.Transparency, true)]
    [InlineData(HeadsetBooleanControl.AncEnabled, false)]
    [InlineData(HeadsetBooleanControl.OnHeadDetection, true)]
    [InlineData(HeadsetBooleanControl.AutoAnswer, false)]
    [InlineData(HeadsetBooleanControl.PauseOnTransparency, false)]
    [InlineData(HeadsetBooleanControl.ImmersiveAudio, false)]
    [InlineData(HeadsetBooleanControl.BatteryProtection, false)]
    public async Task Verified_boolean_capture_change_readback_and_restore(HeadsetBooleanControl control, bool value)
    {
        var replay = new HeadsetCaptureReplay(spatialCapture: control == HeadsetBooleanControl.ImmersiveAudio);
        await using var service = await replay.StartAsync();
        var original = (await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken)).Settings.Get(control)!.Value;
        Assert.NotEqual(original, value);
        var changed = await service.SetBooleanControlAsync(control, value, TestContext.Current.CancellationToken);
        Assert.Equal(value, changed.Settings.Get(control));
        var restored = await service.SetBooleanControlAsync(control, original, TestContext.Current.CancellationToken);
        Assert.Equal(original, restored.Settings.Get(control));
        Assert.Equal(2, replay.Writes.Count);
    }

    [Fact(Timeout = 15000)]
    public async Task Head_tracking_enable_replays_captured_state_and_preserves_spatial_siblings()
    {
        var replay = new HeadsetCaptureReplay(spatialCapture: true);
        await using var service = await replay.StartAsync();
        var before = await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.True(before.Settings.ImmersiveAudio);
        Assert.False(before.Settings.HeadTracking);
        Assert.Equal(0, before.Settings.SoundMode);
        var after = await service.SetBooleanControlAsync(HeadsetBooleanControl.HeadTracking, true, TestContext.Current.CancellationToken);
        Assert.True(after.Settings.HeadTracking);
        Assert.Equal(before.Settings.ImmersiveAudio, after.Settings.ImmersiveAudio);
        Assert.Equal(before.Settings.SoundMode, after.Settings.SoundMode);
        Assert.Equal(0x3602, Assert.Single(replay.Writes).Command.Value);
        Assert.Equal(new byte[] { 1 }, replay.Writes[0].Payload);
    }

    [Theory(Timeout = 15000)]
    [InlineData(HeadsetIntegerControl.AudioPrompts, 0)]
    [InlineData(HeadsetIntegerControl.AudioPrompts, 1)]
    [InlineData(HeadsetIntegerControl.AntiWind, 1)]
    [InlineData(HeadsetIntegerControl.AncStrength, 1)]
    [InlineData(HeadsetIntegerControl.SoundMode, 0)]
    [InlineData(HeadsetIntegerControl.SoundMode, 2)]
    [InlineData(HeadsetIntegerControl.Sidetone, 1)]
    [InlineData(HeadsetIntegerControl.AutoPowerOff, 1800)]
    public async Task Verified_integer_capture_change_readback_and_restore(HeadsetIntegerControl control, int value)
    {
        var replay = new HeadsetCaptureReplay();
        await using var service = await replay.StartAsync();
        var original = (await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken)).Settings.Get(control)!.Value;
        Assert.NotEqual(original, value);
        Assert.Equal(value, (await service.SetIntegerControlAsync(control, value, TestContext.Current.CancellationToken)).Settings.Get(control));
        Assert.Equal(original, (await service.SetIntegerControlAsync(control, original, TestContext.Current.CancellationToken)).Settings.Get(control));
        Assert.Equal(2, replay.Writes.Count);
        if (control == HeadsetIntegerControl.AntiWind)
        {
            Assert.Equal(new byte[] { 1, 1 }, replay.Writes[0].Payload);
        }
    }

    [Fact(Timeout = 15000)]
    public async Task All_eight_bands_replay_observed_gain_change_and_restore()
    {
        var replay = new HeadsetCaptureReplay();
        await using var service = await replay.StartAsync();
        var snapshot = await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        var topology = new HeadsetEqTopology(snapshot.Model, snapshot.EqFrequenciesHz);
        for (var band = 0; band < 8; band++)
        {
            Assert.Equal(0.1m, (await service.SetControlEqBandAsync(band, 0.1m, topology, TestContext.Current.CancellationToken)).EqGainsDb[band]);
            Assert.Equal(new decimal[8], (await service.SetControlEqBandAsync(band, 0m, topology, TestContext.Current.CancellationToken)).EqGainsDb);
        }

        Assert.Equal(16, replay.Writes.Count);
    }

    [Fact(Timeout = 15000)]
    public async Task Repeated_transparency_levels_replay_actual_hardware_transitions()
    {
        var replay = new HeadsetCaptureReplay(transparencyTransitions: true);
        await using var service = await replay.StartAsync();
        var before = await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        foreach (var level in new[] { 75, 50, 0, 100 })
        {
            var after = await service.SetIntegerControlAsync(HeadsetIntegerControl.TransparencyLevel, level, TestContext.Current.CancellationToken);
            Assert.Equal(level, after.Settings.TransparencyLevel);
            Assert.Equal(level, after.Settings.AncStrength);
            Assert.Equal(level == 100, after.Settings.Transparency);
            Assert.Equal(before.Settings.AncEnabled, after.Settings.AncEnabled);
            Assert.Equal(before.Settings.AntiWind, after.Settings.AntiWind);
            Assert.Equal(before.Settings.AdaptiveAnc, after.Settings.AdaptiveAnc);
        }

        Assert.NotEmpty(replay.Writes);
    }
}

internal sealed class HeadsetCaptureReplay
{
    private readonly List<(GaiaFrame Request, GaiaFrame Response)> _exchanges = [];
    private readonly Dictionary<string, byte[]> _values = [];
    private readonly Dictionary<string, Dictionary<string, byte[]>> _transitions = [];
    public List<GaiaFrame> Writes { get; } = [];

    public HeadsetCaptureReplay(bool transparencyTransitions = false, bool spatialCapture = false)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "CapturedPackets", "M5");
        foreach (var name in new[] { "hardware", "parity", "immersive", "battery-protection" })
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, $"m5-6.21.5-{name}.json")));
            foreach (var row in document.RootElement.GetProperty("exchanges").EnumerateArray())
            {
                if (row.TryGetProperty("tx", out var tx))
                {
                    _exchanges.Add((Parse(tx.GetString()!), Parse(row.GetProperty("rx").GetString()!)));
                }
                else if (row.TryGetProperty("request", out var payload) && row.TryGetProperty("response", out var response))
                {
                    var command = new CommandWord(ushort.Parse(row.GetProperty("command").GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    _exchanges.Add((Parse(GaiaFrameCodec.Encode(0x0495, command, Hex.Parse(payload.GetString()!))),
                        Parse(GaiaFrameCodec.Encode(0x0495, command.AsResponse(), Hex.Parse(response.GetString()!)))));
                }
            }
        }

        foreach (var (request, response) in _exchanges)
        {
            if (CommandCatalog.Find(request.Vendor, request.Command)?.Safety == SafetyClass.Read)
            {
                _values.TryAdd(Key(request), response.Payload);
            }
        }

        if (spatialCapture)
        {
            using var spatial = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "m5-6.21.5-immersive.json")));
            var initialized = new HashSet<ushort>();
            foreach (var row in spatial.RootElement.GetProperty("exchanges").EnumerateArray())
            {
                var id = ushort.Parse(row.GetProperty("command").GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (id is 0x3603 or 0x3605 or 0x0804 && initialized.Add(id))
                {
                    _values[Hex.Format(GaiaFrameCodec.Encode(0x0495, new CommandWord(id), []))] = Hex.Parse(row.GetProperty("response").GetString()!);
                }
            }
        }

        using var transparency = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "m5-6.21.5-transparency.json")));
        var rows = transparency.RootElement.GetProperty("exchanges").EnumerateArray().ToArray();
        var initializedNoise = new HashSet<ushort>();
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            var id = ushort.Parse(row.GetProperty("command").GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var payload = Hex.Parse(row.GetProperty("payload").GetString()!);
            var request = Parse(GaiaFrameCodec.Encode(0x0495, new CommandWord(id), payload));
            if (id is 0x1804 or 0x1802)
            {
                var following = rows.Skip(index + 1).Take(5).ToArray();
                if (transparencyTransitions && following.Length == 5 && following.All(item => item.GetProperty("command").GetString() is "1803" or "1a01" or "1a03" or "1a05" or "1805"))
                {
                    _transitions[Key(request)] = following.ToDictionary(item => Hex.Format(GaiaFrameCodec.Encode(0x0495,
                        new CommandWord(ushort.Parse(item.GetProperty("command").GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture)), [])),
                        item => Hex.Parse(item.GetProperty("response").GetString()!));
                }
            }
            else if (CommandCatalog.Find(request.Vendor, request.Command)?.Safety == SafetyClass.Read)
            {
                var response = Hex.Parse(row.GetProperty("response").GetString()!);
                if (transparencyTransitions && initializedNoise.Add(id))
                {
                    _values[Key(request)] = response;
                }
                else
                {
                    _values.TryAdd(Key(request), response);
                }
            }
        }
    }

    public async Task<Momentum4Service> StartAsync()
    {
        var transport = new FakeTransport { Responder = Respond };
        transport.SetState(TransportState.Disconnected);
        var service = new Momentum4Service(transport, CommandPolicy.Production,
            new Momentum4ServiceOptions { RegisterNotifications = false, ReadSerialNumber = false, EnablePolling = false, Reconnect = false }, NullLoggerFactory.Instance);
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
        var request = Parse(bytes);
        if (_values.TryGetValue(Key(request), out var value))
        {
            return [GaiaFrameCodec.Encode(request.Vendor, request.Command.AsResponse(), value)];
        }

        if (request.Command.Value is 0x2802 or 0x1401)
        {
            return [GaiaFrameCodec.Encode(request.Vendor, request.Command.AsError(), new byte[] { 1 })];
        }

        Writes.Add(request);
        if (_transitions.TryGetValue(Key(request), out var transition))
        {
            if (request.Command.Value == 0x1802)
            {
                Assert.Equal(new byte[] { 1 }, _values[Hex.Format(GaiaFrameCodec.Encode(0x0495, new CommandWord(0x1805), []))]);
            }

            foreach (var (key, nextValue) in transition)
            {
                _values[key] = nextValue;
            }

            return [GaiaFrameCodec.Encode(request.Vendor, request.Command.AsResponse(), [])];
        }

        var index = _exchanges.FindIndex(pair => Key(pair.Request) == Key(request));
        Assert.True(index >= 0, $"Uncaptured request {Key(request)}");
        var captured = _exchanges[index];
        var next = _exchanges[index + 1];
        if (request.Command.Value == 0x1001)
        {
            Assert.Equal((ushort)0x1002, next.Request.Command.Value);
            var key = Hex.Format(GaiaFrameCodec.Encode(0x0495, new CommandWord(0x1003), new byte[] { 0 }));
            _values[key][request.Payload[0]] = next.Response.Payload[0];
        }
        else
        {
            var readbacks = _exchanges.Skip(index + 1)
                .TakeWhile(pair => CommandCatalog.Find(pair.Request.Vendor, pair.Request.Command)?.Safety == SafetyClass.Read || pair.Request.Command.Value == 0x3601).ToArray();
            var confirmed = readbacks.First(pair => pair.Request.Command.Value == request.Command.Value + 1);
            _values[Key(confirmed.Request)] = confirmed.Response.Payload;
            if (request.Command.Value is 0x3602 or 0x3604)
            {
                foreach (var readback in readbacks.Where(pair => pair.Request.Command.Value is 0x3603 or 0x3605 or 0x0804))
                {
                    _values[Key(readback.Request)] = readback.Response.Payload;
                }
            }
        }

        return [captured.Response.Raw];
    }

    private static string Key(GaiaFrame frame) => Hex.Format(frame.Raw);

    private static GaiaFrame Parse(string hex) => Parse(Hex.Parse(hex));

    private static GaiaFrame Parse(byte[] bytes) => new GaiaFrameReader(strict: true).Push(bytes).Frames.Single();
}
