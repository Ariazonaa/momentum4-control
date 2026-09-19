// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Momentum4.App.Localization;
using Momentum4.Core.Services;
using Momentum4.Core.State;
using Momentum4.Protocol.Features;

namespace Momentum4.App.ViewModels;

/// <summary>
/// Hauptansicht (Spezifikation §19). Liest nur aus dem <see cref="StateStore"/> und handelt nur über den Service
/// (docs/architecture.md §7); angezeigt wird immer der vom Headset bestätigte Zustand.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IActionRunner, IDisposable
{
    private readonly HeadsetHost _host;
    private readonly DispatcherQueue _dispatcher;
    private readonly EventHandler _hostChanged;
    private readonly EventHandler<StateChangedEventArgs> _stateChanged;
    private StateStore? _store;
    private int _running;

    public MainViewModel(HeadsetHost host, DispatcherQueue dispatcher, Action settingsChanged)
    {
        _host = host;
        _dispatcher = dispatcher;
        NoiseControl = new NoiseControlViewModel(this, dispatcher);
        Equalizer = new EqualizerViewModel(this, host.Presets, dispatcher);
        Behavior = new BehaviorViewModel(this);
        Devices = new DevicesViewModel(this);
        Settings = new SettingsViewModel(host, settingsChanged);
        _hostChanged = (_, _) => _dispatcher.TryEnqueue(Attach);
        _stateChanged = (_, e) => _dispatcher.TryEnqueue(() => Apply(e.Current));
        _host.Changed += _hostChanged;
        Attach(); // der Host läuft schon, wenn das Fenster aus dem Tray neu geöffnet wird
    }

    public NoiseControlViewModel NoiseControl { get; }

    public EqualizerViewModel Equalizer { get; }

    public BehaviorViewModel Behavior { get; }

    public DevicesViewModel Devices { get; }

    public SettingsViewModel Settings { get; }

    /// <summary>Für den Protokoll-Inspektor; <c>null</c>, solange kein Service läuft.</summary>
    public Momentum4.Core.Queue.GaiaCommandQueue? Queue => _host.Service?.Queue;

    [ObservableProperty]
    public partial bool ShowSettings { get; set; }

    [ObservableProperty]
    public partial bool ShowMain { get; set; } = true;

    [ObservableProperty]
    public partial string StatusText { get; set; } = L.Get("SearchingHeadset");

    [ObservableProperty]
    public partial string BatteryText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DetailText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowProblem { get; set; }

    [ObservableProperty]
    public partial string ProblemTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProblemMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProblemAction { get; set; } = L.Get("Reconnect");

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool ShowActionError { get; set; }

    [ObservableProperty]
    public partial string ActionError { get; set; } = string.Empty;

    /// <summary>Beim Schließen des Fensters: nicht mehr an Host und Store hängen, damit alles freigegeben werden kann.</summary>
    public void Dispose()
    {
        _host.Changed -= _hostChanged;
        if (_store is not null)
        {
            _store.StateChanged -= _stateChanged;
        }
    }

    public async Task<bool> RunAsync(string what, Func<Momentum4Service, CancellationToken, Task> action)
    {
        if (_host.Service is not { } service)
        {
            return false;
        }

        ShowActionError = false;
        IsBusy = ++_running > 0;
        try
        {
            await action(service, CancellationToken.None);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ActionError = $"{what}: {ActionErrors.Describe(ex)}";
            ShowActionError = true;
            return false;
        }
        finally
        {
            IsBusy = --_running > 0;
            if (_store is { } store)
            {
                Apply(store.Current); // immer den bestätigten Stand zeigen, auch nach einem Fehler
            }
        }
    }

    [RelayCommand]
    private void ToggleSettings()
    {
        ShowSettings = !ShowSettings;
        ShowMain = !ShowSettings;
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        if (_host.Service is { } service)
        {
            service.NotifyHeadsetAvailable("Neu verbinden (User)");
            StatusText = L.Get("Connecting");
        }
        else
        {
            StatusText = L.Get("SearchingHeadset");
            ShowProblem = false;
            await _host.StartAsync(CancellationToken.None);
        }
    }

    private void Attach()
    {
        if (_host.StartupProblem is { } problem)
        {
            StatusText = L.Get("NotConnected");
            ProblemTitle = L.Get("NoHeadset");
            ProblemMessage = problem;
            ProblemAction = L.Get("SearchAgain");
            ShowProblem = true;
            return;
        }

        if (_host.Service is { Store: var store } && _store is null)
        {
            _store = store;
            store.StateChanged += _stateChanged;
            Apply(store.Current);
        }
    }

    private static string? WearText(WearState? wear) => wear switch
    {
        WearState.Worn => L.Get("WornState"),
        WearState.NotWorn => L.Get("NotWornState"),
        _ => null,
    };

    private void Apply(Momentum4State state)
    {
        var connected = state.Connection == ConnectionState.Connected;
        BatteryText = state.DisplayBattery switch
        {
            ({ } percent, false) => $"{percent} %",
            ({ } percent, true) => L.Get("BatteryWindows", percent),
            _ => string.Empty,
        };
        StatusText = state.Connection switch
        {
            ConnectionState.Connected => string.Join(" · ", new[] { L.Get("Connected"), WearText(state.Wear), state.Charging == true ? L.Get("Charging") : null }.OfType<string>()),
            ConnectionState.Connecting or ConnectionState.Initializing => L.Get("Connecting"),
            ConnectionState.WaitingRetry => L.Get("DisconnectedRetry"),
            _ => L.Get("Disconnected"),
        };
        DetailText = state.Device is { } device
            ? $"{device.ModelId} · Firmware {device.Firmware}" + (state.Codec is { } codec ? $" · {Momentum4.Protocol.Features.GenericAudioCodec.CodecName(codec)}" : string.Empty)
            : string.Empty;

        ShowProblem = state.Connection is ConnectionState.WaitingRetry or ConnectionState.Disconnected;
        ProblemTitle = L.Get("HeadsetDisconnected");
        ProblemMessage = L.Get("DisconnectedMessage");
        ProblemAction = L.Get("Reconnect");

        NoiseControl.Apply(state.NoiseControl, state.AutoPauseOnTransparency, connected);
        Equalizer.Apply(state.Equalizer, connected);
        Behavior.Apply(state.Behavior, state.AutoPowerOffSeconds, state.PromptMode, connected);
        Devices.Apply(state.Multipoint, connected);
        Settings.Apply(state);
    }
}
