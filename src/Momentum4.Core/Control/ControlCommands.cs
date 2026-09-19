// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using Momentum4.Core.Presets;
using Momentum4.Core.Queue;
using Momentum4.Core.Services;
using Momentum4.Core.State;
using Momentum4.Core.Transport;
using Momentum4.Protocol.Features;

namespace Momentum4.Core.Control;

/// <summary>Ergebnis eines Steuerbefehls: Exit-Code (0 = ausgeführt) und Text für die Konsole.</summary>
public sealed record ControlResult(int ExitCode, string Output)
{
    public static ControlResult Ok(string output) => new(0, output);

    public static ControlResult Error(string output) => new(1, output);

    public static ControlResult Usage(string output) => new(2, output);
}

/// <summary>
/// Befehle von <c>m4ctl</c> (docs/architecture.md §6.6) – dieselben, ob die App sie über die Named Pipe ausführt oder
/// <c>m4ctl</c> selbst verbindet. Jede Aktion läuft über die Setter des Service (Write → Verify); ausgegeben wird der
/// bestätigte Zustand.
/// </summary>
public static class ControlCommands
{
    public const string Help = """
        m4ctl – MOMENTUM 4 von der Kommandozeile steuern (inoffiziell, lokal)

          m4ctl status                           Zustand anzeigen
          m4ctl battery                          nur den Akku in Prozent (für Skripte)
          m4ctl mode transparency|anc|adaptive|off
                                                 Noise Control (auch: transparenz, aus)
          m4ctl mode toggle                      zwischen Transparenz und ANC wechseln
          m4ctl strength 0..100                  ANC-Stärke (100 = am stärksten), schaltet auf ANC
          m4ctl antiwind off|max|auto            Anti-Wind
          m4ctl soundmode eq|podcast|off         Klangmodus
          m4ctl preset NAME                      EQ-Preset anwenden (Namen wie in der App)
          m4ctl bass on|off                      Bass Boost
          m4ctl onhead|smartpause|touch|autoanswer|comfortcall on|off
                                                 Tragen, Touch und Anrufe
          m4ctl autooff never|15|30|60           automatisch ausschalten nach Minuten
          m4ctl autopause on|off                 Musik anhalten, wenn Transparenz aktiv ist
          m4ctl prompts voice|tones|off          Töne & Sprachansagen (Töne und Stimme, nur Töne, aus)
          m4ctl connect NAME                     gekoppeltes Gerät verbinden (Name wie in der App)
          m4ctl rename NAME                       Headset umbenennen (localName)

        Läuft die App, führt sie den Befehl aus; sonst verbindet m4ctl selbst.
        Exit-Code: 0 ausgeführt, 1 Fehler, 2 falscher Aufruf.
        """;

