using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Momentum4.Core.Queue;
using Momentum4.Core.Services;
using Momentum4.Core.State;
using Momentum4.Core.Transport;
using Momentum4.Protocol.Catalog;
using Momentum4.Protocol.Features;

namespace Momentum4.Core.Tests;

public sealed class SharedHeadsetFailureBoundaryTests
{
    [Theory(Timeout = 15000)]
    [InlineData(false, "io", false)]
    [InlineData(true, "io", false)]
    [InlineData(false, "native", false)]
    [InlineData(true, "native", false)]
    [InlineData(false, "timeout", false)]
    [InlineData(true, "timeout", false)]
    [InlineData(false, "io", true)]
    [InlineData(true, "io", true)]
    [InlineData(false, "native", true)]
    [InlineData(true, "native", true)]
    [InlineData(false, "timeout", true)]
    [InlineData(true, "timeout", true)]
    public async Task Transport_failure_during_getter_or_applied_setter_revokes_session_without_retry(bool windows, string failure, bool beforeMutation)
    {
        var fixture = new SharedHeadsetFixture();
        await using var service = await fixture.StartAsync();
        await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        fixture.Requests.Clear();
        var failingCommand = beforeMutation ? (ushort)0x080d : (ushort)0x080c;
        var transportFailure = failure switch
        {
            "io" => (Exception)new IOException("Simulated output stream failure"),
            "native" => new COMException("Simulated native output failure"),
            _ => new TimeoutException("Simulated transport output timeout"),
        };
        fixture.OnRequest = frame =>
        {
            if (frame.Command.Value != failingCommand) return;
            if (!beforeMutation) fixture.Headset.SmartPause = true;
            throw transportFailure;
        };

        var exception = await Record.ExceptionAsync(() => SetSmartPauseAsync(service, windows));
        Assert.NotNull(exception);
        Assert.Equal(!beforeMutation, fixture.Headset.SmartPause);
        Assert.Equal(ConnectionState.Disconnected, service.Store.Current.Connection);
        Assert.False(service.Features.Writable);
        Assert.Same(transportFailure, Assert.IsType<HeadsetUnavailableException>(exception).InnerException);
        Assert.Single(fixture.Requests, request => request.Command.Value == failingCommand);
        var requestCount = fixture.Requests.Count;
        await Assert.ThrowsAsync<HeadsetUnavailableException>(() => SetSmartPauseAsync(service, windows));
        Assert.Equal(requestCount, fixture.Requests.Count);
        Assert.Equal(beforeMutation ? 0 : 1, fixture.Requests.Count(request => request.Command.Value == 0x080c));
    }

    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transport_read_loss_before_mutation_revokes_session_and_denies_followup_write(bool windows)
    {
        var fixture = new SharedHeadsetFixture();
        await using var service = await fixture.StartAsync();
        await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        fixture.Requests.Clear();
        fixture.OnRequest = frame =>
        {
            if (frame.Command.Value == 0x080d) fixture.Transport.SetState(TransportState.Lost);
        };
        await Assert.ThrowsAnyAsync<Exception>(() => SetSmartPauseAsync(service, windows));
        Assert.Single(fixture.Requests, request => request.Command.Value == 0x080d);
        Assert.Equal(ConnectionState.Disconnected, service.Store.Current.Connection);
        Assert.False(service.Features.Writable);
        var requestCount = fixture.Requests.Count;
        await Assert.ThrowsAsync<HeadsetUnavailableException>(() => SetSmartPauseAsync(service, windows));
        Assert.Equal(requestCount, fixture.Requests.Count);
        Assert.Empty(fixture.Writes);
    }

