// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Collections.Frozen;
using Momentum4.Protocol.Gaia;
using static Momentum4.Protocol.Catalog.ProtocolStatus;
using static Momentum4.Protocol.Catalog.SafetyClass;

namespace Momentum4.Protocol.Catalog;

/// <summary>
/// Allowlist aller bekannten Commands mit Sicherheitsklasse und Teststatus. Spiegelt die Tabellen in
/// docs/protocol.md §5, §6, §6.12 und §9 – ein Test prüft, dass beide übereinstimmen.
/// Wer einen Status ändert, ändert ihn an beiden Stellen.
/// Verifiziert auf Hardware (FW 3.37.3, 2026-09-18/19): Stufe 2/3 (Identifikation, Akku), Stufe 4 (alle Getter des Zustands),
/// Stufe 5 (Notification-Anmeldung) und Stufe 6–8 (ANC-Tabelle, Pegel, Transparent Hearing schreiben).
/// </summary>
public static class CommandCatalog
{
    private const ushort Q = ProtocolConstants.VendorQualcomm;
    private const ushort S = ProtocolConstants.VendorSennheiser;

    // ---- Vendor 0x001D (Qualcomm) --------------------------------------------------------------
    public static readonly CommandDescriptor QcGetApiVersion = New("GetApiVersion", Q, 0x0000, Read, Verified, 0);
    public static readonly CommandDescriptor QcGetSupportedFeatures = New("GetSupportedFeatures", Q, 0x0001, Read, Verified, 0);
    public static readonly CommandDescriptor QcGetSupportedFeaturesNext = New("GetSupportedFeaturesNext", Q, 0x0002, Read, NeedsHardwareTest, 0);
    public static readonly CommandDescriptor QcGetSerialNumber = New("GetSerialNumber", Q, 0x0003, Read, Verified, 0);
    public static readonly CommandDescriptor QcRegisterNotification = New("RegisterNotification", Q, 0x0007, Setting, Unverified, 1);
    public static readonly CommandDescriptor QcDataTransferSetup = New("DataTransferSetup", Q, 0x0009, Blocked, ProtocolStatus.Unknown);
    public static readonly CommandDescriptor QcDataTransferGet = New("DataTransferGet", Q, 0x000A, Blocked, ProtocolStatus.Unknown);
    public static readonly CommandDescriptor QcGetTransportInfo = New("GetTransportInfo", Q, 0x000C, Read, Unverified, 1);
    public static readonly CommandDescriptor QcSetTransportParameter = New("SetTransportParameter", Q, 0x000D, Blocked, ProtocolStatus.Unknown);
    public static readonly CommandDescriptor QcGetVoiceAssistant = New("GetVoiceAssistant", Q, 0x0600, Read, Unverified, 0);
    public static readonly CommandDescriptor QcSetVoiceAssistant = New("SetVoiceAssistant", Q, 0x0601, Blocked, Unverified);
    public static readonly CommandDescriptor QcGetSupportedVoiceAssistants = New("GetSupportedVoiceAssistants", Q, 0x0602, Read, Unverified, 0);
    public static readonly CommandDescriptor QcGetPanicLogInfo = New("GetPanicLogInfo", Q, 0x0803, Blocked, ProtocolStatus.Unknown);
    public static readonly CommandDescriptor QcErasePanicLog = New("ErasePanicLog", Q, 0x0804, Blocked, ProtocolStatus.Unknown);
    public static readonly CommandDescriptor QcUpgradeConnect = New("UpgradeConnect", Q, 0x0C00, Blocked, ProtocolStatus.Unknown);
    public static readonly CommandDescriptor QcUpgradeDisconnect = New("UpgradeDisconnect", Q, 0x0C01, Blocked, ProtocolStatus.Unknown);
    public static readonly CommandDescriptor QcUpgradeControl = New("UpgradeControl", Q, 0x0C02, Blocked, ProtocolStatus.Unknown);
    public static readonly CommandDescriptor QcStatisticsGetCategories = New("StatisticsGetCategories", Q, 0x1800, Read, Unverified);
    public static readonly CommandDescriptor QcStatisticsGetAll = New("StatisticsGetAll", Q, 0x1801, Read, Unverified);
    public static readonly CommandDescriptor QcStatisticsGetValues = New("StatisticsGetValues", Q, 0x1802, Read, Unverified);

