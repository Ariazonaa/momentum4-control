// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Momentum4.Core.State;
using Momentum4.Protocol;
using Momentum4.Protocol.Features;
using Momentum4.Protocol.Gaia;

namespace Momentum4.Core.Device;

/// <summary>
/// Übersetzt Notification-Frames in <see cref="DeviceEvent"/>s (docs/protocol.md §8). Hardware-Stufe 5 (FW 3.37.3):
/// Akku, ANC und EQ-Gains/Bass Boost haben dasselbe Format wie die jeweiligen Getter. Passt eine Payload nicht,
/// wird sie verworfen und <see cref="RefreshRequested"/> ausgelöst – der Zustand wird dann per Getter nachgelesen,
/// statt zu raten.
/// </summary>
public sealed class NotificationDispatcher(ILogger<NotificationDispatcher> logger)
{
    /// <summary>Eine Notification ließ sich nicht deuten; der betroffene Bereich sollte neu gelesen werden.</summary>
    public event EventHandler<RefreshScope>? RefreshRequested;

    public DeviceEvent? Translate(GaiaFrame frame)
    {
        if (frame.Vendor != ProtocolConstants.VendorSennheiser || frame.Type != PacketType.Notification)
        {
            return null;
        }

        var p = frame.Payload;
        try
        {
            return frame.Command.Value switch
            {
                0x0683 => new BatteryChanged(BatteryCodec.DecodeLevel(p)),
                0x0682 => new ChargingChanged(SwitchCodec.Decode(p, "Laden")),
                0x1A85 => new AncEnabledChanged(AncCodec.DecodeEnabled(p)),
                0x1A83 => new AncLevelChanged(AncCodec.DecodeLevel(p)),
                0x1A81 => new AncModesChanged(AncCodec.DecodeModes(p)),
                0x1885 => new TransparentHearingChanged(SwitchCodec.Decode(p, "Transparent Hearing")),
                0x1484 when p.Length == 2 => new PeerConnectionChanged(p[0], p[1] != 0),
                0x1082 => new EqGainsChanged(UserEqCodec.DecodeAllGains(p, p.Length)),
                0x1089 => new BassBoostChanged(UserEqCodec.DecodeBassBoost(p)),
                0x0482 => new WearStateChanged(DeviceCodec.DecodeWearState(p)),

                // Touch-Sperre, gleiches Format wie 0x1607 (1 = gesperrt); kommt 20–40 ms nach 0x1606 (2026-09-19).
                0x1687 => new BehaviorSettingChanged(BehaviorSetting.TouchControl, !SwitchCodec.Decode(p, "Touch-Sperre")),

                // Sound-Mode, gleiches Format wie 0x0804; kommt nach 0x0803 auch ohne Anmeldung von Feature 4 (2026-09-19).
                0x0884 => new SoundModeChanged(GenericAudioCodec.DecodeSoundMode(p)),

                // Nur im Dump nach der Anmeldung gesehen, ändern sich nicht: Frequenzen, Güte, Filtertyp, unbekannt.
                0x108B or 0x108D or 0x108F or 0x1091 or 0x1093 => Ignore(frame),

                // Codec, Sample-Rate, Low Latency: kommen bei jedem Start/Stopp des Audio-Streams, auch ohne Anmeldung
                // von Feature 4 (2026-09-19); die App zeigt sie nicht an.
                0x0880 or 0x089A or 0x0898 => Ignore(frame),
                _ => Unknown(frame, null),
            };
        }
        catch (ProtocolFormatException ex)
        {
            logger.LogWarning("Notification {Frame} passt nicht zum erwarteten Format ({Message}) – Bereich wird neu gelesen.", frame, ex.Message);
            RefreshRequested?.Invoke(this, ScopeOf(frame));
            return null;
        }
    }

    /// <summary>
    /// Antwort-Frames ohne Request, die das Headset als Zustandsmeldung schickt: Nach der Anmeldung von Feature 2 kommt
    /// der Tragezustand als <c>0x0502</c> statt als Notification <c>0x0482</c> (FW 3.37.3, docs/protocol.md §8).
    /// Alles andere liefert <c>null</c> und bleibt eine „unerwartete Antwort“.
    /// </summary>
    public DeviceEvent? TranslateUnsolicitedResponse(GaiaFrame frame)
    {
        if (frame.Vendor != ProtocolConstants.VendorSennheiser || frame.Type != PacketType.Response || frame.Command.Value != 0x0502)
        {
            return null;
        }

        try
        {
            return new WearStateChanged(DeviceCodec.DecodeWearState(frame.Payload));
        }
        catch (ProtocolFormatException ex)
        {
            logger.LogWarning("Tragezustand {Frame} passt nicht zum erwarteten Format ({Message}) – wird neu gelesen.", frame, ex.Message);
            RefreshRequested?.Invoke(this, RefreshScope.Behavior);
            return null;
        }
    }

    private DeviceEvent? Ignore(GaiaFrame frame)
    {
        logger.LogDebug("Notification {Frame}: wird nicht angezeigt, ignoriert.", frame);
        return null;
    }

    private DeviceEvent? Unknown(GaiaFrame frame, RefreshScope? scope)
    {
        if (scope is { } s)
        {
            logger.LogInformation("Notification {Frame}: Format noch nicht verifiziert – Bereich {Scope} wird neu gelesen.", frame, s);
            RefreshRequested?.Invoke(this, s);
        }
        else
        {
            logger.LogInformation("Notification {Frame} ohne Zuordnung – ignoriert.", frame);
        }

        return null;
    }

    private static RefreshScope ScopeOf(GaiaFrame frame) => frame.Command.Feature switch
    {
        3 => RefreshScope.Battery,
        8 or 4 => RefreshScope.Equalizer,
        10 => RefreshScope.Multipoint,
        2 or 11 => RefreshScope.Behavior,
        12 or 13 => RefreshScope.NoiseControl,
        _ => RefreshScope.All,
    };
}

[Flags]
public enum RefreshScope
{
    None = 0,
    Battery = 1,
    NoiseControl = 2,
    Equalizer = 4,
    Multipoint = 8,

    /// <summary>On-Head, Smart Pause, Auto-Answer, Comfort Call, Touch (ohne Notifications) und Tragezustand; beim vollständigen Refresh.</summary>
    Behavior = 16,
    All = Battery | NoiseControl | Equalizer | Multipoint | Behavior,
}
