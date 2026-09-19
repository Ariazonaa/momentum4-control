// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.Extensions.Logging;
using Momentum4.Core.Device;
using Momentum4.Core.Queue;
using Momentum4.Core.State;
using Momentum4.Core.Transport;
using Momentum4.Protocol;
using Momentum4.Protocol.Catalog;
using Momentum4.Protocol.Features;
using Momentum4.Protocol.Gaia;

namespace Momentum4.Core.Services;

public sealed record Momentum4ServiceOptions
{
    /// <summary>Abstand der Fallback-Abfragen (Akku und Noise Control).</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Jeder n-te Poll liest alles (EQ und Multipoint eingeschlossen).</summary>
    public int FullRefreshEvery { get; init; } = 6;

    /// <summary>Notifications anmelden. Die Policy entscheidet, ob <c>0x0007</c> gesendet werden darf.</summary>
    public bool RegisterNotifications { get; init; } = true;

    /// <summary>
    /// Features für die Notification-Registrierung (docs/protocol.md §11): Akku, ANC, EQ, Multipoint, Transparent
    /// Hearing (12: Status sofort, auch bei der Touch-Geste), mmi (11: Touch-Sperre <c>0x1687</c>) und device (2:
    /// Tragezustand; der Dump kommt als Response <c>0x0502</c>, siehe <see cref="GaiaCommandQueue.UnsolicitedResponseHandler"/>).
    /// </summary>
    public IReadOnlyList<byte> NotificationFeatures { get; init; } = [3, 13, 8, 10, 12, 11, 2];

    public IReadOnlyList<TimeSpan>? Backoff { get; init; }

    /// <summary>Nach so vielen Timeouts in Folge wird die Verbindung neu aufgebaut (docs/architecture.md §9).</summary>
    public int MaxConsecutiveTimeouts { get; init; } = 3;

    /// <summary>
    /// Format für <c>0x1A00</c>. Ohne Vorgabe: zuerst die komplette Tabelle; lehnt das Headset mit Reason <c>0x05</c>
    /// (falsches Format) ab, einmal das Einzelpaar – das funktionierende Format wird für die Sitzung gemerkt. Auf
    /// FW 3.37.3 wirken beide Formate (Phase E); der Rückfall ist für andere Firmware-Stände gedacht.
    /// </summary>
    public AncModeWriteFormat? AncModeWriteFormat { get; init; }

    /// <summary>
    /// So lange fragt <see cref="Momentum4Service.ConnectPeerAsync"/> nach <c>0x1402</c> mit <c>0x1404</c> nach
    /// (alle 300 ms). [MC] wartet 15 × 300 ms; wie lange der M4 zum Verbinden braucht, ist noch nicht gemessen.
    /// </summary>
    public TimeSpan PeerConnectTimeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>Startwert für <see cref="Momentum4Service.RestoreTransparencyAfterRestart"/>.</summary>
    public bool RestoreTransparencyAfterRestart { get; init; }
}

/// <summary>Das Headset hat den Befehl angenommen, der zurückgelesene Zustand entspricht aber nicht dem Ziel.</summary>
public abstract class SettingNotAppliedException(string message) : Exception(message);

/// <inheritdoc cref="SettingNotAppliedException"/>
public sealed class SettingNotAppliedException<TState>(string setting, TState actual)
    : SettingNotAppliedException($"{setting}: vom Headset nicht übernommen (gelesen: {actual})")
{
    public TState Actual { get; } = actual;
}

/// <summary>Die Firmware des Headsets unterstützt die Funktion nicht (<see cref="FeatureGate"/>). Es wurde nichts gesendet.</summary>
public sealed class FeatureNotSupportedException(string message) : Exception(message);

/// <summary>
/// Multipoint-Aktion abgelehnt, bevor etwas gesendet wurde: Die Liste hat sich geändert, das Ziel ist dieser PC oder
/// die Grenze aus <c>0x1409</c> ist erreicht (Platz freiräumen hieße <c>0x1403</c> senden, das ist gesperrt).
/// </summary>
public sealed class PeerActionRefusedException(string message) : Exception(message);

/// <summary>
/// Fassade für App, CLI und PoC (docs/architecture.md §6.5): hält Verbindung, Zustand, Notifications und Polling
/// zusammen und bietet die Aktionen für Noise Control und EQ an (Write → Verify).
/// </summary>
public sealed class Momentum4Service : IAsyncDisposable
{
    private readonly GaiaCommandQueue _queue;
    private readonly Momentum4Client _client;
    private readonly NotificationDispatcher _dispatcher;
    private readonly ConnectionSupervisor _supervisor;
    private readonly Momentum4ServiceOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private AncModeWriteFormat? _ancWriteFormat;
    private FeatureGate _gate = new(null);
    private IReadOnlyList<int>? _frequencies;
    private int _pendingScope;
    private int _consecutiveTimeouts;
    private TaskCompletionSource _refreshSignal = NewSignal();

    public Momentum4Service(ITransport transport, CommandPolicy policy, Momentum4ServiceOptions options, ILoggerFactory loggerFactory, TimeProvider? timeProvider = null)
    {
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
        _logger = loggerFactory.CreateLogger<Momentum4Service>();
        _queue = new GaiaCommandQueue(transport, policy, loggerFactory.CreateLogger<GaiaCommandQueue>(), _time);
        _client = new Momentum4Client(_queue);
        _dispatcher = new NotificationDispatcher(loggerFactory.CreateLogger<NotificationDispatcher>());
        _supervisor = new ConnectionSupervisor(transport, RunSessionAsync, Store, loggerFactory.CreateLogger<ConnectionSupervisor>(), _time, options.Backoff);
        _queue.NotificationReceived += OnNotification;
        _dispatcher.RefreshRequested += (_, scope) => RequestRefresh(scope);
        _queue.UnsolicitedResponseHandler = OnUnsolicitedResponse;
        RestoreTransparencyAfterRestart = options.RestoreTransparencyAfterRestart;
    }

    public StateStore Store { get; } = new();

    /// <summary>Für Protocol Logging und Mitschnitte.</summary>
    public GaiaCommandQueue Queue => _queue;

