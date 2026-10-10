// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Momentum4.Protocol.Features;

namespace Momentum4.Core.Services;

/// <summary>
/// Schaltet Funktionen nach Firmware-Version frei (docs/architecture.md §6.5). Unbekannte Firmware bedeutet:
/// konservativ, nur was ohne Einschränkung belegt ist.
/// </summary>
public sealed class FeatureGate(FirmwareVersion? firmware, string? model = "M4AEBT")
{
    /// <summary>Laut [DS] lehnt FW 2.x Bass Boost ab, FW 3.x beantwortet ihn.</summary>
    public static readonly FirmwareVersion BassBoostMinimum = new(3, 0, 0);

    /// <summary>Laut <c>m4.json</c>: <c>Eq5Band_Gen1_MinFwVersion</c>.</summary>
    public static readonly FirmwareVersion EqualizerMinimum = new(2, 12, 0);

    public FirmwareVersion? Firmware { get; } = firmware;

    public bool IsMomentum4 => IsModel(model, "M4AEBT");

    public bool IsMomentum5 => IsModel(model, "M5AEBT");

    public bool Writable => Firmware is not null && (IsMomentum4 || (IsMomentum5 && Firmware == new FirmwareVersion(6, 21, 5)));

    public bool BassBoost => Writable && Firmware >= BassBoostMinimum;

    public bool Equalizer => Writable && Firmware >= EqualizerMinimum;

    private static bool IsModel(string? model, string prefix)
    {
        if (model == prefix)
        {
            return true;
        }

        return model is not null && model.StartsWith(prefix + " ", StringComparison.Ordinal)
            && model.Length > prefix.Length + 1
            && model.AsSpan(prefix.Length + 1).ContainsAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 -".AsSpan()) == false;
    }
}
