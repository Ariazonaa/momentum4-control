// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

namespace Momentum4.Protocol.Gaia;

public enum GaiaReaderDiagnosticKind
{
    /// <summary>Bytes vor dem nächsten Start-of-Frame wurden verworfen.</summary>
    DiscardedBytes,

    /// <summary>Ein <c>FF</c> mit ungültiger Version, ungültigen Flags oder übergroßer Länge wurde übersprungen.</summary>
    InvalidHeader,

    /// <summary>Checksumme stimmte nicht; der vermeintliche Frame wurde verworfen.</summary>
    ChecksumMismatch,

    /// <summary>Gültiger Frame mit Flags ≠ 0 – beim M4 bisher nicht erwartet, daher auffällig.</summary>
    UnusualFlags,
}

public sealed record GaiaReaderDiagnostic(GaiaReaderDiagnosticKind Kind, byte[] Bytes, string Message);

public sealed record GaiaReadResult(IReadOnlyList<GaiaFrame> Frames, IReadOnlyList<GaiaReaderDiagnostic> Diagnostics);

/// <summary>
/// Inkrementeller Parser für den Empfangsstrom (docs/protocol.md §2.3): puffert über mehrere Reads, liefert
/// mehrere Frames pro Read, synchronisiert auf <c>FF</c> neu, akzeptiert Version 1/3/4, wertet die Flags
/// 0x01 (XOR-Checksumme) und 0x02 (2-Byte-Länge) aus. Nicht threadsicher – ein Reader pro Empfangsstrom.
/// </summary>
public sealed class GaiaFrameReader
{
    public const int MaxPayloadShortLength = 255;
    public const int MaxPayloadLongLength = 4096;

    private const byte FlagChecksum = 0x01;
    private const byte FlagLongLength = 0x02;

    private readonly List<byte> _buffer = [];

    /// <summary>Anzahl gepufferter Bytes, die noch keinen vollständigen Frame ergeben.</summary>
    public int PendingByteCount => _buffer.Count;

    public void Reset() => _buffer.Clear();

    public GaiaReadResult Push(ReadOnlySpan<byte> data)
    {
        _buffer.AddRange(data);

        var frames = new List<GaiaFrame>();
        var diagnostics = new List<GaiaReaderDiagnostic>();

        while (_buffer.Count > 0)
        {
            var sof = _buffer.IndexOf(GaiaFrameCodec.StartOfFrame);
            if (sof < 0)
            {
                diagnostics.Add(Discard(_buffer.Count, GaiaReaderDiagnosticKind.DiscardedBytes, "kein Start-of-Frame"));
                break;
            }

            if (sof > 0)
            {
                diagnostics.Add(Discard(sof, GaiaReaderDiagnosticKind.DiscardedBytes, "Bytes vor Start-of-Frame"));
            }

            if (_buffer.Count < 4)
            {
                break; // Header noch unvollständig
            }

            var version = _buffer[1];
            var flags = _buffer[2];
            if (version is not (1 or 3 or 4) || (flags & ~(FlagChecksum | FlagLongLength)) != 0)
            {
                diagnostics.Add(Discard(1, GaiaReaderDiagnosticKind.InvalidHeader, $"Version {version} / Flags 0x{flags:X2} ungültig"));
                continue;
            }

            int headerLength;
            int payloadLength;
            if ((flags & FlagLongLength) != 0)
            {
                if (_buffer.Count < 5)
                {
                    break;
                }

                headerLength = 5;
                payloadLength = (_buffer[3] << 8) | _buffer[4];
                if (payloadLength > MaxPayloadLongLength)
                {
                    diagnostics.Add(Discard(1, GaiaReaderDiagnosticKind.InvalidHeader, $"Länge {payloadLength} über Obergrenze"));
                    continue;
                }
            }
            else
            {
                headerLength = 4;
                payloadLength = _buffer[3];
            }

            var hasChecksum = (flags & FlagChecksum) != 0;
            var total = headerLength + 4 + payloadLength + (hasChecksum ? 1 : 0);
            if (_buffer.Count < total)
            {
                break; // auf weitere Bytes warten
            }

            var raw = _buffer.GetRange(0, total).ToArray();
            if (hasChecksum && !ChecksumMatches(raw))
            {
                diagnostics.Add(Discard(1, GaiaReaderDiagnosticKind.ChecksumMismatch, "XOR-Checksumme falsch"));
                continue;
            }

            _buffer.RemoveRange(0, total);

            var vendor = (ushort)((raw[headerLength] << 8) | raw[headerLength + 1]);
            var command = new CommandWord((ushort)((raw[headerLength + 2] << 8) | raw[headerLength + 3]));
            var payload = raw.AsSpan(headerLength + 4, payloadLength).ToArray();
            frames.Add(new GaiaFrame(version, flags, vendor, command, payload, raw));

            if (flags != 0)
            {
                diagnostics.Add(new GaiaReaderDiagnostic(GaiaReaderDiagnosticKind.UnusualFlags, raw, $"Frame mit Flags 0x{flags:X2}"));
            }
        }

        return new GaiaReadResult(frames, diagnostics);
    }

    private GaiaReaderDiagnostic Discard(int count, GaiaReaderDiagnosticKind kind, string message)
    {
        var bytes = _buffer.GetRange(0, count).ToArray();
        _buffer.RemoveRange(0, count);
        return new GaiaReaderDiagnostic(kind, bytes, $"{message}: {count} Byte verworfen");
    }

    private static bool ChecksumMatches(byte[] frame)
    {
        byte xor = 0;
        for (var i = 0; i < frame.Length - 1; i++)
        {
            xor ^= frame[i];
        }

        return xor == frame[^1];
    }
}
