using System.Collections.Frozen;
using Momentum4.Core.Queue;
using Momentum4.Core.State;
using Momentum4.Protocol;
using Momentum4.Protocol.Features;

namespace Momentum4.Core.Services;

public sealed partial class Momentum4Service
{
    private async Task<HeadsetControlSnapshot> ReadControlSnapshotAsync(ControlGroup group, CancellationToken ct)
    {
        var identity = await ReadIdentityAsync(ct);
        var previous = _controlSnapshot;
        if (previous is null || previous.Model != identity.ModelId || previous.Firmware != identity.Firmware)
        {
            previous = null;
            group = ControlGroup.All;
        }

        var notes = new List<string>();
        var settings = previous?.Settings ?? new HeadsetControlSettings();
        var battery = previous?.Battery;
        if (group == ControlGroup.All)
        {
            battery = await OptionalValueAsync(() => _client.GetBatteryLevelAsync(ct), notes);
        }

        foreach (var control in Enum.GetValues<HeadsetBooleanControl>())
        {
            if (control == HeadsetBooleanControl.AdaptiveAnc || (group & GroupFor((HeadsetControl)control)) == 0)
            {
                continue;
            }

            settings = settings.With(control, null);
            if (control is HeadsetBooleanControl.ImmersiveAudio or HeadsetBooleanControl.HeadTracking or HeadsetBooleanControl.BatteryProtection
                && !(_gate.IsMomentum5 && _gate.Writable))
            {
                continue;
            }

            if (control == HeadsetBooleanControl.PauseOnTransparency && !_gate.IsMomentum4 && !(_gate.IsMomentum5 && _gate.Writable))
            {
                continue;
            }

            settings = settings.With(control, await OptionalValueAsync(() => _client.ReadBooleanControlAsync(control, ct), notes));
        }

        foreach (var control in Enum.GetValues<HeadsetIntegerControl>())
        {
            if (control == HeadsetIntegerControl.AntiWind || (group & GroupFor((HeadsetControl)control)) == 0)
            {
                continue;
            }

            settings = settings.With(control, null);
            if (control == HeadsetIntegerControl.TransparencyLevel && !(_gate.IsMomentum5 && _gate.Writable))
            {
                continue;
            }

            if (control is HeadsetIntegerControl.AutoPowerOff or HeadsetIntegerControl.Sidetone
                && !_gate.IsMomentum4 && !(_gate.IsMomentum5 && _gate.Writable))
            {
                continue;
            }

            settings = settings.With(control, await OptionalValueAsync(() => _client.ReadIntegerControlAsync(control, ct), notes));
        }

        if ((group & ControlGroup.Noise) != 0)
        {
            var table = await OptionalReferenceAsync(() => _client.ReadAncModesAsync(ct), notes);
            settings = settings with { AntiWind = table?.AntiWind is { } wind ? (int)wind : null, AdaptiveAnc = table?.Adaptive };
        }

        var gains = previous?.EqGainsDb ?? Array.Empty<decimal>();
        var frequencies = previous?.EqFrequenciesHz ?? Array.Empty<int>();
        var minimum = previous?.EqMinimumDb;
        var maximum = previous?.EqMaximumDb;
        if ((group & ControlGroup.Equalizer) != 0)
        {
            gains = Array.Empty<decimal>();
            frequencies = Array.Empty<int>();
            minimum = null;
            maximum = null;
            var config = await OptionalReferenceAsync(() => _client.ReadEqConfigAsync(ct), notes);
            if (config is not null)
            {
                minimum = config.MinGainDb;
                maximum = config.MaxGainDb;
                var observed = await OptionalReferenceAsync(() => _client.ReadEqGainsAsync(config.BandCount, ct), notes);
                if (observed is not null)
                {
                    if (observed.Any(gain => gain < config.MinGainDb || gain > config.MaxGainDb))
                    {
                        throw new ProtocolFormatException("EQ gain outside device range", []);
                    }

                    gains = observed;
                    var observedFrequencies = new List<int>();
                    for (var band = 0; band < config.BandCount; band++)
                    {
                        var index = band;
                        var frequency = await OptionalValueAsync(() => _client.ReadEqFrequencyAsync(index, ct), notes);
                        if (frequency is null)
                        {
                            observedFrequencies.Clear();
                            break;
                        }

                        observedFrequencies.Add(frequency.Value);
                    }

                    frequencies = observedFrequencies;
                }
            }

            settings = settings with { SoundMode = await OptionalValueAsync(() => _client.ReadIntegerControlAsync(HeadsetIntegerControl.SoundMode, ct), notes) };
        }

        var name = previous?.DeviceName;
        var charging = previous?.Charging;
        var wear = previous?.WearState;
        var personalization = previous?.PersonalizationState;
        var peers = previous?.Peers ?? Array.Empty<HeadsetControlPeer>();
        var capacity = previous?.MaxConnections;
        var peersAvailable = previous?.PeersAvailable ?? false;
        if ((group & ControlGroup.Metadata) != 0)
        {
            name = null;
            charging = null;
            wear = null;
            personalization = null;
            peers = Array.Empty<HeadsetControlPeer>();
            capacity = null;
            peersAvailable = false;
            if (_gate.IsMomentum4 || (_gate.IsMomentum5 && _gate.Writable))
            {
                name = await OptionalReferenceAsync(() => _client.ReadDeviceNameAsync(ct), notes);
                charging = await OptionalValueAsync(() => _client.ReadChargingAsync(ct), notes);
                var wearState = await OptionalValueAsync(() => _client.ReadWearStateAsync(ct), notes);
                wear = wearState is { } state ? (int)state : null;
                personalization = await OptionalValueAsync(() => _client.ReadPersonalizationStateAsync(ct), notes);
                var count = await OptionalValueAsync(() => _client.ReadPeerCountAsync(ct), notes);
                if (count is { } peerCount)
                {
                    var own = await OptionalValueAsync(() => _client.ReadOwnPeerIndexAsync(ct), notes);
                    capacity = await OptionalValueAsync(() => _client.ReadMaxConnectionsAsync(ct), notes);
                    if (own is { } ownIndex && (ownIndex >= peerCount || ownIndex < 0))
                    {
                        throw new ProtocolFormatException("Invalid own peer index", []);
                    }

                    if (capacity is { } maximumConnections && maximumConnections is < 1 or > 64)
                    {
                        throw new ProtocolFormatException("Invalid maximum connections", []);
                    }

                    var entries = new List<HeadsetControlPeer>();
                    peersAvailable = own is not null && capacity is not null;
                    for (var index = 0; index < peerCount; index++)
                    {
                        var selectedIndex = index;
                        var entry = await OptionalReferenceAsync(() => _client.ReadPeerAsync(selectedIndex, ct), notes);
                        if (entry is null)
                        {
                            peersAvailable = false;
                            continue;
                        }

                        entries.Add(new HeadsetControlPeer(entry.Index, entry.Name, entry.ConnectionStatus is 0 or 1 ? entry.ConnectionStatus == 1 : null,
                            entry.Index == own, entry.Priority));
                    }

                    peers = entries;
                }
            }
        }

        var writable = new HashSet<HeadsetControl>();
        var choices = new Dictionary<HeadsetIntegerControl, IReadOnlyList<int>>();
        if (_gate.Writable)
        {
            foreach (var control in Enum.GetValues<HeadsetBooleanControl>())
            {
                if (settings.Get(control) is not null)
                {
                    writable.Add((HeadsetControl)control);
                }
            }

            foreach (var control in Enum.GetValues<HeadsetIntegerControl>())
            {
                if (settings.Get(control) is not null)
                {
                    writable.Add((HeadsetControl)control);
                }
            }

            if (_gate.Equalizer && gains.Count is >= 1 and <= 16 && frequencies.Count == gains.Count)
            {
                writable.Add(HeadsetControl.Equalizer);
            }

            if (name is not null)
            {
                writable.Add(HeadsetControl.Rename);
            }

            if (_gate.IsMomentum4 && peersAvailable && peers.All(peer => peer.Connected is not null)
                && peers.Count(peer => peer.IsThisComputer && peer.Connected == true) == 1)
            {
                writable.Add(HeadsetControl.ConnectPeer);
            }

            if (!_gate.IsMomentum5)
            {
                writable.Remove(HeadsetControl.Sidetone);
            }

            if (!_gate.BassBoost)
            {
                writable.Remove(HeadsetControl.BassBoost);
            }

            if (!_gate.Equalizer)
            {
                writable.Remove(HeadsetControl.SoundMode);
            }

            if (settings is not { ImmersiveAudio: not null, HeadTracking: not null, SoundMode: not null })
            {
                writable.Remove(HeadsetControl.ImmersiveAudio);
                writable.Remove(HeadsetControl.HeadTracking);
            }

            if (settings is not { AncEnabled: not null, AncStrength: not null, Transparency: not null })
            {
                writable.Remove(HeadsetControl.AntiWind);
                writable.Remove(HeadsetControl.AdaptiveAnc);
                writable.Remove(HeadsetControl.AncEnabled);
            }
        }

        if (settings.AutoPowerOff is not null)
        {
            choices[HeadsetIntegerControl.AutoPowerOff] = Array.AsReadOnly(Momentum4State.AutoPowerOffChoices.ToArray());
        }

        if (_gate.IsMomentum5 && _gate.Writable && settings.AntiWind is not null)
        {
            choices[HeadsetIntegerControl.AntiWind] = Array.AsReadOnly(new[] { 0, 1 });
        }

        if (!_gate.Writable)
        {
            notes.Add("Experimental read-only model or firmware; writes require verified device captures.");
        }

        if (settings.SoundMode is not null and not 1)
        {
            notes.Add("EQ gains are stored; select Equalizer sound mode to hear them.");
        }

        ct.ThrowIfCancellationRequested();
        var snapshot = new HeadsetControlSnapshot(identity.ModelId, identity.Firmware, battery, settings,
            Array.AsReadOnly(gains.ToArray()), Array.AsReadOnly(frequencies.ToArray()), minimum, maximum,
            writable.ToFrozenSet(), choices.ToFrozenDictionary(), Array.AsReadOnly(notes.Distinct().Take(32).ToArray()), name, charging, wear,
            personalization, Array.AsReadOnly(peers.ToArray()), capacity, peersAvailable, true);
        _controlSnapshot = snapshot;
        PublishControlSnapshot(snapshot);
        return snapshot;
    }

