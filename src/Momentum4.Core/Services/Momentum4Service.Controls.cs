using System.Collections.Frozen;
using Momentum4.Core.Device;
using Momentum4.Core.Queue;
using Momentum4.Core.State;
using Momentum4.Core.Transport;
using Momentum4.Protocol;
using Momentum4.Protocol.Catalog;
using Momentum4.Protocol.Features;

namespace Momentum4.Core.Services;

public sealed class HeadsetPartialWriteException(Exception innerException)
    : Exception("Equalizer change incomplete; refresh before retrying.", innerException);

public sealed partial class Momentum4Service
{
    public static readonly TimeSpan ControlOperationTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ControlPeerPollTimeout = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan ControlPeerPollInterval = TimeSpan.FromMilliseconds(300);
    private HeadsetControlSnapshot? _controlSnapshot;

    [Flags]
    private enum ControlGroup
    {
        Noise = 1,
        Equalizer = 2,
        Behavior = 4,
        Ancillary = 8,
        Spatial = 16,
        Metadata = 32,
        All = 63,
    }

    public Task<HeadsetControlSnapshot> GetControlSnapshotAsync(CancellationToken cancellationToken) =>
        ControlOperationAsync(ct => ReadControlSnapshotAsync(ControlGroup.All, ct), cancellationToken);

    public Task<HeadsetControlSnapshot> SetBooleanControlAsync(HeadsetBooleanControl control, bool value, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(Enum.IsDefined(control), false);
        return ControlOperationAsync(async ct =>
        {
            EnsureActionPossible(control.ToString(), [Momentum4Client.BooleanSetter(control)]);
            var group = GroupFor((HeadsetControl)control);
            var before = await ReadControlSnapshotAsync(group, ct);
            RequireControl(before, (HeadsetControl)control);
            if (control is HeadsetBooleanControl.ImmersiveAudio or HeadsetBooleanControl.HeadTracking)
            {
                if (value && before.Settings.SoundMode == 3)
                {
                    throw new FeatureNotSupportedException("Disable Sound Personalization before enabling spatial audio.");
                }

                if (control == HeadsetBooleanControl.HeadTracking && value && before.Settings.ImmersiveAudio != true)
                {
                    throw new FeatureNotSupportedException("Enable immersive audio before head tracking.");
                }
            }

            if (before.Settings.Get(control) == value)
            {
                return before;
            }

            if (control == HeadsetBooleanControl.AdaptiveAnc)
            {
                EnsureNoiseRestorationPossible(control.ToString());
                await WriteAncModeAsync(await _client.ReadAncModesAsync(ct), AncCodec.ModeAdaptive, value ? (byte)1 : (byte)0, ct);
            }
            else
            {
                EnsureActionPossible(control.ToString(), [Momentum4Client.BooleanSetter(control)]);
                if (control == HeadsetBooleanControl.AncEnabled && value)
                {
                    EnsureNoiseRestorationPossible(control.ToString());
                }

                await _client.SetBooleanControlAsync(control, value, ct);
            }

            var after = await ReadControlReadbackAsync(before, group, (HeadsetControl)control, ct);
            if (control == HeadsetBooleanControl.AdaptiveAnc || (control == HeadsetBooleanControl.AncEnabled && value))
            {
                after = await RestoreNoiseSiblingsAsync(before, after, control == HeadsetBooleanControl.AncEnabled, group, (HeadsetControl)control, ct);
            }

            if (after.Settings.Get(control) != value)
            {
                throw new SettingNotAppliedException<HeadsetControlSnapshot>(control.ToString(), after);
            }

            if (control is HeadsetBooleanControl.ImmersiveAudio or HeadsetBooleanControl.HeadTracking)
            {
                if (after.Settings.SoundMode != before.Settings.SoundMode
                    || (control != HeadsetBooleanControl.ImmersiveAudio && after.Settings.ImmersiveAudio != before.Settings.ImmersiveAudio)
                    || (control != HeadsetBooleanControl.HeadTracking && after.Settings.HeadTracking != before.Settings.HeadTracking))
                {
                    throw new SettingNotAppliedException<HeadsetControlSettings>("Related spatial setting changed", after.Settings);
                }
            }

            if (control == HeadsetBooleanControl.BatteryProtection
                && (after.Settings.Sidetone != before.Settings.Sidetone || after.Settings.AutoPowerOff != before.Settings.AutoPowerOff
                    || after.Settings.PauseOnTransparency != before.Settings.PauseOnTransparency))
            {
                throw new SettingNotAppliedException<HeadsetControlSettings>("Related ancillary setting changed", after.Settings);
            }

            return after;
        }, cancellationToken);
    }

