using System.Globalization;
using System.Text.RegularExpressions;

namespace Momentum4.Protocol.Dongle;

public sealed class DongleProtocolException(string message) : FormatException(message);

public enum HidInterface { Control, Upgrade }
public enum HidFeatureOperation { Connect = 2, Disconnect = 7 }
public enum DongleCodec { Sbc = 1, Aptx = 2, AptxAdaptive = 4, AptxLossless = 8, AptxLite = 16, Lc3 = 32 }
public enum UpgradeOpcode { Start = 0x01, Data = 0x04, Reboot = 0x0c, Proceed = 0x0e, Commit = 0x10, Sync = 0x13, RequestData = 0x15, Validate = 0x16, GetVersion = 0x19 }

public readonly record struct FirmwareVersion : IComparable<FirmwareVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public uint WireWord => (uint)Major << 16 | (uint)(Minor * 100 + Patch);

    public FirmwareVersion(int major, int minor, int patch)
    {
        if (major is < 0 or > 65535 || minor < 0 || patch is < 0 or > 99 || (long)minor * 100 + patch > 65535)
            throw new FormatException("Invalid firmware version");
        Major = major; Minor = minor; Patch = patch;
    }

    public static FirmwareVersion Parse(string value)
    {
        if (value is null || value.Length > 32 || !Regex.IsMatch(value, "\\A[0-9]+\\.[0-9]+\\.[0-9]+\\z", RegexOptions.CultureInvariant))
            throw new FormatException("Invalid firmware version");
        var parts = value.Split('.');
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor) || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
            throw new FormatException("Invalid firmware version");
        return new FirmwareVersion(major, minor, patch);
    }

    public int CompareTo(FirmwareVersion other) => WireWord.CompareTo(other.WireWord);
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
    public static bool operator <(FirmwareVersion left, FirmwareVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(FirmwareVersion left, FirmwareVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(FirmwareVersion left, FirmwareVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(FirmwareVersion left, FirmwareVersion right) => left.CompareTo(right) >= 0;
}

public readonly record struct UpgradeVersion(FirmwareVersion Firmware, ushort Config);
