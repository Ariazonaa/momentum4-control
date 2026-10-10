using Momentum4.Protocol.Dongle;

namespace Momentum4.Core.Dongle;

public sealed class DongleUpdatePlan
{
    private readonly object issuer;
    private readonly FirmwareImage? image;
    private readonly string? officialUrl;
    public DongleTarget Target { get; }
    public UpgradeVersion Current { get; }
    public FirmwareVersion TargetVersion { get; }
    public bool Available => image is not null;
    public string Reason { get; }
    public string? Sha256 => image?.Sha256;
    public int? ImageSize => image?.Size;

    internal DongleUpdatePlan(object issuer, DongleTarget target, UpgradeVersion current, FirmwareVersion targetVersion, FirmwareImage? image, string? officialUrl)
    {
        this.issuer = issuer; Target = target; Current = current; TargetVersion = targetVersion; this.image = image; this.officialUrl = officialUrl;
        Reason = image is null ? $"BTD 700 is up to date ({current.Firmware}); no newer official firmware is available." : $"Official firmware {targetVersion} is available.";
    }

    internal FirmwareImage Validate(object owner)
    {
        if (!ReferenceEquals(owner, issuer) || image is null || officialUrl is null) throw new DongleException("Update must be prepared through the official firmware check");
        DongleFirmwareService.ValidateImageUrl(officialUrl, TargetVersion);
        var parsed = FirmwareImage.Parse(image.CopyData());
        if (parsed.Sha256 != image.Sha256 || parsed.Size != image.Size || parsed.Version != TargetVersion || parsed.ConfigVersion != image.ConfigVersion || !parsed.CompatibleVersions.SequenceEqual(image.CompatibleVersions) || !parsed.CompatibleConfigs.SequenceEqual(image.CompatibleConfigs) || !parsed.Partitions.SequenceEqual(image.Partitions) || !parsed.ResumeId.SequenceEqual(image.ResumeId))
            throw new DongleException("Prepared firmware bytes or metadata changed");
        if (TargetVersion <= Current.Firmware) throw new DongleException("Only strictly newer firmware updates are supported");
        parsed.RequireCompatible(Current); return parsed;
    }
}

public sealed record DongleUpdateProgress(string Stage, int Percent);
public sealed class DongleUpdateException(string stage, string detail, bool recoveryRequired, bool interrupted, Exception? innerException = null)
    : DongleException($"Firmware update failed during {stage}: {detail}" + (recoveryRequired ? "; recovery required; do not restart or replay the update" : ""), innerException)
{
    public string Stage { get; } = stage;
    public bool RecoveryRequired { get; } = recoveryRequired;
    public bool Interrupted { get; private set; } = interrupted;
    internal void MarkInterrupted() => Interrupted = true;
}
