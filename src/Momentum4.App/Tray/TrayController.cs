// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Momentum4.App.Localization;
using Momentum4.App.ViewModels;
using Momentum4.Core.Services;
using Momentum4.Core.State;
using Momentum4.Protocol.Features;

namespace Momentum4.App.Tray;

/// <summary>
/// Bedienung aus dem Tray (Spezifikation §17): Tooltip mit Akku, Linksklick = Schnellzugriff, Doppelklick = Hauptfenster,
/// Kontextmenü mit Noise Control, Anti-Wind, EQ-Preset, Geräten, Öffnen und Beenden. Das Menü arbeitet direkt mit dem
/// Service – ein Fenster muss dafür nicht existieren.
/// Zuordnung der Noise-Control-Einträge wie in docs/architecture.md §7.
/// </summary>
internal sealed class TrayController : IDisposable
{
    private const uint OpenId = 1, QuitId = 2;
    private const uint ModeBase = 100, AntiWindBase = 110, PresetBase = 200, PeerBase = 300;

    private static readonly (NoiseControlMode Mode, string Text)[] Modes =
    [
        (NoiseControlMode.Adaptive, "Adaptive ANC"),
        (NoiseControlMode.Custom, "ANC"),
        (NoiseControlMode.Transparency, L.Get("ModeTransparency")),
        (NoiseControlMode.Off, L.Get("ModeOff")),
    ];

    private static readonly string[] AntiWindTexts = [L.Get("AntiWindOff"), L.Get("AntiWindMax"), L.Get("AntiWindAuto")];

    private readonly HeadsetHost _host;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action _open;
    private readonly Action<(int Left, int Top, int Right, int Bottom)?> _quick;
    private readonly Func<Task> _quit;
    private readonly ILogger _logger;
    private readonly TrayIcon _icon;
    private StateStore? _store;

    public TrayController(HeadsetHost host, DispatcherQueue dispatcher, Action open, Action<(int Left, int Top, int Right, int Bottom)?> quick, Func<Task> quit)
    {
        _host = host;
        _dispatcher = dispatcher;
        _open = open;
        _quick = quick;
        _quit = quit;
        _logger = host.LoggerFactory.CreateLogger<TrayController>();
        _icon = new TrayIcon("MOMENTUM 4", Path.Combine(AppContext.BaseDirectory, "Assets"));
        _icon.OpenRequested += (_, _) => _dispatcher.TryEnqueue(() => _open());
        _icon.SelectRequested += (_, _) =>
        {
            var anchor = _icon.GetIconRect(); // jetzt lesen – die Taskleiste ist in diesem Moment sicher eingeblendet
            _dispatcher.TryEnqueue(() => _quick(anchor));
        };
        _icon.MenuRequested += (_, _) => _dispatcher.TryEnqueue(ShowMenu);
        _host.Changed += (_, _) => _dispatcher.TryEnqueue(Attach);
        Attach();
    }

    public void ShowBalloon(string title, string text, bool warning = false) => _icon.ShowBalloon(title, text, warning);

    /// <summary>Tooltip neu aufbauen, z. B. nach Änderung der Einstellung „Akku im Tooltip“.</summary>
    public void Refresh() => UpdateTooltip(_store?.Current);

    public void Dispose() => _icon.Dispose();

    private void Attach()
    {
        if (_host.Service is { Store: var store } && _store is null)
        {
            _store = store;
            store.StateChanged += (_, e) =>
            {
                if (e.Previous.DisplayBattery != e.Current.DisplayBattery || e.Previous.Connection != e.Current.Connection || e.Previous.Charging != e.Current.Charging)
                {
                    _dispatcher.TryEnqueue(() => UpdateTooltip(e.Current));
                }
            };
        }

        UpdateTooltip(_store?.Current);
    }

    private void UpdateTooltip(Momentum4State? state)
    {
        var text = state?.Connection switch
        {
            ConnectionState.Connected when !_host.Settings.BatteryInTooltip || state.BatteryPercent is null => L.Get("TrayConnected"),
            ConnectionState.Connected => L.Get("TrayConnectedBattery", state.BatteryPercent) + (state.Charging == true ? L.Get("ChargingSuffix") : string.Empty),
            ConnectionState.Connecting or ConnectionState.Initializing => L.Get("TrayConnecting"),
            null when _host.StartupProblem is not null => L.Get("TrayNoHeadset"),
            _ when state?.DisplayBattery is (var percent, true) && _host.Settings.BatteryInTooltip => L.Get("TrayWindows", percent),
            _ => L.Get("TrayDisconnected"),
        };
        _icon.SetTooltip(text);
    }