    private async Task<HeadsetControlSnapshot> ReadControlReadbackAsync(HeadsetControlSnapshot before, ControlGroup group, HeadsetControl control, CancellationToken ct)
    {
        var after = await ReadControlSnapshotAsync(group, ct);
        if (before.Model != after.Model || before.Firmware != after.Firmware)
        {
            throw new ProtocolFormatException("Headset identity changed during write", []);
        }

        if (!after.Writable.Contains(control))
        {
            throw new ProtocolFormatException("Write readback capability unavailable", []);
        }

        if (control == HeadsetControl.Equalizer && !before.EqFrequenciesHz.SequenceEqual(after.EqFrequenciesHz))
        {
            throw new ProtocolFormatException("EQ topology changed during write", []);
        }

        return after;
    }

    private static async Task<T?> OptionalValueAsync<T>(Func<Task<T>> read, List<string> notes) where T : struct
    {
        try
        {
            return await read();
        }
        catch (CommandNotAllowedException exception)
        {
            notes.Add($"Optional read unavailable in command policy: {exception.Command.Name}");
            return null;
        }
        catch (GaiaErrorException exception) when (exception.Frame.Payload.Length == 1)
        {
            notes.Add($"Headset rejected optional read 0x{exception.Command.Command.Value:x4}");
            return null;
        }
    }

