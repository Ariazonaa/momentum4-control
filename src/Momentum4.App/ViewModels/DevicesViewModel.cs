// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Momentum4.App.Localization;
using Momentum4.Core.State;

namespace Momentum4.App.ViewModels;

public sealed partial class PeerViewModel(PeerDevice device, bool canConnect, Func<PeerViewModel, Task> connect) : ObservableObject
{
    public PeerDevice Device { get; } = device;

    public string Name => Device.IsThisComputer ? L.Get("ThisPc", Device.Name) : Device.Name;

    public string StatusText => Device.Connected ? L.Get("Connected") : L.Get("NotConnected");

    public bool CanConnect { get; } = canConnect;

    [RelayCommand]
    private Task ConnectAsync() => connect(this);
}

/// <summary>
/// Gekoppelte Geräte (Multipoint, docs/architecture.md §8.5). Verbinden geht nur mit Namensprüfung; Trennen bietet
/// die App nicht an, weil <c>0x1403</c> gesperrt ist – das macht der User am jeweiligen Gerät.
/// </summary>
public sealed partial class DevicesViewModel(IActionRunner run) : ObservableObject
{
    private MultipointState? _last;
    private bool _lastConnected;

    public ObservableCollection<PeerViewModel> Peers { get; } = [];

    [ObservableProperty]
    public partial string Hint { get; set; } = string.Empty;

    public void Apply(MultipointState? state, bool connected)
    {
        if (Equals(state, _last) && connected == _lastConnected && Peers.Count > 0)
        {
            return;
        }

        _last = state;
        _lastConnected = connected;
        Peers.Clear();
        if (state is null)
        {
            Hint = string.Empty;
            return;
        }

        var connectedCount = state.Devices.Count(d => d.Connected);
        var full = state.MaxConnections is { } max && connectedCount >= max;
        foreach (var device in state.Devices)
        {
            Peers.Add(new PeerViewModel(device, connected && !device.Connected && !device.IsThisComputer && !full, ConnectAsync));
        }

        Hint = full
            ? L.Get("MultipointHint", connectedCount, state.MaxConnections)
            : string.Empty;
    }

    private Task ConnectAsync(PeerViewModel peer) =>
        run.RunAsync(L.Get("ActConnectPeer", peer.Device.Name), (service, ct) => service.ConnectPeerAsync(peer.Device.Index, peer.Device.Name, ct));
}
