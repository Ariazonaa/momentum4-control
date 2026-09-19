// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

namespace Momentum4.Protocol.Features;

/// <summary>Sound-Mode laut <c>0x0803</c>/<c>0x0804</c>, docs/protocol.md §6.3.</summary>
public enum SoundMode : byte
{
    Off = 0,
    Equalizer = 1,

    /// <summary>In [MC] „Podcast“, in [DS] „Speech Clarity“.</summary>
    Podcast = 2,
    SoundPersonalization = 3,
}

/// <summary>Sprache der Ansagen (<c>0x0807</c>), Werte laut [SDC].</summary>
public enum PromptLanguage : byte
{
    English = 0,
    German = 1,
    French = 2,
    Spanish = 3,
    Chinese = 4,
    Japanese = 5,
    Russian = 6,
    Korean = 7,
}

/// <summary>Töne und Sprachansagen (<c>0x0801</c>/<c>0x0802</c>), Werte laut <c>m4.json</c> (<c>Setting_AudioPromptMode</c>).</summary>
public enum AudioPromptMode : byte
{
    /// <summary><c>toneVoiceOff</c>: keine Töne, keine Ansagen.</summary>
    Off = 0,

    /// <summary><c>toneOn</c>: nur Töne.</summary>
    TonesOnly = 1,

    /// <summary><c>toneVoiceOn</c>: Töne und Sprachansagen.</summary>
    TonesAndVoice = 2,
}

/// <summary>Bluetooth-Codec (<c>0x0800</c>/<c>0x0900</c>), Werte laut [SDC]. Der Push <c>0x0880</c> nutzt eine andere Codierung.</summary>
public enum BluetoothCodec : byte
{
    Sbc = 0,
    Aac = 1,
    AptX = 2,
    AptXLowLatency = 3,
    Mp3 = 4,
    AptXHd = 5,
    FastStream = 6,
    Lhdc = 7,
    AptXAdaptive = 8,
    AptXLossless = 9,
    Lc3 = 10,
    None = 255,
}

/// <summary>Decoder für Feature 4 (genericAudio), docs/protocol.md §6.3.</summary>
public static class GenericAudioCodec
{
    /// <summary><c>0x0800</c> → genau 1 Byte (Codec-Enum). Am M4 verifiziert: 5 = aptX HD.</summary>
    public static BluetoothCodec DecodeCodec(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 1)
        {
            throw new ProtocolFormatException("Codec: genau 1 Byte erwartet", payload);
        }

        return (BluetoothCodec)payload[0];
    }

    /// <summary><c>0x0807</c> → genau 1 Byte (Sprach-Enum).</summary>
    public static PromptLanguage DecodePromptLanguage(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 1)
        {
            throw new ProtocolFormatException("Prompt-Sprache: genau 1 Byte erwartet", payload);
        }

        return (PromptLanguage)payload[0];
    }

    /// <summary>Anzeigename der Sprache.</summary>
    public static string LanguageName(PromptLanguage language) => language switch
    {
        PromptLanguage.English => "Englisch",
        PromptLanguage.German => "Deutsch",
        PromptLanguage.French => "Französisch",
        PromptLanguage.Spanish => "Spanisch",
        PromptLanguage.Chinese => "Chinesisch",
        PromptLanguage.Japanese => "Japanisch",
        PromptLanguage.Russian => "Russisch",
        PromptLanguage.Korean => "Koreanisch",
        _ => $"unbekannt ({(byte)language})",
    };

    /// <summary>Anzeigename des Codecs.</summary>
    public static string CodecName(BluetoothCodec codec) => codec switch
    {
        BluetoothCodec.Sbc => "SBC",
        BluetoothCodec.Aac => "AAC",
        BluetoothCodec.AptX => "aptX",
        BluetoothCodec.AptXLowLatency => "aptX Low Latency",
        BluetoothCodec.Mp3 => "MP3",
        BluetoothCodec.AptXHd => "aptX HD",
        BluetoothCodec.FastStream => "FastStream",
        BluetoothCodec.Lhdc => "LHDC",
        BluetoothCodec.AptXAdaptive => "aptX Adaptive",
        BluetoothCodec.AptXLossless => "aptX Lossless",
        BluetoothCodec.Lc3 => "LC3",
        BluetoothCodec.None => "keiner",
        _ => $"unbekannt ({(byte)codec})",
    };

    /// <summary><c>0x0804</c> → genau <c>[0x00, mode]</c>.</summary>
    public static SoundMode DecodeSoundMode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 2 || payload[0] != 0 || payload[1] > 3)
        {
            throw new ProtocolFormatException("Sound-Mode: genau [00, 0…3] erwartet", payload);
        }

        return (SoundMode)payload[1];
    }

    /// <summary><c>0x0802</c> → genau 1 Byte, 0…2.</summary>
    public static AudioPromptMode DecodeAudioPromptMode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 1 || payload[0] > 2)
        {
            throw new ProtocolFormatException("Prompt-Modus: genau 1 Byte mit 0…2 erwartet", payload);
        }

        return (AudioPromptMode)payload[0];
    }

    /// <summary><c>0x0801</c> ← <c>[mode]</c>.</summary>
    public static byte[] EncodeAudioPromptMode(AudioPromptMode mode) =>
        Enum.IsDefined(mode) ? [(byte)mode] : throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unbekannter Prompt-Modus.");

    /// <summary><c>0x0803</c> ← <c>[0x00, mode]</c> ([MC], [DS]; Byte 0 ist immer 0).</summary>
    public static byte[] EncodeSoundMode(SoundMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unbekannter Sound-Mode.");
        }

        return [0x00, (byte)mode];
    }
}
