using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Momentum4.Core.Queue;
using Momentum4.Core.Services;
using Momentum4.Core.State;

namespace Momentum4.CoreHost;

public sealed class HeadsetOperations : IAsyncDisposable
{
    private int _disposed;
    private readonly InheritedTransport _transport;
    private readonly Momentum4Service _service;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static readonly IReadOnlyDictionary<string, HeadsetBooleanControl> BooleanControls = new Dictionary<string, HeadsetBooleanControl>(StringComparer.Ordinal)
    {
        ["anc_enabled"] = HeadsetBooleanControl.AncEnabled,
        ["transparency"] = HeadsetBooleanControl.Transparency,
        ["touch_controls"] = HeadsetBooleanControl.TouchControls,
        ["smart_pause"] = HeadsetBooleanControl.SmartPause,
        ["on_head_detection"] = HeadsetBooleanControl.OnHeadDetection,
        ["auto_answer"] = HeadsetBooleanControl.AutoAnswer,
        ["comfort_call"] = HeadsetBooleanControl.ComfortCall,
        ["bass_boost"] = HeadsetBooleanControl.BassBoost,
        ["pause_on_transparency"] = HeadsetBooleanControl.PauseOnTransparency,
        ["adaptive_anc"] = HeadsetBooleanControl.AdaptiveAnc,
        ["immersive_audio"] = HeadsetBooleanControl.ImmersiveAudio,
        ["head_tracking"] = HeadsetBooleanControl.HeadTracking,
        ["battery_protection"] = HeadsetBooleanControl.BatteryProtection,
    };

    private static readonly IReadOnlyDictionary<string, HeadsetIntegerControl> IntegerControls = new Dictionary<string, HeadsetIntegerControl>(StringComparer.Ordinal)
    {
        ["anc_strength"] = HeadsetIntegerControl.AncStrength,
        ["transparency_level"] = HeadsetIntegerControl.TransparencyLevel,
        ["antiwind"] = HeadsetIntegerControl.AntiWind,
        ["audio_prompts"] = HeadsetIntegerControl.AudioPrompts,
        ["sidetone"] = HeadsetIntegerControl.Sidetone,
        ["auto_power_off"] = HeadsetIntegerControl.AutoPowerOff,
        ["sound_mode"] = HeadsetIntegerControl.SoundMode,
    };

    public HeadsetOperations(int descriptor)
    {
        _transport = new(descriptor);
        _service = new(_transport, CommandPolicy.Production, new Momentum4ServiceOptions
        {
            ReadSerialNumber = false,
            EnablePolling = false,
            Reconnect = false,
            RegisterNotifications = false,
        }, NullLoggerFactory.Instance);
        _service.Store.StateChanged += StateChanged;
        _service.Start();
    }

    public Task Completion => _service.Completion;

    private void StateChanged(object? sender, StateChangedEventArgs state)
    {
        if (state.Current.Connection == ConnectionState.Connected) _ready.TrySetResult();
    }

    public async Task<HostValue> DispatchAsync(PrivateRequest request, CancellationToken cancellationToken)
    {
        var operation = Parse(request);
        await _ready.Task.WaitAsync(cancellationToken);
        var state = await operation(cancellationToken);
        return new(writer => WriteSnapshot(writer, state));
    }

