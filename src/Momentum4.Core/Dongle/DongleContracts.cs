using Momentum4.Protocol.Dongle;

namespace Momentum4.Core.Dongle;

public class DongleException(string message, Exception? innerException = null) : IOException(message, innerException);
public sealed class DongleDisconnectedException(string message, Exception? innerException = null) : DongleException(message, innerException);
public sealed record DongleTarget(string ControlPath, string UpgradePath, string UsbPath, string Identity);
public sealed record DongleObservation(DongleTarget Target, int DeviceNumber, ulong NodeDevice, ulong NodeInode, long ChangeStamp);
public sealed record DongleStatus(DongleTarget Target, FirmwareVersion Firmware, int State, int AudioMode, int Transport, ushort CodecMask, int? SampleBits, int? SampleRate, ushort SupportedCodecMask = 0);

public interface IDongleLeaseProvider
{
    ValueTask<IDongleLease> AcquireAsync(DongleTarget target, TimeSpan timeout, CancellationToken cancellationToken);
}

public interface IDongleLease : IAsyncDisposable
{
    DongleTarget Target { get; }
    ValueTask<IReadOnlyList<DongleObservation>> ObserveAsync(TimeSpan timeout, CancellationToken cancellationToken);
    ValueTask<IHidEndpoint> OpenAsync(DongleTarget target, HidInterface kind, TimeSpan timeout, CancellationToken cancellationToken);
}

public interface IHidEndpoint : IAsyncDisposable
{
    ValueTask<ReadOnlyMemory<byte>> GetDescriptorAsync(TimeSpan timeout, CancellationToken cancellationToken);
    ValueTask<ReadOnlyMemory<byte>> ReadReportAsync(TimeSpan timeout, CancellationToken cancellationToken);
    ValueTask<int> WriteReportAsync(ReadOnlyMemory<byte> report, TimeSpan timeout, CancellationToken cancellationToken);
    ValueTask<int> SetFeatureAsync(ReadOnlyMemory<byte> report, TimeSpan timeout, CancellationToken cancellationToken);
}
