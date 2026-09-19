// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

namespace Momentum4.Core.State;

public sealed class StateChangedEventArgs(Momentum4State previous, Momentum4State current, DeviceEvent cause) : EventArgs
{
    public Momentum4State Previous { get; } = previous;

    public Momentum4State Current { get; } = current;

    public DeviceEvent Cause { get; } = cause;
}

/// <summary>
/// Hält den aktuellen, unveränderlichen Zustand. <see cref="Apply"/> wendet ein Event über den Reducer an und meldet
/// nur echte Änderungen. Threadsicher; <see cref="StateChanged"/> kommt aus dem Thread des Aufrufers (die UI wechselt
/// selbst in ihren Thread).
/// </summary>
public sealed class StateStore
{
    private readonly Lock _gate = new();
    private Momentum4State _state = Momentum4State.Initial;

    public event EventHandler<StateChangedEventArgs>? StateChanged;

    public Momentum4State Current
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Wendet das Event an; liefert <c>true</c>, wenn sich der Zustand dadurch geändert hat.</summary>
    public bool Apply(DeviceEvent deviceEvent)
    {
        Momentum4State previous;
        Momentum4State next;
        lock (_gate)
        {
            previous = _state;
            next = StateReducer.Apply(previous, deviceEvent);
            if (next == previous)
            {
                return false;
            }

            _state = next;
        }

        StateChanged?.Invoke(this, new StateChangedEventArgs(previous, next, deviceEvent));
        return true;
    }
}

/// <summary>Reine Funktion: alter Zustand + Event → neuer Zustand.</summary>
public static class StateReducer
{
    public static Momentum4State Apply(Momentum4State state, DeviceEvent deviceEvent) => deviceEvent switch
    {
        ConnectionChanged e => state with
        {
            Connection = e.State,
            LastError = e.State == ConnectionState.Connected ? null : e.Error ?? state.LastError,
        },
        DeviceIdentified e => state with { Device = e.Device },
        BatteryChanged e => state with { BatteryPercent = e.Percent },
        ChargingChanged e => state with { Charging = e.Charging },
        NoiseControlChanged e => state with { NoiseControl = e.State },

        // Teil-Updates wirken nur auf einen bereits vollständig gelesenen Zustand; sonst bleibt „unbekannt“,
        // bis der nächste Refresh alles liest.
        AncEnabledChanged e when state.NoiseControl is { } nc => state with { NoiseControl = nc with { AncEnabled = e.Enabled } },
        AncLevelChanged e when state.NoiseControl is { } nc => state with { NoiseControl = nc with { Level = e.Level } },
        AncModesChanged e when state.NoiseControl is { } nc => state with
        {
            NoiseControl = nc with
            {
                AntiWind = e.Table.AntiWind ?? nc.AntiWind,
                Adaptive = e.Table.Adaptive ?? nc.Adaptive,
            },
        },
        TransparentHearingChanged e when state.NoiseControl is { } nc => state with { NoiseControl = nc with { TransparentHearing = e.On } },

        EqualizerChanged e => state with { Equalizer = e.State },
        EqGainsChanged e when state.Equalizer is { } eq && eq.Bands.Count == e.GainsDb.Count => state with
        {
            Equalizer = eq with { Bands = eq.Bands.Select((b, i) => b with { GainDb = e.GainsDb[i] }).ToList() },
        },
        BassBoostChanged e when state.Equalizer is { } eq => state with { Equalizer = eq with { BassBoost = e.Enabled } },
        SoundModeChanged e when state.Equalizer is { } eq => state with { Equalizer = eq with { SoundMode = e.Mode } },
        MultipointChanged e => state with { Multipoint = e.State },
        PeerConnectionChanged e when state.Multipoint is { } mp => state with
        {
            Multipoint = mp with
            {
                Devices = mp.Devices.Select(d => d.Index == e.Index ? d with { Connected = e.Connected } : d).ToList(),
            },
        },
        BehaviorChanged e => state with { Behavior = e.State },
        BehaviorSettingChanged e when state.Behavior is { } behavior => state with { Behavior = behavior.With(e.Setting, e.On) },
        WearStateChanged e => state with { Wear = e.State },
        WindowsBatteryChanged e => state with { WindowsBatteryPercent = e.Percent },
        AutoPowerOffChanged e => state with { AutoPowerOffSeconds = e.Seconds },
        PromptModeChanged e => state with { PromptMode = e.Mode },
        DeviceNameChanged e => state with { DeviceName = e.Name },
        CodecChanged e => state with { Codec = e.Codec },
        SerialNumberRead e => state with { SerialNumber = e.Serial },
        PromptLanguageChanged e => state with { PromptLanguage = e.Language },
        AutoPauseChanged e => state with { AutoPauseOnTransparency = e.On },
        FullRefreshCompleted e => state with { LastFullRefresh = e.At },
        _ => state,
    };
}
