// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Momentum4.Core.Presets;
using Momentum4.Core.State;
using Momentum4.Protocol.Features;

namespace Momentum4.App.ViewModels;

/// <summary>Ein EQ-Band; die Frequenz kommt vom Gerät (<c>0x100B</c>), nie aus festen Labels.</summary>
public sealed partial class EqBandViewModel(int index, string frequencyLabel, double minimum, double maximum, Action<EqBandViewModel> changed)
    : ObservableObject
{
    public int Index { get; } = index;

    public string FrequencyLabel { get; } = frequencyLabel;

    public double Minimum { get; } = minimum;

    public double Maximum { get; } = maximum;

    internal bool Applying { get; set; }

    [ObservableProperty]
    public partial double Gain { get; set; }

    [ObservableProperty]
    public partial string GainText { get; set; } = 0.0.ToString("0.0", CultureInfo.CurrentCulture);

    partial void OnGainChanged(double value)
    {
        GainText = value.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture);
        if (!Applying)
        {
            changed(this);
        }
    }
}

/// <summary>
/// Equalizer: Klangmodus (Equalizer / Podcast / Neutral), Bänder mit Gerätefrequenzen, Presets (App-seitig,
/// <see cref="EqPresetStore"/>) und Bass Boost. Die EQ-Kurve wirkt nur im Klangmodus Equalizer (protocol.md §7.2).
/// </summary>
public sealed partial class EqualizerViewModel : ObservableObject
{
    /// <summary>Reihenfolge der Auswahl; Sound Personalization ist nicht wählbar (Vorbedingungen ungeprüft).</summary>
    private static readonly SoundMode[] SoundModeOrder = [SoundMode.Equalizer, SoundMode.Podcast, SoundMode.Off];

    private readonly IActionRunner _run;
    private readonly EqPresetStore _presets;
    private readonly DispatcherQueueTimer _debounce;
    private EqualizerState? _last;
    private bool _applying;
    private bool _pending;

    public EqualizerViewModel(IActionRunner run, EqPresetStore presets, DispatcherQueue dispatcher)
    {
        _run = run;
        _presets = presets;
        _debounce = dispatcher.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(300);
        _debounce.IsRepeating = false;
        _debounce.Tick += async (_, _) => await SendCurveAsync();
        ReloadPresetNames();
    }

    public ObservableCollection<EqBandViewModel> Bands { get; } = [];

    public ObservableCollection<string> PresetNames { get; } = [];

    // List<T> statt Collection-Expression: WinRT/AOT kann nur echte Listen als ItemsSource übergeben.
    public List<string> SoundModes { get; } = ["Equalizer", "Podcast (Sprache)", "Neutral (aus)"];

    [ObservableProperty]
    public partial int SoundModeIndex { get; set; } = -1;

    [ObservableProperty]
    public partial bool IsAvailable { get; set; }

    [ObservableProperty]
    public partial int SelectedPresetIndex { get; set; } = -1;

    [ObservableProperty]
    public partial string PresetHint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasBassBoost { get; set; }

    [ObservableProperty]
    public partial bool BassBoost { get; set; }

    [ObservableProperty]
    public partial bool ShowSoundModeHint { get; set; }

    [ObservableProperty]
    public partial string SoundModeHint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewPresetName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PresetFileHint { get; set; } = string.Empty;

