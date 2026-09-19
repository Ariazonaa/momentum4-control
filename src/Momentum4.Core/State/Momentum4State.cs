// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Momentum4.Protocol.Features;

namespace Momentum4.Core.State;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Initializing,
    Connected,
    WaitingRetry,
}

/// <summary>Identifikation des Headsets (Hardware-Stufe 2).</summary>
public sealed record DeviceInfo(string ModelId, FirmwareVersion Firmware);

public enum NoiseControlMode
{
    Off,
    Adaptive,
    Custom,

    /// <summary>Transparent Hearing (<c>0x1805</c> = 1) – die „laute“ Transparenz, die der User gewohnt ist (Phase E).</summary>
    Transparency,
}

/// <summary>
/// Noise Control genau so, wie das Headset sie abbildet (docs/protocol.md §7.1): ANC ein/aus, Adaptive ja/nein,
/// ein Pegel 0…100 und Anti-Wind in drei Stufen, dazu Transparent Hearing (<c>0x1805</c>). Hardware-Befunde Phase E:
/// Transparent Hearing ist ein eigener Modus (beim Einschalten setzt das Headset den Pegel auf 100), wirkt aber nur
/// bei eingeschaltetem ANC. Bei ANC aus bleibt <c>0x1805</c> auf 1 stehen, der User hört nur passive Dämpfung.
/// Deshalb gilt: ANC aus vor Transparenz vor Adaptive/Custom.
/// </summary>
public sealed record NoiseControlState(bool AncEnabled, bool Adaptive, int Level, AntiWindMode AntiWind, bool? TransparentHearing)
{
    public NoiseControlMode Mode =>
        !AncEnabled ? NoiseControlMode.Off
        : TransparentHearing == true ? NoiseControlMode.Transparency
        : Adaptive ? NoiseControlMode.Adaptive
        : NoiseControlMode.Custom;
}

/// <summary>Ein EQ-Band. <see cref="FrequencyHz"/> kommt nur vom Gerät (<c>0x100B</c>), nie aus Annahmen.</summary>
public sealed record EqBand(int Index, decimal GainDb, int? FrequencyHz);

public sealed record EqualizerState(IReadOnlyList<EqBand> Bands, decimal MinGainDb, decimal MaxGainDb, bool? BassBoost, SoundMode? SoundMode)
{
    public bool Equals(EqualizerState? other) =>
        other is not null && Bands.SequenceEqual(other.Bands) && MinGainDb == other.MinGainDb && MaxGainDb == other.MaxGainDb
        && BassBoost == other.BassBoost && SoundMode == other.SoundMode;

    public override int GetHashCode() => HashCode.Combine(Bands.Count, MinGainDb, MaxGainDb, BassBoost, SoundMode);

    public override string ToString() =>
        $"EQ [{string.Join(" ", Bands.Select(b => b.GainDb.ToString("+0.0;-0.0;0.0", System.Globalization.CultureInfo.InvariantCulture)))}] dB, "
        + $"Bass Boost {(BassBoost is { } on ? (on ? "an" : "aus") : "?")}, Sound-Mode {SoundMode?.ToString() ?? "?"}";
}

/// <summary>Gekoppeltes Gerät; adressiert nur über den Index (keine MAC im Protokoll).</summary>
public sealed record PeerDevice(int Index, string Name, bool Connected, bool IsThisComputer);

public sealed record MultipointState(IReadOnlyList<PeerDevice> Devices, int? MaxConnections, int? OwnIndex)
{
    public bool Equals(MultipointState? other) =>
        other is not null && Devices.SequenceEqual(other.Devices) && MaxConnections == other.MaxConnections && OwnIndex == other.OwnIndex;

    public override int GetHashCode() => HashCode.Combine(Devices.Count, MaxConnections, OwnIndex);
}

/// <summary>Einstellungen rund ums Tragen und Telefonieren (docs/protocol.md §6.1, §6.3), je ein Ein/Aus-Wert.</summary>
public enum BehaviorSetting
{
    /// <summary>On-Head-Erkennung (<c>0x0400</c>/<c>0x0401</c>).</summary>
    OnHeadDetection,

    /// <summary>Smart Pause: Wiedergabe beim Absetzen anhalten (<c>0x080C</c>/<c>0x080D</c>).</summary>
    SmartPause,

    /// <summary>Anrufe automatisch annehmen (<c>0x080A</c>/<c>0x080B</c>).</summary>
    AutoAnswer,

    /// <summary>Comfort Call (<c>0x0814</c>/<c>0x0815</c>).</summary>
    ComfortCall,

