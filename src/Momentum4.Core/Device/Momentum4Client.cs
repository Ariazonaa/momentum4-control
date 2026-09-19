// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Momentum4.Core.Queue;
using Momentum4.Core.State;
using Momentum4.Protocol;
using Momentum4.Protocol.Catalog;
using Momentum4.Protocol.Features;

namespace Momentum4.Core.Device;

/// <summary>
/// Typisierte Befehle ans Headset: Lesen (Identifikation, Akku, Noise Control, EQ, Multipoint), Notification-
/// Registrierung und – ab Phase E – die ANC-Setter. Die Setter schreiben nur; Verifikation und Zustandsübernahme
/// macht <see cref="Services.Momentum4Service"/>.
/// </summary>
public sealed class Momentum4Client(GaiaCommandQueue queue)
{
    /// <summary>Obergrenze für indizierte Serien (Geräteliste, EQ-Bänder) – Schutz vor unplausiblen Antworten.</summary>
    private const int MaxIndexedItems = 16;

    public async Task<string> GetModelIdAsync(CancellationToken cancellationToken)
    {
        var response = await queue.SendAsync(CommandCatalog.GetModelId, cancellationToken);
        return VersionsCodec.DecodeModelId(response.Payload);
    }

    public async Task<FirmwareVersion> GetFirmwareVersionAsync(CancellationToken cancellationToken)
    {
        var response = await queue.SendAsync(CommandCatalog.GetFirmwareVersion, cancellationToken);
        return VersionsCodec.DecodeFirmwareVersion(response.Payload);
    }

    public async Task<GaiaApiVersion> GetGaiaApiVersionAsync(CancellationToken cancellationToken)
    {
        var response = await queue.SendAsync(CommandCatalog.QcGetApiVersion, cancellationToken);
        return CoreCodec.DecodeApiVersion(response.Payload);
    }

    public async Task<int> GetBatteryLevelAsync(CancellationToken cancellationToken)
    {
        var response = await queue.SendAsync(CommandCatalog.GetBatteryLevel, cancellationToken);
        return BatteryCodec.DecodeLevel(response.Payload);
    }

    /// <summary>Liest <c>0x1A05</c>, <c>0x1A01</c>, <c>0x1A03</c> und <c>0x1805</c>.</summary>
    public async Task<NoiseControlState> ReadNoiseControlAsync(CancellationToken cancellationToken) =>
        (await ReadNoiseControlWithTableAsync(cancellationToken)).State;

    /// <summary>Wie <see cref="ReadNoiseControlAsync"/>, liefert zusätzlich die rohe Modus-Tabelle (für Read-Modify-Write).</summary>
    public async Task<(NoiseControlState State, AncModeTable Table)> ReadNoiseControlWithTableAsync(CancellationToken cancellationToken)
    {
        var enabled = AncCodec.DecodeEnabled((await queue.SendAsync(CommandCatalog.GetAncEnabled, cancellationToken)).Payload);
        var modesPayload = (await queue.SendAsync(CommandCatalog.GetAncModes, cancellationToken)).Payload;
        var modes = AncCodec.DecodeModes(modesPayload);
        var level = AncCodec.DecodeLevel((await queue.SendAsync(CommandCatalog.GetAncLevel, cancellationToken)).Payload);
        var transparentHearing = SwitchCodec.Decode((await queue.SendAsync(CommandCatalog.GetTransparentHearingStatus, cancellationToken)).Payload, "Transparent Hearing");

        if (modes.AntiWind is not { } antiWind || modes.Adaptive is not { } adaptive)
        {
            throw new ProtocolFormatException("ANC-Modi: Anti-Wind oder Adaptive fehlt", modesPayload);
        }

        return (new NoiseControlState(enabled, adaptive, level, antiWind, transparentHearing), modes);
    }

    /// <summary><c>0x1A04</c>: ANC ein/aus.</summary>
    public Task SetAncEnabledAsync(bool enabled, CancellationToken cancellationToken) =>
        queue.SendAsync(CommandCatalog.SetAncEnabled, AncCodec.EncodeEnabled(enabled), cancellationToken);

