// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using Momentum4.Protocol.Catalog;
using Momentum4.Protocol.Features;
using Momentum4.Protocol.Gaia;

namespace Momentum4.Protocol.Tests;

/// <summary>
/// Spielt die kommentierten Mitschnitte vom echten Headset (tests/CapturedPackets) gegen Encoder, Reader,
/// Katalog und Decoder ab. Eine Datei enthält entweder einen Austausch (<c>exchange</c> + <c>expected</c>) oder
/// mehrere (<c>steps</c>), optional dazu unaufgeforderte Meldungen (<c>notifications</c>).
/// </summary>
public sealed class CapturedPacketTests
{
    private static readonly string CaptureDirectory = Path.Combine(AppContext.BaseDirectory, "CapturedPackets");

    public static TheoryData<string> CaptureFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(CaptureDirectory, "*.json").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    [Fact]
    public void Captures_are_present()
    {
        Assert.True(Directory.Exists(CaptureDirectory) && Directory.EnumerateFiles(CaptureDirectory, "*.json").Any(), "Keine Mitschnitte gefunden.");
    }

    [Theory]
    [MemberData(nameof(CaptureFiles))]
    public void Capture_replays_through_codec_and_decoder(string fileName)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(CaptureDirectory, fileName)));
        var root = json.RootElement;

        // Keine unkommentierten Hex-Dumps (Spezifikation §29)
        foreach (var field in new[] { "title", "date", "firmware", "action", "reference" })
        {
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty(field).GetString()), $"{fileName}: Feld '{field}' fehlt");
        }

        Assert.True(root.GetProperty("anonymized").GetBoolean(), $"{fileName}: nicht als anonymisiert markiert");

        if (root.TryGetProperty("steps", out var steps))
        {
            var count = 0;
            foreach (var step in steps.EnumerateArray())
            {
                Verify(
                    $"{fileName} / {step.GetProperty("command").GetString()}",
                    step.GetProperty("tx").GetString()!,
                    step.GetProperty("rx").GetString()!,
                    step.GetProperty("decoder").GetString()!,
                    step.GetProperty("expected").GetString()!);
                count++;
            }

            if (root.TryGetProperty("notifications", out var notifications))
            {
                foreach (var n in notifications.EnumerateArray())
                {
                    VerifyNotification(
                        $"{fileName} / {n.GetProperty("id").GetString()}",
                        n.GetProperty("rx").GetString()!,
                        n.GetProperty("decoder").GetString()!,
                        n.GetProperty("expected").GetString()!);
                    count++;
                }
            }

            // Ein Mitschnitt darf nur aus Notifications bestehen (z. B. eine Touch-Geste am Headset), aber nie leer sein.
            Assert.True(count > 0, $"{fileName}: weder Schritte noch Notifications");
        }
        else
        {
            var exchange = root.GetProperty("exchange").EnumerateArray().ToArray();
            var expected = root.GetProperty("expected");
            Verify(
                fileName,
                exchange.Single(e => e.GetProperty("dir").GetString() == "TX").GetProperty("hex").GetString()!,
                exchange.Single(e => e.GetProperty("dir").GetString() == "RX").GetProperty("hex").GetString()!,
                expected.GetProperty("decoder").GetString()!,
                expected.GetProperty("value").GetString()!);
        }
    }

    private static void Verify(string context, string txHex, string rxHex, string decoder, string expected)
    {
        var tx = Hex.Parse(txHex);
        var rx = Hex.Parse(rxHex);

        // TX: genau ein Frame, stammt aus dem Katalog und wird von unserem Encoder identisch erzeugt
        var request = Assert.Single(new GaiaFrameReader().Push(tx).Frames);
        var descriptor = CommandCatalog.Find(request.Vendor, request.Command);
        Assert.True(descriptor is not null, $"{context}: TX nicht im Katalog");
        Assert.Equal(descriptor.Command, request.Command);
        Assert.Equal(tx, GaiaFrameCodec.Encode(descriptor.Vendor, descriptor.Command, request.Payload));
        Assert.True(descriptor.RequestLength is null || descriptor.RequestLength == request.Payload.Length, $"{context}: Request-Länge passt nicht zum Katalog");

        // RX: genau ein Frame, ohne Auffälligkeiten, Antwort auf genau diesen Request
        var read = new GaiaFrameReader().Push(rx);
        Assert.Empty(read.Diagnostics);
        var response = Assert.Single(read.Frames);
        Assert.Equal(PacketType.Response, response.Type);
        Assert.Equal(request.Vendor, response.Vendor);
        Assert.Equal(request.Command, response.Command.AsCommand());
        Assert.Equal(0, response.Flags);

        var arg = request.Payload.Length > 0 ? request.Payload[0] : 0;
        var decoded = Decode(context, decoder, response.Payload, arg);
        Assert.True(expected == decoded, $"{context}: erwartet '{expected}', dekodiert '{decoded}'");
    }

    /// <summary>Notification: ein Frame vom Typ Notification, dessen Command im Katalog steht, Payload wie erwartet.</summary>
    private static void VerifyNotification(string context, string rxHex, string decoder, string expected)
    {
        var read = new GaiaFrameReader().Push(Hex.Parse(rxHex));
        Assert.Empty(read.Diagnostics);
        var frame = Assert.Single(read.Frames);
        Assert.Equal(PacketType.Notification, frame.Type);
        Assert.Equal(0, frame.Flags);
        Assert.True(CommandCatalog.Find(frame.Vendor, frame.Command) is not null, $"{context}: zugehöriger Command fehlt im Katalog");

        var decoded = Decode(context, decoder, frame.Payload, 0);
        Assert.True(expected == decoded, $"{context}: erwartet '{expected}', dekodiert '{decoded}'");
    }

    private static string Decode(string context, string decoder, byte[] p, int arg)
    {
        return decoder switch
        {
            "modelId" => VersionsCodec.DecodeModelId(p),
            "firmwareVersion" => VersionsCodec.DecodeFirmwareVersion(p).ToString(),
            "gaiaApiVersion" => CoreCodec.DecodeApiVersion(p).ToString(),
            "battery" => BatteryCodec.DecodeLevel(p).ToString(CultureInfo.InvariantCulture),
            "ancEnabled" => AncCodec.DecodeEnabled(p).ToString(),
            "ancModes" => Describe(AncCodec.DecodeModes(p)),
            "ancLevel" => AncCodec.DecodeLevel(p).ToString(CultureInfo.InvariantCulture),
            "switch" => SwitchCodec.Decode(p, context).ToString(),
            "soundMode" => GenericAudioCodec.DecodeSoundMode(p).ToString(),
            "btCompatibilityMode" => DeviceCodec.DecodeBtCompatibilityMode(p).ToString(),
            "personalizationState" => DeviceCodec.DecodePersonalizationState(p).ToString(),
            "wearState" => DeviceCodec.DecodeWearState(p).ToString(),
            "audioPromptMode" => GenericAudioCodec.DecodeAudioPromptMode(p).ToString(),
            "codec" => GenericAudioCodec.DecodeCodec(p).ToString(),
            "promptLanguage" => GenericAudioCodec.DecodePromptLanguage(p).ToString(),
            "serialNumber" => CoreCodec.DecodeSerialNumber(p),
            "eqConfig" => Describe(UserEqCodec.DecodeConfig(p)),
            "eqBandGain" => Db(UserEqCodec.DecodeBandGain(p, arg)),
            "eqAllGains" => string.Join(' ', UserEqCodec.DecodeAllGains(p, p.Length).Select(Db)),
            "eqBandFrequency" => UserEqCodec.DecodeBandFrequency(p, arg).ToString(CultureInfo.InvariantCulture),
            "pairedDeviceCount" => DeviceManagementCodec.DecodeCount(p).ToString(CultureInfo.InvariantCulture),
            "pairedDeviceEntry" => Describe(DeviceManagementCodec.DecodeEntry(p, arg)),
            "ownDeviceIndex" => DeviceManagementCodec.DecodeOwnIndex(p).ToString(CultureInfo.InvariantCulture),
            "maxConnections" => DeviceManagementCodec.DecodeMaxConnections(p).ToString(CultureInfo.InvariantCulture),
            "timerSeconds" => BatteryCodec.DecodeTimerSeconds(p, (byte)arg).ToString(CultureInfo.InvariantCulture),
            "bandU16List" => string.Join(' ', UserEqCodec.DecodeBandValueList(p, 2).Select(e => e.Value)),
            "bandU8List" => string.Join(' ', UserEqCodec.DecodeBandValueList(p, 1).Select(e => e.Value)),
            "empty" => p.Length == 0 ? string.Empty : throw new InvalidOperationException($"{context}: Payload nicht leer"),
            "raw" => Hex.Format(p),
            var other => throw new InvalidOperationException($"{context}: unbekannter Decoder '{other}'"),
        };
    }

    private static string Db(decimal value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    private static string Describe(AncModeTable t) =>
        $"AntiWind={t.AntiWind}, Comfort={t.Comfort}, Adaptive={t.Adaptive}";

    private static string Describe(EqConfig c) =>
        $"{c.BandCount} bands, {Db(c.MinGainDb)}..{Db(c.MaxGainDb)} dB, extra [{Hex.Format(c.Extra)}]";

    private static string Describe(PairedDeviceEntry e) =>
        $"#{e.Index} '{e.Name}' prio {e.Priority} status {e.ConnectionStatus}";
}