    public FeatureGate Features => _gate;

    /// <summary>
    /// Das Headset startet immer ohne Transparent Hearing (docs/protocol.md §7.3). Ist diese Option an, war vor der Trennung
    /// Transparenz aktiv und kommt das Headset mit ANC an, aber ohne Transparent Hearing zurück, schaltet der Service es
    /// beim Wiederverbinden wieder ein. Beim ersten Verbinden nach dem Start gibt es keinen Vorher-Stand – dann nichts.
    /// </summary>
    public bool RestoreTransparencyAfterRestart { get; set; }

    public void Start() => _supervisor.Start();

    public Task StopAsync() => _supervisor.StopAsync();

    /// <summary>Headset hat sich gemeldet, Bluetooth ist wieder an o. Ä. – Wartezeit sofort beenden.</summary>
    public void NotifyHeadsetAvailable(string reason) => _supervisor.Poke(reason);

    /// <summary>Bereich möglichst bald neu lesen (z. B. nach einer undeutbaren Notification).</summary>
    public void RequestRefresh(RefreshScope scope)
    {
        Interlocked.Or(ref _pendingScope, (int)scope);
        Volatile.Read(ref _refreshSignal).TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await _supervisor.DisposeAsync();
        _queue.Dispose();
        _actionGate.Dispose();
    }

    /// <summary>Das zuletzt erfolgreich verwendete Format für <c>0x1A00</c> (null, solange noch nichts geschrieben wurde).</summary>
    public AncModeWriteFormat? AncWriteFormat => _ancWriteFormat ?? _options.AncModeWriteFormat;

    /// <summary>
    /// Noise-Control-Modus setzen (docs/protocol.md §7.1), Abläufe nach den Hardware-Befunden aus Phase E:
    /// Off = nur <c>0x1A04 [00]</c>. Transparency = ggf. erst ANC an (das beendet Transparent Hearing), dann
    /// <c>0x1804 [01]</c> (das Headset setzt den Pegel auf 100). Adaptive/Custom = bei ANC an Transparent Hearing aus
    /// (<c>0x1804 [00]</c>, wie [MC]), sonst ANC an; dann Adaptive 1/0 in <c>0x1A00</c>. Liefert den zurückgelesenen,
    /// bestätigten Zustand.
    /// </summary>
    public Task<NoiseControlState> SetNoiseControlModeAsync(NoiseControlMode mode, CancellationToken cancellationToken) =>
        NoiseControlActionAsync(
            $"Noise Control {mode}",
            CommandsFor(mode),
            async (before, table, ct) => await ApplyModeAsync(mode, before, table, ct),
            after => after.Mode == mode,
            cancellationToken);

