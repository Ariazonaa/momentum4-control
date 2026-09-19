// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.Logging;
using Momentum4.Core.Transport;
using Momentum4.Protocol;
using Windows.Devices.Bluetooth;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace Momentum4.Bluetooth.Rfcomm;

/// <summary>
/// RFCOMM-Kanal zum GAIA-Dienst des Headsets über WinRT (<see cref="StreamSocket"/>).
/// Kennt kein GAIA: liefert Rohbytes nach oben und schreibt Rohbytes serialisiert.
/// </summary>
public sealed class RfcommTransport : ITransport
{
    private const uint ReadBufferSize = 1024;

    private readonly BluetoothAddress _address;
    private readonly ILogger<RfcommTransport> _logger;
    private readonly TimeSpan _connectTimeout;
    private readonly int _serviceIndex;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // Serialisiert Verbinden, Trennen und die Behandlung eines Verbindungsverlusts. Die Lese-Schleife wartet nie
    // auf dieses Lock (nur Wait(0)), damit Trennen – das auf die Lese-Schleife wartet – nicht blockieren kann.
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Lock _gate = new();

    private TransportState _state = TransportState.Disconnected;
    private BluetoothDevice? _device;
    private StreamSocket? _socket;
    private CancellationTokenSource? _readCancellation;
    private Task? _readLoop;

    public RfcommTransport(BluetoothAddress address, ILogger<RfcommTransport> logger, TimeSpan? connectTimeout = null, int serviceIndex = 0)
    {
        _address = address;
        _logger = logger;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(10);
        _serviceIndex = serviceIndex;
    }

    public event EventHandler<TransportState>? StateChanged;

    public event EventHandler<ReadOnlyMemory<byte>>? DataReceived;

