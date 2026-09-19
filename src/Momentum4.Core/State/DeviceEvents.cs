// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

namespace Momentum4.Core.State;

/// <summary>Etwas hat sich am Headset oder an der Verbindung geändert (bestätigt gelesen oder gemeldet).</summary>
public abstract record DeviceEvent;

public sealed record ConnectionChanged(ConnectionState State, string? Error = null) : DeviceEvent;

public sealed record DeviceIdentified(DeviceInfo Device) : DeviceEvent;

public sealed record BatteryChanged(int Percent) : DeviceEvent;

public sealed record ChargingChanged(bool Charging) : DeviceEvent;

/// <summary>Vollständig gelesene Noise Control.</summary>
public sealed record NoiseControlChanged(NoiseControlState State) : DeviceEvent;

/// <summary>Teil-Updates aus Notifications (<c>0x1A85</c>, <c>0x1A83</c>, <c>0x1A81</c>, <c>0x1885</c>).</summary>
public sealed record AncEnabledChanged(bool Enabled) : DeviceEvent;

public sealed record AncLevelChanged(int Level) : DeviceEvent;

public sealed record AncModesChanged(Protocol.Features.AncModeTable Table) : DeviceEvent;

public sealed record TransparentHearingChanged(bool On) : DeviceEvent;

public sealed record EqualizerChanged(EqualizerState State) : DeviceEvent;

/// <summary>Teil-Update aus <c>0x1082</c>: alle Gains (am M4 beobachtet, gleiches Format wie <c>0x1003</c>).</summary>
public sealed record EqGainsChanged(IReadOnlyList<decimal> GainsDb) : DeviceEvent;

/// <summary>Teil-Update aus <c>0x1089</c>.</summary>
public sealed record BassBoostChanged(bool Enabled) : DeviceEvent;

public sealed record SoundModeChanged(Protocol.Features.SoundMode Mode) : DeviceEvent;

public sealed record MultipointChanged(MultipointState State) : DeviceEvent;

/// <summary>Teil-Update aus <c>0x1484</c> [index, status].</summary>
public sealed record PeerConnectionChanged(int Index, bool Connected) : DeviceEvent;

/// <summary>Alle vier Werte aus <see cref="BehaviorState"/>, gelesen (es gibt dafür keine Notifications).</summary>
public sealed record BehaviorChanged(BehaviorState State) : DeviceEvent;

/// <summary>Teil-Update einer einzelnen Einstellung, z. B. Touch aus <c>0x1687</c>.</summary>
public sealed record BehaviorSettingChanged(BehaviorSetting Setting, bool On) : DeviceEvent;

/// <summary>Tragezustand aus <c>0x0402</c>, der Notification <c>0x0482</c> oder dem Dump <c>0x0502</c> nach der Anmeldung.</summary>
public sealed record WearStateChanged(Protocol.Features.WearState State) : DeviceEvent;

/// <summary>Akku laut Windows (HFP), nur solange GAIA nicht verbunden ist; <c>null</c> = keiner.</summary>
public sealed record WindowsBatteryChanged(int? Percent) : DeviceEvent;

/// <summary>Auto Power Off in Sekunden (<c>0x0601 [00]</c>), 0 = nie.</summary>
public sealed record AutoPowerOffChanged(int Seconds) : DeviceEvent;

/// <summary>Töne und Sprachansagen (<c>0x0802</c>).</summary>
public sealed record PromptModeChanged(Protocol.Features.AudioPromptMode Mode) : DeviceEvent;

/// <summary>Gerätename (localName, <c>0x2802</c>).</summary>
public sealed record DeviceNameChanged(string Name) : DeviceEvent;

/// <summary>Aktueller Bluetooth-Codec (<c>0x0800</c>).</summary>
public sealed record CodecChanged(Protocol.Features.BluetoothCodec Codec) : DeviceEvent;

/// <summary>Seriennummer (<c>001D/0x0003</c>, statisch).</summary>
public sealed record SerialNumberRead(string Serial) : DeviceEvent;

/// <summary>Sprache der Ansagen (<c>0x0807</c>).</summary>
public sealed record PromptLanguageChanged(Protocol.Features.PromptLanguage Language) : DeviceEvent;

/// <summary>Automatische Pause bei Transparenz (<c>0x1801</c>): Musik anhalten, wenn Transparenz aktiv ist.</summary>
public sealed record AutoPauseChanged(bool On) : DeviceEvent;

public sealed record FullRefreshCompleted(DateTimeOffset At) : DeviceEvent;
