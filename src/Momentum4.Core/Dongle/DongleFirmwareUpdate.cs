using System.Buffers.Binary;
using Momentum4.Protocol.Dongle;

namespace Momentum4.Core.Dongle;

public sealed partial class DongleFirmwareService
{
    public static TimeSpan UpdateTimeout { get; } = TimeSpan.FromSeconds(600);
    public const int MaximumPackets = 100000;
    public const int MaximumValidationPolls = 256;

    public async Task<UpgradeVersion> ApplyUpdateAsync(DongleUpdatePlan plan, IProgress<DongleUpdateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var budget = new DongleBudget(clock, UpdateTimeout, cancellationToken);
        IDongleLease? lease = null; DongleUpgradeSession? transport = null;
        var stage = "preflight"; var started = false; var packets = 0; Exception? failure = null;
        var emitted = new HashSet<(string Stage, int Percent)>();
        UpgradeVersion finalVersion;
        void Report(string currentStage, int percent)
        { if (emitted.Add((currentStage, percent))) progress?.Report(new DongleUpdateProgress(currentStage, percent)); }
        void Packet()
        { budget.Check(); if (++packets > MaximumPackets) throw new DongleException("Update packet budget exceeded"); }
        async Task Send(UpgradeOpcode opcode, byte[] payload)
        { Packet(); await transport!.SendAsync(opcode, payload).ConfigureAwait(false); }
        async Task<(byte Opcode, byte[] Payload)> Receive(params byte[] expected)
        { Packet(); var reply = await transport!.ReceiveAsync(expected).ConfigureAwait(false); budget.Check(); return reply; }
        async Task Sync(byte[] resumeId, byte checkpoint)
        {
            await Send(UpgradeOpcode.Sync, resumeId).ConfigureAwait(false);
            var (_, payload) = await Receive(0x14).ConfigureAwait(false);
            if (payload.Length is not (5 or 6) || !payload.AsSpan(1, 4).SequenceEqual(resumeId)) throw new DongleException("Invalid sync response or resume identifier");
            if (payload.Length == 6 && payload[5] != 1) throw new DongleException("Unsupported upgrade protocol version");
            if (payload[0] != checkpoint) throw new DongleException("Unsupported update checkpoint");
        }
        async Task Start()
        {
            await Send(UpgradeOpcode.Start, []).ConfigureAwait(false);
            var (_, payload) = await Receive(2).ConfigureAwait(false);
            if (payload.Length != 3 || payload[0] != 0) throw new DongleException("Device refused update start");
        }
        async Task EmptyReply(byte expected)
        { var (_, payload) = await Receive(expected).ConfigureAwait(false); if (payload.Length != 0) throw new DongleException("Unexpected update acknowledgement payload"); }
        try
        {
            budget.Check(); var image = plan.Validate(issuer); var bytes = image.CopyData();
            lease = await leases.AcquireAsync(plan.Target, budget.Remaining, budget.Token).ConfigureAwait(false);
            if (lease.Target != plan.Target) throw new DongleException("BTD 700 lease identity mismatch");
            var observation = await DongleIo.ObserveAsync(lease, plan.Target, budget, true).ConfigureAwait(false) ?? throw new DongleDisconnectedException("BTD 700 disconnected");
            await using (var control = await DongleIo.OpenAsync(lease, observation, HidInterface.Control, budget).ConfigureAwait(false))
            {
                var status = await DongleIo.StatusAsync(control, plan.Target, budget).ConfigureAwait(false);
                if (status.State is 3 or 4) throw new DongleException("Stop audio playback and calls before updating the dongle");
                if (status.State is not (0 or 1 or 2)) throw new DongleException("Unknown dongle audio state; update refused");
                if (await DongleIo.ObserveAsync(lease, plan.Target, budget, true).ConfigureAwait(false) != observation) throw new DongleException("BTD 700 identity changed during preflight");
            }
            transport = new DongleUpgradeSession(lease, plan.Target, budget);
            await transport.ConnectAsync().ConfigureAwait(false);
            if (await transport.GetVersionAsync().ConfigureAwait(false) != plan.Current) throw new DongleException("Dongle firmware or configuration changed since update preparation");
            budget.Check(); Report("preflight", 0);
            stage = "sync"; started = true; await Sync(image.ResumeId, 0).ConfigureAwait(false);
            stage = "start"; await Start().ConfigureAwait(false);
            stage = "transfer"; await Send(UpgradeOpcode.RequestData, []).ConfigureAwait(false);
            var cursor = 0;
            while (cursor < bytes.Length)
            {
                var (_, payload) = await Receive(3).ConfigureAwait(false);
                if (payload.Length != 8) throw new DongleException("Invalid data request length");
                var length = BinaryPrimitives.ReadUInt32BigEndian(payload); var offset = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(4));
                if (offset != 0 || length == 0 || length > bytes.Length - cursor) throw new DongleException("Unsupported data offset or out-of-range data request");
                var end = cursor + (int)length;
                while (cursor < end)
                {
                    var next = Math.Min(end, cursor + 249); var packet = new byte[next - cursor + 1];
                    packet[0] = next == bytes.Length ? (byte)1 : (byte)0; bytes.AsSpan(cursor, next - cursor).CopyTo(packet.AsSpan(1));
                    await Send(UpgradeOpcode.Data, packet).ConfigureAwait(false); cursor = next; Report("transfer", cursor * 90 / bytes.Length);
                }
            }
            stage = "validation"; Report("validation", 90); var validated = false;
            for (var poll = 0; poll < MaximumValidationPolls; poll++)
            {
                await Send(UpgradeOpcode.Validate, []).ConfigureAwait(false);
                var (opcode, payload) = await Receive(0x17, 0x0b).ConfigureAwait(false);
                if (opcode == 0x0b)
                {
                    if (payload.Length != 0) throw new DongleException("Invalid validation completion payload");
                    validated = true; break;
                }
                if (payload.Length != 2) throw new DongleException("Invalid validation backoff");
                await budget.DelayAsync(BinaryPrimitives.ReadUInt16BigEndian(payload) / 1000.0).ConfigureAwait(false);
            }
            if (!validated) throw new DongleException("Validation polling budget exceeded");
            stage = "reboot"; Report("reboot", 93); await Send(UpgradeOpcode.Reboot, [0]).ConfigureAwait(false);
            await transport.ReopenAsync().ConfigureAwait(false); budget.Check(); await transport.ConnectAsync().ConfigureAwait(false);
            stage = "post-reboot sync"; await Sync(image.ResumeId, 3).ConfigureAwait(false);
            stage = "post-reboot start"; await Start().ConfigureAwait(false);
            stage = "proceed"; await Send(UpgradeOpcode.Proceed, [0]).ConfigureAwait(false); await EmptyReply(0x0f).ConfigureAwait(false);
            stage = "commit"; Report("commit", 97); await Send(UpgradeOpcode.Commit, [0]).ConfigureAwait(false); await EmptyReply(0x12).ConfigureAwait(false);
            stage = "final version"; budget.Check(); finalVersion = await transport.GetVersionAsync().ConfigureAwait(false);
            if (finalVersion != new UpgradeVersion(image.Version, image.ConfigVersion)) throw new DongleException("Final dongle firmware/configuration does not match the image");
            budget.Check();
        }
        catch (OperationCanceledException exception) when (!started && cancellationToken.IsCancellationRequested) { failure = exception; throw; }
        catch (Exception exception)
        {
            failure = new DongleUpdateException(stage, exception.Message, started, exception is OperationCanceledException && cancellationToken.IsCancellationRequested, exception);
            throw failure;
        }
        finally
        {
            Exception? cleanupError = null;
            try { if (transport is not null) await transport.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { cleanupError = exception; }
            try { if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { cleanupError ??= exception; }
            if (cleanupError is not null)
            {
                if (failure is null) throw new DongleUpdateException(stage, "Failed to close upgrade session", started, cleanupError is OperationCanceledException, cleanupError);
                if (cleanupError is OperationCanceledException && failure is DongleUpdateException updateFailure) updateFailure.MarkInterrupted();
                failure.Data["CleanupFailure"] = cleanupError;
            }
        }
        Report("complete", 100); return finalVersion;
    }
}