    public static async Task<ControlResult> ExecuteAsync(IReadOnlyList<string> args, Momentum4Service service, EqPresetStore? presets, CancellationToken cancellationToken)
    {
        if (args.Count == 0 || args[0] is "help" or "-h" or "--help" or "/?")
        {
            return ControlResult.Usage(Help);
        }

        var command = args[0].ToLowerInvariant();
        var value = args.Count > 1 ? string.Join(' ', args.Skip(1)) : null;
        try
        {
            if (command is "status")
            {
                return ControlResult.Ok(Describe(service.Store.Current, presets));
            }

            if (command is "battery")
            {
                return service.Store.Current.DisplayBattery is ({ } percent, _)
                    ? ControlResult.Ok(percent.ToString(CultureInfo.InvariantCulture))
                    : ControlResult.Error("Akku unbekannt.");
            }

            if (service.Store.Current.Connection != ConnectionState.Connected)
            {
                return ControlResult.Error($"Headset nicht verbunden ({service.Store.Current.Connection}).");
            }

            return command switch
            {
                "mode" => await ModeAsync(service, value, cancellationToken),
                "strength" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var strength) && strength is >= 0 and <= 100 =>
                    Confirmed(await service.SetNoiseControlLevelAsync(100 - strength, cancellationToken)),
                "antiwind" when ParseAntiWind(value) is { } antiWind => Confirmed(await service.SetAntiWindAsync(antiWind, cancellationToken)),
                "soundmode" when ParseSoundMode(value) is { } soundMode => Confirmed(await service.SetSoundModeAsync(soundMode, cancellationToken)),
                "preset" when value is not null => await PresetAsync(service, presets, value, cancellationToken),
                "bass" when ParseSwitch(value) is { } on => Confirmed(await service.SetBassBoostAsync(on, cancellationToken)),
                "onhead" or "smartpause" or "touch" or "autoanswer" or "comfortcall" when ParseSwitch(value) is { } on =>
                    Confirmed(await service.SetBehaviorAsync(BehaviorOf(command), on, cancellationToken), BehaviorOf(command)),
                "autooff" when ParseAutoOff(value) is { } seconds => ControlResult.Ok($"Automatisch ausschalten: {AutoOffName(await service.SetAutoPowerOffAsync(seconds, cancellationToken))}"),
                "autopause" when ParseSwitch(value) is { } ap => ControlResult.Ok($"Automatische Pause: {(await service.SetAutoPauseAsync(ap, cancellationToken) ? "an" : "aus")}"),
                "prompts" when ParsePrompts(value) is { } prompts => ControlResult.Ok($"Töne & Sprachansagen: {PromptName(await service.SetAudioPromptModeAsync(prompts, cancellationToken))}"),
                "rename" when value is not null => ControlResult.Ok($"Headset heißt jetzt „{await service.SetDeviceNameAsync(value, cancellationToken)}“."),
                "connect" when value is not null => await ConnectAsync(service, value, cancellationToken),
                _ => ControlResult.Usage($"Unbekannter Befehl oder Wert: {string.Join(' ', args)}\n\n{Help}"),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ControlResult.Error($"Nicht ausgeführt: {DescribeError(ex)}");
        }
    }

    /// <summary>Kurzbeschreibung eines Fehlers wie in der App (ohne Stacktrace).</summary>
    public static string DescribeError(Exception exception) => exception switch
    {
        SettingNotAppliedException ex => ex.Message,
        HeadsetUnavailableException => "Headset nicht verbunden.",
        CommandNotAllowedException ex => $"nicht erlaubt ({ex.Message}).",
        GaiaErrorException ex => $"vom Headset abgelehnt{(ex.Reason is { } reason ? $" (Code 0x{reason:X2})" : string.Empty)}.",
        TimeoutException => "keine Antwort vom Headset.",
        _ => exception.Message,
    };

    private static async Task<ControlResult> ModeAsync(Momentum4Service service, string? value, CancellationToken ct)
    {
        NoiseControlMode? mode = value?.ToLowerInvariant() switch
        {
            "transparency" or "transparenz" => NoiseControlMode.Transparency,
            "anc" => NoiseControlMode.Custom,
            "adaptive" => NoiseControlMode.Adaptive,
            "off" or "aus" => NoiseControlMode.Off,
            "toggle" => service.Store.Current.NoiseControl?.Mode == NoiseControlMode.Transparency ? NoiseControlMode.Custom : NoiseControlMode.Transparency,
            _ => null,
        };
        return mode is { } target
            ? Confirmed(await service.SetNoiseControlModeAsync(target, ct))
            : ControlResult.Usage("mode: transparency, anc, adaptive, off oder toggle");
    }

    private static async Task<ControlResult> PresetAsync(Momentum4Service service, EqPresetStore? presets, string name, CancellationToken ct)
    {
        if (presets?.Find(name) is not { } preset)
        {
            var known = presets is null ? string.Empty : $" Vorhanden: {string.Join(", ", presets.All.Select(p => p.Name))}.";
            return ControlResult.Error($"Preset „{name}“ gibt es nicht.{known}");
        }

        await service.SetEqGainsAsync(preset.GainsDb, ct);
        return ControlResult.Ok($"Preset „{preset.Name}“ angewendet.");
    }

