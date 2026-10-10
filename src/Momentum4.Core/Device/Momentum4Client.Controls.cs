using Momentum4.Core.State;
using Momentum4.Protocol.Catalog;
using Momentum4.Protocol.Features;

namespace Momentum4.Core.Device;

public sealed partial class Momentum4Client
{
    public async Task<bool> ReadBooleanControlAsync(HeadsetBooleanControl control, CancellationToken cancellationToken)
    {
        var payload = (await queue.SendAsync(BooleanGetter(control), cancellationToken)).Payload;
        var value = SwitchCodec.Decode(payload, control.ToString());
        return control == HeadsetBooleanControl.TouchControls ? !value : value;
    }

    public Task SetBooleanControlAsync(HeadsetBooleanControl control, bool value, CancellationToken cancellationToken) =>
        queue.SendAsync(BooleanSetter(control), SwitchCodec.Encode(control == HeadsetBooleanControl.TouchControls ? !value : value), cancellationToken);

    public async Task<int> ReadIntegerControlAsync(HeadsetIntegerControl control, CancellationToken cancellationToken) => control switch
    {
        HeadsetIntegerControl.AutoPowerOff => await ReadAutoPowerOffAsync(cancellationToken),
        HeadsetIntegerControl.SoundMode => (int)await ReadSoundModeAsync(cancellationToken),
        HeadsetIntegerControl.AudioPrompts => (int)await ReadAudioPromptModeAsync(cancellationToken),
        HeadsetIntegerControl.AncStrength => AncCodec.DecodeLevel((await queue.SendAsync(CommandCatalog.GetAncLevel, cancellationToken)).Payload),
        HeadsetIntegerControl.TransparencyLevel => AncCodec.DecodeLevel((await queue.SendAsync(CommandCatalog.GetTransparentHearingTw, cancellationToken)).Payload),
        HeadsetIntegerControl.Sidetone => SharedHeadsetCodec.DecodeByte((await queue.SendAsync(CommandCatalog.GetSidetone, cancellationToken)).Payload, 5, "sidetone"),
        _ => throw new ArgumentOutOfRangeException(nameof(control)),
    };

    public Task SetIntegerControlAsync(HeadsetIntegerControl control, int value, CancellationToken cancellationToken) => control switch
    {
        HeadsetIntegerControl.AncStrength => SetAncLevelAsync(value, cancellationToken),
        HeadsetIntegerControl.TransparencyLevel => queue.SendAsync(CommandCatalog.SetTransparentHearingTw, AncCodec.EncodeLevel(value), cancellationToken),
        HeadsetIntegerControl.Sidetone when value is >= 0 and <= 5 => queue.SendAsync(CommandCatalog.SetSidetone, new byte[] { (byte)value }, cancellationToken),
        HeadsetIntegerControl.AutoPowerOff => SetAutoPowerOffAsync(value, cancellationToken),
        HeadsetIntegerControl.SoundMode => SetSoundModeAsync((SoundMode)value, cancellationToken),
        HeadsetIntegerControl.AudioPrompts => SetAudioPromptModeAsync((AudioPromptMode)value, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(control)),
    };

    public async Task<AncModeTable> ReadAncModesAsync(CancellationToken cancellationToken) =>
        AncCodec.DecodeModes((await queue.SendAsync(CommandCatalog.GetAncModes, cancellationToken)).Payload);

    public async Task<EqConfig> ReadEqConfigAsync(CancellationToken cancellationToken) =>
        UserEqCodec.DecodeConfig((await queue.SendAsync(CommandCatalog.GetEqConfig, cancellationToken)).Payload);

    public async Task<int> ReadEqFrequencyAsync(int band, CancellationToken cancellationToken) =>
        UserEqCodec.DecodeBandFrequency((await queue.SendAsync(CommandCatalog.GetEqBandFrequency, new byte[] { (byte)band }, cancellationToken)).Payload, band);

    public async Task<bool> ReadChargingAsync(CancellationToken cancellationToken) =>
        SharedHeadsetCodec.DecodeCharging((await queue.SendAsync(CommandCatalog.GetChargingState, cancellationToken)).Payload);

    public async Task<int> ReadPersonalizationStateAsync(CancellationToken cancellationToken) =>
        SharedHeadsetCodec.DecodeByte((await queue.SendAsync(CommandCatalog.GetPersonalizationState, cancellationToken)).Payload, 3, "personalization state");

    public async Task<int> ReadPeerCountAsync(CancellationToken cancellationToken) =>
        DeviceManagementCodec.DecodeCount((await queue.SendAsync(CommandCatalog.GetPairedDeviceCount, cancellationToken)).Payload);