    // ---- Vendor 0x0495 (Sennheiser) – core (Feature 0) und upgrade (Feature 1) ----------------
    public static readonly CommandDescriptor GetSupportedFeatures = New("GetSupportedFeatures", S, 0x0001, Read, Verified, 0);
    public static readonly CommandDescriptor GetSupportedFeaturesNext = New("GetSupportedFeaturesNext", S, 0x0002, Read, NeedsHardwareTest, 0);
    public static readonly CommandDescriptor RegisterNotification = New("RegisterNotification", S, 0x0007, Setting, Verified, 1);
    public static readonly CommandDescriptor FactoryReset = New("FactoryReset", S, 0x0040, Blocked, ProtocolStatus.Unknown);
    public static readonly CommandDescriptor GetPrimarySide = New("GetPrimarySide", S, 0x0041, Read, Unverified, 0);
    public static readonly CommandDescriptor UpgradeEnable = New("UpgradeEnable", S, 0x0200, Blocked, ProtocolStatus.Unknown);
    public static readonly CommandDescriptor UpgradeFileSize = New("UpgradeFileSize", S, 0x0201, Blocked, ProtocolStatus.Unknown);

    // ---- localName (Feature 20) – am M4 verifiziert 2026-09-19 (Getter 0x2802 = Gerätename) ----
    public static readonly CommandDescriptor SetLocalName = New("SetLocalName", S, 0x2801, Setting, Verified);
    public static readonly CommandDescriptor GetLocalName = New("GetLocalName", S, 0x2802, Read, Verified, 0);

    // ---- device (Feature 2) ----
    public static readonly CommandDescriptor SetOnHeadDetection = New("SetOnHeadDetection", S, 0x0400, Setting, Verified, 1);
    public static readonly CommandDescriptor GetOnHeadDetection = New("GetOnHeadDetection", S, 0x0401, Read, Verified, 0);
    public static readonly CommandDescriptor GetPhysicalState = New("GetPhysicalState", S, 0x0402, Read, Verified, 0);
    public static readonly CommandDescriptor SetBtCompatibilityMode = New("SetBtCompatibilityMode", S, 0x0405, Setting, Verified, 1);
    public static readonly CommandDescriptor GetBtCompatibilityMode = New("GetBtCompatibilityMode", S, 0x0406, Read, Verified, 0);

    // ---- battery / power (Feature 3) ----
    public static readonly CommandDescriptor SetTimer = New("SetTimer", S, 0x0600, Setting, Verified, 3);
    public static readonly CommandDescriptor GetTimer = New("GetTimer", S, 0x0601, Read, Verified, 1);
    public static readonly CommandDescriptor GetChargingState = New("GetChargingState", S, 0x0602, Read, Verified, 0);
    public static readonly CommandDescriptor GetBatteryLevel = New("GetBatteryLevel", S, 0x0603, Read, Verified, 0, TimeSpan.FromSeconds(9));
    public static readonly CommandDescriptor Unknown0607 = New("Unknown0607", S, 0x0607, Blocked, ProtocolStatus.Unknown);
    public static readonly CommandDescriptor Unknown0613 = New("Unknown0613", S, 0x0613, Blocked, ProtocolStatus.Unknown);
    public static readonly CommandDescriptor Unknown0685 = New("Unknown0685", S, 0x0685, Blocked, ProtocolStatus.Unknown);

    // ---- genericAudio (Feature 4) ----
    public static readonly CommandDescriptor GetCodec = New("GetCodec", S, 0x0800, Read, Verified, 0);
    public static readonly CommandDescriptor SetAudioPromptMode = New("SetAudioPromptMode", S, 0x0801, Setting, Verified, 1);
    public static readonly CommandDescriptor GetAudioPromptMode = New("GetAudioPromptMode", S, 0x0802, Read, Verified, 0);
    public static readonly CommandDescriptor SetSoundMode = New("SetSoundMode", S, 0x0803, Setting, Verified, 2);
    public static readonly CommandDescriptor GetSoundMode = New("GetSoundMode", S, 0x0804, Read, Verified, 0);
    public static readonly CommandDescriptor SetSidetone = New("SetSidetone", S, 0x0805, Setting, Unverified, 1);
    public static readonly CommandDescriptor GetSidetone = New("GetSidetone", S, 0x0806, Read, Verified, 0);
    public static readonly CommandDescriptor GetPromptLanguage = New("GetPromptLanguage", S, 0x0807, Read, Verified, 0);
    public static readonly CommandDescriptor GetAvailablePromptLanguages = New("GetAvailablePromptLanguages", S, 0x0808, Read, Verified, 0);
    public static readonly CommandDescriptor GetPromptLanguageVersions = New("GetPromptLanguageVersions", S, 0x0809, Read, Verified, 1);
    public static readonly CommandDescriptor SetAutoAnswer = New("SetAutoAnswer", S, 0x080A, Setting, Verified, 1);
    public static readonly CommandDescriptor GetAutoAnswer = New("GetAutoAnswer", S, 0x080B, Read, Verified, 0);
    public static readonly CommandDescriptor SetSmartPause = New("SetSmartPause", S, 0x080C, Setting, Verified, 1);
    public static readonly CommandDescriptor GetSmartPause = New("GetSmartPause", S, 0x080D, Read, Verified, 0);
    public static readonly CommandDescriptor Unknown0813 = New("Unknown0813", S, 0x0813, SafetyClass.Unknown, ProtocolStatus.Unknown);
    public static readonly CommandDescriptor SetComfortCall = New("SetComfortCall", S, 0x0814, Setting, Verified, 1);
    public static readonly CommandDescriptor GetComfortCall = New("GetComfortCall", S, 0x0815, Read, Verified, 0);
    public static readonly CommandDescriptor SetLowLatency = New("SetLowLatency", S, 0x0817, Blocked, Unverified);
    public static readonly CommandDescriptor GetLowLatency = New("GetLowLatency", S, 0x0818, Read, Unverified, 0);
    public static readonly CommandDescriptor Unknown0819 = New("Unknown0819", S, 0x0819, SafetyClass.Unknown, ProtocolStatus.Unknown);

