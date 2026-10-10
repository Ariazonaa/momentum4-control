import argparse
from dataclasses import asdict
import json
import math
import sys
import threading

from .backend import BluetoothClient, ControlError, Device, Snapshot, discover

_BOOLEAN = ('anc_enabled', 'adaptive_anc', 'transparency', 'touch_controls', 'smart_pause',
            'on_head_detection', 'auto_answer', 'comfort_call', 'bass_boost',
            'pause_on_transparency', 'immersive_audio', 'head_tracking', 'battery_protection')
_CODECS = {'sbc': 'SBC', 'aptx-classic': 'APTX', 'aptx-adaptive': 'APTX_ADAPTIVE',
           'aptx-lossless': 'APTX_LOSSLESS', 'aptx-lite': 'APTX_LITE', 'lc3': 'LC3'}
_INTEGER = {'anc_strength': 100, 'transparency_level': 100, 'antiwind': 2, 'audio_prompts': 2, 'sound_mode': 2, 'auto_power_off': 3600, 'sidetone': 5}


def discover_dongles():
    from .dongle import discover_dongles as discover_usb
    return discover_usb()


def read_dongle_status(device, cancelled: threading.Event | None = None):
    from .dongle import read_status
    return read_status(device, cancelled=cancelled.is_set if cancelled is not None else None)


def set_dongle_codec(device, codec, cancelled: threading.Event | None = None):
    from .dongle import set_codec
    return set_codec(device, codec, cancelled=cancelled.is_set if cancelled is not None else None)


def check_dongle_update(device, cancelled: threading.Event | None = None):
    from .firmware import check_for_update
    return check_for_update(device, cancelled=cancelled.is_set if cancelled is not None else None)


def apply_dongle_update(plan, progress, cancelled: threading.Event | None = None):
    from .firmware_update import apply_update
    return apply_update(plan, progress, cancelled=cancelled.is_set if cancelled is not None else None)


def _dongle_command(args, cancelled):
    devices = discover_dongles()
    if args.dongle is not None:
        if not 1 <= args.dongle <= len(devices):
            raise ControlError('No matching BTD 700; choose an available --dongle INDEX')
        devices = [devices[args.dongle - 1]]
    if not devices:
        raise ControlError('No BTD 700 detected; connect the USB dongle and check its uaccess permission')
    if len(devices) != 1:
        raise ControlError('Multiple BTD 700 dongles detected; select one with --dongle INDEX (starting at 1)')
    device = devices[0]
    if args.command in ('dongle-status', 'dongle-codec'):
        if args.command == 'dongle-codec':
            from .dongle import Codec
            status = set_dongle_codec(device, Codec[_CODECS[args.codec]], cancelled=cancelled)
        else:
            status = read_dongle_status(device, cancelled=cancelled)
        state = {0: 'Disconnected', 1: 'Disconnected', 2: 'Connected', 3: 'Streaming audio', 4: 'Voice streaming'}.get(status.state, 'Unknown audio state')
        codec = {1: 'SBC', 2: 'aptX Classic', 4: 'aptX Adaptive', 8: 'aptX Lossless',
                 16: 'aptX Lite (QMAP)', 32: 'LC3'}.get(status.codec_mask, 'Unknown codec')
        values = dict(firmware=str(status.firmware), state=int(status.state), audio_state=state, codec=codec,
            audio_mode=status.audio_mode, transport=status.transport, codec_mask=status.codec_mask,
            sample_bits=status.sample_bits, sample_rate=status.sample_rate,
            supported_codec_mask=getattr(status, 'supported_codec_mask', 0))
    else:
        if args.command == 'dongle-update':
            status = read_dongle_status(device, cancelled=cancelled)
            if status.state not in (0, 1, 2):
                raise ControlError('Stop audio playback/calls before updating BTD 700 firmware')
        plan = check_dongle_update(device, cancelled=cancelled)
        if plan.device != device:
            raise ControlError('Update preparation returned a different dongle')
        values = dict(current=str(plan.current.firmware), current_config=plan.current.config,
            latest=str(plan.target), available=plan.available, reason=plan.reason,
            experimental=True, hardware_flashing_verified=False, recovery_verified=False)
        if args.command == 'dongle-update':
            if not plan.available:
                raise ControlError(plan.reason)
            print(f'Experimental BTD 700 update {plan.current.firmware} → {plan.target}: audio interruption/reboot; no rollback; interrupted recovery is not hardware-verified.', file=sys.stderr)
            def progress(stage, percent):
                print(f'{stage}: {percent}%', file=sys.stderr)
            result = apply_dongle_update(plan, progress, cancelled=cancelled)
            values = dict(firmware=str(result.firmware), config=result.config, verified=True)
    if args.json:
        print(json.dumps(values, ensure_ascii=True))
    else:
        for name, value in values.items():
            print(f'{name}: {value}')
    return 0