    public void Apply(EqualizerState? state, bool connected)
    {
        _last = state;
        _applying = true;
        try
        {
            IsAvailable = connected && state is not null;
            if (state is null)
            {
                return;
            }

            if (Bands.Count != state.Bands.Count)
            {
                Bands.Clear();
                foreach (var band in state.Bands)
                {
                    Bands.Add(new EqBandViewModel(band.Index, FormatFrequency(band.FrequencyHz), (double)state.MinGainDb, (double)state.MaxGainDb, OnBandChanged));
                }
            }

            if (!_pending)
            {
                foreach (var (vm, band) in Bands.Zip(state.Bands))
                {
                    vm.Applying = true;
                    vm.Gain = (double)band.GainDb;
                    vm.Applying = false;
                }
            }

            var gains = state.Bands.Select(b => b.GainDb).ToList();
            var active = _presets.FindMatching(gains);
            SelectedPresetIndex = active is null ? -1 : PresetNames.IndexOf(active.Name);
            PresetHint = active is null ? "Eigene Kurve (keinem Preset zugeordnet)" : string.Empty;

            HasBassBoost = state.BassBoost is not null;
            BassBoost = state.BassBoost == true;

            SoundModeIndex = state.SoundMode is { } mode ? Array.IndexOf(SoundModeOrder, mode) : -1;
            ShowSoundModeHint = state.SoundMode is not SoundMode.Equalizer;
            SoundModeHint = state.SoundMode switch
            {
                SoundMode.Off => "Klangmodus Neutral – die EQ-Kurve wirkt gerade nicht, bleibt aber gespeichert.",
                SoundMode.Podcast => "Klangmodus Podcast – die EQ-Kurve wirkt vermutlich nicht, bleibt aber gespeichert.",
                SoundMode.SoundPersonalization => "Klangmodus Sound Personalization (in Smart Control eingerichtet) – die EQ-Kurve wirkt vermutlich nicht.",
                _ => string.Empty,
            };
        }
        finally
        {
            _applying = false;
        }
    }

    partial void OnSelectedPresetIndexChanged(int value)
    {
        if (_applying || value < 0 || value >= PresetNames.Count || _presets.Find(PresetNames[value]) is not { } preset
            || _last is { } last && preset.Matches(last.Bands.Select(b => b.GainDb).ToList()))
        {
            return;
        }

        _ = _run.RunAsync($"Preset „{preset.Name}“", (service, ct) => service.SetEqGainsAsync(preset.GainsDb, ct));
    }

    partial void OnBassBoostChanged(bool value)
    {
        if (_applying || _last?.BassBoost == value)
        {
            return;
        }

        _ = _run.RunAsync($"Bass Boost {(value ? "an" : "aus")}", (service, ct) => service.SetBassBoostAsync(value, ct));
    }

    partial void OnSoundModeIndexChanged(int value)
    {
        if (_applying || value < 0 || value >= SoundModeOrder.Length || _last?.SoundMode == SoundModeOrder[value])
        {
            return;
        }

        var mode = SoundModeOrder[value];
        _ = _run.RunAsync($"Klangmodus {SoundModes[value]}", (service, ct) => service.SetSoundModeAsync(mode, ct));
    }

    [RelayCommand]
    private void SavePreset()
    {
        if (_last is not { } state)
        {
            return;
        }

        try
        {
            var saved = _presets.Save(NewPresetName, [.. state.Bands.Select(b => b.GainDb)]);
            ReloadPresetNames();
            NewPresetName = string.Empty;
            PresetFileHint = $"„{saved.Name}“ gespeichert.";
            Apply(state, IsAvailable);
        }
        catch (ArgumentException ex)
        {
            PresetFileHint = ex.Message;
        }
    }

    private void OnBandChanged(EqBandViewModel band)
    {
        if (_applying)
        {
            return;
        }

        _pending = true;
        _debounce.Stop();
        _debounce.Start();
    }

    private async Task SendCurveAsync()
    {
        var gains = Bands.Select(b => Math.Round((decimal)b.Gain, 1)).ToList();
        await _run.RunAsync("EQ", async (service, ct) =>
        {
            try
            {
                await service.SetEqGainsAsync(gains, ct);
            }
            finally
            {
                if (Bands.Select(b => Math.Round((decimal)b.Gain, 1)).SequenceEqual(gains))
                {
                    _pending = false;
                }
            }
        });
    }

    private void ReloadPresetNames()
    {
        PresetNames.Clear();
        foreach (var preset in _presets.All)
        {
            PresetNames.Add(preset.Name);
        }
    }

    private static string FormatFrequency(int? hz) => hz switch
    {
        null or 0 => "?",
        >= 1000 => (hz.Value / 1000.0).ToString("0.#", CultureInfo.CurrentCulture) + " kHz",
        _ => hz.Value.ToString(CultureInfo.CurrentCulture) + " Hz",
    };
}
