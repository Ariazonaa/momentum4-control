using Momentum4.Core.State;
using Momentum4.Protocol.Features;

namespace Momentum4.Core.Services;

public sealed partial class Momentum4Service
{
    private async Task<T> LegacyControlAsync<T>(Func<Task<HeadsetControlSnapshot>> operation, Func<HeadsetControlSnapshot, T> convert,
        Func<Momentum4State, T> actual, string description)
    {
        try
        {
            return convert(await operation());
        }
        catch (HeadsetPartialWriteException exception) when (exception.InnerException is SettingNotAppliedException<HeadsetControlSnapshot> failure)
        {
            throw new SettingNotAppliedException<T>(description, convert(failure.Actual));
        }
        catch (SettingNotAppliedException<HeadsetControlSnapshot> exception)
        {
            throw new SettingNotAppliedException<T>(description, convert(exception.Actual));
        }
        catch (SettingNotAppliedException<T>)
        {
            throw;
        }
        catch (SettingNotAppliedException)
        {
            throw new SettingNotAppliedException<T>(description, actual(Store.Current));
        }
    }

    private HeadsetEqTopology LegacyEqTopology()
    {
        var current = Store.Current;
        if (current.Device is null || current.Equalizer is null || current.Equalizer.Bands.Any(band => band.FrequencyHz is null))
        {
            throw new FeatureNotSupportedException("Refresh the equalizer before applying.");
        }

        return new HeadsetEqTopology(current.Device.ModelId, current.Equalizer.Bands.Select(band => band.FrequencyHz!.Value).ToArray());
    }

    private Task<HeadsetControlSnapshot> SetCustomControlLevelAsync(int level, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 100);
        return ControlOperationAsync(async ct =>
        {
            EnsureActionPossible("Custom ANC level", [.. CommandsFor(NoiseControlMode.Custom), Momentum4.Protocol.Catalog.CommandCatalog.SetAncLevel]);
            var before = await ReadControlSnapshotAsync(ControlGroup.Noise, ct);
            RequireControl(before, HeadsetControl.AncEnabled);
            RequireControl(before, HeadsetControl.AdaptiveAnc);
            RequireControl(before, HeadsetControl.AncStrength);
            await ApplyModeAsync(NoiseControlMode.Custom, NoiseState(before.Settings), await _client.ReadAncModesAsync(ct), ct);
            if (before.Settings.AncStrength != level || NoiseState(before.Settings).Mode != NoiseControlMode.Custom)
            {
                await _client.SetAncLevelAsync(level, ct);
            }

            var after = await ReadControlReadbackAsync(before, ControlGroup.Noise, HeadsetControl.AncStrength, ct);
            if (NoiseState(after.Settings).Mode != NoiseControlMode.Custom || after.Settings.AncStrength != level)
            {
                throw new SettingNotAppliedException<NoiseControlState>("Custom ANC level", NoiseState(after.Settings));
            }

            return after;
        }, cancellationToken);
    }

    private Task<HeadsetControlSnapshot> ConnectLegacyPeerAsync(int index, string expectedName, CancellationToken cancellationToken) =>
        ControlOperationAsync(async ct =>
        {
            var before = await ReadControlSnapshotAsync(ControlGroup.Metadata, ct);
            RequireControl(before, HeadsetControl.ConnectPeer);
            var target = before.Peers.SingleOrDefault(peer => peer.Index == index && peer.Name == expectedName)
                ?? throw new PeerActionRefusedException("Selected peer changed; refresh before connecting.");
            return await ConnectControlPeerCoreAsync(new HeadsetPeerSelection(index, expectedName, target.Priority, target.IsThisComputer), before, ct);
        }, cancellationToken);

    private static BehaviorState BehaviorStateOf(HeadsetControlSettings settings) =>
        new(settings.OnHeadDetection ?? throw new FeatureNotSupportedException("On-head state unavailable"),
            settings.SmartPause ?? throw new FeatureNotSupportedException("Smart pause state unavailable"),
            settings.AutoAnswer ?? throw new FeatureNotSupportedException("Auto answer state unavailable"),
            settings.ComfortCall ?? throw new FeatureNotSupportedException("Comfort call state unavailable"),
            settings.TouchControls ?? throw new FeatureNotSupportedException("Touch state unavailable"));

    private static MultipointState MultipointStateOf(HeadsetControlSnapshot snapshot) =>
        new(snapshot.Peers.Select(peer => new PeerDevice(peer.Index, peer.Name, peer.Connected == true, peer.IsThisComputer)).ToArray(),
            snapshot.MaxConnections, snapshot.Peers.SingleOrDefault(peer => peer.IsThisComputer)?.Index);

    private static HeadsetBooleanControl BooleanControlFor(BehaviorSetting setting) => setting switch
    {
        BehaviorSetting.OnHeadDetection => HeadsetBooleanControl.OnHeadDetection,
        BehaviorSetting.SmartPause => HeadsetBooleanControl.SmartPause,
        BehaviorSetting.AutoAnswer => HeadsetBooleanControl.AutoAnswer,
        BehaviorSetting.ComfortCall => HeadsetBooleanControl.ComfortCall,
        BehaviorSetting.TouchControl => HeadsetBooleanControl.TouchControls,
        _ => throw new ArgumentOutOfRangeException(nameof(setting)),
    };
}