    private Func<CancellationToken, Task<HeadsetControlSnapshot>> Parse(PrivateRequest request)
    {
        var args = request.Arguments;
        switch (request.Operation)
        {
            case "snapshot":
                PrivateProtocol.Fields(args);
                return _service.GetControlSnapshotAsync;
            case "set-boolean":
                PrivateProtocol.Fields(args, "control", "value");
                if (!BooleanControls.TryGetValue(PrivateProtocol.Text(args, "control", 32), out var boolean)) throw new PrivateProtocolException();
                var enabled = PrivateProtocol.Boolean(args, "value");
                return token => _service.SetBooleanControlAsync(boolean, enabled, token);
            case "set-integer":
                PrivateProtocol.Fields(args, "control", "value");
                if (!IntegerControls.TryGetValue(PrivateProtocol.Text(args, "control", 32), out var integer)) throw new PrivateProtocolException();
                var number = Int32(args, "value");
                return token => _service.SetIntegerControlAsync(integer, number, token);
            case "eq-band":
                PrivateProtocol.Fields(args, "band", "gain", "model", "frequencies");
                var band = Int32(args, "band");
                var gain = Decimal(args.GetProperty("gain"));
                var topology = Topology(args);
                return token => _service.SetControlEqBandAsync(band, gain, topology, token);
            case "eq-curve":
                PrivateProtocol.Fields(args, "gains", "model", "frequencies");
                var gainsValue = args.GetProperty("gains");
                if (gainsValue.ValueKind != JsonValueKind.Array || gainsValue.GetArrayLength() is < 1 or > 16) throw new PrivateProtocolException();
                var gains = gainsValue.EnumerateArray().Select(Decimal).ToArray();
                var expected = Topology(args);
                return token => _service.SetControlEqualizerAsync(gains, expected, token);
            case "rename":
                PrivateProtocol.Fields(args, "name");
                var name = PrivateProtocol.Text(args, "name", 128);
                return token => _service.RenameControlAsync(name, token);
            case "peer":
                PrivateProtocol.Fields(args, "index", "name", "priority", "is_this_pc");
                var peer = new HeadsetPeerSelection(Int32(args, "index"), PrivateProtocol.Text(args, "name", 128), Int32(args, "priority"), PrivateProtocol.Boolean(args, "is_this_pc"));
                return token => _service.ConnectControlPeerAsync(peer, token);
            case "noise-mode":
                PrivateProtocol.Fields(args, "mode");
                var mode = PrivateProtocol.Text(args, "mode", 16) switch
                {
                    "anc" => NoiseControlMode.Custom,
                    "adaptive" => NoiseControlMode.Adaptive,
                    "transparency" => NoiseControlMode.Transparency,
                    "off" => NoiseControlMode.Off,
                    _ => throw new PrivateProtocolException(),
                };
                return token => _service.SetControlNoiseModeAsync(mode, token);
            default: throw new PrivateProtocolException();
        }
    }

    private static int Int32(JsonElement args, string name)
    {
        var value = PrivateProtocol.Integer(args, name);
        if (value < int.MinValue || value > int.MaxValue) throw new PrivateProtocolException();
        return (int)value;
    }

