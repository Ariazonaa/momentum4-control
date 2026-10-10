using Momentum4.Protocol.Dongle;

namespace Momentum4.Core.Dongle;

internal static class DongleIo
{
    internal static async Task<DongleObservation?> ObserveAsync(IDongleLease lease, DongleTarget target, DongleBudget budget, bool exact = false)
    {
        var observations = await lease.ObserveAsync(budget.Remaining, budget.Token).ConfigureAwait(false);
        budget.Check();
        if (observations.Count > 256) throw new DongleException("Too many USB observations");
        var candidates = observations.Where(item => item.Target.UsbPath == target.UsbPath).ToArray();
        if (candidates.Length == 0) return null;
        if (candidates.Length != 1 || candidates[0].Target.Identity != target.Identity) throw new DongleException("BTD 700 replacement identity is wrong or ambiguous");
        var current = candidates[0];
        if (current.DeviceNumber is < 1 or > 127 || current.ChangeStamp < 0 || (exact && current.Target != target)) throw new DongleException("BTD 700 identity or USB instance changed");
        return current;
    }

    internal static async Task<IHidEndpoint> OpenAsync(IDongleLease lease, DongleObservation observation, HidInterface kind, DongleBudget budget)
    {
        var endpoint = await lease.OpenAsync(observation.Target, kind, budget.Remaining, budget.Token).ConfigureAwait(false);
        try
        {
            var descriptor = await endpoint.GetDescriptorAsync(budget.Remaining, budget.Token).ConfigureAwait(false);
            HidDescriptor.Validate(descriptor.Span, kind);
            if (await ObserveAsync(lease, observation.Target, budget, true).ConfigureAwait(false) != observation) throw new DongleException("BTD 700 USB instance changed while opening");
            return endpoint;
        }
        catch
        {
            await endpoint.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static async Task<byte[]> ReadAsync(IHidEndpoint endpoint, DongleBudget budget)
    {
        var report = await endpoint.ReadReportAsync(budget.Remaining, budget.Token).ConfigureAwait(false);
        budget.Check();
        if (report.IsEmpty) throw new DongleDisconnectedException("BTD 700 disconnected");
        if (report.Length > DongleReports.MaximumReportLength) throw new DongleException("Oversized HID report");
        return report.ToArray();
    }

    internal static async Task WriteAsync(IHidEndpoint endpoint, byte[] report, DongleBudget budget)
    {
        if (report.Length is < 1 or > DongleReports.MaximumReportLength) throw new DongleException("Invalid HID report size");
        if (await endpoint.WriteReportAsync(report, budget.Remaining, budget.Token).ConfigureAwait(false) != report.Length) throw new DongleException("Incomplete BTD 700 report write");
        budget.Check();
    }

    internal static async Task<byte[]> ControlAsync(IHidEndpoint endpoint, byte command, byte[] payload, int[] lengths, DongleBudget budget)
    {
        using var commandBudget = budget.Slice(3);
        await WriteAsync(endpoint, DongleReports.ControlRequest(command, payload), commandBudget).ConfigureAwait(false);
        while (true)
        {
            var report = await ReadAsync(endpoint, commandBudget).ConfigureAwait(false);
            var reply = DongleReports.ControlReply(report, command, lengths);
            if (reply is not null) return reply;
        }
    }

    internal static async Task<DongleStatus> StatusAsync(IHidEndpoint endpoint, DongleTarget target, DongleBudget budget)
    {
        var firmware = await ControlAsync(endpoint, 0x12, [], [3, 4], budget).ConfigureAwait(false);
        var state = await ControlAsync(endpoint, 6, [], [1], budget).ConfigureAwait(false);
        var mode = await ControlAsync(endpoint, 1, [], [2, 3], budget).ConfigureAwait(false);
        var supported = await ControlAsync(endpoint, 3, [], [1, 2], budget).ConfigureAwait(false);
        var codec = await ControlAsync(endpoint, 5, [], [1, 2], budget).ConfigureAwait(false);
        await ControlAsync(endpoint, 7, [], [1], budget).ConfigureAwait(false);
        var quality = await ControlAsync(endpoint, 8, [], [2], budget).ConfigureAwait(false);
        await ControlAsync(endpoint, 0x15, [], [1], budget).ConfigureAwait(false);
        var patch = firmware.Length == 4 ? firmware[2] | firmware[3] << 8 : firmware[2];
        return new DongleStatus(target, new FirmwareVersion(firmware[0], firmware[1], patch), state[0], mode[0], mode[^1], Word(codec), quality[0] switch { 1 => 16, 2 => 24, _ => null }, quality[1] switch { 1 => 44100, 2 => 48000, 3 => 96000, _ => null }, Word(supported));
    }
    private static ushort Word(byte[] bytes) => (ushort)(bytes[0] | (bytes.Length == 2 ? bytes[1] << 8 : 0));
}
