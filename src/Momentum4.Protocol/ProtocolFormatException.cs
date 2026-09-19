// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

namespace Momentum4.Protocol;

/// <summary>Eine Antwort passt nicht zum dokumentierten Format. Es wird nie still ein Wert geraten.</summary>
public sealed class ProtocolFormatException : Exception
{
    public ProtocolFormatException(string message, ReadOnlySpan<byte> payload)
        : base($"{message} (Payload: [{Hex.Format(payload)}])")
    {
        Payload = payload.ToArray();
    }

    public byte[] Payload { get; }
}
