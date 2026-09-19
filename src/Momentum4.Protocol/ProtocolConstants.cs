// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

namespace Momentum4.Protocol;

/// <summary>
/// Feste Werte des MOMENTUM-4-Steuerkanals. Quellen und Teststatus: docs/protocol.md §1 und §4.
/// </summary>
public static class ProtocolConstants
{
    /// <summary>
    /// SDP-Dienst „GAIA“ des Headsets, über den der RFCOMM-Steuerkanal läuft.
    /// Nicht zu verwechseln mit <c>00000000-DECA-FADE-DECA-DEAFDECACAFF</c> (Apple iAP2).
    /// </summary>
    public static readonly Guid GaiaServiceUuid = new("A2129FF3-081B-4C45-8AFE-469D9C4842EC");

    /// <summary>Teil des Gerätenamens, an dem das Headset (zusammen mit dem GAIA-Dienst) erkannt wird.</summary>
    public const string DeviceNameMarker = "MOMENTUM 4";

    /// <summary>Sennheiser-Befehlssatz.</summary>
    public const ushort VendorSennheiser = 0x0495;

    /// <summary>Qualcomm-GAIA-Core (API-Version, Seriennummer, …).</summary>
    public const ushort VendorQualcomm = 0x001D;
}