    /// <summary><c>0x1A02</c>: Pegel 0…100 (gilt im Modus Custom).</summary>
    public Task SetAncLevelAsync(int level, CancellationToken cancellationToken) =>
        queue.SendAsync(CommandCatalog.SetAncLevel, AncCodec.EncodeLevel(level), cancellationToken);

    /// <summary><c>0x1A00</c>: einen Wert der Modus-Tabelle setzen (Anti-Wind oder Adaptive).</summary>
    public Task SetAncModeAsync(AncModeTable current, byte modeId, byte value, AncModeWriteFormat format, CancellationToken cancellationToken) =>
        queue.SendAsync(CommandCatalog.SetAncMode, AncCodec.EncodeMode(current, modeId, value, format), cancellationToken);

    /// <summary><c>0x1804</c>: Transparent Hearing ein/aus (Seiteneffekte auf den Pegel: docs/protocol.md §6.8).</summary>
    public Task SetTransparentHearingStatusAsync(bool on, CancellationToken cancellationToken) =>
        queue.SendAsync(CommandCatalog.SetTransparentHearingStatus, SwitchCodec.Encode(on), cancellationToken);

    /// <summary>
    /// Liest Konfiguration (<c>0x1000</c>), alle Gains in einem Request (<c>0x1003</c>), Bass Boost (<c>0x1009</c>, nur wenn
    /// <paramref name="readBassBoost"/>) und Sound-Mode (<c>0x0804</c>). Frequenzen (<c>0x100B</c>, ein Request je Band)
    /// werden nur gelesen, wenn <paramref name="knownFrequencies"/> sie nicht schon liefert – sie ändern sich nicht.
    /// </summary>
    public async Task<EqualizerState> ReadEqualizerAsync(IReadOnlyList<int>? knownFrequencies, bool readBassBoost, CancellationToken cancellationToken)
    {
        var config = UserEqCodec.DecodeConfig((await queue.SendAsync(CommandCatalog.GetEqConfig, cancellationToken)).Payload);
        var bandCount = Math.Min(config.BandCount, MaxIndexedItems);
        var gains = await ReadEqGainsAsync(bandCount, cancellationToken);

        var frequencies = knownFrequencies is { } known && known.Count == bandCount ? known : await ReadBandFrequenciesAsync(bandCount, cancellationToken);

        bool? bassBoost = readBassBoost ? await ReadBassBoostAsync(cancellationToken) : null;
        var soundMode = await ReadSoundModeAsync(cancellationToken);

        var bands = Enumerable.Range(0, bandCount).Select(i => new EqBand(i, gains[i], frequencies[i])).ToList();
        return new EqualizerState(bands, config.MinGainDb, config.MaxGainDb, bassBoost, soundMode);
    }

    /// <summary><c>0x1003 [00]</c>: alle Gains in einem Request.</summary>
    public async Task<decimal[]> ReadEqGainsAsync(int bandCount, CancellationToken cancellationToken) =>
        UserEqCodec.DecodeAllGains((await queue.SendAsync(CommandCatalog.GetEqAllGains, new byte[] { 0x00 }, cancellationToken)).Payload, bandCount);

    /// <summary><c>0x1009</c>: Bass Boost.</summary>
    public async Task<bool> ReadBassBoostAsync(CancellationToken cancellationToken) =>
        UserEqCodec.DecodeBassBoost((await queue.SendAsync(CommandCatalog.GetBassBoost, cancellationToken)).Payload);

    /// <summary><c>0x0804</c>: Sound-Mode.</summary>
    public async Task<SoundMode> ReadSoundModeAsync(CancellationToken cancellationToken) =>
        GenericAudioCodec.DecodeSoundMode((await queue.SendAsync(CommandCatalog.GetSoundMode, cancellationToken)).Payload);

