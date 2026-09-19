// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Momentum4.Core.State;
using Momentum4.Protocol.Features;

namespace Momentum4.Core.Tests;

public sealed class StateStoreTests
{
    private static readonly NoiseControlState Custom100 = new(AncEnabled: true, Adaptive: false, Level: 100, AntiWind: AntiWindMode.Maximum, TransparentHearing: true);

    [Fact]
    public void Raises_event_only_on_real_change()
    {
        var store = new StateStore();
        var events = new List<StateChangedEventArgs>();
        store.StateChanged += (_, e) => events.Add(e);

        Assert.True(store.Apply(new BatteryChanged(80)));
        Assert.False(store.Apply(new BatteryChanged(80)));

        var e = Assert.Single(events);
        Assert.Null(e.Previous.BatteryPercent);
        Assert.Equal(80, e.Current.BatteryPercent);
        Assert.IsType<BatteryChanged>(e.Cause);
    }

    [Fact]
    public void Equal_lists_do_not_count_as_change()
    {
        var store = new StateStore();
        store.Apply(new MultipointChanged(new MultipointState([new PeerDevice(0, "PC-A", true, true)], 2, 0)));

        Assert.False(store.Apply(new MultipointChanged(new MultipointState([new PeerDevice(0, "PC-A", true, true)], 2, 0))));
        Assert.False(store.Apply(new EqualizerChanged(Eq(6.0m))) && store.Apply(new EqualizerChanged(Eq(6.0m))));
    }

    [Fact]
    public void Partial_noise_control_updates_need_a_full_state_first()
    {
        var store = new StateStore();

        Assert.False(store.Apply(new AncLevelChanged(50)));
        Assert.Null(store.Current.NoiseControl);

        store.Apply(new NoiseControlChanged(Custom100));
        store.Apply(new AncLevelChanged(50));
        store.Apply(new AncModesChanged(new AncModeTable(AntiWindMode.Automatic, 0, true, [])));
        store.Apply(new AncEnabledChanged(false));

        var nc = store.Current.NoiseControl!;
        Assert.Equal(50, nc.Level);
        Assert.Equal(AntiWindMode.Automatic, nc.AntiWind);
        Assert.False(nc.AncEnabled);
    }

    [Theory]
    [InlineData(false, false, false, NoiseControlMode.Off)]
    [InlineData(true, true, false, NoiseControlMode.Adaptive)]
    [InlineData(true, false, false, NoiseControlMode.Custom)]
    [InlineData(true, false, true, NoiseControlMode.Transparency)]
    [InlineData(false, false, true, NoiseControlMode.Off)] // Transparent Hearing wirkt ohne ANC nicht (Phase E)
    public void Derives_noise_control_mode(bool ancEnabled, bool adaptive, bool transparentHearing, NoiseControlMode expected)
    {
        Assert.Equal(expected, (Custom100 with { AncEnabled = ancEnabled, Adaptive = adaptive, TransparentHearing = transparentHearing }).Mode);
    }

    [Fact]
    public void Peer_connection_change_updates_matching_device()
    {
        var store = new StateStore();
        store.Apply(new MultipointChanged(new MultipointState([new PeerDevice(0, "PC-A", true, true), new PeerDevice(2, "Phone-C", false, false)], 2, 0)));

        store.Apply(new PeerConnectionChanged(2, true));

        Assert.True(store.Current.Multipoint!.Devices.Single(d => d.Index == 2).Connected);
    }

    [Fact]
    public void Keeps_last_values_but_clears_error_when_connected_again()
    {
        var store = new StateStore();
        store.Apply(new BatteryChanged(70));
        store.Apply(new ConnectionChanged(ConnectionState.WaitingRetry, "Verbindung verloren"));

        Assert.Equal(70, store.Current.BatteryPercent);
        Assert.Equal("Verbindung verloren", store.Current.LastError);

        store.Apply(new ConnectionChanged(ConnectionState.Connected));
        Assert.Null(store.Current.LastError);
    }

    [Fact]
    public void Display_battery_prefers_gaia_and_falls_back_to_windows_only_while_disconnected()
    {
        var store = new StateStore();
        store.Apply(new BatteryChanged(70));
        store.Apply(new WindowsBatteryChanged(80));
        Assert.Equal((80, true), store.Current.DisplayBattery); // noch nicht verbunden: der alte GAIA-Wert zählt nicht

        store.Apply(new ConnectionChanged(ConnectionState.Connected));
        Assert.Equal((70, false), store.Current.DisplayBattery);

        store.Apply(new ConnectionChanged(ConnectionState.WaitingRetry));
        store.Apply(new WindowsBatteryChanged(null));
        Assert.Null(store.Current.DisplayBattery);
    }

    private static EqualizerState Eq(decimal firstGain) =>
        new([new EqBand(0, firstGain, 90), new EqBand(1, 0, 325)], -6, 6, true, SoundMode.Equalizer);
}