    private void ShowMenu()
    {
        var state = _store?.Current;
        var connected = state?.Connection == ConnectionState.Connected;
        var items = new List<TrayMenuItem>
        {
            new("MOMENTUM 4", Enabled: false),
            new(state?.DisplayBattery switch
            {
                (var percent, false) => L.Get("TrayBattery", percent),
                (var percent, true) => L.Get("TrayBatteryWindows", percent),
                _ => L.Get("NotConnected"),
            }, Enabled: false),
            TrayMenuItem.Separator,
        };

        if (connected && state!.NoiseControl is { } nc)
        {
            items.Add(new TrayMenuItem("Noise Control", Children: [.. Modes.Select((m, i) => new TrayMenuItem(m.Text, ModeBase + (uint)i, Checked: nc.Mode == m.Mode))]));
            items.Add(new TrayMenuItem(L.Get("AntiWind"), Children: [.. AntiWindTexts.Select((t, i) => new TrayMenuItem(t, AntiWindBase + (uint)i, Checked: (int)nc.AntiWind == i))]));
        }

        var presets = _host.Presets.All;
        if (connected && state!.Equalizer is { } eq)
        {
            var active = _host.Presets.FindMatching([.. eq.Bands.Select(b => b.GainDb)]);
            items.Add(new TrayMenuItem(L.Get("TrayEqPreset"), Children: [.. presets.Select((p, i) => new TrayMenuItem(p.Name, PresetBase + (uint)i, Checked: ReferenceEquals(p, active) || p == active))]));
        }

        var devices = state?.Multipoint;
        if (devices is { Devices.Count: > 0 })
        {
            var full = devices.MaxConnections is { } max && devices.Devices.Count(d => d.Connected) >= max;
            items.Add(new TrayMenuItem(L.Get("Devices"), Children: [.. devices.Devices.Select(d => new TrayMenuItem(
                d.IsThisComputer ? L.Get("ThisPc", d.Name) : d.Name,
                PeerBase + (uint)d.Index,
                Enabled: connected && !d.Connected && !d.IsThisComputer && !full,
                Checked: d.Connected))]));
        }

        items.Add(TrayMenuItem.Separator);
        items.Add(new TrayMenuItem(L.Get("Open"), OpenId));
        items.Add(new TrayMenuItem(L.Get("Quit"), QuitId));

        var id = _icon.ShowMenu(items);
        _dispatcher.TryEnqueue(async () => await HandleAsync(id, state, presets));
    }

    private async Task HandleAsync(uint id, Momentum4State? state, IReadOnlyList<Core.Presets.EqPreset> presets)
    {
        switch (id)
        {
            case 0:
                return;
            case OpenId:
                _open();
                return;
            case QuitId:
                await _quit();
                return;
            case >= ModeBase and < ModeBase + 4:
                var (mode, text) = Modes[id - ModeBase];
                await RunAsync(L.Get("ActNoiseControl", text), (s, ct) => s.SetNoiseControlModeAsync(mode, ct));
                return;
            case >= AntiWindBase and < AntiWindBase + 3:
                var antiWind = (AntiWindMode)(id - AntiWindBase);
                await RunAsync(L.Get("ActAntiWind", AntiWindTexts[id - AntiWindBase]), (s, ct) => s.SetAntiWindAsync(antiWind, ct));
                return;
            case >= PresetBase and < PeerBase when id - PresetBase < presets.Count:
                var preset = presets[(int)(id - PresetBase)];
                await RunAsync(L.Get("ActPreset", preset.Name), (s, ct) => s.SetEqGainsAsync(preset.GainsDb, ct));
                return;
            case >= PeerBase when state?.Multipoint?.Devices.FirstOrDefault(d => d.Index == id - PeerBase) is { } peer:
                await RunAsync(L.Get("ActConnectPeer", peer.Name), (s, ct) => s.ConnectPeerAsync(peer.Index, peer.Name, ct));
                return;
        }
    }

    private async Task RunAsync(string what, Func<Momentum4Service, CancellationToken, Task> action)
    {
        if (_host.Service is not { } service)
        {
            return;
        }

        try
        {
            await action(service, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Tray: {Action} nicht ausgeführt: {Reason}", what, ex.Message);
            _icon.ShowBalloon(L.Get("NotApplied"), $"{what}: {ActionErrors.Describe(ex)}", warning: true);
        }
    }
}
