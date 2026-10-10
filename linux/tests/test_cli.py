import contextlib
import io
import json
import unittest
from unittest.mock import patch

try:
    from momentum_control import cli
except ImportError:
    cli = None


class CliTests(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(cli, 'Linux CLI implementation is required')

    def test_help_no_bluetooth(self):
        with patch.object(cli, 'discover', side_effect=AssertionError('unexpected Bluetooth discovery')):
            with contextlib.redirect_stdout(io.StringIO()), self.assertRaises(SystemExit) as result:
                cli.main(['--help'])
        self.assertEqual(result.exception.code, 0)

    def test_devices_json(self):
        device = cli.Device('00:11:22:33:44:55', 'Renamed', '/org/bluez/hci0/dev_00_11_22_33_44_55', False)
        output = io.StringIO()
        with patch.object(cli, 'discover', return_value=[device]), contextlib.redirect_stdout(output):
            self.assertEqual(cli.main(['devices', '--json']), 0)
        self.assertEqual(json.loads(output.getvalue())[0]['name'], 'Renamed')

    def test_ambiguous_device_rejected(self):
        device = cli.Device('00:11:22:33:44:55', 'Renamed', '/org/bluez/hci0/dev_00_11_22_33_44_55', False)
        with patch.object(cli, 'discover', return_value=[device, device]), contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(cli.main(['status']), 1)

    def test_no_device_error_actionable(self):
        output = io.StringIO()
        with patch.object(cli, 'discover', return_value=[]), contextlib.redirect_stderr(output):
            self.assertEqual(cli.main(['status']), 1)
        self.assertIn('pair', output.getvalue())

    def test_invalid_and_destructive_commands_rejected(self):
        for args in (['raw', '0040'], ['reset'], ['set', 'anc_strength', 'abc'], ['eq', '0', 'nan']):
            with self.subTest(args=args), patch.object(cli, 'discover', side_effect=AssertionError('invalid action reached Bluetooth')):
                with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as result:
                    cli.main(args)
                self.assertEqual(result.exception.code, 2)


class DongleCliTests(unittest.TestCase):
    def test_unconfirmed_update_is_rejected_before_any_hardware_or_network(self):
        output = io.StringIO()
        with patch.object(cli, 'discover', side_effect=AssertionError('Bluetooth access')), patch.object(cli, 'discover_dongles', side_effect=AssertionError('USB access')), patch.object(cli, 'check_dongle_update', side_effect=AssertionError('Firmware access')), contextlib.redirect_stderr(output):
            with self.assertRaises(SystemExit) as result:
                cli.main(['dongle-update'])
        self.assertEqual(result.exception.code, 2)
        self.assertIn('--confirm', output.getvalue())

    def test_status_works_without_bluetooth_and_does_not_leak_identity(self):
        from types import SimpleNamespace
        device = SimpleNamespace(identity='private-serial', control_path='/fake', usb_path='/private', upgrade_path='/fake2')
        status = SimpleNamespace(device=device, firmware='3.11.0', state=3, audio_mode=1,
            transport=1, codec_mask=4, sample_bits=24, sample_rate=96000)
        output = io.StringIO()
        with patch.object(cli, 'discover_dongles', return_value=[device], create=True), patch.object(cli, 'read_dongle_status', return_value=status, create=True), patch.object(cli, 'discover', side_effect=AssertionError('Bluetooth access')), contextlib.redirect_stdout(output):
            self.assertEqual(cli.main(['dongle-status', '--json']), 0)
        data = json.loads(output.getvalue())
        self.assertEqual(data['audio_state'], 'Streaming audio')
        self.assertEqual(data['firmware'], '3.11.0')
        self.assertNotIn('private', output.getvalue())

    def test_ambiguous_dongle_requires_explicit_index(self):
        from types import SimpleNamespace
        device = SimpleNamespace(identity='private', control_path='/fake')
        output = io.StringIO()
        with patch.object(cli, 'discover_dongles', return_value=[device, device], create=True), contextlib.redirect_stderr(output):
            self.assertEqual(cli.main(['dongle-check']), 1)
        self.assertIn('--dongle', output.getvalue())

    def test_latest_check_and_streaming_update_never_flash(self):
        from types import SimpleNamespace
        device = SimpleNamespace(identity='private', control_path='/fake')
        plan = SimpleNamespace(device=device, current=SimpleNamespace(firmware='3.11.0', config=1),
            target='3.11.0', available=False, image=None, reason='No newer firmware available')
        status = SimpleNamespace(device=device, firmware='3.11.0', state=3)
        with patch.object(cli, 'discover_dongles', return_value=[device], create=True), patch.object(cli, 'check_dongle_update', return_value=plan, create=True), patch.object(cli, 'read_dongle_status', return_value=status, create=True), patch.object(cli, 'apply_dongle_update', side_effect=AssertionError('Unexpected flash'), create=True):
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                self.assertEqual(cli.main(['dongle-check', '--json']), 0)
            self.assertFalse(json.loads(output.getvalue())['available'])
            with contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(cli.main(['dongle-update', '--confirm']), 1)

    def test_interrupted_update_retains_stage_recovery_error_and_exit130(self):
        from types import SimpleNamespace
        from momentum_control.firmware_update import UpdateError
        device = SimpleNamespace(identity='private', control_path='/fake')
        plan = SimpleNamespace(device=device, current=SimpleNamespace(firmware='3.11.0', config=1),
            target='3.12.0', available=True, image=object(), reason='New firmware')
        error = UpdateError('transfer', 'Interrupted by user', True, interrupted=True)
        output = io.StringIO()
        with patch.object(cli, 'discover_dongles', return_value=[device]), patch.object(cli, 'read_dongle_status', return_value=SimpleNamespace(state=2)), patch.object(cli, 'check_dongle_update', return_value=plan), patch.object(cli, 'apply_dongle_update', side_effect=error), contextlib.redirect_stderr(output):
            self.assertEqual(cli.main(['dongle-update', '--confirm']), 130)
        self.assertIn('transfer', output.getvalue())
        self.assertIn('recovery required', output.getvalue())