    /// <summary>
    /// Pegel 0…100 setzen; schaltet dafür auf Custom. Der Pegel ist die ANC-Stärke: 0 = am stärksten, 100 = schwächer
    /// (A/B-Hörtest Phase E, docs/protocol.md §6.9). Die Transparenz ist <see cref="NoiseControlMode.Transparency"/>.
    /// </summary>
    public Task<NoiseControlState> SetNoiseControlLevelAsync(int level, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 100);
        return NoiseControlActionAsync(
            $"Pegel {level}",
            [.. CommandsFor(NoiseControlMode.Custom), CommandCatalog.SetAncLevel],
            async (before, table, ct) =>
            {
                await ApplyModeAsync(NoiseControlMode.Custom, before, table, ct);
                if (before.Level != level || before.Mode != NoiseControlMode.Custom)
                {
                    await _client.SetAncLevelAsync(level, ct);
                }
            },
            after => after.Mode == NoiseControlMode.Custom && after.Level == level,
            cancellationToken);
    }

    /// <summary>
    /// Transparent Hearing (<c>0x1804</c>) ein-/ausschalten. Hardware-Befund (Phase E): <c>[01]</c> schaltet die vom User
    /// gewohnte Transparenz ein und setzt den Pegel auf 100; jedes Schreiben von <c>0x1A00</c> beendet sie wieder.
    /// </summary>
    public Task<NoiseControlState> SetTransparentHearingAsync(bool on, CancellationToken cancellationToken) =>
        NoiseControlActionAsync(
            $"Transparent Hearing {(on ? "an" : "aus")}",
            [CommandCatalog.SetTransparentHearingStatus],
            async (before, _, ct) =>
            {
                if (before.TransparentHearing != on)
                {
                    await _client.SetTransparentHearingStatusAsync(on, ct);
                }
            },
            after => after.TransparentHearing == on,
            cancellationToken);

    /// <summary>
    /// Nur ANC ein/aus (<c>0x1A04</c>), ohne Transparent Hearing oder Adaptive anzufassen – für Hardware-Tests und
    /// Diagnose. Die App nutzt <see cref="SetNoiseControlModeAsync"/>.
    /// </summary>
    public Task<NoiseControlState> SetAncEnabledAsync(bool enabled, CancellationToken cancellationToken) =>
        NoiseControlActionAsync(
            $"ANC {(enabled ? "an" : "aus")}",
            [CommandCatalog.SetAncEnabled],
            async (before, _, ct) =>
            {
                if (before.AncEnabled != enabled)
                {
                    await _client.SetAncEnabledAsync(enabled, ct);
                }
            },
            after => after.AncEnabled == enabled,
            cancellationToken);

    /// <summary>
    /// Anti-Wind setzen. Weil jedes Schreiben von <c>0x1A00</c> Transparent Hearing beendet (Hardware-Befund Phase E),
    /// schaltet der Service es danach wieder ein, wenn der Modus vorher Transparency war – der Modus bleibt so erhalten.
    /// Bei ANC aus schaltet er es nicht wieder ein: <c>0x1804 [01]</c> ohne ANC ist nicht geprüft und wirkt dort ohnehin nicht.
    /// </summary>
    public Task<NoiseControlState> SetAntiWindAsync(AntiWindMode antiWind, CancellationToken cancellationToken)
    {
        var transparencyBefore = false;
        return NoiseControlActionAsync(
            $"Anti-Wind {antiWind}",
            [CommandCatalog.SetAncMode, CommandCatalog.SetTransparentHearingStatus],
            async (before, table, ct) =>
            {
                transparencyBefore = before.Mode == NoiseControlMode.Transparency;
                if (before.AntiWind == antiWind)
                {
                    return;
                }

                await WriteAncModeAsync(table, AncCodec.ModeAntiWind, (byte)antiWind, ct);
                if (transparencyBefore)
                {
                    await _client.SetTransparentHearingStatusAsync(true, ct);
                }
            },
            after => after.AntiWind == antiWind && (after.Mode == NoiseControlMode.Transparency) == transparencyBefore,
            cancellationToken);
    }

    /// <summary>Gain eines EQ-Bands setzen (Write → Verify über <c>0x1003</c>).</summary>
    public Task<EqualizerState> SetEqBandAsync(int band, decimal gainDb, CancellationToken cancellationToken) =>
        EqualizerActionAsync(
            $"EQ-Band {band} auf {FormatDb(gainDb)} dB",
            gate => gate.Equalizer,
            [CommandCatalog.SetEqBand],
            async (before, ct) =>
            {
                ValidateGain(before, band, gainDb);
                if (before.Bands[band].GainDb != gainDb)
                {
                    await _client.SetEqBandAsync(band, gainDb, ct);
                }
            },
            after => after.Bands[band].GainDb == gainDb,
            cancellationToken);

    /// <summary>
    /// Ganze EQ-Kurve setzen: Band für Band, nur geänderte Bänder. Schlägt ein Schreiben fehl, werden die schon
    /// geschriebenen Bänder in umgekehrter Reihenfolge auf den Stand davor zurückgesetzt (docs/architecture.md §8.2).
    /// </summary>
    public Task<EqualizerState> SetEqGainsAsync(IReadOnlyList<decimal> gainsDb, CancellationToken cancellationToken) =>
        EqualizerActionAsync(
            $"EQ-Kurve [{string.Join(" ", gainsDb.Select(FormatDb))}] dB",
            gate => gate.Equalizer,
            [CommandCatalog.SetEqBand],
            async (before, ct) =>
            {
                if (gainsDb.Count != before.Bands.Count)
                {
                    throw new ArgumentException($"{before.Bands.Count} Werte erwartet, {gainsDb.Count} erhalten.", nameof(gainsDb));
                }

                for (var i = 0; i < gainsDb.Count; i++)
                {
                    ValidateGain(before, i, gainsDb[i]);
                }

                var written = new List<int>();
                try
                {
                    for (var i = 0; i < gainsDb.Count; i++)
                    {
                        if (before.Bands[i].GainDb != gainsDb[i])
                        {
                            written.Add(i);
                            await _client.SetEqBandAsync(i, gainsDb[i], ct);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("EQ-Kurve: Schreiben fehlgeschlagen ({Reason}) – setze {Count} Band/Bänder zurück.", ex.Message, written.Count);
                    await RollBackBandsAsync(before, written, ct);
                    throw;
                }
            },
            after => after.Bands.Select(b => b.GainDb).SequenceEqual(gainsDb),
            cancellationToken);

    /// <summary>Bass Boost ein/aus (<c>0x1008</c>, erst ab FW 3.x, <see cref="FeatureGate.BassBoost"/>).</summary>
    public Task<EqualizerState> SetBassBoostAsync(bool enabled, CancellationToken cancellationToken) =>
        EqualizerActionAsync(
            $"Bass Boost {(enabled ? "an" : "aus")}",
            gate => gate.BassBoost,
            [CommandCatalog.SetBassBoost],
            async (before, ct) =>
            {
                if (before.BassBoost != enabled)
                {
                    await _client.SetBassBoostAsync(enabled, ct);
                }
            },
            after => after.BassBoost == enabled,
            cancellationToken);

    /// <summary>
    /// Sound-Mode setzen (<c>0x0803</c>). Klanglich wirkt der EQ vermutlich nur im Mode Equalizer (docs/protocol.md §7.2).
    /// Sound Personalization wird abgelehnt: Die Vorbedingungen aus [MC] (<c>0x2001</c> = 2, <c>0x0406</c> = 1) sind
    /// nicht geprüft. Weil [MC] nach dem Setzen bis zu 20-mal nachfragt, fragt der Service bis zu 2 s lang nach.
    /// </summary>
    public Task<EqualizerState> SetSoundModeAsync(SoundMode mode, CancellationToken cancellationToken)
    {
        if (mode is not (SoundMode.Off or SoundMode.Equalizer or SoundMode.Podcast))
        {
            throw new FeatureNotSupportedException($"Sound-Mode {mode}: wird noch nicht unterstützt (Vorbedingungen ungeprüft).");
        }

        return EqualizerActionAsync(
            $"Sound-Mode {mode}",
            gate => gate.Equalizer,
            [CommandCatalog.SetSoundMode],
            async (before, ct) =>
            {
                if (before.SoundMode == mode)
                {
                    return;
                }

                await _client.SetSoundModeAsync(mode, ct);
                for (var attempt = 0; attempt < 8 && await _client.ReadSoundModeAsync(ct) != mode; attempt++)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), _time, ct);
                }
            },
            after => after.SoundMode == mode,
            cancellationToken);
    }

    /// <summary>
    /// On-Head-Erkennung, Smart Pause, Auto-Answer, Comfort Call oder Touch-Steuerung ein-/ausschalten (docs/protocol.md
    /// §6.1, §6.3, §6.7): alle lesen, nur bei Abweichung schreiben, alle zurücklesen. Das Headset meldet diese Änderungen nicht.
    /// </summary>
    public async Task<BehaviorState> SetBehaviorAsync(BehaviorSetting setting, bool on, CancellationToken cancellationToken)
    {
        var description = $"{BehaviorState.NameOf(setting)} {(on ? "an" : "aus")}";
        EnsureActionPossible(description, [Momentum4Client.SetterFor(setting)]);

        await _actionGate.WaitAsync(cancellationToken);
        try
        {
            var before = await _client.ReadBehaviorAsync(cancellationToken);
            Store.Apply(new BehaviorChanged(before));
            _logger.LogInformation("{Action}: vorher {Before}.", description, before);

            if (before.Get(setting) != on)
            {
                await _client.SetBehaviorAsync(setting, on, cancellationToken);
            }

            var after = await _client.ReadBehaviorAsync(cancellationToken);
            Store.Apply(new BehaviorChanged(after));
            if (after.Get(setting) != on)
            {
                _logger.LogWarning("{Action}: nicht übernommen, gelesen {After}.", description, after);
                throw new SettingNotAppliedException<BehaviorState>(description, after);
            }

            _logger.LogInformation("{Action}: bestätigt {After}.", description, after);
            return after;
        }
        finally
        {
            _actionGate.Release();
        }
    }

    /// <summary>
    /// Auto Power Off setzen (<c>0x0600 [00, s u16]</c>, docs/protocol.md §6.2), nur mit einem Wert aus
    /// <see cref="Momentum4State.AutoPowerOffChoices"/>: Andere Werte sind nicht belegt, und im Bereich <c>0x06xx</c>
    /// nimmt Unbekanntes das Headset laut [DS] offline. Write → Verify über <c>0x0601 [00]</c>.
    /// </summary>
    public async Task<int> SetAutoPowerOffAsync(int seconds, CancellationToken cancellationToken)
    {
        if (!Momentum4State.AutoPowerOffChoices.Contains(seconds))
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, $"Erlaubt sind {string.Join(", ", Momentum4State.AutoPowerOffChoices)} Sekunden.");
        }

        var description = $"Auto Power Off {(seconds == 0 ? "nie" : $"{seconds / 60} min")}";
        EnsureActionPossible(description, [CommandCatalog.SetTimer]);

        await _actionGate.WaitAsync(cancellationToken);
        try
        {
            var before = await _client.ReadAutoPowerOffAsync(cancellationToken);
            Store.Apply(new AutoPowerOffChanged(before));
            _logger.LogInformation("{Action}: vorher {Before} s.", description, before);

            if (before != seconds)
            {
                await _client.SetAutoPowerOffAsync(seconds, cancellationToken);
            }

            var after = await _client.ReadAutoPowerOffAsync(cancellationToken);
            Store.Apply(new AutoPowerOffChanged(after));
            if (after != seconds)
            {
                _logger.LogWarning("{Action}: nicht übernommen, gelesen {After} s.", description, after);
                throw new SettingNotAppliedException<int>(description, after);
            }

            _logger.LogInformation("{Action}: bestätigt.", description);
            return after;
        }
        finally
        {
            _actionGate.Release();
        }
    }

    /// <summary>Töne und Sprachansagen setzen (<c>0x0801</c>, docs/protocol.md §6.3); Write → Verify über <c>0x0802</c>.</summary>
    public async Task<AudioPromptMode> SetAudioPromptModeAsync(AudioPromptMode mode, CancellationToken cancellationToken)
    {
        var description = $"Töne & Sprachansagen {mode}";
        EnsureActionPossible(description, [CommandCatalog.SetAudioPromptMode]);

        await _actionGate.WaitAsync(cancellationToken);
        try
        {
            var before = await _client.ReadAudioPromptModeAsync(cancellationToken);
            Store.Apply(new PromptModeChanged(before));
            _logger.LogInformation("{Action}: vorher {Before}.", description, before);

            if (before != mode)
            {
                await _client.SetAudioPromptModeAsync(mode, cancellationToken);
            }

            var after = await _client.ReadAudioPromptModeAsync(cancellationToken);
            Store.Apply(new PromptModeChanged(after));
            if (after != mode)
            {
                _logger.LogWarning("{Action}: nicht übernommen, gelesen {After}.", description, after);
                throw new SettingNotAppliedException<AudioPromptMode>(description, after);
            }

            _logger.LogInformation("{Action}: bestätigt.", description);
            return after;
        }
        finally
        {
            _actionGate.Release();
        }
    }

    /// <summary>
    /// ANC-Modus „Comfort“ (<c>0x1A00</c> Modus 2, <c>m4.json</c>: 0 aus, 1 an). Wirkung noch ungeklärt – nur für den
    /// Hardware-Test mit <c>m4poc</c>. Wie bei Anti-Wind beendet das Schreiben Transparent Hearing; war es an, schaltet
    /// der Service es danach wieder ein. Liefert den Zustand und den zurückgelesenen Comfort-Wert.
    /// </summary>
    public async Task<(NoiseControlState State, byte? Comfort)> SetAncComfortAsync(bool on, CancellationToken cancellationToken)
    {
        var description = $"ANC-Comfort {(on ? "an" : "aus")}";
        EnsureActionPossible(description, [CommandCatalog.SetAncMode, CommandCatalog.SetTransparentHearingStatus]);

        await _actionGate.WaitAsync(cancellationToken);
        try
        {
            var (before, table) = await _client.ReadNoiseControlWithTableAsync(cancellationToken);
            Store.Apply(new NoiseControlChanged(before));
            _logger.LogInformation("{Action}: vorher {Before}, Comfort {Comfort}.", description, before, table.Comfort);
            var value = on ? (byte)1 : (byte)0;
            if (table.Comfort != value)
            {
                await WriteAncModeAsync(table, AncCodec.ModeComfort, value, cancellationToken);
                if (before.Mode == NoiseControlMode.Transparency)
                {
                    await _client.SetTransparentHearingStatusAsync(true, cancellationToken);
                }
            }

            var (after, afterTable) = await _client.ReadNoiseControlWithTableAsync(cancellationToken);
            Store.Apply(new NoiseControlChanged(after));
            if (afterTable.Comfort != value)
            {
                throw new SettingNotAppliedException<byte?>(description, afterTable.Comfort);
            }

            _logger.LogInformation("{Action}: bestätigt {After}, Comfort {Comfort}.", description, after, afterTable.Comfort);
            return (after, afterTable.Comfort);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    /// <summary>
    /// BT-Kompatibilitätsmodus setzen (<c>0x0405</c>; vermutlich „High Resolution Audio“: 0 = besserer Klang, 1 = bessere
    /// Kompatibilität). Ungeprüft – nur für den Hardware-Test mit <c>m4poc</c>; das Headset verbindet sich danach
    /// möglicherweise neu. Write → Verify über <c>0x0406</c>.
    /// </summary>
    public async Task<BtCompatibilityMode> SetBtCompatibilityModeAsync(BtCompatibilityMode mode, CancellationToken cancellationToken)
    {
        var description = $"BT-Kompatibilitätsmodus {mode}";
        EnsureActionPossible(description, [CommandCatalog.SetBtCompatibilityMode]);

        await _actionGate.WaitAsync(cancellationToken);
        try
        {
            var before = await _client.ReadBtCompatibilityModeAsync(cancellationToken);
            _logger.LogInformation("{Action}: vorher {Before}.", description, before);
            if (before != mode)
            {
                await _client.SetBtCompatibilityModeAsync(mode, cancellationToken);
            }

            var after = await _client.ReadBtCompatibilityModeAsync(cancellationToken);
            if (after != mode)
            {
                throw new SettingNotAppliedException<BtCompatibilityMode>(description, after);
            }

            _logger.LogInformation("{Action}: bestätigt.", description);
            return after;
        }
        finally
        {
            _actionGate.Release();
        }
    }

    /// <summary>
    /// Gerätenamen (localName, <c>0x2801</c>/<c>0x2802</c>, docs/protocol.md §6.11) setzen; Write → Verify. Der Name ist
    /// roher UTF-8; leer wird abgelehnt. Am M4 (FW 3.38.3) ohne Neustart.
    /// </summary>
    public async Task<string> SetDeviceNameAsync(string name, CancellationToken cancellationToken)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("Der Name darf nicht leer sein.", nameof(name));
        }

        var description = $"Gerätename „{name}“";
        EnsureActionPossible(description, [CommandCatalog.SetLocalName]);

        await _actionGate.WaitAsync(cancellationToken);
        try
        {
            var before = await _client.ReadDeviceNameAsync(cancellationToken);
            Store.Apply(new DeviceNameChanged(before));
            _logger.LogInformation("{Action}: vorher „{Before}“.", description, before);

            if (before != name)
            {
                await _client.SetDeviceNameAsync(name, cancellationToken);
            }

            var after = await _client.ReadDeviceNameAsync(cancellationToken);
            Store.Apply(new DeviceNameChanged(after));
            if (after != name)
            {
                _logger.LogWarning("{Action}: nicht übernommen, gelesen „{After}“.", description, after);
                throw new SettingNotAppliedException<string>(description, after);
            }

            _logger.LogInformation("{Action}: bestätigt.", description);
            return after;
        }
        finally
        {
            _actionGate.Release();
        }
    }

    /// <summary>
    /// Automatische Pause bei Transparenz setzen (<c>0x1800</c>, docs/protocol.md §6.8): Musik anhalten, wenn
    /// Transparenz aktiv ist. Write → Verify über <c>0x1801</c>. Entspricht dem Smart-Control-Schalter „Automatische Pause“.
    /// </summary>
    public async Task<bool> SetAutoPauseAsync(bool on, CancellationToken cancellationToken)
    {
        var description = $"Automatische Pause {(on ? "an" : "aus")}";
        EnsureActionPossible(description, [CommandCatalog.SetTransparentHearingMode]);

        await _actionGate.WaitAsync(cancellationToken);
        try
        {
            var before = await _client.ReadAutoPauseAsync(cancellationToken);
            Store.Apply(new AutoPauseChanged(before));
            _logger.LogInformation("{Action}: vorher {Before}.", description, before);

            if (before != on)
            {
                await _client.SetAutoPauseAsync(on, cancellationToken);
            }

            var after = await _client.ReadAutoPauseAsync(cancellationToken);
            Store.Apply(new AutoPauseChanged(after));
            if (after != on)
            {
                _logger.LogWarning("{Action}: nicht übernommen, gelesen {After}.", description, after);
                throw new SettingNotAppliedException<bool>(description, after);
            }

            _logger.LogInformation("{Action}: bestätigt.", description);
            return after;
        }
        finally
        {
            _actionGate.Release();
        }
    }

    /// <summary>
    /// Gekoppeltes Gerät verbinden (docs/architecture.md §8.5). Vorher: Liste frisch lesen, am Platz
    /// <paramref name="index"/> muss <paramref name="expectedName"/> stehen, das Ziel darf nicht dieser PC sein und die
    /// Grenze aus <c>0x1409</c> darf nicht erreicht sein. Danach <c>0x1402</c>, <c>0x1404</c> abfragen, Liste neu lesen.
    /// Erfolg nur, wenn das Ziel (per Name) verbunden ist und dieser PC verbunden bleibt. Kein Wiederholversuch;
    /// getrennt wird nie (<c>0x1403</c> ist gesperrt).
    /// </summary>
    public async Task<MultipointState> ConnectPeerAsync(int index, string expectedName, CancellationToken cancellationToken)
    {
        var description = $"Verbinden mit Platz {index} („{expectedName}“)";
        EnsureActionPossible(description, [CommandCatalog.ConnectPairedDevice, CommandCatalog.GetPairedDeviceStatus]);

        await _actionGate.WaitAsync(cancellationToken);
        try
        {
            var before = await _client.ReadMultipointAsync(cancellationToken);
            Store.Apply(new MultipointChanged(before));
            var target = before.Devices.FirstOrDefault(d => d.Index == index);
            if (target is null || target.Name != expectedName)
            {
                throw new PeerActionRefusedException($"{description}: Dort steht jetzt „{target?.Name ?? "nichts"}“ – die Liste hat sich geändert.");
            }

            if (target.IsThisComputer)
            {
                throw new PeerActionRefusedException($"{description}: Das ist dieser PC.");
            }

            if (target.Connected)
            {
                _logger.LogInformation("{Action}: ist schon verbunden.", description);
                return before;
            }

            var connectedCount = before.Devices.Count(d => d.Connected);
            if (before.MaxConnections is { } max && connectedCount >= max)
            {
                throw new PeerActionRefusedException($"{description}: Schon {connectedCount} von {max} Geräten verbunden – erst eins am Gerät selbst trennen.");
            }

            _logger.LogInformation("{Action}: vorher {Devices}.", description, Describe(before));
            await _client.ConnectPeerAsync(index, cancellationToken);

            var started = _time.GetTimestamp();
            while (!await _client.GetPeerConnectedAsync(index, cancellationToken) && _time.GetElapsedTime(started) < _options.PeerConnectTimeout)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300), _time, cancellationToken);
            }

            var after = await _client.ReadMultipointAsync(cancellationToken);
            Store.Apply(new MultipointChanged(after));
            var ok = after.Devices.Any(d => d.Name == expectedName && d.Connected) && after.Devices.Any(d => d.IsThisComputer && d.Connected);
            if (!ok)
            {
                _logger.LogWarning("{Action}: nicht bestätigt, gelesen {Devices}.", description, Describe(after));
                throw new SettingNotAppliedException<string>(description, Describe(after));
            }

            _logger.LogInformation("{Action}: bestätigt nach {Ms} ms, {Devices}.", description, (int)_time.GetElapsedTime(started).TotalMilliseconds, Describe(after));
            return after;
        }
        finally
        {
            _actionGate.Release();
        }

        static string Describe(MultipointState s) =>
            string.Join(", ", s.Devices.Select(d => $"#{d.Index} {d.Name}{(d.IsThisComputer ? " (dieser PC)" : string.Empty)}{(d.Connected ? " verbunden" : string.Empty)}"));
    }

    /// <summary>
    /// Gemeinsamer Ablauf aller Noise-Control-Aktionen (docs/architecture.md §8.2): nacheinander, nur bei Verbindung,
    /// Zustand frisch lesen, schreiben, zurücklesen, Store aktualisieren, Ziel prüfen.
    /// </summary>
    private async Task<NoiseControlState> NoiseControlActionAsync(
        string description,
        IReadOnlyList<CommandDescriptor> mayWrite,
        Func<NoiseControlState, AncModeTable, CancellationToken, Task> write,
        Func<NoiseControlState, bool> reached,
        CancellationToken cancellationToken)
    {
        EnsureActionPossible(description, mayWrite);

        await _actionGate.WaitAsync(cancellationToken);
        try
        {
            var (before, table) = await _client.ReadNoiseControlWithTableAsync(cancellationToken);
            Store.Apply(new NoiseControlChanged(before));
            _logger.LogInformation("{Action}: vorher {Before}.", description, before);

            await write(before, table, cancellationToken);

            var after = await _client.ReadNoiseControlAsync(cancellationToken);
            Store.Apply(new NoiseControlChanged(after));
            if (!reached(after))
            {
                _logger.LogWarning("{Action}: nicht übernommen, gelesen {After}.", description, after);
                throw new SettingNotAppliedException<NoiseControlState>(description, after);
            }

            _logger.LogInformation("{Action}: bestätigt {After}.", description, after);
            return after;
        }
        finally
        {
            _actionGate.Release();
        }
    }

    /// <summary>
    /// Gemeinsamer Ablauf der EQ-Aktionen: wie <see cref="NoiseControlActionAsync"/>, dazu Firmware-Prüfung. Gelesen
    /// werden nur die veränderlichen Werte (Gains über <c>0x1003</c>, Bass Boost, Sound-Mode); Bandanzahl, Bereich und
    /// Frequenzen kommen aus dem zuletzt gelesenen Zustand.
    /// </summary>
    private async Task<EqualizerState> EqualizerActionAsync(
        string description,
        Func<FeatureGate, bool> supported,
        IReadOnlyList<CommandDescriptor> mayWrite,
        Func<EqualizerState, CancellationToken, Task> write,
        Func<EqualizerState, bool> reached,
        CancellationToken cancellationToken)
    {
        EnsureActionPossible(description, mayWrite);
        if (!supported(_gate))
        {
            throw new FeatureNotSupportedException($"{description}: von Firmware {_gate.Firmware?.ToString() ?? "(unbekannt)"} nicht unterstützt.");
        }

        await _actionGate.WaitAsync(cancellationToken);
        try
        {
            var before = await ReadEqualizerValuesAsync(description, cancellationToken);
            _logger.LogInformation("{Action}: vorher {Before}.", description, before);

            await write(before, cancellationToken);

            var after = await ReadEqualizerValuesAsync(description, cancellationToken);
            if (!reached(after))
            {
                _logger.LogWarning("{Action}: nicht übernommen, gelesen {After}.", description, after);
                throw new SettingNotAppliedException<EqualizerState>(description, after);
            }

            _logger.LogInformation("{Action}: bestätigt {After}.", description, after);
            return after;
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<EqualizerState> ReadEqualizerValuesAsync(string description, CancellationToken cancellationToken)
    {
        var bandCount = Store.Current.Equalizer?.Bands.Count
            ?? throw new InvalidOperationException($"{description}: EQ-Konfiguration noch nicht gelesen.");
        Store.Apply(new EqGainsChanged(await _client.ReadEqGainsAsync(bandCount, cancellationToken)));
        if (_gate.BassBoost)
        {
            Store.Apply(new BassBoostChanged(await _client.ReadBassBoostAsync(cancellationToken)));
        }

        Store.Apply(new SoundModeChanged(await _client.ReadSoundModeAsync(cancellationToken)));
        return Store.Current.Equalizer!;
    }

    private async Task RollBackBandsAsync(EqualizerState before, List<int> written, CancellationToken cancellationToken)
    {
        for (var i = written.Count - 1; i >= 0; i--)
        {
            var band = written[i];
            try
            {
                await _client.SetEqBandAsync(band, before.Bands[band].GainDb, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError("EQ-Rollback: Band {Band} konnte nicht zurückgesetzt werden ({Reason}).", band, ex.Message);
            }
        }
    }

    private static void ValidateGain(EqualizerState state, int band, decimal gainDb)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(band);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(band, state.Bands.Count);
        if (gainDb < state.MinGainDb || gainDb > state.MaxGainDb || gainDb * 10m != decimal.Truncate(gainDb * 10m))
        {
            throw new ArgumentOutOfRangeException(nameof(gainDb), gainDb, $"Gain muss in 0,1-dB-Schritten zwischen {FormatDb(state.MinGainDb)} und {FormatDb(state.MaxGainDb)} dB liegen.");
        }
    }

    private static string FormatDb(decimal db) => db.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);

    /// <summary>Nur bei Verbindung, und jeder Befehl, den die Aktion schreiben könnte, muss erlaubt sein – sonst bliebe sie halb ausgeführt.</summary>
    private void EnsureActionPossible(string description, IReadOnlyList<CommandDescriptor> mayWrite)
    {
        if (Store.Current.Connection != ConnectionState.Connected)
        {
            throw new HeadsetUnavailableException($"{description}: Headset nicht verbunden ({Store.Current.Connection}).");
        }

        foreach (var command in mayWrite)
        {
            if (!_queue.Policy.IsAllowed(command, out var reason))
            {
                throw new CommandNotAllowedException(command, $"{description}: {reason}");
            }
        }
    }

    /// <summary>Alle Befehle, die <see cref="ApplyModeAsync"/> für diesen Zielmodus schreiben könnte.</summary>
    private static CommandDescriptor[] CommandsFor(NoiseControlMode mode) => mode switch
    {
        NoiseControlMode.Transparency => [CommandCatalog.SetAncEnabled, CommandCatalog.SetTransparentHearingStatus],
        NoiseControlMode.Off => [CommandCatalog.SetAncEnabled],
        _ => [CommandCatalog.SetTransparentHearingStatus, CommandCatalog.SetAncEnabled, CommandCatalog.SetAncMode],
    };

    private async Task ApplyModeAsync(NoiseControlMode mode, NoiseControlState before, AncModeTable table, CancellationToken ct)
    {
        if (mode == NoiseControlMode.Off)
        {
            // Transparent Hearing bleibt dabei formal an, wirkt aber ohne ANC nicht (Hardware-Befund Phase E).
            if (before.AncEnabled)
            {
                await _client.SetAncEnabledAsync(false, ct);
            }

            return;
        }

        if (mode == NoiseControlMode.Transparency)
        {
            if (!before.AncEnabled)
            {
                // ANC einschalten beendet Transparent Hearing – danach muss es neu eingeschaltet werden.
                await _client.SetAncEnabledAsync(true, ct);
                await _client.SetTransparentHearingStatusAsync(true, ct);
            }
            else if (before.TransparentHearing != true)
            {
                await _client.SetTransparentHearingStatusAsync(true, ct);
            }

            return;
        }

        if (before.TransparentHearing == true && before.AncEnabled)
        {
            await _client.SetTransparentHearingStatusAsync(false, ct);
        }

        if (!before.AncEnabled)
        {
            await _client.SetAncEnabledAsync(true, ct);
        }

        var adaptive = mode == NoiseControlMode.Adaptive;
        if (before.Adaptive != adaptive)
        {
            await WriteAncModeAsync(table, AncCodec.ModeAdaptive, adaptive ? (byte)1 : (byte)0, ct);
        }
    }

    private async Task WriteAncModeAsync(AncModeTable table, byte modeId, byte value, CancellationToken ct)
    {
        var format = AncWriteFormat ?? Protocol.Features.AncModeWriteFormat.FullTable;
        try
        {
            await _client.SetAncModeAsync(table, modeId, value, format, ct);
            _ancWriteFormat = format;
        }
        catch (GaiaErrorException ex) when (AncWriteFormat is null && ex.Reason == 0x05)
        {
            var other = format == Protocol.Features.AncModeWriteFormat.FullTable
                ? Protocol.Features.AncModeWriteFormat.SinglePair
                : Protocol.Features.AncModeWriteFormat.FullTable;
            _logger.LogWarning("0x1A00 im Format {Format} abgelehnt (Reason 0x05) – versuche {Other}.", format, other);
            await _client.SetAncModeAsync(table, modeId, value, other, ct);
            _ancWriteFormat = other;
        }
    }

    private async Task RunSessionAsync(CancellationToken session)
    {
        _consecutiveTimeouts = 0;

        // Letzter bestätigter Stand der vorigen Sitzung (der Store behält ihn über die Trennung hinweg).
        var modeBefore = Store.Current.NoiseControl?.Mode;

        var model = await _client.GetModelIdAsync(session);
        var firmware = await _client.GetFirmwareVersionAsync(session);
        _gate = new FeatureGate(firmware);
        Store.Apply(new DeviceIdentified(new DeviceInfo(model, firmware)));
        _logger.LogInformation("Headset: {Model}, Firmware {Firmware}.", model, firmware);
        try
        {
            Store.Apply(new SerialNumberRead(await _client.ReadSerialNumberAsync(session)));
        }
        catch (Exception ex) when (ex is ProtocolFormatException or GaiaErrorException or TimeoutException)
        {
            _logger.LogWarning("Seriennummer nicht gelesen: {Message}", ex.Message);
        }

        if (_options.RegisterNotifications)
        {
            await RegisterNotificationsAsync(session);
        }

        await RefreshAsync(RefreshScope.All, session);

        // Timer vor der Meldung „Connected“ anlegen, damit der Poll-Takt ab diesem Moment läuft.
        using var timer = new PeriodicTimer(_options.PollInterval, _time);
        Store.Apply(new ConnectionChanged(ConnectionState.Connected));
        await RestoreTransparencyIfNeededAsync(modeBefore, session);
        await PollLoopAsync(timer, session);
    }

    private async Task RestoreTransparencyIfNeededAsync(NoiseControlMode? modeBefore, CancellationToken session)
    {
        if (!RestoreTransparencyAfterRestart || modeBefore != NoiseControlMode.Transparency
            || Store.Current.NoiseControl is not { AncEnabled: true, TransparentHearing: false })
        {
            return;
        }

        _logger.LogInformation("Headset kam ohne Transparent Hearing zurück (vorher Transparenz) – schalte es wieder ein.");
        try
        {
            await SetNoiseControlModeAsync(NoiseControlMode.Transparency, session);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Transparenz nicht wiederhergestellt: {Message}", ex.Message);
        }
    }

    private async Task RegisterNotificationsAsync(CancellationToken session)
    {
        foreach (var feature in _options.NotificationFeatures)
        {
            try
            {
                await _client.RegisterNotificationsAsync(feature, session);
                _logger.LogInformation("Notifications für Feature {Feature} angemeldet.", feature);
            }
            catch (CommandNotAllowedException ex)
            {
                _logger.LogInformation("Notifications nicht angemeldet ({Reason}) – Zustand kommt nur per Polling.", ex.Message);
                return;
            }
            catch (GaiaErrorException ex)
            {
                _logger.LogWarning("Notification-Anmeldung für Feature {Feature} abgelehnt: {Message}", feature, ex.Message);
            }
        }
    }

    private async Task PollLoopAsync(PeriodicTimer timer, CancellationToken session)
    {
        Task<bool>? tick = null;
        var ticks = 0;
        while (!session.IsCancellationRequested)
        {
            tick ??= timer.WaitForNextTickAsync(session).AsTask();
            var signal = Volatile.Read(ref _refreshSignal).Task;
            var completed = await Task.WhenAny(tick, signal);

            var scope = (RefreshScope)Interlocked.Exchange(ref _pendingScope, 0);
            if (completed == signal)
            {
                Interlocked.Exchange(ref _refreshSignal, NewSignal());
            }
            else
            {
                if (!await tick)
                {
                    return;
                }

                tick = null;
                ticks++;
                scope |= ticks % Math.Max(1, _options.FullRefreshEvery) == 0
                    ? RefreshScope.All
                    : RefreshScope.Battery | RefreshScope.NoiseControl;
            }

            session.ThrowIfCancellationRequested();
            await RefreshAsync(scope, session);
        }

        session.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Liest die angeforderten Bereiche und legt sie im Store ab. Formatfehler und Ablehnungen werden protokolliert,
    /// der Bereich bleibt beim letzten bestätigten Stand. Wiederholte Timeouts beenden die Sitzung (Reconnect).
    /// </summary>
    private async Task RefreshAsync(RefreshScope scope, CancellationToken session)
    {
        await Step(RefreshScope.Battery, async () => Store.Apply(new BatteryChanged(await _client.GetBatteryLevelAsync(session))));
        await Step(RefreshScope.NoiseControl, async () => Store.Apply(new NoiseControlChanged(await _client.ReadNoiseControlAsync(session))));
        await Step(RefreshScope.NoiseControl, async () => Store.Apply(new AutoPauseChanged(await _client.ReadAutoPauseAsync(session))));
        await Step(RefreshScope.Equalizer, async () =>
        {
            if (!_gate.Equalizer)
            {
                return;
            }

            var eq = await _client.ReadEqualizerAsync(_frequencies, _gate.BassBoost, session);
            _frequencies = eq.Bands.Select(b => b.FrequencyHz ?? 0).ToList();
            Store.Apply(new EqualizerChanged(eq));
        });
        await Step(RefreshScope.Multipoint, async () => Store.Apply(new MultipointChanged(await _client.ReadMultipointAsync(session))));
        await Step(RefreshScope.Behavior, async () => Store.Apply(new BehaviorChanged(await _client.ReadBehaviorAsync(session))));
        await Step(RefreshScope.Behavior, async () => Store.Apply(new WearStateChanged(await _client.ReadWearStateAsync(session))));
        await Step(RefreshScope.Behavior, async () => Store.Apply(new AutoPowerOffChanged(await _client.ReadAutoPowerOffAsync(session))));
        await Step(RefreshScope.Behavior, async () => Store.Apply(new PromptModeChanged(await _client.ReadAudioPromptModeAsync(session))));
        await Step(RefreshScope.Behavior, async () => Store.Apply(new DeviceNameChanged(await _client.ReadDeviceNameAsync(session))));
        await Step(RefreshScope.Behavior, async () => Store.Apply(new CodecChanged(await _client.ReadCodecAsync(session))));
        await Step(RefreshScope.Behavior, async () => Store.Apply(new PromptLanguageChanged(await _client.ReadPromptLanguageAsync(session))));

        if (scope == RefreshScope.All)
        {
            Store.Apply(new FullRefreshCompleted(_time.GetUtcNow()));
        }

        async Task Step(RefreshScope part, Func<Task> read)
        {
            if (!scope.HasFlag(part))
            {
                return;
            }

            try
            {
                await read();
                _consecutiveTimeouts = 0;
            }
            catch (TimeoutException ex)
            {
                _consecutiveTimeouts++;
                _logger.LogWarning("Lesen von {Part} ohne Antwort ({Count}/{Max}): {Message}", part, _consecutiveTimeouts, _options.MaxConsecutiveTimeouts, ex.Message);
                if (_consecutiveTimeouts >= _options.MaxConsecutiveTimeouts)
                {
                    throw;
                }
            }
            catch (Exception ex) when (ex is ProtocolFormatException or GaiaErrorException or CommandNotAllowedException)
            {
                _logger.LogWarning("Lesen von {Part} fehlgeschlagen, letzter Stand bleibt: {Message}", part, ex.Message);
            }
        }
    }

    private void OnNotification(object? sender, GaiaFrame frame)
    {
        if (_dispatcher.Translate(frame) is { } deviceEvent)
        {
            ApplyPushed(deviceEvent);
        }
    }

    /// <summary>Vom Headset gemeldete Änderung übernehmen; der Tragezustand kommt zusätzlich ins Log (für Diagnose).</summary>
    private void ApplyPushed(DeviceEvent deviceEvent)
    {
        if (Store.Apply(deviceEvent) && deviceEvent is WearStateChanged wear)
        {
            _logger.LogInformation("Tragezustand gemeldet: {State}.", wear.State);
        }
    }

    private bool OnUnsolicitedResponse(GaiaFrame frame)
    {
        if (_dispatcher.TranslateUnsolicitedResponse(frame) is not { } deviceEvent)
        {
            return false;
        }

        ApplyPushed(deviceEvent);
        return true;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