    public async Task<int> ReadOwnPeerIndexAsync(CancellationToken cancellationToken) =>
        DeviceManagementCodec.DecodeOwnIndex((await queue.SendAsync(CommandCatalog.GetOwnDeviceIndex, cancellationToken)).Payload);

    public async Task<int> ReadMaxConnectionsAsync(CancellationToken cancellationToken) =>
        DeviceManagementCodec.DecodeMaxConnections((await queue.SendAsync(CommandCatalog.GetMaxConnections, cancellationToken)).Payload);

    public async Task<PairedDeviceEntry> ReadPeerAsync(int index, CancellationToken cancellationToken)
    {
        var payload = (await queue.SendAsync(CommandCatalog.GetPairedDeviceInfo, DeviceManagementCodec.EncodeIndex(index), cancellationToken)).Payload;
        var entry = DeviceManagementCodec.DecodeEntry(payload, index);
        return entry with { Name = SharedHeadsetCodec.DecodeName(payload.AsSpan(3), 128) };
    }

    internal static CommandDescriptor IntegerSetter(HeadsetIntegerControl control) => control switch
    {
        HeadsetIntegerControl.AncStrength => CommandCatalog.SetAncLevel,
        HeadsetIntegerControl.TransparencyLevel => CommandCatalog.SetTransparentHearingTw,
        HeadsetIntegerControl.AntiWind => CommandCatalog.SetAncMode,
        HeadsetIntegerControl.AudioPrompts => CommandCatalog.SetAudioPromptMode,
        HeadsetIntegerControl.Sidetone => CommandCatalog.SetSidetone,
        HeadsetIntegerControl.AutoPowerOff => CommandCatalog.SetTimer,
        HeadsetIntegerControl.SoundMode => CommandCatalog.SetSoundMode,
        _ => throw new ArgumentOutOfRangeException(nameof(control)),
    };

    internal static CommandDescriptor BooleanGetter(HeadsetBooleanControl control) => control switch
    {
        HeadsetBooleanControl.AncEnabled => CommandCatalog.GetAncEnabled,
        HeadsetBooleanControl.Transparency => CommandCatalog.GetTransparentHearingStatus,
        HeadsetBooleanControl.TouchControls => CommandCatalog.GetTouchLock,
        HeadsetBooleanControl.SmartPause => CommandCatalog.GetSmartPause,
        HeadsetBooleanControl.OnHeadDetection => CommandCatalog.GetOnHeadDetection,
        HeadsetBooleanControl.AutoAnswer => CommandCatalog.GetAutoAnswer,
        HeadsetBooleanControl.ComfortCall => CommandCatalog.GetComfortCall,
        HeadsetBooleanControl.BassBoost => CommandCatalog.GetBassBoost,
        HeadsetBooleanControl.PauseOnTransparency => CommandCatalog.GetTransparentHearingMode,
        HeadsetBooleanControl.ImmersiveAudio => CommandCatalog.GetImmersiveAudio,
        HeadsetBooleanControl.HeadTracking => CommandCatalog.GetHeadTracking,
        HeadsetBooleanControl.BatteryProtection => CommandCatalog.GetBatteryProtection,
        _ => throw new ArgumentOutOfRangeException(nameof(control)),
    };

    internal static CommandDescriptor BooleanSetter(HeadsetBooleanControl control) => control switch
    {
        HeadsetBooleanControl.AncEnabled => CommandCatalog.SetAncEnabled,
        HeadsetBooleanControl.Transparency => CommandCatalog.SetTransparentHearingStatus,
        HeadsetBooleanControl.TouchControls => CommandCatalog.SetTouchLock,
        HeadsetBooleanControl.SmartPause => CommandCatalog.SetSmartPause,
        HeadsetBooleanControl.OnHeadDetection => CommandCatalog.SetOnHeadDetection,
        HeadsetBooleanControl.AutoAnswer => CommandCatalog.SetAutoAnswer,
        HeadsetBooleanControl.ComfortCall => CommandCatalog.SetComfortCall,
        HeadsetBooleanControl.BassBoost => CommandCatalog.SetBassBoost,
        HeadsetBooleanControl.PauseOnTransparency => CommandCatalog.SetTransparentHearingMode,
        HeadsetBooleanControl.ImmersiveAudio => CommandCatalog.SetImmersiveAudio,
        HeadsetBooleanControl.HeadTracking => CommandCatalog.SetHeadTracking,
        HeadsetBooleanControl.BatteryProtection => CommandCatalog.SetBatteryProtection,
        HeadsetBooleanControl.AdaptiveAnc => CommandCatalog.SetAncMode,
        _ => throw new ArgumentOutOfRangeException(nameof(control)),
    };
}