    // ---- userEQ (Feature 8) ----
    public static readonly CommandDescriptor GetEqConfig = New("GetEqConfig", S, 0x1000, Read, Verified, 0);
    public static readonly CommandDescriptor SetEqBand = New("SetEqBand", S, 0x1001, Setting, Verified, 2);
    public static readonly CommandDescriptor GetEqBand = New("GetEqBand", S, 0x1002, Read, Verified, 1);
    public static readonly CommandDescriptor GetEqAllGains = New("GetEqAllGains", S, 0x1003, Read, Verified, 1);
    public static readonly CommandDescriptor SetBassBoost = New("SetBassBoost", S, 0x1008, Setting, Verified, 1);
    public static readonly CommandDescriptor GetBassBoost = New("GetBassBoost", S, 0x1009, Read, Verified, 0);
    public static readonly CommandDescriptor GetEqBandFrequency = New("GetEqBandFrequency", S, 0x100B, Read, Verified, 1);
    public static readonly CommandDescriptor GetEqBandQ = New("GetEqBandQ", S, 0x100D, Read, Unverified, 1);
    public static readonly CommandDescriptor GetEqBandFilterType = New("GetEqBandFilterType", S, 0x100F, Read, Unverified, 1);
    public static readonly CommandDescriptor Unknown1011 = New("Unknown1011", S, 0x1011, SafetyClass.Unknown, ProtocolStatus.Unknown);
    public static readonly CommandDescriptor Unknown1013 = New("Unknown1013", S, 0x1013, SafetyClass.Unknown, ProtocolStatus.Unknown);

    // ---- versions (Feature 9) ----
    public static readonly CommandDescriptor GetHwRevision = New("GetHwRevision", S, 0x1200, Read, Unverified, 0);
    public static readonly CommandDescriptor GetFirmwareVersion = New("GetFirmwareVersion", S, 0x1201, Read, Verified, 0, TimeSpan.FromSeconds(10));
    public static readonly CommandDescriptor GetFirmwareVersions = New("GetFirmwareVersions", S, 0x1202, Read, Unverified, 0);
    public static readonly CommandDescriptor GetModelId = New("GetModelId", S, 0x1206, Read, Verified, 0);

    // ---- deviceManagement (Feature 10) ----
    public static readonly CommandDescriptor GetPairedDeviceCount = New("GetPairedDeviceCount", S, 0x1400, Read, Verified, 0, TimeSpan.FromSeconds(9));
    public static readonly CommandDescriptor GetPairedDeviceInfo = New("GetPairedDeviceInfo", S, 0x1401, Read, Verified, 1);
    public static readonly CommandDescriptor ConnectPairedDevice = New("ConnectPairedDevice", S, 0x1402, SafetyClass.Action, Verified, 1);
    public static readonly CommandDescriptor DisconnectPairedDevice = New("DisconnectPairedDevice", S, 0x1403, Blocked, ProtocolStatus.Unknown);
    public static readonly CommandDescriptor GetPairedDeviceStatus = New("GetPairedDeviceStatus", S, 0x1404, Read, Verified, 1);
    public static readonly CommandDescriptor DeletePairedDevice = New("DeletePairedDevice", S, 0x1405, Blocked, ProtocolStatus.Unknown);
    public static readonly CommandDescriptor DeletePairedDeviceList = New("DeletePairedDeviceList", S, 0x1406, Blocked, ProtocolStatus.Unknown);
    public static readonly CommandDescriptor GetOwnDeviceIndex = New("GetOwnDeviceIndex", S, 0x1407, Read, Verified, 0);
    public static readonly CommandDescriptor GetMaxConnections = New("GetMaxConnections", S, 0x1409, Read, Verified, 0);
    public static readonly CommandDescriptor Unknown140B = New("Unknown140B", S, 0x140B, SafetyClass.Unknown, ProtocolStatus.Unknown);