    /// <summary>
    /// Touch-Steuerung an (<c>0x1606</c>/<c>0x1607</c>). Das Headset kennt eine Touch-<em>Sperre</em> mit invertierten
    /// Werten (0 = Touch aktiv); hier gilt <c>true</c> = Gesten funktionieren.
    /// </summary>
    TouchControl,
}

/// <summary>
/// Die Ein/Aus-Einstellungen aus <see cref="BehaviorSetting"/>. Für die ersten vier meldet das Headset Änderungen nicht
/// ([DS], am M4 bestätigt), sie kommen nur über Lesen: beim vollständigen Refresh und nach jedem eigenen Setzen. Die
/// Touch-Sperre meldet sich per <c>0x1687</c>, wenn Feature 11 angemeldet ist.
/// </summary>
public sealed record BehaviorState(bool OnHeadDetection, bool SmartPause, bool AutoAnswer, bool ComfortCall, bool TouchControl)
{
    public bool Get(BehaviorSetting setting) => setting switch
    {
        BehaviorSetting.OnHeadDetection => OnHeadDetection,
        BehaviorSetting.SmartPause => SmartPause,
        BehaviorSetting.AutoAnswer => AutoAnswer,
        BehaviorSetting.ComfortCall => ComfortCall,
        BehaviorSetting.TouchControl => TouchControl,
        _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, null),
    };

    public BehaviorState With(BehaviorSetting setting, bool on) => setting switch
    {
        BehaviorSetting.OnHeadDetection => this with { OnHeadDetection = on },
        BehaviorSetting.SmartPause => this with { SmartPause = on },
        BehaviorSetting.AutoAnswer => this with { AutoAnswer = on },
        BehaviorSetting.ComfortCall => this with { ComfortCall = on },
        BehaviorSetting.TouchControl => this with { TouchControl = on },
        _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, null),
    };

    public static string NameOf(BehaviorSetting setting) => setting switch
    {
        BehaviorSetting.OnHeadDetection => "On-Head-Erkennung",
        BehaviorSetting.SmartPause => "Smart Pause",
        BehaviorSetting.AutoAnswer => "Anrufe automatisch annehmen",
        BehaviorSetting.ComfortCall => "Comfort Call",
        BehaviorSetting.TouchControl => "Touch-Steuerung",
        _ => setting.ToString(),
    };
}

/// <summary>
/// Der eine, zentrale Zustand (Spezifikation §16). <c>null</c> heißt „unbekannt“ (noch nicht gelesen, nicht
/// unterstützt oder nicht verifiziert) – die UI zeigt dann „–“, nie einen Standardwert. Nach einem
/// Verbindungsverlust bleiben die letzten bestätigten Werte stehen; <see cref="Connection"/> zeigt, dass sie alt sind.
/// </summary>
public sealed record Momentum4State(
    ConnectionState Connection,
    string? LastError,
    DeviceInfo? Device,
    int? BatteryPercent,
    bool? Charging,
    NoiseControlState? NoiseControl,
    EqualizerState? Equalizer,
    MultipointState? Multipoint,
    BehaviorState? Behavior,
    WearState? Wear,
    DateTimeOffset? LastFullRefresh,
    int? WindowsBatteryPercent = null,
    int? AutoPowerOffSeconds = null,
    AudioPromptMode? PromptMode = null,
    string? DeviceName = null,
    BluetoothCodec? Codec = null,
    string? SerialNumber = null,
    PromptLanguage? PromptLanguage = null,
    bool? AutoPauseOnTransparency = null)
{
    /// <summary>
    /// Werte für „Automatisch ausschalten“, die belegt sind: 0 = nie, 15 min (eigenes Gerät), 30 und 60 min ([DS]: so
    /// liest das Headset die Stufen der Smart-Control-App). Andere Werte schreibt die App nicht.
    /// </summary>
    public static IReadOnlyList<int> AutoPowerOffChoices { get; } = [0, 900, 1800, 3600];

    public static Momentum4State Initial { get; } = new(ConnectionState.Disconnected, null, null, null, null, null, null, null, null, null, null);

    /// <summary>Akku für die Anzeige: per GAIA, solange verbunden; sonst der Wert von Windows (HFP), falls vorhanden.</summary>
    public (int Percent, bool FromWindows)? DisplayBattery =>
        Connection == ConnectionState.Connected && BatteryPercent is { } gaia ? (gaia, false)
        : Connection != ConnectionState.Connected && WindowsBatteryPercent is { } windows ? (windows, true)
        : null;
}
