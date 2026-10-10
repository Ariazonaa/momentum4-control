using Momentum4.Protocol.Features;

namespace Momentum4.Core.State;

public enum HeadsetControl
{
    AncEnabled = 0,
    Transparency = 1,
    TouchControls = 2,
    SmartPause = 3,
    OnHeadDetection = 4,
    AutoAnswer = 5,
    ComfortCall = 6,
    BassBoost = 7,
    PauseOnTransparency = 8,
    AdaptiveAnc = 9,
    ImmersiveAudio = 10,
    HeadTracking = 11,
    BatteryProtection = 12,
    AncStrength = 13,
    TransparencyLevel = 14,
    AntiWind = 15,
    AudioPrompts = 16,
    Sidetone = 17,
    AutoPowerOff = 18,
    SoundMode = 19,
    Equalizer = 20,
    Rename = 21,
    ConnectPeer = 22,
}

public enum HeadsetBooleanControl
{
    AncEnabled = 0,
    Transparency = 1,
    TouchControls = 2,
    SmartPause = 3,
    OnHeadDetection = 4,
    AutoAnswer = 5,
    ComfortCall = 6,
    BassBoost = 7,
    PauseOnTransparency = 8,
    AdaptiveAnc = 9,
    ImmersiveAudio = 10,
    HeadTracking = 11,
    BatteryProtection = 12,
}

public enum HeadsetIntegerControl
{
    AncStrength = 13,
    TransparencyLevel = 14,
    AntiWind = 15,
    AudioPrompts = 16,
    Sidetone = 17,
    AutoPowerOff = 18,
    SoundMode = 19,
}

public sealed record HeadsetControlSettings
{
    public bool? AncEnabled { get; init; }

    public bool? Transparency { get; init; }

    public bool? TouchControls { get; init; }

    public bool? SmartPause { get; init; }

    public bool? OnHeadDetection { get; init; }

    public bool? AutoAnswer { get; init; }

    public bool? ComfortCall { get; init; }

    public bool? BassBoost { get; init; }

    public bool? PauseOnTransparency { get; init; }

    public bool? AdaptiveAnc { get; init; }

    public bool? ImmersiveAudio { get; init; }

    public bool? HeadTracking { get; init; }

    public bool? BatteryProtection { get; init; }

    public int? AncStrength { get; init; }

    public int? TransparencyLevel { get; init; }

    public int? AntiWind { get; init; }

    public int? AudioPrompts { get; init; }

    public int? Sidetone { get; init; }

    public int? AutoPowerOff { get; init; }

    public int? SoundMode { get; init; }

    public bool? Get(HeadsetBooleanControl control) => control switch
    {
        HeadsetBooleanControl.AncEnabled => AncEnabled,
        HeadsetBooleanControl.Transparency => Transparency,
        HeadsetBooleanControl.TouchControls => TouchControls,
        HeadsetBooleanControl.SmartPause => SmartPause,
        HeadsetBooleanControl.OnHeadDetection => OnHeadDetection,
        HeadsetBooleanControl.AutoAnswer => AutoAnswer,
        HeadsetBooleanControl.ComfortCall => ComfortCall,
        HeadsetBooleanControl.BassBoost => BassBoost,
        HeadsetBooleanControl.PauseOnTransparency => PauseOnTransparency,
        HeadsetBooleanControl.AdaptiveAnc => AdaptiveAnc,
        HeadsetBooleanControl.ImmersiveAudio => ImmersiveAudio,
        HeadsetBooleanControl.HeadTracking => HeadTracking,
        HeadsetBooleanControl.BatteryProtection => BatteryProtection,
        _ => throw new ArgumentOutOfRangeException(nameof(control)),
    };

    internal HeadsetControlSettings With(HeadsetBooleanControl control, bool? value) => control switch
    {
        HeadsetBooleanControl.AncEnabled => this with { AncEnabled = value },
        HeadsetBooleanControl.Transparency => this with { Transparency = value },
        HeadsetBooleanControl.TouchControls => this with { TouchControls = value },
        HeadsetBooleanControl.SmartPause => this with { SmartPause = value },
        HeadsetBooleanControl.OnHeadDetection => this with { OnHeadDetection = value },
        HeadsetBooleanControl.AutoAnswer => this with { AutoAnswer = value },
        HeadsetBooleanControl.ComfortCall => this with { ComfortCall = value },
        HeadsetBooleanControl.BassBoost => this with { BassBoost = value },
        HeadsetBooleanControl.PauseOnTransparency => this with { PauseOnTransparency = value },
        HeadsetBooleanControl.AdaptiveAnc => this with { AdaptiveAnc = value },
        HeadsetBooleanControl.ImmersiveAudio => this with { ImmersiveAudio = value },
        HeadsetBooleanControl.HeadTracking => this with { HeadTracking = value },
        HeadsetBooleanControl.BatteryProtection => this with { BatteryProtection = value },
        _ => throw new ArgumentOutOfRangeException(nameof(control)),
    };

    public int? Get(HeadsetIntegerControl control) => control switch
    {
        HeadsetIntegerControl.AncStrength => AncStrength,
        HeadsetIntegerControl.TransparencyLevel => TransparencyLevel,
        HeadsetIntegerControl.AntiWind => AntiWind,
        HeadsetIntegerControl.AudioPrompts => AudioPrompts,
        HeadsetIntegerControl.Sidetone => Sidetone,
        HeadsetIntegerControl.AutoPowerOff => AutoPowerOff,
        HeadsetIntegerControl.SoundMode => SoundMode,
        _ => throw new ArgumentOutOfRangeException(nameof(control)),
    };

    internal HeadsetControlSettings With(HeadsetIntegerControl control, int? value) => control switch
    {
        HeadsetIntegerControl.AncStrength => this with { AncStrength = value },
        HeadsetIntegerControl.TransparencyLevel => this with { TransparencyLevel = value },
        HeadsetIntegerControl.AntiWind => this with { AntiWind = value },
        HeadsetIntegerControl.AudioPrompts => this with { AudioPrompts = value },
        HeadsetIntegerControl.Sidetone => this with { Sidetone = value },
        HeadsetIntegerControl.AutoPowerOff => this with { AutoPowerOff = value },
        HeadsetIntegerControl.SoundMode => this with { SoundMode = value },
        _ => throw new ArgumentOutOfRangeException(nameof(control)),
    };

}

public sealed record HeadsetEqTopology(string Model, IReadOnlyList<int> FrequenciesHz);

public sealed record HeadsetPeerSelection(int Index, string Name, int Priority, bool IsThisComputer);

public sealed record HeadsetControlPeer(int Index, string Name, bool? Connected, bool IsThisComputer, int Priority);

public sealed record HeadsetControlSnapshot(
    string Model, FirmwareVersion Firmware, int? Battery, HeadsetControlSettings Settings,
    IReadOnlyList<decimal> EqGainsDb, IReadOnlyList<int> EqFrequenciesHz, decimal? EqMinimumDb, decimal? EqMaximumDb,
    IReadOnlySet<HeadsetControl> Writable, IReadOnlyDictionary<HeadsetIntegerControl, IReadOnlyList<int>> Choices,
    IReadOnlyList<string> Notes, string? DeviceName, bool? Charging, int? WearState, int? PersonalizationState,
    IReadOnlyList<HeadsetControlPeer> Peers, int? MaxConnections, bool PeersAvailable, bool VerifiedSession);
