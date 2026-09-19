// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

namespace Momentum4.Protocol.Features;

/// <summary>
/// EQ-Konfiguration aus <c>0x1000</c>: Bandanzahl und Gain-Bereich in dB. Der M4 (FW 3.37.3) sendet zwei Byte mehr
/// als von [MC] erwartet; ihre Bedeutung ist unbekannt, sie bleiben in <see cref="Extra"/> erhalten.
/// </summary>
public sealed record EqConfig(int BandCount, decimal MinGainDb, decimal MaxGainDb, byte[] Extra)
{
    public EqConfig(int bandCount, decimal minGainDb, decimal maxGainDb)
        : this(bandCount, minGainDb, maxGainDb, [])
    {
    }

    public bool Equals(EqConfig? other) =>
        other is not null && BandCount == other.BandCount && MinGainDb == other.MinGainDb && MaxGainDb == other.MaxGainDb
        && Extra.AsSpan().SequenceEqual(other.Extra);

    public override int GetHashCode() => HashCode.Combine(BandCount, MinGainDb, MaxGainDb, Extra.Length);
}

/// <summary>Decoder für Feature 8 (userEQ), docs/protocol.md §6.4. Gains sind int8 in 0,1 dB.</summary>
public static class UserEqCodec
{
    /// <summary><c>0x1000</c> → <c>[bands, min i8, max i8, (weitere Bytes …)]</c>.</summary>
    public static EqConfig DecodeConfig(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 3 || payload[0] == 0 || (sbyte)payload[1] >= (sbyte)payload[2])
        {
            throw new ProtocolFormatException("EQ-Konfiguration: mindestens [Bänder > 0, min < max] erwartet", payload);
        }

        return new EqConfig(payload[0], ToDb(payload[1]), ToDb(payload[2]), payload[3..].ToArray());
    }

    /// <summary>
    /// <c>0x1002 [band]</c> → <c>[gain]</c> oder <c>[band, gain]</c> ([MC] akzeptiert beides; welches der M4 sendet,
    /// klärt Hardware-Stufe 4). Das Band-Echo muss passen.
    /// </summary>
    public static decimal DecodeBandGain(ReadOnlySpan<byte> payload, int band)
    {
        switch (payload.Length)
        {
            case 1:
                return ToDb(payload[0]);
            case 2 when payload[0] == band:
                return ToDb(payload[1]);
            case 2:
                throw new ProtocolFormatException($"EQ-Band: Echo {payload[0]} passt nicht zu Band {band}", payload);
            default:
                throw new ProtocolFormatException("EQ-Band: 1 oder 2 Byte erwartet", payload);
        }
    }

    /// <summary>
    /// <c>0x1003 [beliebiges Byte]</c> → ein Gain je Band (am M4 mit FW 3.37.3 beobachtet: 5 Byte, identisch mit
    /// den Einzelwerten aus <c>0x1002</c>).
    /// </summary>
    public static decimal[] DecodeAllGains(ReadOnlySpan<byte> payload, int bandCount)
    {
        if (payload.Length != bandCount)
        {
            throw new ProtocolFormatException($"Alle EQ-Gains: {bandCount} Byte erwartet", payload);
        }

        var gains = new decimal[bandCount];
        for (var i = 0; i < bandCount; i++)
        {
            gains[i] = ToDb(payload[i]);
        }

        return gains;
    }

    /// <summary><c>0x100B [band]</c> → <c>[band, Hz u16 BE]</c> (am M4: 90, 325, 1500, 6500, 6500 Hz).</summary>
    public static int DecodeBandFrequency(ReadOnlySpan<byte> payload, int band)
    {
        if (payload.Length != 3 || payload[0] != band)
        {
            throw new ProtocolFormatException($"EQ-Frequenz: genau [{band}, u16] erwartet", payload);
        }

        return (payload[1] << 8) | payload[2];
    }

    /// <summary>
    /// Band-Listen aus dem Notification-Dump nach der Anmeldung (FW 3.37.3): <c>[band, Wert]</c> je Band, z. B.
    /// <c>0x108B</c> Frequenzen (u16), <c>0x108D</c> Güte (u16), <c>0x108F</c> Filtertyp (u8).
    /// </summary>
    public static IReadOnlyList<(int Band, int Value)> DecodeBandValueList(ReadOnlySpan<byte> payload, int valueBytes)
    {
        if (valueBytes is not (1 or 2) || payload.Length == 0 || payload.Length % (1 + valueBytes) != 0)
        {
            throw new ProtocolFormatException($"Band-Liste: Einträge aus [band, {valueBytes} Byte] erwartet", payload);
        }

        var entries = new List<(int, int)>();
        for (var i = 0; i < payload.Length; i += 1 + valueBytes)
        {
            var value = valueBytes == 1 ? payload[i + 1] : (payload[i + 1] << 8) | payload[i + 2];
            if (payload[i] != entries.Count)
            {
                throw new ProtocolFormatException($"Band-Liste: Band {payload[i]} an Position {entries.Count}", payload);
            }

            entries.Add((payload[i], value));
        }

        return entries;
    }

    /// <summary><c>0x1009</c> → <c>[0/1]</c>.</summary>
    public static bool DecodeBassBoost(ReadOnlySpan<byte> payload) => SwitchCodec.Decode(payload, "Bass Boost");

    /// <summary>
    /// <c>0x1001</c> ← <c>[band, gain i8]</c>, Gain in 0,1 dB ([MC], [DS]). Den erlaubten Bereich aus <c>0x1000</c> und die
    /// Bandanzahl prüft der Aufrufer; hier nur, ob der Wert in 0,1-dB-Schritten als int8 kodierbar ist.
    /// </summary>
    public static byte[] EncodeBandGain(int band, decimal gainDb)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(band);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(band, byte.MaxValue);
        var raw = gainDb * 10m;
        if (raw != decimal.Truncate(raw) || raw < sbyte.MinValue || raw > sbyte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(gainDb), gainDb, "Gain muss in 0,1-dB-Schritten zwischen −12,8 und +12,7 dB liegen.");
        }

        return [(byte)band, unchecked((byte)(sbyte)raw)];
    }

    /// <summary><c>0x1008</c> ← <c>[0/1]</c>.</summary>
    public static byte[] EncodeBassBoost(bool enabled) => SwitchCodec.Encode(enabled);

    /// <summary>Gain in 0,1 dB (vorzeichenbehaftetes Byte) → dB.</summary>
    public static decimal ToDb(byte raw) => (sbyte)raw / 10m;
}