    /// <summary><c>0x0803</c>: Sound-Mode setzen.</summary>
    public Task SetSoundModeAsync(SoundMode mode, CancellationToken cancellationToken) =>
        queue.SendAsync(CommandCatalog.SetSoundMode, GenericAudioCodec.EncodeSoundMode(mode), cancellationToken);

    /// <summary><c>0x1001</c>: Gain eines Bands (0-basiert) in dB.</summary>
    public Task SetEqBandAsync(int band, decimal gainDb, CancellationToken cancellationToken) =>
        queue.SendAsync(CommandCatalog.SetEqBand, UserEqCodec.EncodeBandGain(band, gainDb), cancellationToken);

    /// <summary><c>0x1008</c>: Bass Boost ein/aus.</summary>
    public Task SetBassBoostAsync(bool enabled, CancellationToken cancellationToken) =>
        queue.SendAsync(CommandCatalog.SetBassBoost, UserEqCodec.EncodeBassBoost(enabled), cancellationToken);

    /// <summary>Liest <c>0x1400</c>, <c>0x1401</c> je Eintrag, <c>0x1407</c> und <c>0x1409</c>.</summary>
    public async Task<MultipointState> ReadMultipointAsync(CancellationToken cancellationToken)
    {
        var count = Math.Min(DeviceManagementCodec.DecodeCount((await queue.SendAsync(CommandCatalog.GetPairedDeviceCount, cancellationToken)).Payload), MaxIndexedItems);
        var ownIndex = DeviceManagementCodec.DecodeOwnIndex((await queue.SendAsync(CommandCatalog.GetOwnDeviceIndex, cancellationToken)).Payload);
        var maxConnections = DeviceManagementCodec.DecodeMaxConnections((await queue.SendAsync(CommandCatalog.GetMaxConnections, cancellationToken)).Payload);

        var devices = new List<PeerDevice>();
        for (var index = 0; index < count; index++)
        {
            var payload = (await queue.SendAsync(CommandCatalog.GetPairedDeviceInfo, new[] { (byte)index }, cancellationToken)).Payload;
            if (payload.Length == 0)
            {
                // Regel aus docs/protocol.md §9: leeres OK auf einen indizierten Request = Aktion, sofort aufhören.
                throw new ProtocolFormatException($"Geräteeintrag {index}: leere Antwort", payload);
            }

            var entry = DeviceManagementCodec.DecodeEntry(payload, index);
            devices.Add(new PeerDevice(entry.Index, entry.Name, entry.IsConnected, entry.Index == ownIndex));
        }

        return new MultipointState(devices, maxConnections, ownIndex);
    }

    /// <summary><c>0x1404 [index]</c>: ist das gekoppelte Gerät gerade verbunden?</summary>
    public async Task<bool> GetPeerConnectedAsync(int index, CancellationToken cancellationToken) =>
        DeviceManagementCodec.DecodeConnectionStatus(
            (await queue.SendAsync(CommandCatalog.GetPairedDeviceStatus, DeviceManagementCodec.EncodeIndex(index), cancellationToken)).Payload, index);

    /// <summary><c>0x1402 [index]</c>: gekoppeltes Gerät verbinden (Action, wird nie automatisch wiederholt).</summary>
    public Task ConnectPeerAsync(int index, CancellationToken cancellationToken) =>
        queue.SendAsync(CommandCatalog.ConnectPairedDevice, DeviceManagementCodec.EncodeIndex(index), cancellationToken);

    /// <summary>
    /// On-Head-Erkennung, Smart Pause, Auto-Answer, Comfort Call und Touch-Sperre lesen (<c>0x0401</c>, <c>0x080D</c>,
    /// <c>0x080B</c>, <c>0x0815</c>, <c>0x1607</c>). Die Touch-Sperre ist invertiert: 0 = Touch aktiv.
    /// </summary>
    public async Task<BehaviorState> ReadBehaviorAsync(CancellationToken cancellationToken) =>
        new(
            await ReadSwitchAsync(CommandCatalog.GetOnHeadDetection, "On-Head-Erkennung", cancellationToken),
            await ReadSwitchAsync(CommandCatalog.GetSmartPause, "Smart Pause", cancellationToken),
            await ReadSwitchAsync(CommandCatalog.GetAutoAnswer, "Auto-Answer", cancellationToken),
            await ReadSwitchAsync(CommandCatalog.GetComfortCall, "Comfort Call", cancellationToken),
            !await ReadSwitchAsync(CommandCatalog.GetTouchLock, "Touch-Sperre", cancellationToken));