def _print_snapshot(snapshot: Snapshot, as_json: bool):
    values = asdict(snapshot)
    values['writable'] = sorted(snapshot.writable)
    if as_json:
        print(json.dumps(values, ensure_ascii=True))
        return
    print(f'Model: {snapshot.model}\nFirmware: {snapshot.firmware}')
    print(f'Battery: {snapshot.battery}%' if snapshot.battery is not None else 'Battery: unavailable')
    for name, value in snapshot.settings.items():
        print(f'{name}: {value}')
    for band, gain in enumerate(snapshot.eq_bands):
        frequency = f' ({snapshot.eq_frequencies[band]} Hz)' if band < len(snapshot.eq_frequencies) else ''
        print(f'EQ {band}{frequency}: {gain:.1f} dB')
    for note in snapshot.notes:
        print(note)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description='Momentum headset controls over Bluetooth and BTD 700 USB audio/firmware')
    sub = parser.add_subparsers(dest='command', required=True)
    for command in ('devices', 'status', 'set', 'eq', 'rename', 'peers', 'connect-peer'):
        child = sub.add_parser(command)
        child.add_argument('--json', action='store_true')
        if command != 'devices':
            child.add_argument('--device', help='Paired Bluetooth address; required when multiple headsets are present')
        if command == 'set':
            child.add_argument('name', choices=(*_BOOLEAN, *_INTEGER))
            child.add_argument('value', help='true/false for switches; integer for levels and modes')
        if command == 'rename':
            child.add_argument('name', help='Printable name within the verified device UTF-8 byte limit')
        if command == 'connect-peer':
            child.add_argument('index', type=int, help='Peer index from peers')
            child.add_argument('--expected-name', required=True, help='Exact peer name from peers; stale identity is refused')
        if command == 'eq':
            child.add_argument('band', type=int, help='Zero-based device EQ band')
            child.add_argument('gain', type=float, help='Gain in 0.1 dB steps within the headset range')
    for command in ('dongle-status', 'dongle-check', 'dongle-update', 'dongle-codec'):
        child = sub.add_parser(command)
        child.add_argument('--json', action='store_true')
        child.add_argument('--dongle', type=int, help='Dongle index starting at 1; required when multiple are present')
        if command == 'dongle-codec':
            child.add_argument('codec', choices=tuple(_CODECS), help='Supported Bluetooth codec; switching can briefly reconnect audio')
        if command == 'dongle-update':
            child.add_argument('--confirm', action='store_true', help='Confirm experimental transfer, audio interruption/reboot, no rollback, and unverified interrupted recovery')
    args = parser.parse_args(argv)
    if args.command == 'dongle-update' and not args.confirm:
        parser.error('dongle-update requires --confirm: experimental transfer interrupts audio/reboots; no rollback; recovery is hardware-unverified')
    value: bool | int | float = False
    if args.command == 'set':
        if args.name in _BOOLEAN:
            if args.value.lower() not in ('true', 'false', 'on', 'off', '1', '0'):
                parser.error('switch values must be true/false or on/off')
            value = args.value.lower() in ('true', 'on', '1')
        else:
            try:
                value = int(args.value)
            except ValueError:
                parser.error('setting value must be an integer')
            if args.name == 'auto_power_off' and value not in (0, 900, 1800, 3600):
                parser.error('auto_power_off must be 0, 900, 1800 or 3600 seconds')
            if not 0 <= value <= _INTEGER[args.name]:
                parser.error('setting value is outside the allowed range')
    if args.command == 'rename' and (not args.name.isprintable() or not args.name.strip() or len(args.name.encode('utf-8')) > 30):
        parser.error('rename requires a printable device name within 30 UTF-8 bytes; M5 allows 16')
    if args.command == 'connect-peer' and (not 0 <= args.index < 64 or not args.expected_name.isprintable() or not args.expected_name or len(args.expected_name.encode('utf-8')) > 128):
        parser.error('connect-peer requires index 0..63 and the exact printable peer name')
    if args.command == 'eq' and (not 0 <= args.band < 16 or not math.isfinite(args.gain)):
        parser.error('EQ requires a band from 0 to 15 and a finite gain')
    cancelled = threading.Event()
    client = None
    try:
        if args.command.startswith('dongle-'):
            return _dongle_command(args, cancelled)
        devices = discover(cancelled)
        if args.command == 'devices':
            if args.json:
                print(json.dumps([asdict(device) for device in devices], ensure_ascii=True))
            else:
                for device in devices:
                    print(f'{device.address}  {device.name!r}')
                if not devices:
                    print('No paired GAIA headset found. Enable Bluetooth and pair the headset in system settings.')
            return 0
        if args.device:
            devices = [device for device in devices if device.address.upper() == args.device.upper()]
        if not devices:
            raise ControlError('No matching headset: enable Bluetooth and pair a GAIA headset in system settings')
        if len(devices) != 1:
            raise ControlError('Multiple headsets found; select one with --device ADDRESS')
        client = BluetoothClient(devices[0], cancelled)
        client.connect()
        if args.command == 'rename':
            snapshot = client.rename(args.name)
        elif args.command == 'connect-peer':
            state = client.snapshot()
            peers = [peer for peer in state.peers if peer.index == args.index and peer.name == args.expected_name]
            if len(peers) != 1:
                raise ControlError('Peer identity changed; read peers again')
            snapshot = client.connect_peer(peers[0])
        elif args.command == 'peers':
            snapshot = client.snapshot()
            if args.json:
                print(json.dumps(dict(peers=[asdict(peer) for peer in snapshot.peers], max_connections=snapshot.max_connections, available=snapshot.peers_available, writable='connect_peer' in snapshot.writable), ensure_ascii=True))
            else:
                print(f'Connection capacity: {snapshot.max_connections if snapshot.max_connections is not None else "unavailable"}')
                for peer in snapshot.peers:
                    print(f'{peer.index}: {peer.name!r} · {"connected" if peer.connected is True else "disconnected" if peer.connected is False else "unknown"}' + (' · this PC' if peer.is_this_pc else ''))
                if 'connect_peer' not in snapshot.writable:
                    print('Peer connection is read-only on this model or firmware.')
            return 0
        elif args.command == 'set':
            snapshot = client.set_value(args.name, value)
        elif args.command == 'eq':
            snapshot = client.set_value('eq', args.gain, args.band)
        else:
            snapshot = client.snapshot()
        _print_snapshot(snapshot, args.json)
        return 0
    except Exception as error:
        print(f'momentumctl: {error}', file=sys.stderr)
        if args.command == 'dongle-update':
            from .firmware_update import UpdateError
            if isinstance(error, UpdateError) and error.interrupted:
                return 130
        return 1
    except KeyboardInterrupt:
        cancelled.set()
        if client is not None:
            client.cancel()
        return 130
    finally:
        if client is not None:
            client.close()
        if args.command.startswith('dongle-'):
            from .dongle import close
            close()


if __name__ == '__main__':
    raise SystemExit(main())
