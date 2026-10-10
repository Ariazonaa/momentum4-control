using System.Buffers.Binary;

namespace Momentum4.Protocol.Dongle;

public static class HidDescriptor
{
    public const int MaximumLength = 4096;
    private readonly record struct Owner(uint CollectionPage, uint? CollectionUsage, uint Page, uint? Usage);

    public static void Validate(ReadOnlySpan<byte> data, HidInterface kind)
    {
        if (data.IsEmpty || data.Length > MaximumLength || !Enum.IsDefined(kind)) throw new DongleProtocolException("Invalid HID descriptor size or interface");
        var globals = new uint[16];
        var stack = new Stack<uint[]>();
        var collections = new List<(uint Page, uint? Usage)>();
        uint? usage = null;
        var reports = new Dictionary<(uint Id, int Tag), (ulong Bits, Owner Owner)>();
        var offset = 0;
        while (offset < data.Length)
        {
            var prefix = data[offset++];
            if (prefix == 0xfe) throw new DongleProtocolException("Unsupported HID descriptor item");
            var size = (prefix & 3) == 3 ? 4 : prefix & 3;
            if (size > data.Length - offset) throw new DongleProtocolException("Truncated HID descriptor");
            uint value = size switch { 0 => 0, 1 => data[offset], 2 => BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]), _ => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]) };
            offset += size;
            var itemType = prefix >> 2 & 3;
            var tag = prefix >> 4;
            if (itemType == 1)
            {
                if (tag == 10)
                {
                    if (stack.Count >= 16) throw new DongleProtocolException("HID global nesting exceeds limit");
                    stack.Push((uint[])globals.Clone());
                }
                else if (tag == 11)
                {
                    if (stack.Count == 0) throw new DongleProtocolException("Unbalanced HID globals");
                    globals = stack.Pop();
                }
                else globals[tag] = value;
            }
            else if (itemType == 2 && tag == 0) usage = value;
            else if (itemType == 0)
            {
                if (tag == 10)
                {
                    if (collections.Count >= 16) throw new DongleProtocolException("HID collection nesting exceeds limit");
                    collections.Add((globals[0], usage));
                }
                else if (tag == 12)
                {
                    if (collections.Count == 0) throw new DongleProtocolException("Unbalanced HID collection");
                    collections.RemoveAt(collections.Count - 1);
                }
                else if (tag is 8 or 9 or 11)
                {
                    if (collections.Count == 0) throw new DongleProtocolException("HID report outside collection");
                    var reportId = globals[8]; var bits = (ulong)globals[7] * globals[9];
                    if (bits > 32768 || reportId > 255) throw new DongleProtocolException("Oversized HID report");
                    var selected = kind == HidInterface.Control ? reportId == 0x34 : reportId is 3 or 5 or 6;
                    if (selected)
                    {
                        var owner = new Owner(collections[0].Page, collections[0].Usage, globals[0], usage);
                        var key = (reportId, tag);
                        if (reports.TryGetValue(key, out var previous) && previous.Owner != owner) throw new DongleProtocolException("Ambiguous HID report owner");
                        reports[key] = (previous.Bits + bits, owner);
                    }
                }
                usage = null;
            }
        }
        if (collections.Count != 0 || stack.Count != 0) throw new DongleProtocolException("Unbalanced HID descriptor");
        var expected = kind == HidInterface.Control ? new[] { (0x34u, 8, 504ul, 2u), (0x34u, 9, 504ul, 1u) } : new[] { (3u, 11, 496ul, 2u), (5u, 9, 2032ul, 2u), (6u, 8, 96ul, 2u) };
        uint page = kind == HidInterface.Control ? 0xffa2u : 0xff00u;
        foreach (var (id, tag, bits, expectedUsage) in expected)
            if (!reports.TryGetValue((id, tag), out var actual) || actual != (bits, new Owner(page, 1, page, expectedUsage)))
                throw new DongleProtocolException("BTD 700 HID descriptor does not match");
    }
}