    public Task<HeadsetControlSnapshot> SetIntegerControlAsync(HeadsetIntegerControl control, int value, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(Enum.IsDefined(control), false);
        return ControlOperationAsync(async ct =>
        {
            EnsureActionPossible(control.ToString(), [Momentum4Client.IntegerSetter(control)]);
            var group = GroupFor((HeadsetControl)control);
            var before = await ReadControlSnapshotAsync(group, ct);
            RequireControl(before, (HeadsetControl)control);
            var valid = control switch
            {
                HeadsetIntegerControl.AncStrength or HeadsetIntegerControl.TransparencyLevel => value is >= 0 and <= 100,
                HeadsetIntegerControl.AntiWind => value >= 0 && value <= (_gate.IsMomentum5 ? 1 : 2),
                HeadsetIntegerControl.AudioPrompts or HeadsetIntegerControl.SoundMode => value is >= 0 and <= 2,
                HeadsetIntegerControl.Sidetone => value is >= 0 and <= 5,
                HeadsetIntegerControl.AutoPowerOff => Momentum4State.AutoPowerOffChoices.Contains(value),
                _ => false,
            };
            if (!valid)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            if (control == HeadsetIntegerControl.TransparencyLevel)
            {
                return await ApplyTransparencyLevelAsync(before, value, ct);
            }

            if (before.Settings.Get(control) == value)
            {
                return before;
            }

            if (control == HeadsetIntegerControl.AntiWind)
            {
                EnsureNoiseRestorationPossible(control.ToString());
                await WriteAncModeAsync(await _client.ReadAncModesAsync(ct), AncCodec.ModeAntiWind, (byte)value, ct);
            }
            else
            {
                await _client.SetIntegerControlAsync(control, value, ct);
            }

            var after = await ReadControlReadbackAsync(before, group, (HeadsetControl)control, ct);
            if (control == HeadsetIntegerControl.SoundMode)
            {
                for (var attempt = 0; attempt < 8 && after.Settings.SoundMode != value; attempt++)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), _time, ct);
                    after = await ReadControlReadbackAsync(before, group, (HeadsetControl)control, ct);
                }
            }

            if (control == HeadsetIntegerControl.AntiWind)
            {
                after = await RestoreNoiseSiblingsAsync(before, after, false, group, (HeadsetControl)control, ct);
            }

            if (after.Settings.Get(control) != value)
            {
                throw new SettingNotAppliedException<HeadsetControlSnapshot>(control.ToString(), after);
            }

