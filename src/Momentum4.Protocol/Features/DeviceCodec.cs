// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

namespace Momentum4.Protocol.Features;

/// <summary>BT-Kompatibilitätsmodus (<c>0x0405</c>/<c>0x0406</c>). Vermutlich der Schalter „High Resolution Audio“.</summary>
public enum BtCompatibilityMode : byte
{
    BetterAudio = 0,
    BetterCompatibility = 1,
}

/// <summary>Stand der Sound Personalization (<c>0x2001</c>), docs/protocol.md §6.10.</summary>
public enum PersonalizationState : byte
{
    NotParameterized = 0,
    Calibrating = 1,
    Calibrated = 2,
    ActivationInhibited = 3,
}

/// <summary>Tragezustand (<c>0x0402</c>/<c>0x0482</c>), docs/protocol.md §6.1. Werte laut [SDC]/[DS].</summary>
public enum WearState : byte
{
    Unknown = 0,
    InCase = 1,
    NotWorn = 2,
    Worn = 3,
}

/// <summary>Decoder für Feature 2 (device) und 16 (personalizedSound).</summary>
public static class DeviceCodec
{
    /// <summary><c>0x0405</c> ← <c>[0/1]</c>.</summary>
    public static byte[] EncodeBtCompatibilityMode(BtCompatibilityMode mode) =>
        Enum.IsDefined(mode) ? [(byte)mode] : throw new ArgumentOutOfRangeException(nameof(mode), mode, null);

    /// <summary><c>0x0406</c> → genau 1 Byte, 0 oder 1.</summary>
    public static BtCompatibilityMode DecodeBtCompatibilityMode(ReadOnlySpan<byte> payload) =>
        SwitchCodec.Decode(payload, "BT-Kompatibilitätsmodus") ? BtCompatibilityMode.BetterCompatibility : BtCompatibilityMode.BetterAudio;

    /// <summary>
    /// <c>0x0502</c>/<c>0x0482</c> → genau 1 Byte, 0…3. Earbuds melden laut [SDC] links und rechts getrennt; der
    /// Over-Ear-M4 schickt ein Byte (FW 3.37.3). Zwei Bytes gelten deshalb als Formatfehler statt geraten zu werden.
    /// </summary>
    public static WearState DecodeWearState(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 1 || payload[0] > 3)
        {
            throw new ProtocolFormatException("Tragezustand: genau 1 Byte mit 0…3 erwartet", payload);
        }

        return (WearState)payload[0];
    }

    /// <summary><c>0x2001</c> → genau 1 Byte, 0…3.</summary>
    public static PersonalizationState DecodePersonalizationState(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 1 || payload[0] > 3)
        {
            throw new ProtocolFormatException("Personalization-Status: genau 1 Byte mit 0…3 erwartet", payload);
        }

        return (PersonalizationState)payload[0];
    }
}
