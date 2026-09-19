// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

namespace Momentum4.Protocol.Features;

/// <summary>Anti-Wind-Stufe (ANC-Modus 1), docs/protocol.md §6.9.</summary>
public enum AntiWindMode : byte
{
    Off = 0,
    Maximum = 1,
    Automatic = 2,
}

/// <summary>
/// Inhalt von <c>0x1A01</c>: Paare (Modus-ID, Wert). Bekannt: 1 = Anti-Wind, 2 = Comfort (Bedeutung unbekannt),
/// 3 = Adaptive. Unbekannte IDs bleiben in <see cref="Pairs"/> erhalten.
/// </summary>
public sealed record AncModeTable(AntiWindMode? AntiWind, byte? Comfort, bool? Adaptive, IReadOnlyList<(byte Id, byte Value)> Pairs);

/// <summary>
/// Payload-Format für <c>0x1A00</c>. Die Quellen widersprechen sich (docs/protocol.md §6.9): [MC]/[SDC] schreiben ein
/// Paar <c>[mode, state]</c>, [DS] (FW 3.38.3) die ganze Tabelle. Welches der M4 (FW 3.37.3) annimmt, klärt
/// Hardware-Stufe 6.
/// </summary>
public enum AncModeWriteFormat
{
    /// <summary>Komplette Tabelle wie von <c>0x1A01</c> gelesen, ein Wert ersetzt (Read-Modify-Write).</summary>
    FullTable,

    /// <summary>Nur das geänderte Paar <c>[mode, state]</c>.</summary>
    SinglePair,
}

/// <summary>Encoder und Decoder für Feature 13 (ANC), docs/protocol.md §6.9.</summary>
public static class AncCodec
{
    public const byte ModeAntiWind = 1;
    public const byte ModeComfort = 2;
    public const byte ModeAdaptive = 3;

    /// <summary><c>0x1A04</c> → <c>[0/1]</c>.</summary>
    public static byte[] EncodeEnabled(bool enabled) => SwitchCodec.Encode(enabled);

    /// <summary><c>0x1A02</c> → <c>[level]</c>, 0…100.</summary>
    public static byte[] EncodeLevel(int level)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 100);
        return [(byte)level];
    }

    /// <summary>
    /// <c>0x1A00</c>: setzt einen Modus-Wert: Anti-Wind (0…2), Comfort (0/1, <c>m4.json</c>: „2=comfort“, „0 = off,
    /// 1 = on“; Wirkung erst im Hardware-Test geklärt) und Adaptive (0/1). Bei <see cref="AncModeWriteFormat.FullTable"/>
    /// werden die übrigen gelesenen Werte unverändert mitgeschickt.
    /// </summary>
    public static byte[] EncodeMode(AncModeTable current, byte modeId, byte value, AncModeWriteFormat format)
    {
        ArgumentNullException.ThrowIfNull(current);
        var valid = modeId switch
        {
            ModeAntiWind => value <= 2,
            ModeComfort => value <= 1,
            ModeAdaptive => value <= 1,
            _ => throw new ArgumentOutOfRangeException(nameof(modeId), modeId, "Nur Anti-Wind (1), Comfort (2) und Adaptive (3) sind beschreibbar."),
        };
        if (!valid)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, $"Ungültiger Wert für Modus {modeId}.");
        }

        if (format == AncModeWriteFormat.SinglePair)
        {
            return [modeId, value];
        }

        if (!current.Pairs.Any(p => p.Id == modeId))
        {
            throw new ArgumentException($"Modus {modeId} fehlt in der gelesenen Tabelle.", nameof(current));
        }

        var payload = new byte[current.Pairs.Count * 2];
        for (var i = 0; i < current.Pairs.Count; i++)
        {
            var (id, old) = current.Pairs[i];
            payload[2 * i] = id;
            payload[(2 * i) + 1] = id == modeId ? value : old;
        }

        return payload;
    }

    /// <summary><c>0x1A05</c> → <c>[0/1]</c>.</summary>
    public static bool DecodeEnabled(ReadOnlySpan<byte> payload) => SwitchCodec.Decode(payload, "ANC ein/aus");

    /// <summary><c>0x1A03</c> → <c>[level]</c>, 0…100.</summary>
    public static int DecodeLevel(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 1 || payload[0] > 100)
        {
            throw new ProtocolFormatException("ANC-Pegel: genau 1 Byte mit 0…100 erwartet", payload);
        }

        return payload[0];
    }

    /// <summary><c>0x1A01</c> → <c>[id, value, id, value, …]</c>.</summary>
    public static AncModeTable DecodeModes(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2 || payload.Length % 2 != 0)
        {
            throw new ProtocolFormatException("ANC-Modi: Paare aus (ID, Wert) erwartet", payload);
        }

        AntiWindMode? antiWind = null;
        byte? comfort = null;
        bool? adaptive = null;
        var pairs = new List<(byte, byte)>();
        for (var i = 0; i < payload.Length; i += 2)
        {
            var (id, value) = (payload[i], payload[i + 1]);
            pairs.Add((id, value));
            switch (id)
            {
                case ModeAntiWind when value <= 2:
                    antiWind = (AntiWindMode)value;
                    break;
                case ModeAntiWind:
                    throw new ProtocolFormatException($"ANC-Modi: Anti-Wind-Wert {value} unbekannt", payload);
                case ModeComfort:
                    comfort = value;
                    break;
                case ModeAdaptive when value <= 1:
                    adaptive = value == 1;
                    break;
                case ModeAdaptive:
                    throw new ProtocolFormatException($"ANC-Modi: Adaptive-Wert {value} unbekannt", payload);
            }
        }

        return new AncModeTable(antiWind, comfort, adaptive, pairs);
    }
}