    public TransportState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Informationen zum zuletzt verbundenen Dienst (Kanal, SDP), für Diagnose.</summary>
    public GaiaServiceInfo? ServiceInfo { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            await ConnectCoreAsync(cancellationToken);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_state is TransportState.Connecting or TransportState.Connected)
            {
                throw new InvalidOperationException($"Transport ist bereits im Zustand {_state}.");
            }
        }

        SetState(TransportState.Connecting);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_connectTimeout);
        var token = timeout.Token;

        try
        {
            _device = await BluetoothDevice.FromBluetoothAddressAsync(_address.Value).AsTask(token)
                ?? throw new HeadsetUnavailableException($"Kein gekoppeltes Bluetooth-Gerät mit der Adresse {_address}.");
            _device.ConnectionStatusChanged += OnConnectionStatusChanged;
            _logger.LogInformation("Gerät '{Name}' {Address}, Windows-Verbindungsstatus: {Status}.", _device.Name, _address, _device.ConnectionStatus);

            var resolved = await GaiaServiceResolver.ResolveAsync(_device, allowUncached: true, _logger, token, _serviceIndex)
                ?? throw new HeadsetUnavailableException("Der GAIA-Dienst des Headsets ist nicht erreichbar (aus, außer Reichweite oder anderweitig belegt?).");

            using (var service = resolved.Service)
            {
                var sdp = await GaiaServiceResolver.ReadSdpAsync(service, _logger, token);
                ServiceInfo = new GaiaServiceInfo(service.ConnectionHostName.RawName, service.ConnectionServiceName, resolved.Mode, sdp);
                _logger.LogInformation(
                    "GAIA-Dienst: Host {Host}, Dienst {Service}, RFCOMM-Kanal {Channel}, SDP-Name '{SdpName}' ({Mode}).",
                    ServiceInfo.HostName, ServiceInfo.ServiceName, sdp?.RfcommChannel?.ToString() ?? "unbekannt", sdp?.ServiceName ?? "?", resolved.Mode);

                var socket = new StreamSocket();
                _socket = socket;
                await socket.ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName, SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication)
                    .AsTask(token);
            }

            _readCancellation = new CancellationTokenSource();
            var readSocket = _socket;
            var readToken = _readCancellation.Token;
            _readLoop = Task.Run(() => ReadLoopAsync(readSocket, readToken), CancellationToken.None);

            SetState(TransportState.Connected);
            _logger.LogInformation("RFCOMM-Kanal geöffnet.");
        }
        catch (Exception ex)
        {
            await ReleaseAsync(awaitReadLoop: true);
            SetState(TransportState.Disconnected);

            if (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Verbindungsaufbau hat länger als {_connectTimeout.TotalSeconds:0} s gedauert.", ex);
            }

            _logger.LogWarning("Verbindungsaufbau fehlgeschlagen: {Reason}", BluetoothErrors.Describe(ex));
            _logger.LogDebug(ex, "Details zum Verbindungsaufbau");
            if (ex is HeadsetUnavailableException or OperationCanceledException)
            {
                throw;
            }

            // WinRT-Fehler haben oft keinen Text – nach oben (Supervisor, App) geht eine lesbare Beschreibung.
            throw new HeadsetUnavailableException(BluetoothErrors.Describe(ex), ex);
        }
    }

    public async Task DisconnectAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (State == TransportState.Disconnected)
            {
                return;
            }

            await ReleaseAsync(awaitReadLoop: true);
            SetState(TransportState.Disconnected);
            _logger.LogInformation("RFCOMM-Kanal geschlossen.");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var socket = _socket;
            if (State != TransportState.Connected || socket is null)
            {
                throw new InvalidOperationException("Transport ist nicht verbunden.");
            }

            _logger.LogDebug("TX {Count} Byte: {Hex}", data.Length, Hex.Format(data.Span));
            await socket.OutputStream.WriteAsync(data.ToArray().AsBuffer()).AsTask(cancellationToken);
            await socket.OutputStream.FlushAsync().AsTask(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _writeLock.Dispose();
        _lifecycle.Dispose();
    }

    private async Task ReadLoopAsync(StreamSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new Windows.Storage.Streams.Buffer(ReadBufferSize);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await socket.InputStream.ReadAsync(buffer, ReadBufferSize, InputStreamOptions.Partial).AsTask(cancellationToken);
                if (result.Length == 0)
                {
                    _logger.LogWarning("Gegenstelle hat den RFCOMM-Kanal geschlossen (EOF).");
                    await OnLostAsync();
                    return;
                }

                var data = result.ToArray();
                _logger.LogDebug("RX {Count} Byte: {Hex}", data.Length, Hex.Format(data));

                try
                {
                    DataReceived?.Invoke(this, data);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Fehler im DataReceived-Handler (Lese-Schleife läuft weiter).");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Regulär getrennt.
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Lesefehler auf dem RFCOMM-Kanal: {Reason}", BluetoothErrors.Describe(ex));
            await OnLostAsync();
        }
        catch (Exception)
        {
            // Beim Trennen bricht das Schließen des Sockets den laufenden Read ab – kein Fehler.
        }
    }

    private void OnConnectionStatusChanged(BluetoothDevice sender, object args)
    {
        _logger.LogInformation("Windows meldet Verbindungsstatus: {Status}.", sender.ConnectionStatus);
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected && State == TransportState.Connected)
        {
            _ = OnLostAsync();
        }
    }

    private async Task OnLostAsync()
    {
        // Läuft gerade Verbinden oder Trennen, erledigt das den Zustand selbst.
        if (!_lifecycle.Wait(0))
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                if (_state != TransportState.Connected)
                {
                    return;
                }
            }

            // Aus der Lese-Schleife heraus nicht auf sie selbst warten.
            await ReleaseAsync(awaitReadLoop: false);
            SetState(TransportState.Lost);
            _logger.LogWarning("RFCOMM-Verbindung verloren.");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task ReleaseAsync(bool awaitReadLoop)
    {
        StreamSocket? socket;
        BluetoothDevice? device;
        CancellationTokenSource? readCancellation;
        Task? readLoop;

        lock (_gate)
        {
            socket = _socket;
            device = _device;
            readCancellation = _readCancellation;
            readLoop = _readLoop;
            _socket = null;
            _device = null;
            _readCancellation = null;
            _readLoop = null;
        }

        readCancellation?.Cancel();
        socket?.Dispose();

        if (awaitReadLoop && readLoop is not null)
        {
            try
            {
                await readLoop;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Lese-Schleife beim Trennen beendet.");
            }
        }

        readCancellation?.Dispose();
        if (device is not null)
        {
            device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            device.Dispose();
        }
    }

    private void SetState(TransportState state)
    {
        lock (_gate)
        {
            if (_state == state)
            {
                return;
            }

            _state = state;
        }

        _logger.LogDebug("Transport-Zustand: {State}.", state);
        StateChanged?.Invoke(this, state);
    }
}
