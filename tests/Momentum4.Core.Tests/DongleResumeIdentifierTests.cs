using Momentum4.Protocol.Dongle;

namespace Momentum4.Core.Tests;

public sealed class DongleResumeIdentifierTests
{
    [Fact]
    public void ResumeIdentifierMatchesIndependentPythonZlibOracle()
    {
        var image = FirmwareImage.Parse(DongleFirmwareFixture.Image());
        Assert.Equal("15a4310ce76fde4e94b2be6510e5ed3dc28d00694e3dba57f19ef77a7a9d1e7b", image.Sha256);
        Assert.Equal(Convert.FromHexString("f154cc02"), image.ResumeId);
    }
}
