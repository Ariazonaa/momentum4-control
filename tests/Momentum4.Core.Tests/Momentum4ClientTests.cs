// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using Momentum4.Core.Device;
using Momentum4.Core.Queue;
using Momentum4.Protocol;
using Momentum4.Protocol.Catalog;
using Momentum4.Protocol.Features;

namespace Momentum4.Core.Tests;

public sealed class Momentum4ClientTests
{
    [Fact(Timeout = 10_000)]
    public async Task Decodes_identification_and_battery()
    {
        var transport = new FakeTransport
        {
            Responder = request => Hex.Format(request) switch
            {
                "FF 03 00 00 04 95 12 06" => [Hex.Parse("FF 03 00 0A 04 95 13 06 4D 4F 4D 45 4E 54 55 4D 20 34")],
                "FF 03 00 00 04 95 12 01" => [Hex.Parse("FF 03 00 06 04 95 13 01 00 03 00 26 00 03")],
                "FF 03 00 00 00 1D 00 00" => [Hex.Parse("FF 03 00 02 00 1D 01 00 03 01")],
                "FF 03 00 00 04 95 06 03" => [Hex.Parse("FF 03 00 01 04 95 07 03 4E")],
                _ => [],
            },
        };
        var policy = CommandPolicy.ForHardwareTest(
            CommandCatalog.GetModelId, CommandCatalog.GetFirmwareVersion, CommandCatalog.QcGetApiVersion, CommandCatalog.GetBatteryLevel);
        using var queue = new GaiaCommandQueue(transport, policy, NullLogger<GaiaCommandQueue>.Instance);
        var client = new Momentum4Client(queue);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal("MOMENTUM 4", await client.GetModelIdAsync(ct));
        Assert.Equal(new FirmwareVersion(3, 38, 3), await client.GetFirmwareVersionAsync(ct));
        Assert.Equal(new GaiaApiVersion(3, 1), await client.GetGaiaApiVersionAsync(ct));
        Assert.Equal(78, await client.GetBatteryLevelAsync(ct));
    }
}
