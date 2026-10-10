using System.Buffers.Binary;
using Microsoft.Extensions.Time.Testing;
using Momentum4.Core.Dongle;
using Momentum4.Protocol.Dongle;

namespace Momentum4.Core.Tests;

internal sealed class DonglePeer : IDongleLeaseProvider, IDongleLease
{
    public DongleTarget Target { get; } = new("control", "upgrade", "usb", "fingerprint");
    public DongleObservation Observation { get; set; }
    public FakeTimeProvider Clock { get; } = new();
    private readonly long started;
    public double Elapsed => Clock.GetElapsedTime(started).TotalSeconds;
    public ushort Active = 8;
    public ushort Supported = 15;
    public byte State = 3;
    public byte Mode;
    public bool Events;
    public bool PartialWrite;
    public bool InvalidDescriptor;
    public bool LeaseDisposed;
    public int Acquisitions;
    public int Setters;
    public int UpgradeReads;
    public Action<byte>? OnControl;
    public Action<byte, TimeSpan>? OnControlBudget = null;
    public Action? OnOpen;
    public Func<IReadOnlyList<DongleObservation>>? OnObserve = null;
    public List<Endpoint> Endpoints { get; } = [];
    public List<byte[]> Writes { get; } = [];
    public Dictionary<byte, byte[]> ControlOverrides { get; } = [];
    public Func<byte, byte[], byte[]?>? OnUpgrade = null;
    public bool FailFeature = false;
    public bool Rebooted = false;
    public bool EventFlood = false;
    public bool StallRead = false;
    public bool RemoveOnSetter = false;
    public List<TimeSpan> ReadTimeouts { get; } = [];
    public List<byte[]> Features { get; } = [];
    public Func<HidInterface, Exception?>? OnClose = null;

    public DonglePeer() { started = Clock.GetTimestamp(); Observation = new(Target, 4, 1, 20, 300); }
    public ValueTask<IDongleLease> AcquireAsync(DongleTarget target, TimeSpan timeout, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); Acquisitions++; LeaseDisposed = false; return ValueTask.FromResult<IDongleLease>(this); }
    public ValueTask<IReadOnlyList<DongleObservation>> ObserveAsync(TimeSpan timeout, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(OnObserve?.Invoke() ?? new[] { Observation }); }
    public ValueTask<IHidEndpoint> OpenAsync(DongleTarget target, HidInterface kind, TimeSpan timeout, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); var endpoint = new Endpoint(this, kind); Endpoints.Add(endpoint); OnOpen?.Invoke(); return ValueTask.FromResult<IHidEndpoint>(endpoint); }
    public ValueTask DisposeAsync() { LeaseDisposed = true; return ValueTask.CompletedTask; }
    public async Task<T> Complete<T>(Task<T> task)
    {
        for (var index = 0; index < 7000 && !task.IsCompleted; index++) { Clock.Advance(TimeSpan.FromMilliseconds(100)); await Task.Delay(5, TestContext.Current.CancellationToken); }
        return await task;
    }
    public static byte[] ControlReply(byte command, byte[] payload, byte marker = 0xff)
    { var report = new byte[64]; report[0] = 0x34; report[1] = marker; report[2] = command; report[3] = (byte)payload.Length; payload.CopyTo(report, 4); return report; }
    public static byte[] UpgradeReply(byte opcode, byte[] payload)
    { var report = new byte[13]; report[0] = 6; report[1] = (byte)(3 + payload.Length); report[2] = opcode; BinaryPrimitives.WriteUInt16BigEndian(report.AsSpan(3), (ushort)payload.Length); payload.CopyTo(report, 5); return report; }

    internal sealed class Endpoint(DonglePeer peer, HidInterface kind) : IHidEndpoint
    {
        public bool Disposed;
        private readonly Queue<byte[]> replies = new();
        public ValueTask<ReadOnlyMemory<byte>> GetDescriptorAsync(TimeSpan timeout, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult<ReadOnlyMemory<byte>>(peer.InvalidDescriptor ? [0] : Convert.FromHexString(kind == HidInterface.Control ? "06a2ff0901a10185347508953f09028102953f09019102c0" : "0600ff0901a10185037508953e0902b102850595fe090291028506950c09028102c0")); }
        public async ValueTask<ReadOnlyMemory<byte>> ReadReportAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            peer.ReadTimeouts.Add(timeout); if (kind == HidInterface.Upgrade) peer.UpgradeReads++; cancellationToken.ThrowIfCancellationRequested();
            if (peer.EventFlood) { peer.Clock.Advance(TimeSpan.FromMilliseconds(50)); return ControlReply(6, [3], 0xfc); }
            if (peer.StallRead) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (peer.RemoveOnSetter && peer.Setters > 0 && kind == HidInterface.Control) { peer.RemoveOnSetter = false; throw new DongleDisconnectedException("removed"); }
            if (replies.Count == 0) throw new DongleDisconnectedException("BTD 700 disconnected"); return replies.Dequeue();
        }
        public ValueTask<int> WriteReportAsync(ReadOnlyMemory<byte> report, TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); var frame = report.ToArray(); peer.Writes.Add(frame);
            if (kind == HidInterface.Control)
            {
                var command = frame[2]; if (command == 4) peer.Setters++; peer.OnControlBudget?.Invoke(command, timeout); peer.OnControl?.Invoke(command);
                byte[] payload = command switch { 0x12 => [3, 11, 0], 6 => [peer.State], 1 => [peer.Mode, 3, 1], 3 => [(byte)peer.Supported, (byte)(peer.Supported >> 8)], 5 => [(byte)peer.Active, (byte)(peer.Active >> 8)], 7 => [1], 8 => [1, 1], 0x15 => [1], 4 => [0], _ => throw new InvalidOperationException() };
                if (peer.Events) replies.Enqueue(ControlReply(command, [], 0xfc));
                replies.Enqueue(peer.ControlOverrides.GetValueOrDefault(command) ?? ControlReply(command, payload));
            }
            else
            {
                var payload = frame.AsSpan(5, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(3))).ToArray();
                var reply = peer.OnUpgrade?.Invoke(frame[2], payload); if (reply is not null) replies.Enqueue(reply);
            }
            return ValueTask.FromResult(peer.PartialWrite ? frame.Length - 1 : frame.Length);
        }
        public ValueTask<int> SetFeatureAsync(ReadOnlyMemory<byte> report, TimeSpan timeout, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); peer.Features.Add(report.ToArray()); if (peer.FailFeature) return ValueTask.FromResult(62); if (report.Span[2] == 2) replies.Enqueue(new byte[] { 6, 1, 0 }.Concat(new byte[10]).ToArray()); return ValueTask.FromResult(63); }
        public ValueTask DisposeAsync()
        { Disposed = true; var error = peer.OnClose?.Invoke(kind); return error is null ? ValueTask.CompletedTask : ValueTask.FromException(error); }
    }
}
