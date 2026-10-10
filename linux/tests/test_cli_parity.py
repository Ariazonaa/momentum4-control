import contextlib
import io
from types import SimpleNamespace
from unittest.mock import patch
import unittest
from momentum_control import cli
from momentum_control.backend import Snapshot, PeerDevice


class CliParityTests(unittest.TestCase):
    def setUp(self):
        self.calls = []
        self.peer = PeerDevice(1, 'Phone', False, False, 4)
        self.state = Snapshot('M5AEBT Black', '6.21.5', 50, {}, (), (), frozenset(), ())
        from dataclasses import replace
        self.state = replace(self.state, peers=(self.peer,))
        self.device = cli.Device('AA:BB:CC:DD:EE:FF', 'Headset', '/device', True)
        owner = self
        class Client:
            def __init__(self, device, cancelled):
                pass
            def connect(self):
                owner.calls.append(('connect',))
            def snapshot(self):
                return owner.state
            def set_value(self, name, value):
                owner.calls.append((name, value))
                return owner.state
            def rename(self, name):
                owner.calls.append(('rename', name))
                return owner.state
            def connect_peer(self, peer):
                owner.calls.append(('peer', peer))
                return owner.state
            def close(self):
                owner.calls.append(('close',))
        self.client = Client

    def run_cli(self, args):
        with patch.object(cli, 'discover', return_value=[self.device]), patch.object(cli, 'BluetoothClient', self.client), contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            return cli.main(args)

    def test_named_parity_settings_send_exact_types(self):
        for name, raw, value in (('immersive_audio', 'true', True), ('head_tracking', 'false', False), ('pause_on_transparency', 'true', True), ('auto_power_off', '1800', 1800), ('sidetone', '5', 5)):
            with self.subTest(name=name):
                try:
                    result = self.run_cli(['set', name, raw])
                except SystemExit as error:
                    self.fail(f'Named parity setting rejected: {error}')
                self.assertEqual(result, 0)
                self.assertIn((name, value), self.calls)
                self.assertIs(type(self.calls[-2][1]), type(value))

    def test_auto_power_and_sidetone_invalid_values_reject_before_discovery(self):
        for args in (['set', 'auto_power_off', '1'], ['set', 'auto_power_off', '9000'], ['set', 'sidetone', '6']):
            with self.subTest(args=args), patch.object(cli, 'discover', side_effect=AssertionError('hardware access')), contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as result:
                cli.main(args)
            self.assertEqual(result.exception.code, 2)

    def test_rename_and_peer_identity_route_named_methods(self):
        for args in (['rename', 'Safe name'], ['peers', '--json'], ['connect-peer', '1', '--expected-name', 'Phone']):
            try:
                self.assertEqual(self.run_cli(args), 0)
            except SystemExit as error:
                self.fail(f'Parity command rejected: {error}')
        self.assertIn(('rename', 'Safe name'), self.calls)
        self.assertIn(('peer', self.peer), self.calls)
        self.assertEqual(self.run_cli(['connect-peer', '1', '--expected-name', 'Foreign']), 1)
        self.assertEqual(sum(call[0] == 'peer' for call in self.calls), 1)

    def test_connect_peer_requires_name_before_hardware(self):
        with patch.object(cli, 'discover', side_effect=AssertionError('hardware access')), contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as result:
            cli.main(['connect-peer', '1'])
        self.assertEqual(result.exception.code, 2)
