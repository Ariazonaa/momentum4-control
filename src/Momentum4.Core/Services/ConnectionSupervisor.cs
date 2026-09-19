// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Momentum4.Core.State;
using Momentum4.Core.Transport;

namespace Momentum4.Core.Services;

/// <summary>
/// Hält die Verbindung am Leben (docs/architecture.md §8.1): verbinden → Sitzung ausführen → bei Verlust oder
/// Fehler trennen → Backoff (1/2/5/10/30 s, danach 60 s) → erneut. <see cref="Poke"/> beendet eine Wartezeit
/// sofort (Headset meldet sich, Bluetooth wieder an, Aufwachen).
/// </summary>
public sealed class ConnectionSupervisor : IAsyncDisposable
{
    public static readonly IReadOnlyList<TimeSpan> DefaultBackoff =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60),
    ];

    private readonly ITransport _transport;
    private readonly Func<CancellationToken, Task> _runSession;
    private readonly StateStore _store;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly IReadOnlyList<TimeSpan> _backoff;
    private CancellationTokenSource? _stop;
    private CancellationTokenSource? _session;
    private Task? _loop;
    private TaskCompletionSource _wake = NewSignal();

    /// <param name="runSession">
    /// Läuft, solange die Verbindung steht (Initialisierung, danach Polling). Das Token wird bei Verbindungsverlust
    /// oder Stopp abgebrochen; eine Ausnahme beendet die Sitzung und führt zum Reconnect.
    /// </param>
    public ConnectionSupervisor(ITransport transport, Func<CancellationToken, Task> runSession, StateStore store, ILogger logger, TimeProvider? timeProvider = null, IReadOnlyList<TimeSpan>? backoff = null)
    {
        _transport = transport;
        _runSession = runSession;
        _store = store;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _backoff = backoff is { Count: > 0 } ? backoff : DefaultBackoff;
    }

    public void Start()
    {
        if (_loop is not null)
        {
            throw new InvalidOperationException("Bereits gestartet.");
        }

        _stop = new CancellationTokenSource();
        _transport.StateChanged += OnTransportStateChanged;
        var stop = _stop.Token;
        _loop = Task.Run(() => RunAsync(stop), CancellationToken.None);
    }

    /// <summary>Beendet eine laufende Wartezeit vor dem nächsten Verbindungsversuch.</summary>
    public void Poke(string reason)
    {
        _logger.LogInformation("Sofortiger Verbindungsversuch angefordert: {Reason}.", reason);
        Interlocked.Exchange(ref _wake, NewSignal()).TrySetResult();
    }

    public async Task StopAsync()
    {
        if (_stop is null || _loop is null)
        {
            return;
        }

        await _stop.CancelAsync();
        try
        {
            await _loop;
        }
        finally
        {
            _transport.StateChanged -= OnTransportStateChanged;
            _stop.Dispose();
            _stop = null;
            _loop = null;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task RunAsync(CancellationToken stop)
    {
        var attempt = 0;
        while (!stop.IsCancellationRequested)
        {
            string error;
            _store.Apply(new ConnectionChanged(ConnectionState.Connecting));
            try
            {
                await _transport.ConnectAsync(stop);
                using var session = CancellationTokenSource.CreateLinkedTokenSource(stop);
                Volatile.Write(ref _session, session);
                if (_transport.State != TransportState.Connected)
                {
                    await session.CancelAsync();
                }

                _store.Apply(new ConnectionChanged(ConnectionState.Initializing));
                await _runSession(session.Token);
                error = "Sitzung beendet";
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                error = "Verbindung verloren";
            }
            catch (Exception ex)
            {
                error = ex.Message;
                if (ex is HeadsetUnavailableException or TimeoutException)
                {
                    // Erwartet (Headset aus, Bluetooth aus …): eine Zeile genügt, der Stacktrace sagt hier nichts.
                    _logger.LogWarning("Verbindung/Sitzung fehlgeschlagen: {Message}", ex.Message);
                }
                else
                {
                    _logger.LogWarning(ex, "Verbindung/Sitzung fehlgeschlagen: {Message}", ex.Message);
                }
            }
            finally
            {
                Volatile.Write(ref _session, null);
            }

            await DisconnectQuietlyAsync();
            if (stop.IsCancellationRequested)
            {
                break;
            }

            // Nach einer erfolgreichen Sitzung beginnt der Backoff wieder von vorn.
            if (_store.Current.Connection == ConnectionState.Connected)
            {
                attempt = 0;
            }

            var delay = _backoff[Math.Min(attempt, _backoff.Count - 1)];
            attempt++;
            _logger.LogInformation("Nächster Verbindungsversuch in {Seconds:0} s ({Reason}).", delay.TotalSeconds, error);
            await WaitAsync(delay, error, stop);
        }

        await DisconnectQuietlyAsync();
        _store.Apply(new ConnectionChanged(ConnectionState.Disconnected));
    }

    private async Task WaitAsync(TimeSpan delay, string reason, CancellationToken stop)
    {
        // Erst den Timer anlegen, dann den Zustand melden: Wer „WaitingRetry“ sieht, kann sich darauf verlassen,
        // dass die Wartezeit bereits läuft (wichtig für Tests mit einer künstlichen Uhr).
        var wake = Volatile.Read(ref _wake).Task;
        var timer = Task.Delay(delay, _time, stop);
        _store.Apply(new ConnectionChanged(ConnectionState.WaitingRetry, reason));
        try
        {
            await Task.WhenAny(timer, wake);
        }
        catch (OperationCanceledException)
        {
            // Stopp – die Schleife prüft das Token.
        }
    }

    private void OnTransportStateChanged(object? sender, TransportState state)
    {
        if (state is TransportState.Lost or TransportState.Disconnected)
        {
            try
            {
                Volatile.Read(ref _session)?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Sitzung ist bereits vorbei.
            }
        }
    }

    private async Task DisconnectQuietlyAsync()
    {
        try
        {
            await _transport.DisconnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Fehler beim Trennen (ignoriert).");
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
