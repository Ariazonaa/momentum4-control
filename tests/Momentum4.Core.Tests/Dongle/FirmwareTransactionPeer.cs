using System.Buffers.Binary;
using Momentum4.Core.Dongle;
using Momentum4.Protocol.Dongle;

namespace Momentum4.Core.Tests;

internal sealed class FirmwareTransactionPeer
{
    internal DonglePeer Adapter { get; } = new() { State = 2 };
    internal List<byte> Received { get; } = [];
    internal int ImageSize;
    internal int RequestSize;
    internal int Commits;
    internal int Syncs;
    internal int ValidationPolls;
    internal bool LegacySync;
    internal bool LostCommit;
    internal byte PostCheckpoint = 3;
    internal UpgradeVersion Source = new(new FirmwareVersion(3, 11, 0), 1);
    internal UpgradeVersion FinalVersion = new(new FirmwareVersion(4, 0, 0), 2);
    internal string RebootChange = "devnum";
    internal Action? OnData;
    internal Dictionary<byte, (byte Opcode, byte[] Payload)> Overrides { get; } = [];
    private int pendingEnd;
    private bool rebooted;

    internal FirmwareTransactionPeer() => Adapter.OnUpgrade = Send;
    private byte[]? Send(byte opcode, byte[] payload)
    {
        if (opcode == 0x13) Syncs++;
        if (opcode == 0x16) ValidationPolls++;
        if (opcode == 0x10) Commits++;
        if (Overrides.TryGetValue(opcode, out var overridden)) return DonglePeer.UpgradeReply(overridden.Opcode, overridden.Payload);
        switch (opcode)
        {
            case 0x19:
                var version = rebooted ? FinalVersion : Source; var bytes = new byte[6];
                BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)version.Firmware.Major); BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), (ushort)(version.Firmware.Minor * 100 + version.Firmware.Patch)); BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), version.Config);
                return DonglePeer.UpgradeReply(0x1a, bytes);
            case 0x13: return DonglePeer.UpgradeReply(0x14, new byte[] { rebooted ? PostCheckpoint : (byte)0 }.Concat(payload).Concat(LegacySync ? [] : new byte[] { 1 }).ToArray());
            case 1: return DonglePeer.UpgradeReply(2, [0, 0, 100]);
            case 0x15:
                pendingEnd = RequestSize == 0 ? ImageSize : Math.Min(ImageSize, RequestSize);
                return DonglePeer.UpgradeReply(3, DongleFirmwareFixture.DataRequest((uint)pendingEnd));
            case 4:
                Received.AddRange(payload[1..]); OnData?.Invoke();
                if (RequestSize > 0 && Received.Count == pendingEnd && Received.Count < ImageSize)
                { var count = Math.Min(RequestSize, ImageSize - Received.Count); pendingEnd += count; return DonglePeer.UpgradeReply(3, DongleFirmwareFixture.DataRequest((uint)count)); }
                return null;
            case 0x16: return ValidationPolls == 1 ? DonglePeer.UpgradeReply(0x17, [0, 0]) : DonglePeer.UpgradeReply(0x0b, []);
            case 0x0c:
                rebooted = true;
                Adapter.Observation = RebootChange switch
                {
                    "devnum" => Adapter.Observation with { DeviceNumber = 5 },
                    "node" => Adapter.Observation with { NodeInode = 21, ChangeStamp = 301 },
                    "ctime" => Adapter.Observation with { ChangeStamp = 301 },
                    "path" => Adapter.Observation with { Target = Adapter.Target with { UpgradePath = "new-upgrade" } },
                    "foreign" => Adapter.Observation with { Target = Adapter.Target with { Identity = "foreign" } },
                    _ => Adapter.Observation
                };
                if (RebootChange == "ambiguous") Adapter.OnObserve = () => new[] { Adapter.Observation, Adapter.Observation };
                return null;
            case 0x0e: return DonglePeer.UpgradeReply(0x0f, []);
            case 0x10: return LostCommit ? null : DonglePeer.UpgradeReply(0x12, []);
            default: throw new InvalidOperationException($"Unexpected opcode {opcode}");
        }
    }
}
