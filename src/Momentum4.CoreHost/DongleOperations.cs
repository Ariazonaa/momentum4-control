using System.Text.Json;
using Momentum4.Core.Dongle;
using Momentum4.Protocol.Dongle;

namespace Momentum4.CoreHost;

public sealed class DongleOperations(PrivateHost host, TimeProvider? timeProvider = null) : IDisposable
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private DongleTarget? _target;
    private DongleService? _service;
    private DongleFirmwareService? _firmware;
    private readonly PreparedPlanSlot<DongleUpdatePlan> _plans = new(timeProvider ?? TimeProvider.System);

    public async Task<HostValue> DispatchAsync(PrivateRequest request, CancellationToken cancellationToken)
    {
        var args = request.Arguments;
        if (request.Operation == "dongle-bind")
        {
            if (_target is not null) throw new PrivateProtocolException();
            PrivateProtocol.Fields(args, "control_path", "upgrade_path", "usb_path", "identity");
            _target = RemoteHidProvider.ReadTarget(args);
            var provider = new RemoteHidProvider(host, _target);
            _service = new(provider, _time);
            _firmware = new(provider, _time);
            return new(writer => { writer.WriteStartObject(); writer.WriteEndObject(); });
        }
        if (_target is null || _service is null || _firmware is null) throw new PrivateProtocolException();
        switch (request.Operation)
        {
            case "dongle-status":
                PrivateProtocol.Fields(args);
                var status = await _service.StatusAsync(_target, cancellationToken);
                return new(writer => WriteStatus(writer, status));
            case "dongle-codec":
                PrivateProtocol.Fields(args, "codec");
                var selected = PrivateProtocol.Integer(args, "codec");
                if (selected > int.MaxValue || selected < int.MinValue || !Enum.IsDefined((DongleCodec)selected)) throw new PrivateProtocolException();
                var after = await _service.SetCodecAsync(_target, (DongleCodec)selected, cancellationToken);
                return new(writer => WriteStatus(writer, after));
            case "dongle-check":
                PrivateProtocol.Fields(args);
                _plans.Clear();
                var plan = await _firmware.CheckUpdateAsync(_target, cancellationToken);
                var handle = _plans.Replace(plan, _target.Identity);
                return new(writer =>
                {
                    writer.WriteStartObject(); writer.WriteNumber("handle", handle);
                    writer.WritePropertyName("current"); WriteVersion(writer, plan.Current);
                    writer.WriteString("target", plan.TargetVersion.ToString()); writer.WriteBoolean("available", plan.Available);
                    writer.WriteString("reason", plan.Reason); writer.WriteString("sha256", plan.Sha256);
                    if (plan.ImageSize is { } size) writer.WriteNumber("image_size", size); else writer.WriteNull("image_size");
                    writer.WriteEndObject();
                });
            case "dongle-apply":
                PrivateProtocol.Fields(args, "handle");
                var prepared = _plans.Consume(PrivateProtocol.Integer(args, "handle"), _target.Identity);
                var version = await _firmware.ApplyUpdateAsync(prepared, new InlineProgress(value => host.ProgressAsync(value.Stage, value.Percent, cancellationToken).GetAwaiter().GetResult()), cancellationToken);
                return new(writer => WriteVersion(writer, version));
            default: throw new PrivateProtocolException();
        }
    }

    private sealed class InlineProgress(Action<DongleUpdateProgress> report) : IProgress<DongleUpdateProgress>
    {
        public void Report(DongleUpdateProgress value) => report(value);
    }

    private static void WriteVersion(Utf8JsonWriter writer, UpgradeVersion version)
    {
        writer.WriteStartObject(); writer.WriteString("firmware", version.Firmware.ToString()); writer.WriteNumber("config", version.Config); writer.WriteEndObject();
    }

    private static void WriteStatus(Utf8JsonWriter writer, DongleStatus status)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("device"); writer.WriteStartObject();
        writer.WriteString("control_path", status.Target.ControlPath); writer.WriteString("upgrade_path", status.Target.UpgradePath);
        writer.WriteString("usb_path", status.Target.UsbPath); writer.WriteString("identity", status.Target.Identity); writer.WriteEndObject();
        writer.WriteString("firmware", status.Firmware.ToString());
        writer.WriteNumber("state", status.State); writer.WriteNumber("audio_mode", status.AudioMode); writer.WriteNumber("transport", status.Transport);
        writer.WriteNumber("codec_mask", status.CodecMask); writer.WriteNumber("supported_codec_mask", status.SupportedCodecMask);
        if (status.SampleBits is { } bits) writer.WriteNumber("sample_bits", bits); else writer.WriteNull("sample_bits");
        if (status.SampleRate is { } rate) writer.WriteNumber("sample_rate", rate); else writer.WriteNull("sample_rate");
        writer.WriteEndObject();
    }

    public void Dispose()
    {
        _plans.Clear();
        _firmware?.Dispose();
    }
}

public sealed class PreparedPlanSlot<T>(TimeProvider timeProvider) where T : class
{
    private T? _value;
    private string? _target;
    private long _issued;
    private long _next;
    private long _current;

    public long Replace(T value, string target)
    {
        _value = value;
        _target = target;
        _issued = timeProvider.GetTimestamp();
        _current = checked(++_next);
        return _current;
    }

    public T Consume(long handle, string target)
    {
        if (handle <= 0 || _value is null || handle != _current || target != _target || timeProvider.GetElapsedTime(_issued) >= TimeSpan.FromMinutes(10))
        {
            Clear();
            throw new PrivateProtocolException();
        }
        var value = _value;
        Clear();
        return value;
    }

    public void Clear() { _value = null; _target = null; _current = 0; }
}