    private static async Task<ControlResult> ConnectAsync(Momentum4Service service, string name, CancellationToken ct)
    {
        var peer = service.Store.Current.Multipoint?.Devices.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
        if (peer is null)
        {
            return ControlResult.Error($"Kein gekoppeltes Gerät „{name}“.");
        }

        await service.ConnectPeerAsync(peer.Index, peer.Name, ct);
        return ControlResult.Ok($"„{peer.Name}“ verbunden.");
    }

    private static ControlResult Confirmed(NoiseControlState state) =>
        ControlResult.Ok($"Noise Control: {ModeName(state.Mode)}{(state.Mode == NoiseControlMode.Custom ? $", ANC-Stärke {100 - state.Level}" : string.Empty)}, Anti-Wind {AntiWindName(state.AntiWind)}");

    private static ControlResult Confirmed(EqualizerState state) =>
        ControlResult.Ok($"Klangmodus {SoundModeName(state.SoundMode)}, Bass Boost {OnOff(state.BassBoost)}");

    private static ControlResult Confirmed(BehaviorState state, BehaviorSetting setting) =>
        ControlResult.Ok($"{BehaviorState.NameOf(setting)}: {OnOff(state.Get(setting))}");

    private static string Describe(Momentum4State s, EqPresetStore? presets)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Verbindung:    {ConnectionName(s.Connection)}{(s.Device is { } d ? $" ({d.ModelId}, Firmware {d.Firmware})" : string.Empty)}");
        if (s.DeviceName is { } name)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Name:          {name}");
        }

        if (s.Codec is { } codec)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Codec:         {GenericAudioCodec.CodecName(codec)}");
        }

        if (s.PromptLanguage is { } lang)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Ansagen-Spr.:  {GenericAudioCodec.LanguageName(lang)}");
        }

        if (s.SerialNumber is { } serial)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Seriennummer:  {serial}");
        }
        text.AppendLine(CultureInfo.InvariantCulture, $"Akku:          {s.DisplayBattery switch { ({ } p, false) => $"{p} %", ({ } p, true) => $"{p} % (Windows)", _ => "–" }}{(s.Charging == true ? ", lädt" : string.Empty)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Tragezustand:  {s.Wear switch { WearState.Worn => "aufgesetzt", WearState.NotWorn => "abgesetzt", null => "–", var w => w.ToString() }}");
        if (s.NoiseControl is { } nc)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Noise Control: {ModeName(nc.Mode)}{(nc.Mode == NoiseControlMode.Custom ? $", ANC-Stärke {100 - nc.Level}" : string.Empty)}");
            text.AppendLine(CultureInfo.InvariantCulture, $"Anti-Wind:     {AntiWindName(nc.AntiWind)}");
        }

        if (s.AutoPauseOnTransparency is { } autoPause)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Auto-Pause:    {OnOff(autoPause)}");
        }

        if (s.Equalizer is { } eq)
        {
            var gains = eq.Bands.Select(b => b.GainDb).ToList();
            var preset = presets?.FindMatching(gains)?.Name ?? "eigene Kurve";
            text.AppendLine(CultureInfo.InvariantCulture, $"Klangmodus:    {SoundModeName(eq.SoundMode)}");
            text.AppendLine(CultureInfo.InvariantCulture, $"Equalizer:     {string.Join(" ", gains.Select(g => g.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture)))} dB ({preset}), Bass Boost {OnOff(eq.BassBoost)}");
        }

        if (s.Behavior is { } b)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Verhalten:     On-Head {OnOff(b.OnHeadDetection)}, Smart Pause {OnOff(b.SmartPause)}, Touch {OnOff(b.TouchControl)}, Anrufe annehmen {OnOff(b.AutoAnswer)}, Comfort Call {OnOff(b.ComfortCall)}");
        }

        if (s.AutoPowerOffSeconds is { } autoOff)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Auto-Aus:      {AutoOffName(autoOff)}");
        }

        if (s.PromptMode is { } prompts)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Ansagen:       {PromptName(prompts)}");
        }

        if (s.Multipoint is { Devices.Count: > 0 } mp)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Geräte:        {string.Join(", ", mp.Devices.Select(d => $"{d.Name}{(d.IsThisComputer ? " (dieser PC)" : string.Empty)}{(d.Connected ? " – verbunden" : string.Empty)}"))}");
        }

        return text.ToString().TrimEnd();
    }

    private static BehaviorSetting BehaviorOf(string command) => command switch
    {
        "onhead" => BehaviorSetting.OnHeadDetection,
        "smartpause" => BehaviorSetting.SmartPause,
        "touch" => BehaviorSetting.TouchControl,
        "autoanswer" => BehaviorSetting.AutoAnswer,
        _ => BehaviorSetting.ComfortCall,
    };

    private static AudioPromptMode? ParsePrompts(string? value) => value?.ToLowerInvariant() switch
    {
        "voice" or "stimme" => AudioPromptMode.TonesAndVoice,
        "tones" or "töne" => AudioPromptMode.TonesOnly,
        "off" or "aus" => AudioPromptMode.Off,
        _ => null,
    };

    private static string PromptName(AudioPromptMode mode) => mode switch
    {
        AudioPromptMode.TonesAndVoice => "Töne und Stimme",
        AudioPromptMode.TonesOnly => "nur Töne",
        _ => "aus",
    };

    private static int? ParseAutoOff(string? value) => value?.ToLowerInvariant() switch
    {
        "never" or "nie" or "0" => 0,
        "15" => 900,
        "30" => 1800,
        "60" => 3600,
        _ => null,
    };

    private static string AutoOffName(int seconds) => seconds == 0 ? "nie" : $"nach {seconds / 60} min";

    private static bool? ParseSwitch(string? value) => value?.ToLowerInvariant() switch
    {
        "on" or "an" or "1" => true,
        "off" or "aus" or "0" => false,
        _ => null,
    };

    private static AntiWindMode? ParseAntiWind(string? value) => value?.ToLowerInvariant() switch
    {
        "off" or "aus" => AntiWindMode.Off,
        "max" or "maximum" => AntiWindMode.Maximum,
        "auto" or "automatisch" => AntiWindMode.Automatic,
        _ => null,
    };

    private static SoundMode? ParseSoundMode(string? value) => value?.ToLowerInvariant() switch
    {
        "eq" or "equalizer" => SoundMode.Equalizer,
        "podcast" => SoundMode.Podcast,
        "off" or "aus" or "neutral" => SoundMode.Off,
        _ => null,
    };

    private static string ModeName(NoiseControlMode mode) => mode switch
    {
        NoiseControlMode.Transparency => "Transparenz",
        NoiseControlMode.Custom => "ANC",
        NoiseControlMode.Adaptive => "Adaptive",
        _ => "Aus",
    };

    private static string AntiWindName(AntiWindMode mode) => mode switch
    {
        AntiWindMode.Off => "aus",
        AntiWindMode.Maximum => "Maximum",
        AntiWindMode.Automatic => "automatisch",
        _ => mode.ToString(),
    };

    private static string SoundModeName(SoundMode? mode) => mode switch
    {
        SoundMode.Equalizer => "Equalizer",
        SoundMode.Podcast => "Podcast",
        SoundMode.Off => "Neutral",
        SoundMode.SoundPersonalization => "Sound Personalization",
        _ => "–",
    };

    private static string ConnectionName(ConnectionState state) => state switch
    {
        ConnectionState.Connected => "verbunden",
        ConnectionState.Connecting or ConnectionState.Initializing => "verbinde …",
        ConnectionState.WaitingRetry => "getrennt, nächster Versuch folgt",
        _ => "getrennt",
    };

    private static string OnOff(bool? value) => value switch
    {
        true => "an",
        false => "aus",
        null => "–",
    };
}