    /// <summary>Eine der Einstellungen schreiben (<c>[00]</c>/<c>[01]</c>, Touch invertiert); Verifikation macht der Service.</summary>
    public Task SetBehaviorAsync(BehaviorSetting setting, bool on, CancellationToken cancellationToken) =>
        queue.SendAsync(SetterFor(setting), SwitchCodec.Encode(setting == BehaviorSetting.TouchControl ? !on : on), cancellationToken);

    /// <summary>Auto Power Off in Sekunden lesen (<c>0x0601 [00]</c>), 0 = nie.</summary>
    public async Task<int> ReadAutoPowerOffAsync(CancellationToken cancellationToken) =>
        BatteryCodec.DecodeTimerSeconds(
            (await queue.SendAsync(CommandCatalog.GetTimer, new[] { BatteryCodec.TimerAutoPowerOff }, cancellationToken)).Payload,
            BatteryCodec.TimerAutoPowerOff);

    /// <summary>Auto Power Off schreiben (<c>0x0600 [00, s u16]</c>); Verifikation macht der Service.</summary>
    public Task SetAutoPowerOffAsync(int seconds, CancellationToken cancellationToken) =>
        queue.SendAsync(CommandCatalog.SetTimer, BatteryCodec.EncodeTimer(BatteryCodec.TimerAutoPowerOff, seconds), cancellationToken);

    /// <summary>Töne und Sprachansagen lesen (<c>0x0802</c>).</summary>
    public async Task<AudioPromptMode> ReadAudioPromptModeAsync(CancellationToken cancellationToken) =>
        GenericAudioCodec.DecodeAudioPromptMode((await queue.SendAsync(CommandCatalog.GetAudioPromptMode, cancellationToken)).Payload);

    /// <summary>Töne und Sprachansagen schreiben (<c>0x0801 [mode]</c>); Verifikation macht der Service.</summary>
    public Task SetAudioPromptModeAsync(AudioPromptMode mode, CancellationToken cancellationToken) =>
        queue.SendAsync(CommandCatalog.SetAudioPromptMode, GenericAudioCodec.EncodeAudioPromptMode(mode), cancellationToken);

    /// <summary>BT-Kompatibilitätsmodus lesen (<c>0x0406</c>).</summary>
    public async Task<BtCompatibilityMode> ReadBtCompatibilityModeAsync(CancellationToken cancellationToken) =>
        DeviceCodec.DecodeBtCompatibilityMode((await queue.SendAsync(CommandCatalog.GetBtCompatibilityMode, cancellationToken)).Payload);

    /// <summary>BT-Kompatibilitätsmodus schreiben (<c>0x0405</c>).</summary>
    public Task SetBtCompatibilityModeAsync(BtCompatibilityMode mode, CancellationToken cancellationToken) =>
        queue.SendAsync(CommandCatalog.SetBtCompatibilityMode, DeviceCodec.EncodeBtCompatibilityMode(mode), cancellationToken);

    /// <summary>Gerätenamen lesen (localName, <c>0x2802</c>) – roher UTF-8.</summary>
    public async Task<string> ReadDeviceNameAsync(CancellationToken cancellationToken) =>
        System.Text.Encoding.UTF8.GetString((await queue.SendAsync(CommandCatalog.GetLocalName, cancellationToken)).Payload).TrimEnd('\0');

    /// <summary>Gerätenamen setzen (<c>0x2801</c>, roher UTF-8); Verifikation macht der Service.</summary>
    public Task SetDeviceNameAsync(string name, CancellationToken cancellationToken) =>
        queue.SendAsync(CommandCatalog.SetLocalName, System.Text.Encoding.UTF8.GetBytes(name), cancellationToken);