    [Fact(Timeout = 15000)]
    public async Task Caller_validation_without_io_preserves_verified_session()
    {
        var fixture = new SharedHeadsetFixture();
        await using var service = await fixture.StartAsync();
        await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        var requestCount = fixture.Requests.Count;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SetBooleanControlAsync((HeadsetBooleanControl)999, true, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SetControlNoiseModeAsync((NoiseControlMode)999, TestContext.Current.CancellationToken));
        Assert.Equal(requestCount, fixture.Requests.Count);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SetIntegerControlAsync(HeadsetIntegerControl.Sidetone, -1, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Writes);
        Assert.Equal(ConnectionState.Connected, service.Store.Current.Connection);
        Assert.True(service.Features.Writable);
        Assert.True((await service.SetBooleanControlAsync(HeadsetBooleanControl.SmartPause, true, TestContext.Current.CancellationToken)).Settings.SmartPause);
    }

    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sound_mode_accepts_delayed_application_with_one_setter(bool windows)
    {
        var fixture = M4();
        await using var service = await fixture.StartAsync();
        var afterWriteReads = 0;
        fixture.OnRequest = frame =>
        {
            if (frame.Command.Value != 0x0804 || !fixture.Writes.Any(write => write.Command.Value == 0x0803)) return;
            afterWriteReads++;
            if (afterWriteReads <= 2) fixture.Overrides[0x0804] = [0, 1];
            else fixture.Overrides.Remove(0x0804);
        };
        var mode = await SetSoundModeAsync(service, windows, TestContext.Current.CancellationToken);
        Assert.Equal(2, mode);
        Assert.True(afterWriteReads >= 3);
        Assert.Single(fixture.Requests, request => request.Command.Value == 0x0803);
        Assert.Equal(ConnectionState.Connected, service.Store.Current.Connection);
    }

    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sound_mode_nonconvergence_is_bounded_and_never_resends(bool windows)
    {
        var fixture = M4();
        var time = new FakeTimeProvider();
        fixture.Time = time;
        fixture.Headset.IgnoreWrites = true;
        await using var service = await fixture.StartAsync();
        var start = time.GetTimestamp();
        var operation = SetSoundModeAsync(service, windows, TestContext.Current.CancellationToken);
        await AdvanceUntilCompleteAsync(operation, time, TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<SettingNotAppliedException>(() => operation);
        Assert.Single(fixture.Requests, request => request.Command.Value == 0x0803);
        Assert.Equal(18, fixture.Requests.SkipWhile(request => request.Command.Value != 0x0803).Count(request => request.Command.Value == 0x0804));
        Assert.InRange(time.GetElapsedTime(start), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
        Assert.Equal(ConnectionState.Disconnected, service.Store.Current.Connection);
        Assert.False(service.Features.Writable);
    }

    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sound_mode_cancellation_stops_convergence_without_resending(bool windows)
    {
        var fixture = M4();
        fixture.Time = new FakeTimeProvider();
        fixture.Headset.IgnoreWrites = true;
        await using var service = await fixture.StartAsync();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var operation = SetSoundModeAsync(service, windows, cancellation.Token);
        Assert.False(operation.IsCompleted);
        Assert.Single(fixture.Requests, request => request.Command.Value == 0x0803);
        Assert.Equal(2, fixture.Requests.SkipWhile(request => request.Command.Value != 0x0803).Count(request => request.Command.Value == 0x0804));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Single(fixture.Requests, request => request.Command.Value == 0x0803);
        Assert.Equal(ConnectionState.Disconnected, service.Store.Current.Connection);
        Assert.False(service.Features.Writable);
    }

    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sound_mode_convergence_uses_remaining_total_budget(bool windows)
    {
        var fixture = M4();
        var time = new FakeTimeProvider();
        fixture.Time = time;
        await using var service = await fixture.StartAsync();
        await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        var operationTimeout = TimeSpan.FromSeconds(30);
        Assert.Equal(operationTimeout, Momentum4Service.ControlOperationTimeout);
        var start = time.GetTimestamp();
        fixture.OnRequest = frame =>
        {
            if (frame.Command.Value == 0x0803) time.Advance(operationTimeout - TimeSpan.FromMilliseconds(500));
            if (frame.Command.Value == 0x0804 && fixture.Writes.Any(write => write.Command.Value == 0x0803)) fixture.Overrides[0x0804] = [0, 1];
        };
        var operation = SetSoundModeAsync(service, windows, TestContext.Current.CancellationToken);
        await AdvanceUntilCompleteAsync(operation, time, TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Single(fixture.Requests, request => request.Command.Value == 0x0803);
        Assert.InRange(time.GetElapsedTime(start), operationTimeout, operationTimeout + TimeSpan.FromMilliseconds(250));
        Assert.Equal(ConnectionState.Disconnected, service.Store.Current.Connection);
        Assert.False(service.Features.Writable);
    }

    [Theory(Timeout = 15000)]
    [InlineData(false, "adaptive")]
    [InlineData(false, "enabled")]
    [InlineData(false, "strength")]
    [InlineData(false, "transparency")]
    [InlineData(false, "level")]
    [InlineData(true, "antiwind")]
    [InlineData(true, "enabled")]
    [InlineData(true, "strength")]
    [InlineData(true, "transparency")]
    [InlineData(true, "level")]
    public async Task Noise_mode_pair_write_detects_each_unrelated_sibling_change(bool adaptiveTarget, string sibling)
    {
        var fixture = new SharedHeadsetFixture();
        fixture.Headset.TransparentHearing = false;
        fixture.Headset.AncTable = [1, 1, 2, 0, 3, 1];
        await using var service = await fixture.StartAsync();
        fixture.OnRequest = frame =>
        {
            if (frame.Command.Value != 0x1a00) return;
            switch (sibling)
            {
                case "adaptive": fixture.Headset.AncTable = [1, 1, 2, 0, 3, 0]; break;
                case "antiwind": fixture.Headset.AncTable = [1, 0, 2, 0, 3, 1]; break;
                case "enabled": fixture.Overrides[0x1a05] = [0]; break;
                case "strength": fixture.Overrides[0x1a03] = [45]; break;
                case "transparency": fixture.Overrides[0x1805] = [1]; break;
                case "level": fixture.Overrides[0x1803] = [45]; break;
            }
        };
        await Assert.ThrowsAnyAsync<SettingNotAppliedException>(() => adaptiveTarget
            ? service.SetBooleanControlAsync(HeadsetBooleanControl.AdaptiveAnc, false, TestContext.Current.CancellationToken)
            : service.SetIntegerControlAsync(HeadsetIntegerControl.AntiWind, 0, TestContext.Current.CancellationToken));
        Assert.Single(fixture.Requests, request => request.Command.Value == 0x1a00);
        Assert.DoesNotContain(fixture.Requests, request => request.Command.Value == 0x1a00 && request.Payload[0] != (adaptiveTarget ? AncCodec.ModeAdaptive : AncCodec.ModeAntiWind));
        Assert.Equal(ConnectionState.Disconnected, service.Store.Current.Connection);
        Assert.False(service.Features.Writable);
    }

    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Noise_mode_pair_write_preserves_other_mode_pair(bool adaptiveTarget)
    {
        var fixture = new SharedHeadsetFixture();
        fixture.Headset.TransparentHearing = false;
        fixture.Headset.AncTable = [1, 1, 2, 0, 3, 1];
        await using var service = await fixture.StartAsync();
        var after = adaptiveTarget
            ? await service.SetBooleanControlAsync(HeadsetBooleanControl.AdaptiveAnc, false, TestContext.Current.CancellationToken)
            : await service.SetIntegerControlAsync(HeadsetIntegerControl.AntiWind, 0, TestContext.Current.CancellationToken);
        Assert.Equal(adaptiveTarget, after.Settings.AntiWind == 1);
        Assert.Equal(!adaptiveTarget, after.Settings.AdaptiveAnc);
        Assert.Single(fixture.Writes);
        Assert.Equal(ConnectionState.Connected, service.Store.Current.Connection);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Nonpositive_peer_timeout_is_rejected_before_transport_subscription_or_logger_creation(int milliseconds)
    {
        var transport = new ConstructionProbeTransport();
        var loggerFactory = new ConstructionProbeLoggerFactory();
        Assert.Throws<ArgumentOutOfRangeException>(() => new Momentum4Service(transport, CommandPolicy.Production,
            new Momentum4ServiceOptions { PeerConnectTimeout = TimeSpan.FromMilliseconds(milliseconds) }, loggerFactory));
        Assert.Equal(0, loggerFactory.CreatedLoggers);
        Assert.Equal(0, transport.Subscriptions);
        Assert.Equal(0, transport.Operations);
    }

    [Fact(Timeout = 15000)]
    public async Task Positive_short_peer_timeout_bounds_polling_and_default_remains_eight_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(8), new Momentum4ServiceOptions().PeerConnectTimeout);
        var time = new FakeTimeProvider();
        var fixture = M4();
        fixture.Time = time;
        fixture.PeerConnectTimeout = TimeSpan.FromMilliseconds(100);
        fixture.Headset.PeersNeverConnect = true;
        await using var service = await fixture.StartAsync();
        var before = await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        var peer = before.Peers[1];
        var started = time.GetTimestamp();
        var operation = service.ConnectControlPeerAsync(new HeadsetPeerSelection(peer.Index, peer.Name, peer.Priority, false), TestContext.Current.CancellationToken);
        Assert.False(operation.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<SettingNotAppliedException<string>>(() => operation);
        Assert.Equal(TimeSpan.FromMilliseconds(100), time.GetElapsedTime(started));
        Assert.Single(fixture.Requests, request => request.Command.Value == 0x1402);
    }

    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unexpected_failure_after_applied_mutation_revokes_session(bool windows)
    {
        var fixture = new SharedHeadsetFixture();
        await using var service = await fixture.StartAsync();
        await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        var unexpected = new ArgumentException("Simulated state consumer failure");
        service.Store.StateChanged += (_, change) =>
        {
            if (change.Cause is BehaviorChanged && change.Current.Behavior?.SmartPause == true) throw unexpected;
        };
        var exception = await Record.ExceptionAsync(() => SetSmartPauseAsync(service, windows));
        Assert.Same(unexpected, exception);
        Assert.True(fixture.Headset.SmartPause);
        Assert.Single(fixture.Writes);
        Assert.Equal(ConnectionState.Disconnected, service.Store.Current.Connection);
        Assert.False(service.Features.Writable);
        await Assert.ThrowsAsync<HeadsetUnavailableException>(() => SetSmartPauseAsync(service, windows));
        Assert.Single(fixture.Writes);
    }

    [Theory(Timeout = 15000)]
    [InlineData("antiwind", 0x1804)]
    [InlineData("adaptive", 0x1804)]
    [InlineData("enabled", 0x1804)]
    [InlineData("antiwind", 0x1a02)]
    [InlineData("adaptive", 0x1a02)]
    [InlineData("enabled", 0x1a02)]
    public async Task Noise_action_denies_forbidden_possible_correction_before_primary_write(string target, int forbidden)
    {
        var fixture = new SharedHeadsetFixture();
        fixture.Headset.AncTable = [1, 1, 2, 0, 3, 1];
        fixture.Headset.AncEnabled = target != "enabled";
        fixture.Headset.TransparentHearing = forbidden == 0x1804;
        fixture.Headset.AncLevel = forbidden == 0x1804 ? (byte)100 : (byte)30;
        await using var service = await StartWithPolicyAsync(fixture, PolicyWithout((ushort)forbidden));
        var primary = target == "enabled" ? (ushort)0x1a04 : (ushort)0x1a00;
        fixture.OnRequest = frame =>
        {
            if (frame.Command.Value == primary && forbidden == 0x1a02) fixture.Overrides[0x1a03] = [45];
        };
        var failure = await Assert.ThrowsAsync<CommandNotAllowedException>(() => target switch
        {
            "antiwind" => service.SetIntegerControlAsync(HeadsetIntegerControl.AntiWind, 0, TestContext.Current.CancellationToken),
            "adaptive" => service.SetBooleanControlAsync(HeadsetBooleanControl.AdaptiveAnc, false, TestContext.Current.CancellationToken),
            _ => service.SetBooleanControlAsync(HeadsetBooleanControl.AncEnabled, true, TestContext.Current.CancellationToken),
        });
        Assert.Equal((ushort)forbidden, failure.Command.Command.Value);
        Assert.Empty(fixture.Writes);
        Assert.DoesNotContain(fixture.Requests, request => request.Command.Value == primary);
        Assert.Equal(ConnectionState.Connected, service.Store.Current.Connection);
        Assert.True(service.Features.Writable);
    }

    [Theory(Timeout = 15000)]
    [InlineData(0x1804)]
    [InlineData(0x1a04)]
    [InlineData(0x1a00)]
    [InlineData(0x1a02)]
    public async Task Windows_custom_strength_checks_every_possible_setter_before_first_write(int forbidden)
    {
        var fixture = new SharedHeadsetFixture();
        fixture.Headset.AncTable = [1, 1, 2, 0, 3, 1];
        fixture.Headset.AncEnabled = forbidden != 0x1804;
        fixture.Headset.TransparentHearing = forbidden != 0x1804;
        await using var service = await StartWithPolicyAsync(fixture, PolicyWithout((ushort)forbidden));
        var failure = await Assert.ThrowsAsync<CommandNotAllowedException>(() => service.SetNoiseControlLevelAsync(20, TestContext.Current.CancellationToken));
        Assert.Equal((ushort)forbidden, failure.Command.Command.Value);
        Assert.Empty(fixture.Writes);
        Assert.Equal(ConnectionState.Connected, service.Store.Current.Connection);
        Assert.True(service.Features.Writable);
    }

    private static CommandPolicy PolicyWithout(ushort forbidden) => CommandPolicy.Only(CommandCatalog.All
        .Where(command => command.Status == ProtocolStatus.Verified && command.Safety is not (SafetyClass.Blocked or SafetyClass.Unknown)
            && command.Command.Value != forbidden).ToArray());

    private static async Task<Momentum4Service> StartWithPolicyAsync(SharedHeadsetFixture fixture, CommandPolicy policy)
    {
        var service = new Momentum4Service(fixture.Transport, policy,
            new Momentum4ServiceOptions { RegisterNotifications = false, ReadSerialNumber = false, EnablePolling = false, Reconnect = false }, NullLoggerFactory.Instance);
        service.Start();
        for (var attempt = 0; attempt < 1000 && service.Store.Current.Connection != ConnectionState.Connected; attempt++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        Assert.Equal(ConnectionState.Connected, service.Store.Current.Connection);
        return service;
    }

    private static async Task SetSmartPauseAsync(Momentum4Service service, bool windows)
    {
        if (windows) await service.SetBehaviorAsync(BehaviorSetting.SmartPause, true, TestContext.Current.CancellationToken);
        else await service.SetBooleanControlAsync(HeadsetBooleanControl.SmartPause, true, TestContext.Current.CancellationToken);
    }

    private static async Task<int?> SetSoundModeAsync(Momentum4Service service, bool windows, CancellationToken? cancellation = null)
    {
        var token = cancellation ?? TestContext.Current.CancellationToken;
        if (windows) return (int?)(await service.SetSoundModeAsync(SoundMode.Podcast, token)).SoundMode;
        return (await service.SetIntegerControlAsync(HeadsetIntegerControl.SoundMode, 2, token)).Settings.SoundMode;
    }

    private static async Task AdvanceUntilCompleteAsync(Task operation, FakeTimeProvider time, TimeSpan step)
    {
        for (var iteration = 0; iteration < 100 && !operation.IsCompleted; iteration++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
            if (!operation.IsCompleted) time.Advance(step);
        }
        Assert.True(operation.IsCompleted);
    }

    private static SharedHeadsetFixture M4() => new() { Model = "M4AEBT", Firmware = [0, 3, 0, 37, 0, 3] };

    private sealed class ConstructionProbeLoggerFactory : ILoggerFactory
    {
        public int CreatedLoggers { get; private set; }
        public void AddProvider(ILoggerProvider provider) => throw new InvalidOperationException();
        public ILogger CreateLogger(string categoryName)
        {
            CreatedLoggers++;
            return NullLogger.Instance;
        }
        public void Dispose() { }
    }

    private sealed class ConstructionProbeTransport : ITransport
    {
        public int Subscriptions { get; private set; }
        public int Operations { get; private set; }
        public TransportState State => TransportState.Disconnected;
        public event EventHandler<TransportState>? StateChanged { add => Subscriptions++; remove => Subscriptions--; }
        public event EventHandler<ReadOnlyMemory<byte>>? DataReceived { add => Subscriptions++; remove => Subscriptions--; }
        public Task ConnectAsync(CancellationToken cancellationToken) { Operations++; return Task.CompletedTask; }
        public Task DisconnectAsync() { Operations++; return Task.CompletedTask; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) { Operations++; return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() { Operations++; return ValueTask.CompletedTask; }
    }
}