    // ---- mmi (Feature 11) ----
    public static readonly CommandDescriptor SetMmiConfig = New("SetMmiConfig", S, 0x1600, Setting, NeedsHardwareTest, 3);
    public static readonly CommandDescriptor GetMmiConfig = New("GetMmiConfig", S, 0x1601, Read, Verified, 2);
    public static readonly CommandDescriptor ResetMmiConfig = New("ResetMmiConfig", S, 0x1604, Blocked, Unverified);
    public static readonly CommandDescriptor IsDefaultMmiConfig = New("IsDefaultMmiConfig", S, 0x1605, Read, Verified, 0);
    public static readonly CommandDescriptor SetTouchLock = New("SetTouchLock", S, 0x1606, Setting, Verified, 1);
    public static readonly CommandDescriptor GetTouchLock = New("GetTouchLock", S, 0x1607, Read, Verified, 0);

    // ---- transparentHearing (Feature 12) ----
    public static readonly CommandDescriptor SetTransparentHearingMode = New("SetTransparentHearingMode", S, 0x1800, Setting, Verified, 1);
    public static readonly CommandDescriptor GetTransparentHearingMode = New("GetTransparentHearingMode", S, 0x1801, Read, Verified, 0);
    public static readonly CommandDescriptor SetTransparentHearingTw = New("SetTransparentHearingTw", S, 0x1802, SafetyClass.Unknown, ProtocolStatus.Unknown);
    public static readonly CommandDescriptor GetTransparentHearingTw = New("GetTransparentHearingTw", S, 0x1803, Read, Verified, 0);
    public static readonly CommandDescriptor SetTransparentHearingStatus = New("SetTransparentHearingStatus", S, 0x1804, Setting, Verified, 1);
    public static readonly CommandDescriptor GetTransparentHearingStatus = New("GetTransparentHearingStatus", S, 0x1805, Read, Verified, 0);

    // ---- ANC (Feature 13) ----
    public static readonly CommandDescriptor SetAncMode = New("SetAncMode", S, 0x1A00, Setting, Verified); // Tabelle (6 Byte) und Einzelpaar wirken (Phase E)
    public static readonly CommandDescriptor GetAncModes = New("GetAncModes", S, 0x1A01, Read, Verified, 0);
    public static readonly CommandDescriptor SetAncLevel = New("SetAncLevel", S, 0x1A02, Setting, Verified, 1);
    public static readonly CommandDescriptor GetAncLevel = New("GetAncLevel", S, 0x1A03, Read, Verified, 0);
    public static readonly CommandDescriptor SetAncEnabled = New("SetAncEnabled", S, 0x1A04, Setting, Verified, 1);
    public static readonly CommandDescriptor GetAncEnabled = New("GetAncEnabled", S, 0x1A05, Read, Verified, 0);

    // ---- personalizedSound (Feature 16) ----
    public static readonly CommandDescriptor GetPersonalizationState = New("GetPersonalizationState", S, 0x2001, Read, Verified, 0);
    public static readonly CommandDescriptor Unknown2003 = New("Unknown2003", S, 0x2003, SafetyClass.Unknown, ProtocolStatus.Unknown);
    public static readonly CommandDescriptor Unknown2005 = New("Unknown2005", S, 0x2005, SafetyClass.Unknown, ProtocolStatus.Unknown);
    public static readonly CommandDescriptor Unknown2007 = New("Unknown2007", S, 0x2007, SafetyClass.Unknown, ProtocolStatus.Unknown);

    private static readonly FrozenDictionary<(ushort Vendor, ushort Command), CommandDescriptor> ByKey = typeof(CommandCatalog)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.FieldType == typeof(CommandDescriptor))
        .Select(f => (CommandDescriptor)f.GetValue(null)!)
        .ToFrozenDictionary(d => (d.Vendor, d.Command.Value));

    public static IReadOnlyCollection<CommandDescriptor> All => ByKey.Values;

    /// <summary>Sucht einen Eintrag über Vendor und Command-ID (Typ-Bits werden ignoriert).</summary>
    public static CommandDescriptor? Find(ushort vendor, CommandWord command) =>
        ByKey.GetValueOrDefault((vendor, command.AsCommand().Value));

    private static CommandDescriptor New(string name, ushort vendor, ushort id, SafetyClass safety, ProtocolStatus status, int? requestLength = null, TimeSpan? timeout = null)
    {
        var command = new CommandWord(id);
        if (command.Type != PacketType.Command && safety != Blocked)
        {
            throw new InvalidOperationException($"{name}: 0x{id:X4} ist kein Command-Word vom Typ Command.");
        }

        return new CommandDescriptor(name, vendor, command, safety, status, requestLength, timeout);
    }
}