    /// <summary>Seriennummer lesen (<c>001D/0x0003</c>).</summary>
    public async Task<string> ReadSerialNumberAsync(CancellationToken cancellationToken) =>
        CoreCodec.DecodeSerialNumber((await queue.SendAsync(CommandCatalog.QcGetSerialNumber, cancellationToken)).Payload);

    /// <summary>Sprache der Ansagen lesen (<c>0x0807</c>).</summary>
    public async Task<PromptLanguage> ReadPromptLanguageAsync(CancellationToken cancellationToken) =>
        GenericAudioCodec.DecodePromptLanguage((await queue.SendAsync(CommandCatalog.GetPromptLanguage, cancellationToken)).Payload);

    /// <summary>Automatische Pause bei Transparenz lesen (<c>0x1801</c>): 1 = Musik anhalten.</summary>
    public async Task<bool> ReadAutoPauseAsync(CancellationToken cancellationToken) =>
        SwitchCodec.Decode((await queue.SendAsync(CommandCatalog.GetTransparentHearingMode, cancellationToken)).Payload, "Automatische Pause");

    /// <summary>Automatische Pause setzen (<c>0x1800</c>); Verifikation macht der Service.</summary>
    public Task SetAutoPauseAsync(bool on, CancellationToken cancellationToken) =>
        queue.SendAsync(CommandCatalog.SetTransparentHearingMode, SwitchCodec.Encode(on), cancellationToken);

    /// <summary>Aktuellen Bluetooth-Codec lesen (<c>0x0800</c>).</summary>
    public async Task<BluetoothCodec> ReadCodecAsync(CancellationToken cancellationToken) =>
        GenericAudioCodec.DecodeCodec((await queue.SendAsync(CommandCatalog.GetCodec, cancellationToken)).Payload);

    /// <summary>Tragezustand lesen (<c>0x0402</c>).</summary>
    public async Task<WearState> ReadWearStateAsync(CancellationToken cancellationToken) =>
        DeviceCodec.DecodeWearState((await queue.SendAsync(CommandCatalog.GetPhysicalState, cancellationToken)).Payload);

    public static CommandDescriptor SetterFor(BehaviorSetting setting) => setting switch
    {
        BehaviorSetting.OnHeadDetection => CommandCatalog.SetOnHeadDetection,
        BehaviorSetting.SmartPause => CommandCatalog.SetSmartPause,
        BehaviorSetting.AutoAnswer => CommandCatalog.SetAutoAnswer,
        BehaviorSetting.ComfortCall => CommandCatalog.SetComfortCall,
        BehaviorSetting.TouchControl => CommandCatalog.SetTouchLock,
        _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, null),
    };

    /// <summary>Meldet das Headset für Notifications eines Features an (<c>0x0495/0x0007</c>).</summary>
    public async Task RegisterNotificationsAsync(byte featureId, CancellationToken cancellationToken) =>
        await queue.SendAsync(CommandCatalog.RegisterNotification, new[] { featureId }, cancellationToken);

    private async Task<bool> ReadSwitchAsync(CommandDescriptor getter, string what, CancellationToken cancellationToken) =>
        SwitchCodec.Decode((await queue.SendAsync(getter, cancellationToken)).Payload, what);

    private async Task<IReadOnlyList<int>> ReadBandFrequenciesAsync(int bandCount, CancellationToken cancellationToken)
    {
        var frequencies = new int[bandCount];
        for (var band = 0; band < bandCount; band++)
        {
            var payload = (await queue.SendAsync(CommandCatalog.GetEqBandFrequency, new[] { (byte)band }, cancellationToken)).Payload;
            if (payload.Length == 0)
            {
                throw new ProtocolFormatException($"EQ-Frequenz Band {band}: leere Antwort", payload);
            }

            frequencies[band] = UserEqCodec.DecodeBandFrequency(payload, band);
        }

        return frequencies;
    }
}
