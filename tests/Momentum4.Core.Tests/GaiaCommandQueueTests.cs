// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Momentum4.Core.Queue;
using Momentum4.Core.Transport;
using Momentum4.Protocol;
using Momentum4.Protocol.Catalog;
using Momentum4.Protocol.Gaia;

namespace Momentum4.Core.Tests;

public sealed class GaiaCommandQueueTests
{
    // Schutz gegen hängende Tests: die Queue wartet sonst auf eine Antwort, deren Timeout auf der
    // eingefrorenen FakeTimeProvider-Uhr nie abläuft.
    private const int TestTimeoutMs = 10_000;

    private const string BatteryRequest = "FF 03 00 00 04 95 06 03";
    private const string BatteryResponse70 = "FF 03 00 01 04 95 07 03 46";

    private static readonly CommandPolicy TestPolicy = CommandPolicy.ForHardwareTest(
        CommandCatalog.GetBatteryLevel,
        CommandCatalog.GetModelId,
        CommandCatalog.GetFirmwareVersion,
        CommandCatalog.SetAncEnabled,
        CommandCatalog.GetPairedDeviceInfo);

    private readonly FakeTransport _transport = new();
    private readonly FakeTimeProvider _time = new();

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Sends_encoded_request_and_returns_matching_response()
    {
        _transport.Responder = _ => [Hex.Parse(BatteryResponse70)];
        using var queue = CreateQueue();

        var response = await queue.SendAsync(CommandCatalog.GetBatteryLevel, TestContext.Current.CancellationToken);

        Assert.Equal(BatteryRequest, Hex.Format(Assert.Single(_transport.Writes)));
        Assert.Equal(new CommandWord(0x0703), response.Command);
        Assert.Equal(new byte[] { 0x46 }, response.Payload);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Reassembles_fragmented_response()
    {
        _transport.Responder = _ => [Hex.Parse("FF 03 00"), Hex.Parse("01 04 95 07"), Hex.Parse("03 46")];
        using var queue = CreateQueue();

        var response = await queue.SendAsync(CommandCatalog.GetBatteryLevel, TestContext.Current.CancellationToken);

        Assert.Equal(new byte[] { 0x46 }, response.Payload);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Error_frame_throws_with_reason()
    {
        _transport.Responder = _ => [Hex.Parse("FF 03 00 01 04 95 07 83 05")];
        using var queue = CreateQueue();

        var error = await Assert.ThrowsAsync<GaiaErrorException>(() => queue.SendAsync(CommandCatalog.GetBatteryLevel, TestContext.Current.CancellationToken));

        Assert.Equal((byte)0x05, error.Reason);
        Assert.Single(_transport.Writes); // Error wird nicht wiederholt
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Notification_during_request_is_forwarded_and_request_still_completes()
    {
        _transport.Responder = _ => [Hex.Parse("FF 03 00 01 04 95 1A 85 00"), Hex.Parse(BatteryResponse70)];
        using var queue = CreateQueue();
        var notifications = new List<GaiaFrame>();
        queue.NotificationReceived += (_, frame) => notifications.Add(frame);

        var response = await queue.SendAsync(CommandCatalog.GetBatteryLevel, TestContext.Current.CancellationToken);

        Assert.Equal(new CommandWord(0x0703), response.Command);
        Assert.Equal(new CommandWord(0x1A85), Assert.Single(notifications).Command);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Unrelated_response_does_not_complete_request()
    {
        // Antwort auf 0x1A05 (z. B. verspätet) vor der eigentlichen Antwort
        _transport.Responder = _ => [Hex.Parse("FF 03 00 01 04 95 1B 05 01"), Hex.Parse(BatteryResponse70)];
        using var queue = CreateQueue();

        var response = await queue.SendAsync(CommandCatalog.GetBatteryLevel, TestContext.Current.CancellationToken);

        Assert.Equal(new CommandWord(0x0703), response.Command);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Response_from_other_vendor_does_not_complete_request()
    {
        _transport.Responder = _ => [Hex.Parse("FF 03 00 01 00 1D 07 03 11"), Hex.Parse(BatteryResponse70)];
        using var queue = CreateQueue();

        var response = await queue.SendAsync(CommandCatalog.GetBatteryLevel, TestContext.Current.CancellationToken);

        Assert.Equal((ushort)0x0495, response.Vendor);
        Assert.Equal(new byte[] { 0x46 }, response.Payload);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Read_is_retried_once_after_timeout()
    {
        var calls = 0;
        _transport.Responder = _ => ++calls == 1 ? [] : [Hex.Parse("FF 03 00 02 04 95 13 06 4D 34")];
        using var queue = CreateQueue();

        var task = queue.SendAsync(CommandCatalog.GetModelId, TestContext.Current.CancellationToken);
        await AdvanceUntilCompletedAsync(task);

        var response = await task;
        Assert.Equal(2, _transport.WriteCount);
        Assert.Equal("4D 34", Hex.Format(response.Payload));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Read_gives_up_after_second_timeout()
    {
        using var queue = CreateQueue();

        var task = queue.SendAsync(CommandCatalog.GetModelId, TestContext.Current.CancellationToken);
        await AdvanceUntilCompletedAsync(task);

        await Assert.ThrowsAsync<TimeoutException>(() => task);
        Assert.Equal(2, _transport.WriteCount);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Setting_is_not_retried()
    {
        using var queue = CreateQueue();

        var task = queue.SendAsync(CommandCatalog.SetAncEnabled, new byte[] { 0x01 }, TestContext.Current.CancellationToken);
        await AdvanceUntilCompletedAsync(task);

        await Assert.ThrowsAsync<TimeoutException>(() => task);
        Assert.Equal(1, _transport.WriteCount);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Requests_are_sent_one_at_a_time()
    {
        using var queue = CreateQueue();
        var ct = TestContext.Current.CancellationToken;

        var first = queue.SendAsync(CommandCatalog.GetModelId, ct);
        var second = queue.SendAsync(CommandCatalog.GetFirmwareVersion, ct);

        await _transport.NextWriteAsync(ct);
        await Task.Delay(50, ct);
        Assert.Equal(1, _transport.WriteCount); // zweiter Request wartet

        _transport.Receive(Hex.Parse("FF 03 00 02 04 95 13 06 4D 34"));
        Assert.Equal("4D 34", Hex.Format((await first).Payload));

        var secondWrite = await _transport.NextWriteAsync(ct);
        Assert.Equal("FF 03 00 00 04 95 12 01", Hex.Format(secondWrite));
        _transport.Receive(Hex.Parse("FF 03 00 06 04 95 13 01 00 03 00 26 00 03"));
        Assert.Equal("00 03 00 26 00 03", Hex.Format((await second).Payload));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Connection_loss_fails_pending_request()
    {
        using var queue = CreateQueue();
        var ct = TestContext.Current.CancellationToken;

        var task = queue.SendAsync(CommandCatalog.GetBatteryLevel, ct);
        await _transport.NextWriteAsync(ct);
        _transport.SetState(TransportState.Lost);

        await Assert.ThrowsAsync<HeadsetUnavailableException>(() => task);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Refuses_to_send_when_not_connected()
    {
        _transport.SetState(TransportState.Disconnected);
        using var queue = CreateQueue();

        await Assert.ThrowsAsync<HeadsetUnavailableException>(() => queue.SendAsync(CommandCatalog.GetBatteryLevel, TestContext.Current.CancellationToken));
        Assert.Empty(_transport.Writes);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Blocked_command_is_rejected_without_writing()
    {
        using var queue = CreateQueue();

        await Assert.ThrowsAsync<CommandNotAllowedException>(() => queue.SendAsync(CommandCatalog.FactoryReset, TestContext.Current.CancellationToken));
        Assert.Empty(_transport.Writes);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Untested_command_requires_explicit_hardware_test_policy()
    {
        // Braucht einen Command, der noch NICHT auf Hardware verifiziert ist – bei Statuswechsel anpassen.
        Assert.Equal(ProtocolStatus.NeedsHardwareTest, CommandCatalog.QcGetSupportedFeaturesNext.Status);
        using var queue = CreateQueue(CommandPolicy.Production);

        await Assert.ThrowsAsync<CommandNotAllowedException>(() => queue.SendAsync(CommandCatalog.QcGetSupportedFeaturesNext, TestContext.Current.CancellationToken));
        Assert.Empty(_transport.Writes);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Verified_command_is_allowed_in_production()
    {
        Assert.Equal(ProtocolStatus.Verified, CommandCatalog.GetBatteryLevel.Status);
        _transport.Responder = _ => [Hex.Parse(BatteryResponse70)];
        using var queue = CreateQueue(CommandPolicy.Production);

        var response = await queue.SendAsync(CommandCatalog.GetBatteryLevel, TestContext.Current.CancellationToken);

        Assert.Equal(new byte[] { 0x46 }, response.Payload);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Descriptor_not_from_catalog_is_rejected()
    {
        using var queue = CreateQueue();
        var forged = new CommandDescriptor("Forged", 0x0495, new CommandWord(0x0603), SafetyClass.Read, ProtocolStatus.Verified, 0);

        await Assert.ThrowsAsync<CommandNotAllowedException>(() => queue.SendAsync(forged, TestContext.Current.CancellationToken));
        Assert.Empty(_transport.Writes);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Payload_length_is_checked_before_sending()
    {
        using var queue = CreateQueue();

        await Assert.ThrowsAsync<ArgumentException>(() => queue.SendAsync(CommandCatalog.GetPairedDeviceInfo, TestContext.Current.CancellationToken));
        Assert.Empty(_transport.Writes);
    }

    [Fact]
    public void Hardware_test_policy_cannot_unlock_unverified_or_blocked_commands()
    {
        Assert.Throws<ArgumentException>(() => CommandPolicy.ForHardwareTest(CommandCatalog.QcGetVoiceAssistant));
        Assert.Throws<ArgumentException>(() => CommandPolicy.ForHardwareTest(CommandCatalog.FactoryReset));
        Assert.Throws<ArgumentException>(() => CommandPolicy.ForHardwareTest(CommandCatalog.Unknown0813));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Reports_traffic_for_logging_and_captures()
    {
        _transport.Responder = _ => [Hex.Parse(BatteryResponse70)];
        using var queue = CreateQueue();
        var traffic = new List<GaiaTraffic>();
        queue.Traffic += (_, t) => traffic.Add(t);

        await queue.SendAsync(CommandCatalog.GetBatteryLevel, TestContext.Current.CancellationToken);

        Assert.Collection(
            traffic,
            tx => Assert.Equal((TrafficDirection.Tx, BatteryRequest, "GetBatteryLevel"), (tx.Direction, Hex.Format(tx.Raw), tx.CommandName)),
            rx =>
            {
                Assert.Equal((TrafficDirection.Rx, BatteryResponse70, "GetBatteryLevel"), (rx.Direction, Hex.Format(rx.Raw), rx.CommandName));
                Assert.NotNull(rx.Duration);
            });
    }

    private GaiaCommandQueue CreateQueue(CommandPolicy? policy = null) =>
        new(_transport, policy ?? TestPolicy, NullLogger<GaiaCommandQueue>.Instance, _time);

    /// <summary>Schiebt die Test-Uhr vor, bis die Aufgabe fertig ist (Timeouts laufen über die FakeTimeProvider-Uhr).</summary>
    private async Task AdvanceUntilCompletedAsync(Task task)
    {
        for (var i = 0; i < 200 && !task.IsCompleted; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(task.IsCompleted, "Aufgabe wurde nicht fertig.");
    }
}
