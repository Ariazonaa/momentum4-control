// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Momentum4.Core.Transport;
using Windows.Devices.Bluetooth;
using Windows.Devices.Radios;

namespace Momentum4.Bluetooth.Discovery;

/// <summary>
/// Meldet, wenn sich das Headset bei Windows verbindet/trennt (<see cref="BluetoothDevice.ConnectionStatusChanged"/>)
/// und wenn das Bluetooth-Radio an- oder ausgeht. Daraus folgt ein sofortiger Verbindungsversuch statt des Backoffs.
/// </summary>
public sealed class BluetoothPresenceMonitor(ILogger<BluetoothPresenceMonitor> logger) : IHeadsetPresence
{
    private BluetoothDevice? _device;
    private Radio? _radio;

    public event EventHandler<PresenceChange>? Changed;

    public async Task StartAsync(BluetoothAddress address, CancellationToken cancellationToken)
    {
        _device = await BluetoothDevice.FromBluetoothAddressAsync(address.Value).AsTask(cancellationToken);
        if (_device is not null)
        {
            _device.ConnectionStatusChanged += OnConnectionStatusChanged;
            logger.LogDebug("Beobachte Verbindungsstatus von {Address} (jetzt: {Status}).", address, _device.ConnectionStatus);
        }
        else
        {
            logger.LogWarning("Headset {Address} nicht gefunden – nur das Bluetooth-Radio wird beobachtet.", address);
        }

        try
        {
            var radios = await Radio.GetRadiosAsync().AsTask(cancellationToken);
            _radio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (_radio is not null)
            {
                _radio.StateChanged += OnRadioStateChanged;
                logger.LogDebug("Beobachte Bluetooth-Radio (jetzt: {State}).", _radio.State);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Bluetooth-Radio kann nicht beobachtet werden: {Reason}", BluetoothErrors.Describe(ex));
        }
    }

    public void Dispose()
    {
        if (_device is not null)
        {
            _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            _device.Dispose();
            _device = null;
        }

        if (_radio is not null)
        {
            _radio.StateChanged -= OnRadioStateChanged;
            _radio = null;
        }
    }

    private void OnConnectionStatusChanged(BluetoothDevice sender, object args)
    {
        var connected = sender.ConnectionStatus == BluetoothConnectionStatus.Connected;
        logger.LogInformation("Windows meldet das Headset als {State}.", connected ? "verbunden" : "getrennt");
        Changed?.Invoke(this, new PresenceChange(connected, null));
    }

    private void OnRadioStateChanged(Radio sender, object args)
    {
        var on = sender.State == RadioState.On;
        logger.LogInformation("Bluetooth-Radio ist {State}.", on ? "an" : $"aus ({sender.State})");
        Changed?.Invoke(this, new PresenceChange(null, on));
    }
}