    private static decimal Decimal(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var result)) throw new PrivateProtocolException();
        return result;
    }

    private static HeadsetEqTopology Topology(JsonElement args)
    {
        var value = args.GetProperty("frequencies");
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > 16) throw new PrivateProtocolException();
        var frequencies = value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var frequency) ? frequency : throw new PrivateProtocolException()).ToArray();
        return new(PrivateProtocol.Text(args, "model", 128), frequencies);
    }

    public static void WriteSnapshot(Utf8JsonWriter writer, HeadsetControlSnapshot state)
    {
        writer.WriteStartObject();
        writer.WriteString("model", state.Model);
        writer.WriteString("firmware", state.Firmware.ToString());
        Optional(writer, "battery", state.Battery);
        writer.WritePropertyName("settings"); writer.WriteStartObject();
        var settings = state.Settings;
        Setting(writer, "anc_enabled", settings.AncEnabled);
        Setting(writer, "transparency", settings.Transparency);
        Setting(writer, "touch_controls", settings.TouchControls);
        Setting(writer, "smart_pause", settings.SmartPause);
        Setting(writer, "on_head_detection", settings.OnHeadDetection);
        Setting(writer, "auto_answer", settings.AutoAnswer);
        Setting(writer, "comfort_call", settings.ComfortCall);
        Setting(writer, "bass_boost", settings.BassBoost);
        Setting(writer, "pause_on_transparency", settings.PauseOnTransparency);
        Setting(writer, "adaptive_anc", settings.AdaptiveAnc);
        Setting(writer, "immersive_audio", settings.ImmersiveAudio);
        Setting(writer, "head_tracking", settings.HeadTracking);
        Setting(writer, "battery_protection", settings.BatteryProtection);
        Setting(writer, "anc_strength", settings.AncStrength);
        Setting(writer, "transparency_level", settings.TransparencyLevel);
        Setting(writer, "antiwind", settings.AntiWind);
        Setting(writer, "audio_prompts", settings.AudioPrompts);
        Setting(writer, "sidetone", settings.Sidetone);
        Setting(writer, "auto_power_off", settings.AutoPowerOff);
        Setting(writer, "sound_mode", settings.SoundMode);
        writer.WriteEndObject();
        writer.WritePropertyName("eq_bands"); writer.WriteStartArray();
        foreach (var gain in state.EqGainsDb) writer.WriteNumberValue(gain);
        writer.WriteEndArray();
        writer.WritePropertyName("eq_frequencies"); writer.WriteStartArray();
        foreach (var frequency in state.EqFrequenciesHz) writer.WriteNumberValue(frequency);
        writer.WriteEndArray();
        writer.WritePropertyName("eq_range");
        if (state.EqMinimumDb is { } minimum && state.EqMaximumDb is { } maximum)
        {
            writer.WriteStartArray(); writer.WriteNumberValue(minimum); writer.WriteNumberValue(maximum); writer.WriteEndArray();
        }
        else writer.WriteNullValue();
        writer.WritePropertyName("writable"); writer.WriteStartArray();
        foreach (var control in state.Writable.Order()) writer.WriteStringValue(ControlName(control));
        writer.WriteEndArray();
        writer.WritePropertyName("choices"); writer.WriteStartObject();
        foreach (var choice in state.Choices)
        {
            writer.WritePropertyName(IntegerControls.Single(pair => pair.Value == choice.Key).Key);
            writer.WriteStartArray(); foreach (var item in choice.Value) writer.WriteNumberValue(item); writer.WriteEndArray();
        }
        writer.WriteEndObject();
        writer.WritePropertyName("notes"); writer.WriteStartArray();
        foreach (var note in state.Notes) writer.WriteStringValue(note);
        writer.WriteEndArray();
        writer.WriteString("device_name", state.DeviceName);
        Optional(writer, "charging", state.Charging);
        Optional(writer, "wear_state", state.WearState);
        Optional(writer, "personalization_state", state.PersonalizationState);
        writer.WritePropertyName("peers"); writer.WriteStartArray();
        foreach (var peer in state.Peers)
        {
            writer.WriteStartObject(); writer.WriteNumber("index", peer.Index); writer.WriteString("name", peer.Name);
            Optional(writer, "connected", peer.Connected); writer.WriteBoolean("is_this_pc", peer.IsThisComputer);
            writer.WriteNumber("priority", peer.Priority); writer.WriteEndObject();
        }
        writer.WriteEndArray();
        Optional(writer, "max_connections", state.MaxConnections);
        writer.WriteBoolean("peers_available", state.PeersAvailable);
        writer.WriteBoolean("verified_session", state.VerifiedSession);
        writer.WriteEndObject();
    }

    private static string ControlName(HeadsetControl control) => control switch
    {
        HeadsetControl.Equalizer => "eq",
        HeadsetControl.Rename => "rename",
        HeadsetControl.ConnectPeer => "connect_peer",
        HeadsetControl.AncEnabled => "anc_enabled",
        HeadsetControl.Transparency => "transparency",
        HeadsetControl.TouchControls => "touch_controls",
        HeadsetControl.SmartPause => "smart_pause",
        HeadsetControl.OnHeadDetection => "on_head_detection",
        HeadsetControl.AutoAnswer => "auto_answer",
        HeadsetControl.ComfortCall => "comfort_call",
        HeadsetControl.BassBoost => "bass_boost",
        HeadsetControl.PauseOnTransparency => "pause_on_transparency",
        HeadsetControl.AdaptiveAnc => "adaptive_anc",
        HeadsetControl.ImmersiveAudio => "immersive_audio",
        HeadsetControl.HeadTracking => "head_tracking",
        HeadsetControl.BatteryProtection => "battery_protection",
        HeadsetControl.AncStrength => "anc_strength",
        HeadsetControl.TransparencyLevel => "transparency_level",
        HeadsetControl.AntiWind => "antiwind",
        HeadsetControl.AudioPrompts => "audio_prompts",
        HeadsetControl.Sidetone => "sidetone",
        HeadsetControl.AutoPowerOff => "auto_power_off",
        HeadsetControl.SoundMode => "sound_mode",
        _ => throw new PrivateProtocolException(),
    };

    private static void Setting(Utf8JsonWriter writer, string name, bool? value) { if (value is { } observed) writer.WriteBoolean(name, observed); }
    private static void Setting(Utf8JsonWriter writer, string name, int? value) { if (value is { } observed) writer.WriteNumber(name, observed); }
    private static void Optional(Utf8JsonWriter writer, string name, bool? value) { if (value is { } observed) writer.WriteBoolean(name, observed); else writer.WriteNull(name); }
    private static void Optional(Utf8JsonWriter writer, string name, int? value) { if (value is { } observed) writer.WriteNumber(name, observed); else writer.WriteNull(name); }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _service.Store.StateChanged -= StateChanged;
        await _service.DisposeAsync();
        await _transport.DisposeAsync();
    }
}
