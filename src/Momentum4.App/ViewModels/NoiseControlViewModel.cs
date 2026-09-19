// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Momentum4.App.Localization;
using Momentum4.Core.State;
using Momentum4.Protocol.Features;

namespace Momentum4.App.ViewModels;

/// <summary>
/// Noise Control nach den Hardware-Befunden aus Phase E (docs/architecture.md §7): Moduswahl Transparenz / ANC /
/// Adaptive / Aus, im Modus ANC zusätzlich die ANC-Stärke (Pegel, 0 = am stärksten), dazu Anti-Wind.
/// </summary>
public sealed partial class NoiseControlViewModel : ObservableObject
{
    private static readonly NoiseControlMode[] ModeOrder =
        [NoiseControlMode.Transparency, NoiseControlMode.Custom, NoiseControlMode.Adaptive, NoiseControlMode.Off];

    private readonly IActionRunner _run;
    private readonly DispatcherQueueTimer _strengthDebounce;
    private NoiseControlState? _last;
    private bool _applying;

    /// <summary>Pegel, den der User gerade einstellt – solange gesetzt, überschreiben Notifications den Regler nicht.</summary>
    private int? _pendingLevel;

    public NoiseControlViewModel(IActionRunner run, DispatcherQueue dispatcher)
    {
        _run = run;
        _strengthDebounce = dispatcher.CreateTimer();
        _strengthDebounce.Interval = TimeSpan.FromMilliseconds(300); // [MC]: nur den Endwert senden
        _strengthDebounce.IsRepeating = false;
        _strengthDebounce.Tick += async (_, _) => await SendStrengthAsync();
    }

    // List<T> statt Collection-Expression: WinRT/AOT kann nur echte Listen als ItemsSource übergeben.
    public List<string> Modes { get; } = [L.Get("ModeTransparency"), L.Get("ModeAnc"), L.Get("ModeAdaptive"), L.Get("ModeOff")];

    public List<string> AntiWindOptions { get; } = [L.Get("AntiWindOff"), L.Get("AntiWindMax"), L.Get("AntiWindAuto")];

    [ObservableProperty]
    public partial bool IsAvailable { get; set; }

    [ObservableProperty]
    public partial int SelectedModeIndex { get; set; } = -1;

    [ObservableProperty]
    public partial bool ShowStrength { get; set; }

    /// <summary>0…100, 100 = stärkstes ANC (= Pegel 0).</summary>
    [ObservableProperty]
    public partial double Strength { get; set; }

    [ObservableProperty]
    public partial int AntiWindIndex { get; set; } = -1;

    [ObservableProperty]
    public partial string ModeHint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool AutoPause { get; set; }

    private bool? _lastAutoPause;

    public void Apply(NoiseControlState? state, bool? autoPause, bool connected)
    {
        _last = state;
        _lastAutoPause = autoPause;
        _applying = true;
        try
        {
            AutoPause = autoPause == true;
            IsAvailable = connected && state is not null;
            if (state is null)
            {
                SelectedModeIndex = -1;
                AntiWindIndex = -1;
                ShowStrength = false;
                ModeHint = string.Empty;
                return;
            }

            SelectedModeIndex = Array.IndexOf(ModeOrder, state.Mode);
            ShowStrength = state.Mode == NoiseControlMode.Custom;
            if (_pendingLevel is null)
            {
                Strength = 100 - state.Level;
            }

            AntiWindIndex = (int)state.AntiWind;
            ModeHint = state.Mode switch
            {
                NoiseControlMode.Transparency => L.Get("HintTransparency"),
                NoiseControlMode.Custom => L.Get("HintCustom"),
                NoiseControlMode.Adaptive => L.Get("HintAdaptive"),
                _ => L.Get("HintOff"),
            };
        }
        finally
        {
            _applying = false;
        }
    }

    partial void OnSelectedModeIndexChanged(int value)
    {
        if (_applying || value < 0 || value >= ModeOrder.Length || _last?.Mode == ModeOrder[value])
        {
            return;
        }

        var mode = ModeOrder[value];
        _ = _run.RunAsync(L.Get("ActNoiseControl", Modes[value]), (service, ct) => service.SetNoiseControlModeAsync(mode, ct));
    }

    partial void OnStrengthChanged(double value)
    {
        if (_applying)
        {
            return;
        }

        _pendingLevel = 100 - (int)Math.Round(value);
        _strengthDebounce.Stop();
        _strengthDebounce.Start();
    }

    partial void OnAntiWindIndexChanged(int value)
    {
        if (_applying || value is < 0 or > 2 || _last is { } last && (int)last.AntiWind == value)
        {
            return;
        }

        var antiWind = (AntiWindMode)value;
        _ = _run.RunAsync(L.Get("ActAntiWind", AntiWindOptions[value]), (service, ct) => service.SetAntiWindAsync(antiWind, ct));
    }

    partial void OnAutoPauseChanged(bool value)
    {
        if (_applying || _lastAutoPause == value)
        {
            return;
        }

        _ = _run.RunAsync(L.Get(value ? "ActAutoPauseOn" : "ActAutoPauseOff"), (service, ct) => service.SetAutoPauseAsync(value, ct));
    }

    private async Task SendStrengthAsync()
    {
        if (_pendingLevel is not { } level)
        {
            return;
        }

        await _run.RunAsync(L.Get("ActAncStrength", 100 - level), async (service, ct) =>
        {
            try
            {
                await service.SetNoiseControlLevelAsync(level, ct);
            }
            finally
            {
                // Hat der User inzwischen weitergezogen, gilt sein neuer Wert.
                if (_pendingLevel == level)
                {
                    _pendingLevel = null;
                }
            }
        });
    }
}