            return after;
        }, cancellationToken);
    }

    public Task<HeadsetControlSnapshot> SetControlEqBandAsync(int band, decimal gainDb, HeadsetEqTopology expected, CancellationToken cancellationToken) =>
        ApplyControlEqualizerAsync(null, band, gainDb, expected, cancellationToken);

    public Task<HeadsetControlSnapshot> SetControlEqualizerAsync(IReadOnlyList<decimal> gainsDb, HeadsetEqTopology expected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gainsDb);
        if (gainsDb.Count is < 1 or > 16)
        {
            throw new ArgumentException("EQ requires one to sixteen gains", nameof(gainsDb));
        }

        return ApplyControlEqualizerAsync(gainsDb.ToArray(), null, null, expected, cancellationToken);
    }

    public Task<HeadsetControlSnapshot> RenameControlAsync(string name, CancellationToken cancellationToken) =>
        ControlOperationAsync(async ct =>
        {
            EnsureActionPossible("Rename", [CommandCatalog.SetLocalName]);
            var before = await ReadControlSnapshotAsync(ControlGroup.Metadata, ct);
            RequireControl(before, HeadsetControl.Rename);
            var payload = SharedHeadsetCodec.EncodeName(name, _gate.IsMomentum5 ? 16 : 30);
            name = System.Text.Encoding.UTF8.GetString(payload);
            if (before.DeviceName == name)
            {
                return before;
            }

            await _client.SetDeviceNameAsync(name, ct);
            var after = await ReadControlReadbackAsync(before, ControlGroup.Metadata, HeadsetControl.Rename, ct);
            if (after.DeviceName != name)
            {
                throw new SettingNotAppliedException<HeadsetControlSnapshot>("Device name", after);
            }

            return after;
        }, cancellationToken);

    public Task<HeadsetControlSnapshot> SetControlNoiseModeAsync(NoiseControlMode mode, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        return ControlOperationAsync(async ct =>
        {
            var before = await ReadControlSnapshotAsync(ControlGroup.Noise, ct);
            RequireControl(before, HeadsetControl.AncEnabled);
            RequireControl(before, HeadsetControl.Transparency);
            RequireControl(before, HeadsetControl.AdaptiveAnc);
            EnsureActionPossible(mode.ToString(), CommandsFor(mode));
            await ApplyModeAsync(mode, NoiseState(before.Settings), await _client.ReadAncModesAsync(ct), ct);
            var after = await ReadControlReadbackAsync(before, ControlGroup.Noise, HeadsetControl.AncEnabled, ct);
            if (NoiseState(after.Settings).Mode != mode)
            {
                throw new SettingNotAppliedException<NoiseControlState>("Noise mode", NoiseState(after.Settings));
            }

            return after;
        }, cancellationToken);
    }

    public Task<HeadsetControlSnapshot> ConnectControlPeerAsync(HeadsetPeerSelection selected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selected);
        return ControlOperationAsync(async ct =>
        {
            var before = await ReadControlSnapshotAsync(ControlGroup.Metadata, ct);
            RequireControl(before, HeadsetControl.ConnectPeer);
            return await ConnectControlPeerCoreAsync(selected, before, ct);
        }, cancellationToken);
    }

    private async Task<HeadsetControlSnapshot> ConnectControlPeerCoreAsync(HeadsetPeerSelection selected, HeadsetControlSnapshot before, CancellationToken ct)
    {
        EnsureActionPossible("Connect peer", [Momentum4.Protocol.Catalog.CommandCatalog.ConnectPairedDevice]);
        var target = RequirePeer(before, selected);
        if (target.Connected == true)
        {
            return before;
        }

        if (before.Peers.Count(peer => peer.Connected == true) >= before.MaxConnections)
        {
            throw new PeerActionRefusedException("All headset connection slots are occupied.");
        }

        var own = before.Peers.Single(peer => peer.IsThisComputer);
        await _client.ConnectPeerAsync(target.Index, ct);
        var pollTimeout = TimeSpan.FromTicks(Math.Min(ControlPeerPollTimeout.Ticks, _options.PeerConnectTimeout.Ticks));
        using var pollTimer = new CancellationTokenSource(pollTimeout, _time);
        using var poll = CancellationTokenSource.CreateLinkedTokenSource(ct, pollTimer.Token);
        try
        {
            while (!await _client.GetPeerConnectedAsync(target.Index, poll.Token))
            {
                await Task.Delay(ControlPeerPollInterval, _time, poll.Token);
            }
        }
        catch (OperationCanceledException) when (pollTimer.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new SettingNotAppliedException<string>("Peer connection", "confirmation timed out");
        }

        var after = await ReadControlReadbackAsync(before, ControlGroup.Metadata, HeadsetControl.ConnectPeer, ct);
        if (RequirePeer(after, selected).Connected != true || !after.Peers.Contains(own))
        {
            throw new PeerActionRefusedException("Peer connection did not retain the target and control peer.");
        }

        return after;
    }

    private Task<HeadsetControlSnapshot> ApplyControlEqualizerAsync(IReadOnlyList<decimal>? gains, int? band, decimal? gain, HeadsetEqTopology expected, CancellationToken cancellationToken, bool rollbackRejected = false)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(expected.FrequenciesHz);
        if (expected.FrequenciesHz.Count is < 1 or > 16 || gains is { Count: < 1 or > 16 })
        {
            throw new ArgumentException("EQ requires one to sixteen ordered frequencies and gains", nameof(expected));
        }

        expected = expected with { FrequenciesHz = expected.FrequenciesHz.ToArray() };
        gains = gains?.ToArray();
        return ControlOperationAsync(async ct =>
        {
            EnsureActionPossible("Equalizer", [CommandCatalog.SetEqBand]);
            var before = await ReadControlSnapshotAsync(ControlGroup.Equalizer, ct);
            RequireControl(before, HeadsetControl.Equalizer);
            if (before.Model != expected.Model || !before.EqFrequenciesHz.SequenceEqual(expected.FrequenciesHz))
            {
                throw new FeatureNotSupportedException("EQ identity or ordered topology changed; refresh before applying.");
            }

            var target = gains?.ToArray() ?? before.EqGainsDb.ToArray();
            if (band is { } selectedBand)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(selectedBand);
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(selectedBand, target.Length);
                target[selectedBand] = gain!.Value;
            }

            if (target.Length != before.EqGainsDb.Count)
            {
                throw new ArgumentException("Invalid EQ gain count", nameof(gains));
            }

            var eq = EqualizerStateOf(before);
            for (var index = 0; index < target.Length; index++)
            {
                ValidateGain(eq, index, target[index]);
            }

            var wrote = false;
            var applied = new List<int>();
            try
            {
                for (var index = 0; index < target.Length; index++)
                {
                    if (target[index] != before.EqGainsDb[index])
                    {
                        wrote = true;
                        await _client.SetEqBandAsync(index, target[index], ct);
                        applied.Add(index);
                    }
                }

                var after = await ReadControlReadbackAsync(before, ControlGroup.Equalizer, HeadsetControl.Equalizer, ct);
                if (!after.EqGainsDb.SequenceEqual(target) || after.Settings.SoundMode != before.Settings.SoundMode)
                {
                    throw new SettingNotAppliedException<HeadsetControlSnapshot>("EQ gains or sound mode", after);
                }

                return after;
            }
            catch (GaiaErrorException rejection) when (rollbackRejected && wrote && !ct.IsCancellationRequested && Store.Current.Connection == ConnectionState.Connected)
            {
                try
                {
                    var current = await ReadControlReadbackAsync(before, ControlGroup.Equalizer, HeadsetControl.Equalizer, ct);
                    foreach (var index in applied.AsEnumerable().Reverse())
                    {
                        if (current.EqGainsDb[index] == target[index])
                        {
                            await _client.SetEqBandAsync(index, before.EqGainsDb[index], ct);
                        }
                    }

                    var restored = await ReadControlReadbackAsync(before, ControlGroup.Equalizer, HeadsetControl.Equalizer, ct);
                    if (!restored.EqGainsDb.SequenceEqual(before.EqGainsDb))
                    {
                        throw new SettingNotAppliedException<HeadsetControlSnapshot>("EQ rollback", restored);
                    }
                }
                catch (Exception exception)
                {
                    throw new HeadsetPartialWriteException(new AggregateException(rejection, exception));
                }

                throw;
            }
            catch (Exception exception) when (wrote)
            {
                throw new HeadsetPartialWriteException(exception);
            }
        }, cancellationToken);
    }

    private async Task<HeadsetControlSnapshot> ApplyTransparencyLevelAsync(HeadsetControlSnapshot before, int value, CancellationToken ct)
    {
        RequireTransparencyState(before);
        if (before.Settings.TransparencyLevel == value && before.Settings.AncStrength == value && before.Settings.Transparency == (value == 100))
        {
            return before;
        }

        await _client.SetTransparentHearingStatusAsync(true, ct);
        var after = await ReadControlReadbackAsync(before, ControlGroup.Noise, HeadsetControl.TransparencyLevel, ct);
        VerifyTransparencyState(before, after, 100);
        if (value != 100)
        {
            await _client.SetIntegerControlAsync(HeadsetIntegerControl.TransparencyLevel, value, ct);
            after = await ReadControlReadbackAsync(before, ControlGroup.Noise, HeadsetControl.TransparencyLevel, ct);
            VerifyTransparencyState(before, after, value);
        }

        return after;
    }

    private void EnsureNoiseRestorationPossible(string description) =>
        EnsureActionPossible(description, [CommandCatalog.SetTransparentHearingStatus, CommandCatalog.SetAncLevel]);

    private async Task<HeadsetControlSnapshot> RestoreNoiseSiblingsAsync(HeadsetControlSnapshot before, HeadsetControlSnapshot after, bool changedEnabled, ControlGroup group, HeadsetControl control, CancellationToken ct)
    {
        var restoreTransparency = before.Settings.Transparency == true && after.Settings.AncEnabled == true;
        if (restoreTransparency && after.Settings.Transparency != true)
        {
            await _client.SetTransparentHearingStatusAsync(true, ct);
            after = await ReadControlReadbackAsync(before, group, control, ct);
        }
        else if (!restoreTransparency && before.Settings.AncStrength != after.Settings.AncStrength && before.Settings.AncStrength is { } level)
        {
            await _client.SetAncLevelAsync(level, ct);
            after = await ReadControlReadbackAsync(before, group, control, ct);
        }

        if (after.Settings.AncStrength != before.Settings.AncStrength
            || after.Settings.TransparencyLevel != before.Settings.TransparencyLevel
            || (control != HeadsetControl.AdaptiveAnc && after.Settings.AdaptiveAnc != before.Settings.AdaptiveAnc)
            || (control != HeadsetControl.AntiWind && after.Settings.AntiWind != before.Settings.AntiWind)
            || (!changedEnabled && after.Settings.AncEnabled != before.Settings.AncEnabled)
            || ((before.Settings.AncEnabled == true || restoreTransparency) && after.Settings.Transparency != before.Settings.Transparency))
        {
            throw new SettingNotAppliedException<HeadsetControlSettings>("Related noise setting changed", after.Settings);
        }

        return after;
    }

    private async Task<T> ControlOperationAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        using var budget = CreateOperationBudget(cancellationToken);
        await _actionGate.WaitAsync(budget.Token);
        var mutationSequence = _queue.MutationSequence;
        try
        {
            EnsureActionPossible("Shared headset operation", []);
            var result = await operation(budget.Token);
            EnsureActionPossible("Shared headset operation completed", []);
            return result;
        }
        catch (Exception exception) when (exception is GaiaErrorException or ProtocolFormatException or TimeoutException or OperationCanceledException or HeadsetUnavailableException or HeadsetPartialWriteException or SettingNotAppliedException
            || _queue.MutationSequence != mutationSequence)
        {
            _controlSnapshot = null;
            _gate = new FeatureGate(null, null);
            Store.Apply(new ConnectionChanged(ConnectionState.Disconnected, "Shared headset session invalidated"));
            _supervisor.EndSession();
            throw;
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private OperationBudget CreateOperationBudget(CancellationToken cancellationToken) => new(_time, cancellationToken);

    private sealed class OperationBudget : IDisposable
    {
        private readonly CancellationTokenSource _timeout;
        private readonly CancellationTokenSource _linked;

        public OperationBudget(TimeProvider time, CancellationToken cancellationToken)
        {
            _timeout = new CancellationTokenSource(ControlOperationTimeout, time);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _timeout.Token);
        }

        public CancellationToken Token => _linked.Token;

        public void Dispose()
        {
            _linked.Dispose();
            _timeout.Dispose();
        }
    }

    private async Task RequireWritableIdentityAsync(CancellationToken cancellationToken)
    {
        await ReadIdentityAsync(cancellationToken);
        if (!_gate.Writable)
        {
            throw new FeatureNotSupportedException("Unverified headset model or firmware is read-only.");
        }
    }

    private async Task<DeviceInfo> ReadIdentityAsync(CancellationToken ct)
    {
        var identity = new DeviceInfo(await _client.GetModelIdAsync(ct), await _client.GetFirmwareVersionAsync(ct));
        _gate = new FeatureGate(identity.Firmware, identity.ModelId);
        Store.Apply(new DeviceIdentified(identity));
        return identity;
    }

    private static void RequireControl(HeadsetControlSnapshot snapshot, HeadsetControl control)
    {
        if (!snapshot.VerifiedSession || !snapshot.Writable.Contains(control))
        {
            throw new FeatureNotSupportedException($"{control} is unavailable for this model, firmware or current capability.");
        }
    }

    private static HeadsetControlPeer RequirePeer(HeadsetControlSnapshot state, HeadsetPeerSelection selected)
    {
        var targets = state.Peers.Where(peer => peer.Index == selected.Index).ToArray();
        if (!state.PeersAvailable || state.MaxConnections is null || targets.Length != 1)
        {
            throw new PeerActionRefusedException("Peer capabilities are unavailable.");
        }

        var target = targets[0];
        if (target.Name != selected.Name || target.Priority != selected.Priority || target.IsThisComputer != selected.IsThisComputer
            || target.IsThisComputer || state.Peers.Count(peer => peer.Name == target.Name) != 1
            || state.Peers.Any(peer => peer.Connected is null) || state.Peers.Count(peer => peer.IsThisComputer && peer.Connected == true) != 1)
        {
            throw new PeerActionRefusedException("Selected peer changed, is ambiguous, or is the active control peer.");
        }

        return target;
    }

    private static NoiseControlState NoiseState(HeadsetControlSettings settings) =>
        new(settings.AncEnabled ?? throw new FeatureNotSupportedException("ANC state unavailable"),
            settings.AdaptiveAnc ?? throw new FeatureNotSupportedException("Adaptive state unavailable"),
            settings.AncStrength ?? throw new FeatureNotSupportedException("ANC level unavailable"),
            (AntiWindMode)(settings.AntiWind ?? throw new FeatureNotSupportedException("Anti-wind unavailable")), settings.Transparency);

    private static EqualizerState EqualizerStateOf(HeadsetControlSnapshot snapshot) =>
        new(snapshot.EqGainsDb.Select((gain, index) => new EqBand(index, gain, snapshot.EqFrequenciesHz[index])).ToArray(),
            snapshot.EqMinimumDb!.Value, snapshot.EqMaximumDb!.Value, snapshot.Settings.BassBoost,
            snapshot.Settings.SoundMode is { } mode ? (SoundMode)mode : null);

    private static void RequireTransparencyState(HeadsetControlSnapshot snapshot)
    {
        RequireControl(snapshot, HeadsetControl.Transparency);
        RequireControl(snapshot, HeadsetControl.TransparencyLevel);
        if (snapshot.Settings is not { AncEnabled: true, AncStrength: not null, Transparency: not null, TransparencyLevel: not null, AntiWind: not null, AdaptiveAnc: not null })
        {
            throw new FeatureNotSupportedException("Transparency level requires enabled ANC and complete noise capabilities.");
        }
    }

    private static void VerifyTransparencyState(HeadsetControlSnapshot before, HeadsetControlSnapshot after, int level)
    {
        RequireTransparencyState(after);
        if (after.Settings.TransparencyLevel != level || after.Settings.AncStrength != level || after.Settings.Transparency != (level == 100)
            || after.Settings.AncEnabled != before.Settings.AncEnabled || after.Settings.AntiWind != before.Settings.AntiWind
            || after.Settings.AdaptiveAnc != before.Settings.AdaptiveAnc)
        {
            throw new SettingNotAppliedException<HeadsetControlSettings>("Transparency level or related noise setting", after.Settings);
        }
    }

    private static ControlGroup GroupFor(HeadsetControl control) => control switch
    {
        HeadsetControl.AncEnabled or HeadsetControl.Transparency or HeadsetControl.AdaptiveAnc or HeadsetControl.AncStrength or HeadsetControl.TransparencyLevel or HeadsetControl.AntiWind => ControlGroup.Noise,
        HeadsetControl.BassBoost or HeadsetControl.Equalizer => ControlGroup.Equalizer,
        HeadsetControl.TouchControls or HeadsetControl.SmartPause or HeadsetControl.OnHeadDetection or HeadsetControl.AutoAnswer or HeadsetControl.ComfortCall or HeadsetControl.AudioPrompts => ControlGroup.Behavior,
        HeadsetControl.PauseOnTransparency or HeadsetControl.Sidetone or HeadsetControl.AutoPowerOff or HeadsetControl.BatteryProtection => ControlGroup.Ancillary,
        HeadsetControl.ImmersiveAudio or HeadsetControl.HeadTracking or HeadsetControl.SoundMode => ControlGroup.Spatial | ControlGroup.Equalizer,
        HeadsetControl.Rename or HeadsetControl.ConnectPeer => ControlGroup.Metadata,
        _ => throw new ArgumentOutOfRangeException(nameof(control)),
    };
}