    private static async Task<T?> OptionalReferenceAsync<T>(Func<Task<T>> read, List<string> notes) where T : class
    {
        try
        {
            return await read();
        }
        catch (CommandNotAllowedException exception)
        {
            notes.Add($"Optional read unavailable in command policy: {exception.Command.Name}");
            return null;
        }
        catch (GaiaErrorException exception) when (exception.Frame.Payload.Length == 1)
        {
            notes.Add($"Headset rejected optional read 0x{exception.Command.Command.Value:x4}");
            return null;
        }
    }

    private void PublishControlSnapshot(HeadsetControlSnapshot snapshot)
    {
        if (snapshot.Battery is { } battery)
        {
            Store.Apply(new BatteryChanged(battery));
        }

        if (snapshot.Settings is { AncEnabled: not null, AdaptiveAnc: not null, AncStrength: not null, AntiWind: not null, Transparency: not null })
        {
            Store.Apply(new NoiseControlChanged(NoiseState(snapshot.Settings)));
        }

        if (snapshot.EqGainsDb.Count > 0 && snapshot.EqFrequenciesHz.Count == snapshot.EqGainsDb.Count)
        {
            Store.Apply(new EqualizerChanged(EqualizerStateOf(snapshot)));
        }

        if (snapshot.Settings is { OnHeadDetection: { } detection, SmartPause: { } pause, AutoAnswer: { } answer, ComfortCall: { } comfort, TouchControls: { } touch })
        {
            Store.Apply(new BehaviorChanged(new BehaviorState(detection, pause, answer, comfort, touch)));
        }

        if (snapshot.PeersAvailable)
        {
            Store.Apply(new MultipointChanged(MultipointStateOf(snapshot)));
        }

        if (snapshot.DeviceName is { } name)
        {
            Store.Apply(new DeviceNameChanged(name));
        }

        if (snapshot.Settings.AutoPowerOff is { } seconds)
        {
            Store.Apply(new AutoPowerOffChanged(seconds));
        }

        if (snapshot.Settings.AudioPrompts is { } prompts)
        {
            Store.Apply(new PromptModeChanged((AudioPromptMode)prompts));
        }

        if (snapshot.Settings.PauseOnTransparency is { } autoPause)
        {
            Store.Apply(new AutoPauseChanged(autoPause));
        }
    }
}
