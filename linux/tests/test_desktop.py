from dataclasses import replace
import json
import os
from pathlib import Path
import stat
import tempfile
import unittest
from unittest.mock import patch
import zipfile

from momentum_control import __version__
from momentum_control.backend import Snapshot, PeerDevice
from momentum_control.desktop import autostart_enabled, export_diagnostics, set_autostart
from momentum_control.preferences import PreferenceError


class DesktopTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.path = self.directory / 'autostart' / 'momentum-control.desktop'
        self.launcher = self.directory / 'app bin' / 'momentum-control'
        self.output = self.directory / 'diagnostics.zip'
        self.snapshot = Snapshot('M5AEBT Black', '6.21.5', 87,
                                 {'anc_enabled': True, 'anc_strength': 75, 'sound_mode': 1, 'antiwind': 1},
                                 (0.0, 1.2), (90, 90), frozenset(('anc_enabled', 'eq', 'antiwind')), ('private message',))

    def diagnostic(self):
        with zipfile.ZipFile(self.output) as archive:
            self.assertEqual(archive.namelist(), ['diagnostics.json'])
            return json.loads(archive.read('diagnostics.json'))

    def test_autostart_enable_update_disable_and_private_permissions(self):
        self.assertFalse(autostart_enabled(self.path))
        self.assertFalse(self.path.parent.exists())
        set_autostart(self.path, self.launcher, True)
        self.assertTrue(autostart_enabled(self.path))
        self.assertIn(f'Exec="{self.launcher}"\n', self.path.read_text())
        self.assertEqual(stat.S_IMODE(self.path.stat().st_mode), 0o600)
        self.assertEqual(stat.S_IMODE(self.path.parent.stat().st_mode), 0o700)
        set_autostart(self.path, self.launcher, True, minimized=True)
        self.assertIn(f'Exec="{self.launcher}" --minimized\n', self.path.read_text())
        set_autostart(self.path, self.launcher, False)
        self.assertFalse(self.path.exists())
        set_autostart(self.path, self.launcher, False)

    def test_unrelated_or_oversized_autostart_is_never_adopted(self):
        self.path.parent.mkdir()
        for content in (b'[Desktop Entry]\nName=Other\nExec=/bin/true\n', b'X-Momentum-Control-Managed=true\n', b'x' * 8193):
            self.path.write_bytes(content)
            for action in (lambda: autostart_enabled(self.path), lambda: set_autostart(self.path, self.launcher, True), lambda: set_autostart(self.path, self.launcher, False)):
                with self.assertRaises(PreferenceError):
                    action()
                self.assertEqual(self.path.read_bytes(), content)

    def test_exec_quotes_all_desktop_special_characters(self):
        launcher = Path('/opt/app a"b$c`d\\e%f/momentum-control')
        set_autostart(self.path, launcher, True, True)
        exec_line = next(line for line in self.path.read_text().splitlines() if line.startswith('Exec='))
        self.assertEqual(exec_line, 'Exec="/opt/app a\\\\"b\\\\$c\\\\`d\\\\\\\\e%%f/momentum-control" --minimized')

    def test_autostart_rejects_relative_control_characters_and_nonbooleans(self):
        for launcher, enabled, minimized in ((Path('relative'), True, False), (Path('/opt/a\nb'), True, False), (Path('/opt/a\tb'), True, False), (self.launcher, 1, False), (self.launcher, True, 1)):
            with self.assertRaises(PreferenceError):
                set_autostart(self.path, launcher, enabled, minimized)
            self.assertFalse(self.path.exists())

    def test_symlinks_and_nonregular_desktop_targets_are_refused(self):
        self.path.parent.mkdir()
        target = self.directory / 'private'
        target.write_bytes(b'preserved')
        self.path.symlink_to(target)
        self.output.symlink_to(target)
        for action in (lambda: autostart_enabled(self.path), lambda: set_autostart(self.path, self.launcher, True), lambda: set_autostart(self.path, self.launcher, False), lambda: export_diagnostics(self.output, self.snapshot)):
            with self.assertRaises(PreferenceError):
                action()
        self.assertEqual(target.read_bytes(), b'preserved')
        self.output.unlink()
        os.mkfifo(self.output)
        with self.assertRaises(PreferenceError):
            export_diagnostics(self.output, self.snapshot)
        self.path.unlink()
        self.path.parent.rmdir()
        self.path.parent.symlink_to(self.directory, target_is_directory=True)
        with self.assertRaises(PreferenceError):
            set_autostart(self.path, self.launcher, True)
        with self.assertRaises(PreferenceError):
            export_diagnostics(self.path, self.snapshot)

    def test_diagnostics_exact_redacted_schema(self):
        state = replace(self.snapshot, settings={**self.snapshot.settings, 'device_address': 'private-address', 'notes': 'private-note'}, writable=self.snapshot.writable | {'private@example.org'})
        export_diagnostics(self.output, state, ({'operation': 'refresh', 'status': 'ok', 'elapsed_ms': 12.5, 'error': 'private failure'},))
        self.assertEqual(self.diagnostic(), {
            'app': {'platform': 'linux', 'version': __version__}, 'model_family': 'M5', 'firmware': [6, 21, 5], 'battery': 87,
            'settings': {'anc_enabled': True, 'anc_strength': 75, 'sound_mode': 1, 'antiwind': 1},
            'eq_gains': [0.0, 1.2], 'eq_frequencies': [90, 90], 'writable': ['anc_enabled', 'antiwind', 'eq'],
            'events': [{'operation': 'refresh', 'status': 'ok', 'elapsed_ms': 12.5}]})
        self.assertEqual(stat.S_IMODE(self.output.stat().st_mode), 0o600)

    def test_parity_diagnostics_exact_redaction_and_observed_domains(self):
        settings = {'pause_on_transparency': True, 'immersive_audio': False, 'head_tracking': True,
                    'battery_protection': False, 'auto_power_off': 65535, 'sidetone': 5}
        operations = ('poll', 'preset', 'rename', 'connect-peer', 'project-release', 'location-sample', 'reconnect')
        events = tuple({'operation': name, 'status': 'ok', 'elapsed_ms': index,
                        'name': 'private name', 'location': [52., 13.], 'peer': 'private peer',
                        'error': 'private exception'} for index, name in enumerate(operations))
        state = replace(self.snapshot,
            settings={**settings, 'location': 'private location', 'device_address': 'private address', 'peer': 'private peer'},
            choices={'auto_power_off': (0, 900, 1800, 3600), 'sidetone': (0,)},
            writable=frozenset((*settings, 'eq', 'rename', 'private address')),
            device_name='private device', peers=(PeerDevice(0, 'private peer', True, True),),
            charging=True, wear_state=1, personalization_state=2, max_connections=2, peers_available=True)
        export_diagnostics(self.output, state)
        self.assertEqual(self.diagnostic()['settings'], settings)
        try:
            export_diagnostics(self.output, state, events)
        except PreferenceError as error:
            self.fail(f'Actual GUI operation was rejected: {error}')
        self.assertEqual(self.diagnostic(), {
            'app': {'platform': 'linux', 'version': __version__}, 'model_family': 'M5', 'firmware': [6, 21, 5], 'battery': 87,
            'settings': settings, 'eq_gains': [0.0, 1.2], 'eq_frequencies': [90, 90],
            'writable': sorted((*settings, 'eq')),
            'events': [{'operation': name, 'status': 'ok', 'elapsed_ms': index} for index, name in enumerate(operations)]})
        export_diagnostics(self.output, replace(state, settings={'auto_power_off': 0, 'sidetone': 0}))
        self.assertEqual(self.diagnostic()['settings'], {'auto_power_off': 0, 'sidetone': 0})

    def test_parity_diagnostics_reject_invalid_allowed_values_preserving_target(self):
        self.output.write_bytes(b'preserved')
        invalid = [(name, value) for name in ('pause_on_transparency', 'immersive_audio', 'head_tracking', 'battery_protection')
                   for value in (0, 1, None, 'private')]
        invalid += [(name, value) for name, values in (
            ('auto_power_off', (-1, 65536, True, 900.0, 'private')),
            ('sidetone', (-1, 6, True, 1.0, 'private'))) for value in values]
        for name, value in invalid:
            with self.subTest(name=name, value=value), self.assertRaises(PreferenceError):
                export_diagnostics(self.output, replace(self.snapshot, settings={name: value}))
            self.assertEqual(self.output.read_bytes(), b'preserved')
        for operation in ('location:52,13', 'peer:private', 'rename private device'):
            with self.subTest(operation=operation), self.assertRaises(PreferenceError):
                export_diagnostics(self.output, self.snapshot, ({'operation': operation, 'status': 'ok', 'elapsed_ms': 1},))
            self.assertEqual(self.output.read_bytes(), b'preserved')

    def test_diagnostics_model_family_never_copies_free_text(self):
        for model, family in (('M4AEBT White', 'M4'), ('M5AEBT Black', 'M5'), ('M5AEBTEvil', 'unknown'), ('private@example.org', 'unknown')):
            export_diagnostics(self.output, replace(self.snapshot, model=model))
            self.assertEqual(self.diagnostic()['model_family'], family)

    def test_diagnostics_rejects_invalid_allowed_values_without_replacing_target(self):
        self.output.write_bytes(b'preserved')
        states = [replace(self.snapshot, firmware=value) for value in ('6.21.65536', 'private@example.org', '6.21.True')]
        states += [replace(self.snapshot, battery=value) for value in (True, -1, 101, 'private')]
        states += [replace(self.snapshot, settings={key: value}) for key, value in (('anc_enabled', 1), ('anc_strength', True), ('anc_strength', 101), ('antiwind', 3), ('audio_prompts', 3), ('sound_mode', 4), ('touch_controls', 'private'))]
        states += [replace(self.snapshot, eq_bands=(float('nan'), 0.0)), replace(self.snapshot, eq_bands=(True, 0.0)), replace(self.snapshot, eq_frequencies=(48001, 90)), replace(self.snapshot, eq_bands=(0.0,) * 17, eq_frequencies=(90,) * 17), replace(self.snapshot, eq_frequencies=())]
        for state in states:
            with self.subTest(state=state):
                with self.assertRaises(PreferenceError):
                    export_diagnostics(self.output, state)
                self.assertEqual(self.output.read_bytes(), b'preserved')

    def test_diagnostics_exports_observed_readonly_value_outside_write_choices(self):
        state = replace(self.snapshot, settings={'antiwind': 2}, choices={'antiwind': (0, 1)})
        export_diagnostics(self.output, state)
        diagnostic = self.diagnostic()
        self.assertEqual(diagnostic['settings'], {'antiwind': 2})
        self.assertEqual(diagnostic['writable'], ['antiwind', 'eq'])
        self.assertNotIn('choices', diagnostic)

    def test_diagnostics_event_value_allowlist_and_limits(self):
        event = {'operation': 'refresh', 'status': 'ok', 'elapsed_ms': 12}
        export_diagnostics(self.output, self.snapshot, (event,) * 2000)
        self.assertEqual(len(self.diagnostic()['events']), 2000)
        original = self.output.read_bytes()
        for events in ((event,) * 2001, ({**event, 'operation': 'private@example.org'},), ({**event, 'status': 'private failure'},), ({**event, 'elapsed_ms': True},), ({**event, 'elapsed_ms': float('nan')},), ({**event, 'elapsed_ms': 300001},), ({**event, 'elapsed_ms': -1},), ({'operation': 'refresh'},)):
            with self.subTest(events=events[:1]):
                with self.assertRaises(PreferenceError):
                    export_diagnostics(self.output, self.snapshot, events)
                self.assertEqual(self.output.read_bytes(), original)

    def test_diagnostic_serialized_size_guard_preserves_target(self):
        self.output.write_bytes(b'preserved')
        with patch('momentum_control.desktop.json.dumps', return_value=' ' * (1024 * 1024 + 1)):
            with self.assertRaises(PreferenceError):
                export_diagnostics(self.output, self.snapshot)
        self.assertEqual(self.output.read_bytes(), b'preserved')

    def test_atomic_export_and_autostart_failure_preserves_original(self):
        set_autostart(self.path, self.launcher, True)
        export_diagnostics(self.output, self.snapshot)
        originals = {self.path: self.path.read_bytes(), self.output: self.output.read_bytes()}
        with patch('momentum_control.preferences.os.replace', side_effect=OSError('disk full')):
            for action in (lambda: set_autostart(self.path, self.launcher, True, True), lambda: export_diagnostics(self.output, self.snapshot)):
                with self.assertRaises(PreferenceError):
                    action()
        for path, original in originals.items():
            self.assertEqual(path.read_bytes(), original)
        self.assertEqual(list(self.directory.rglob('*.tmp')), [])


if __name__ == '__main__':
    unittest.main()
