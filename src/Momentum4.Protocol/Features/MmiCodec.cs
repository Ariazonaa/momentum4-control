// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

namespace Momentum4.Protocol.Features;

/// <summary>
/// Funktionen, die einer Touch-Geste zugewiesen werden können (Feature 11, <c>0x1600</c>/<c>0x1601</c>). Namen und
/// Werte stammen aus der offiziellen App (Enum <c>Gaia3MmiConfigurationButtonAction</c>, als Interoperabilitäts-
/// information übernommen, docs/research.md §4.13). Welche der M4 tatsächlich annimmt, klärt der Hardware-Test.
/// </summary>
public enum TouchFunction : byte
{
    None = 0,
    PlayPause = 1,
    NextTrack = 2,
    PreviousTrack = 3,
    VolumeUp = 4,
    VolumeDown = 5,
    TouchOnOff = 6,
    VoiceAssistant = 7,
    AuracastOnOff = 8,
    AcceptMuteCall = 9,
    RejectEndCall = 10,
    Redial = 11,
}

/// <summary>Eine belegbare Touch-Geste: Taste, Muster und die zugewiesene Funktion.</summary>
public sealed record TouchGesture(byte Button, byte Pattern, TouchFunction Function);

/// <summary>Codec für die Gestenbelegung (Feature 11, mmiConfig), docs/protocol.md §6.7.</summary>
public static class MmiCodec
{
    /// <summary><c>0x1601</c> ← <c>[button, pattern]</c>.</summary>
    public static byte[] EncodeQuery(byte button, byte pattern) => [button, pattern];

    /// <summary><c>0x1701</c> → <c>[button, pattern, function]</c>.</summary>
    public static TouchGesture DecodeGesture(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 3)
        {
            throw new ProtocolFormatException("Gestenbelegung: genau [button, pattern, function] erwartet", payload);
        }

        return new TouchGesture(payload[0], payload[1], (TouchFunction)payload[2]);
    }

    /// <summary><c>0x1600</c> ← <c>[button, pattern, function]</c>.</summary>
    public static byte[] EncodeGesture(TouchGesture gesture) => [gesture.Button, gesture.Pattern, (byte)gesture.Function];
}
