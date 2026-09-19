// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Momentum4.Protocol.Features;

namespace Momentum4.Core.Services;

/// <summary>
/// Schaltet Funktionen nach Firmware-Version frei (docs/architecture.md §6.5). Unbekannte Firmware bedeutet:
/// konservativ, nur was ohne Einschränkung belegt ist.
/// </summary>
public sealed class FeatureGate(FirmwareVersion? firmware)
{
    /// <summary>Laut [DS] lehnt FW 2.x Bass Boost ab, FW 3.x beantwortet ihn.</summary>
    public static readonly FirmwareVersion BassBoostMinimum = new(3, 0, 0);

    /// <summary>Laut <c>m4.json</c>: <c>Eq5Band_Gen1_MinFwVersion</c>.</summary>
    public static readonly FirmwareVersion EqualizerMinimum = new(2, 12, 0);

    public FirmwareVersion? Firmware { get; } = firmware;

    public bool BassBoost => Firmware >= BassBoostMinimum;

    public bool Equalizer => Firmware >= EqualizerMinimum;
}
