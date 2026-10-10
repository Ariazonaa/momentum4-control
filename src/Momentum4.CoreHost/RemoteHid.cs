using System.Text.Json;
using Momentum4.Core.Dongle;
using Momentum4.Protocol.Dongle;

namespace Momentum4.CoreHost;

public sealed class RemoteHidProvider(PrivateHost host, DongleTarget boundTarget) : IDongleLeaseProvider
{
    private readonly PrivateHost _host = host;
    private bool _active;

    public async ValueTask<IDongleLease> AcquireAsync(DongleTarget target, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_active || target != boundTarget) throw new DongleException("Invalid bound HID target");
        var value = await CallAsync("acquire", writer => Timeout(writer, timeout), cancellationToken);
        PrivateProtocol.Fields(value);
        _active = true;
        return new Lease(this, target);
    }

    private async Task<JsonElement> CallAsync(string operation, Action<Utf8JsonWriter> arguments, CancellationToken cancellationToken)
    {
        try { return await _host.OsCallAsync(operation, arguments, cancellationToken); }
        catch (HostOsException error) when (error.Removed) { throw new DongleDisconnectedException("HID endpoint disconnected", error); }
        catch (HostOsException error) { throw new DongleException("HID OS operation failed", error); }
    }

    private static void Timeout(Utf8JsonWriter writer, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(600)) throw new DongleException("Invalid HID operation timeout");
        writer.WriteNumber("timeout_ms", Math.Clamp((long)Math.Ceiling(timeout.TotalMilliseconds), 1, 600000));
    }

    private sealed class Lease(RemoteHidProvider owner, DongleTarget target) : IDongleLease
    {
        private readonly Dictionary<DongleTarget, long> _observed = [];
        private bool _disposed;
        public DongleTarget Target { get; } = target;

        public async ValueTask<IReadOnlyList<DongleObservation>> ObserveAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (_disposed) throw new DongleException("HID lease ended");
            var value = await owner.CallAsync("observe", writer => Timeout(writer, timeout), cancellationToken);
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 32) throw new PrivateProtocolException();
            _observed.Clear();
            var values = new List<DongleObservation>();
            foreach (var item in value.EnumerateArray())
            {
                PrivateProtocol.Fields(item, "id", "control_path", "upgrade_path", "usb_path", "identity", "device_number", "node_device", "node_inode", "change_stamp");
                var actual = ReadTarget(item);
                var id = PrivateProtocol.Integer(item, "id");
                var number = PrivateProtocol.Integer(item, "device_number");
                if (id <= 0 || number is < 1 or > 127 || !item.GetProperty("node_device").TryGetUInt64(out var nodeDevice) || !item.GetProperty("node_inode").TryGetUInt64(out var nodeInode) || !_observed.TryAdd(actual, id)) throw new PrivateProtocolException();
                values.Add(new(actual, (int)number, nodeDevice, nodeInode, PrivateProtocol.Integer(item, "change_stamp")));
            }
            return values;
        }

        public async ValueTask<IHidEndpoint> OpenAsync(DongleTarget actual, HidInterface kind, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (_disposed || !_observed.TryGetValue(actual, out var id) || !Enum.IsDefined(kind)) throw new DongleException("Unobserved HID target");
            var value = await owner.CallAsync("open", writer =>
            {
                writer.WriteString("kind", kind == HidInterface.Control ? "control" : "upgrade");
                writer.WriteNumber("target", id);
                Timeout(writer, timeout);
            }, cancellationToken);
            PrivateProtocol.Fields(value, "endpoint");
            var endpoint = PrivateProtocol.Integer(value, "endpoint");
            if (endpoint <= 0) throw new PrivateProtocolException();
            return new Endpoint(owner, endpoint);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            _observed.Clear();
            if (!owner._host.Lifetime.IsCancellationRequested)
            {
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                var value = await owner.CallAsync("release", _ => { }, budget.Token);
                PrivateProtocol.Fields(value);
            }
            owner._active = false;
        }
    }

    private sealed class Endpoint(RemoteHidProvider owner, long endpoint) : IHidEndpoint
    {
        private bool _disposed;

        public ValueTask<ReadOnlyMemory<byte>> GetDescriptorAsync(TimeSpan timeout, CancellationToken cancellationToken) => ReadAsync("descriptor", 4096, timeout, cancellationToken);
        public ValueTask<ReadOnlyMemory<byte>> ReadReportAsync(TimeSpan timeout, CancellationToken cancellationToken) => ReadAsync("read", 255, timeout, cancellationToken);

        private async ValueTask<ReadOnlyMemory<byte>> ReadAsync(string operation, int maximum, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (_disposed) throw new DongleException("HID endpoint ended");
            var value = await owner.CallAsync(operation, writer => { writer.WriteNumber("endpoint", endpoint); Timeout(writer, timeout); }, cancellationToken);
            PrivateProtocol.Fields(value, "data");
            var encoded = PrivateProtocol.Text(value, "data", ((maximum + 2) / 3) * 4);
            var bytes = new byte[maximum];
            if (!Convert.TryFromBase64String(encoded, bytes, out var count) || count is < 1 || count > maximum) throw new PrivateProtocolException();
            return bytes.AsMemory(0, count);
        }

        public ValueTask<int> WriteReportAsync(ReadOnlyMemory<byte> report, TimeSpan timeout, CancellationToken cancellationToken) => WriteAsync("write", report, timeout, cancellationToken);
        public ValueTask<int> SetFeatureAsync(ReadOnlyMemory<byte> report, TimeSpan timeout, CancellationToken cancellationToken) => WriteAsync("feature", report, timeout, cancellationToken);

        private async ValueTask<int> WriteAsync(string operation, ReadOnlyMemory<byte> report, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (_disposed || report.Length is < 1 or > 255 || operation == "feature" && report.Length != 63) throw new DongleException("Invalid bounded HID report");
            var value = await owner.CallAsync(operation, writer =>
            {
                writer.WriteNumber("endpoint", endpoint);
                writer.WriteBase64String("data", report.Span);
                Timeout(writer, timeout);
            }, cancellationToken);
            PrivateProtocol.Fields(value, "written");
            var written = PrivateProtocol.Integer(value, "written");
            if (written < 0 || written > report.Length) throw new PrivateProtocolException();
            return (int)written;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            if (!owner._host.Lifetime.IsCancellationRequested)
            {
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                var value = await owner.CallAsync("close", writer => writer.WriteNumber("endpoint", endpoint), budget.Token);
                PrivateProtocol.Fields(value);
            }
        }
    }

    public static DongleTarget ReadTarget(JsonElement value) => new(PrivateProtocol.Text(value, "control_path", 1024), PrivateProtocol.Text(value, "upgrade_path", 1024), PrivateProtocol.Text(value, "usb_path", 1024), PrivateProtocol.Text(value, "identity", 128));
}
