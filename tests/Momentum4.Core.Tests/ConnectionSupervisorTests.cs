// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Momentum4.Core.Services;
using Momentum4.Core.State;
using Momentum4.Core.Transport;

namespace Momentum4.Core.Tests;

public sealed class ConnectionSupervisorTests
{
    private const int TestTimeoutMs = 10_000;

    private readonly FakeTransport _transport = new() { };
    private readonly FakeTimeProvider _time = new();
    private readonly StateStore _store = new();
    private int _sessions;
    private int _waits;

    public ConnectionSupervisorTests()
    {
        _transport.SetState(TransportState.Disconnected);
        _store.StateChanged += (_, e) =>
        {
            if (e.Current.Connection == ConnectionState.WaitingRetry && e.Previous.Connection != ConnectionState.WaitingRetry)
            {
                Interlocked.Increment(ref _waits);
            }
        };
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Retries_with_backoff_until_connected()
    {
        _transport.ConnectFailures = 3;
        await using var supervisor = Create();

        supervisor.Start();
        await Until(() => _waits == 1, TestContext.Current.CancellationToken);
        Assert.Equal(1, _transport.ConnectCount);

        await AdvanceUntil(TimeSpan.FromSeconds(1), () => _waits == 2, TestContext.Current.CancellationToken); // 1 s
        Assert.Equal(2, _transport.ConnectCount);
        await AdvanceUntil(TimeSpan.FromSeconds(2), () => _waits == 3, TestContext.Current.CancellationToken); // 2 s
        Assert.Equal(3, _transport.ConnectCount);
        _time.Advance(TimeSpan.FromSeconds(4));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(3, _transport.ConnectCount); // 5 s noch nicht um
        await AdvanceUntil(TimeSpan.FromSeconds(1), () => _sessions == 1, TestContext.Current.CancellationToken);

        await Until(() => _store.Current.Connection == ConnectionState.Initializing, TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Poke_ends_waiting_immediately()
    {
        _transport.ConnectFailures = 5;
        await using var supervisor = Create();
        supervisor.Start();
        await Until(() => _waits == 1, TestContext.Current.CancellationToken);
        await AdvanceUntil(TimeSpan.FromSeconds(1), () => _waits == 2, TestContext.Current.CancellationToken);

        supervisor.Poke("Test");

        await Until(() => _transport.ConnectCount == 3, TestContext.Current.CancellationToken); // ohne die 2 s abzuwarten
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Reconnects_after_connection_loss_starting_backoff_again()
    {
        await using var supervisor = Create(markConnected: true);
        supervisor.Start();
        await Until(() => _sessions == 1 && _store.Current.Connection == ConnectionState.Connected, TestContext.Current.CancellationToken);

        _transport.SetState(TransportState.Lost);

        await Until(() => _waits == 1, TestContext.Current.CancellationToken);
        Assert.Equal("Verbindung verloren", _store.Current.LastError);
        await AdvanceUntil(TimeSpan.FromSeconds(1), () => _sessions == 2, TestContext.Current.CancellationToken); // Backoff beginnt wieder bei 1 s
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Failing_session_leads_to_disconnect_and_retry()
    {
        await using var supervisor = Create(sessionThrows: true);
        supervisor.Start();

        await Until(() => _store.Current.Connection == ConnectionState.WaitingRetry, TestContext.Current.CancellationToken);
        Assert.Equal("Sitzung kaputt", _store.Current.LastError);
        Assert.Equal(TransportState.Disconnected, _transport.State);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Stop_ends_in_disconnected_state()
    {
        var supervisor = Create(markConnected: true);
        supervisor.Start();
        await Until(() => _store.Current.Connection == ConnectionState.Connected, TestContext.Current.CancellationToken);

        await supervisor.StopAsync();

        Assert.Equal(ConnectionState.Disconnected, _store.Current.Connection);
        Assert.Equal(TransportState.Disconnected, _transport.State);
    }

    private ConnectionSupervisor Create(bool markConnected = false, bool sessionThrows = false) =>
        new(_transport, async token =>
        {
            Interlocked.Increment(ref _sessions);
            if (sessionThrows)
            {
                throw new InvalidOperationException("Sitzung kaputt");
            }

            if (markConnected)
            {
                _store.Apply(new ConnectionChanged(ConnectionState.Connected));
            }

            await Task.Delay(Timeout.Infinite, token);
        }, _store, NullLogger.Instance, _time);

    private async Task AdvanceUntil(TimeSpan step, Func<bool> condition, CancellationToken cancellationToken)
    {
        _time.Advance(step);
        await Until(condition, cancellationToken);
    }

    private static async Task Until(Func<bool> condition, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, cancellationToken);
        }

        Assert.True(condition(), "Bedingung nicht erreicht.");
    }
}
