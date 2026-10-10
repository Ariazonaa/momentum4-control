from dataclasses import FrozenInstanceError, replace
import json
import os
from pathlib import Path
import stat
import tempfile
import unittest
from unittest.mock import patch

from momentum_control.preferences import AppData, EqPreset, PreferenceError, Preferences, PreferenceStore


class PreferenceTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.path = Path(self.temporary.name) / 'settings' / 'preferences.json'
        self.store = PreferenceStore(self.path)
        self.preset = EqPreset('../Warm', 'M4AEBT Black', (90, 90, 1500), (-6.0, 0.1, 6.0))
        self.data = AppData(Preferences(), (self.preset,))

    def document(self):
        self.store.save(self.data)
        return json.loads(self.path.read_text())

    def test_missing_defaults_are_frozen_and_do_not_write(self):
        data = self.store.load()
        self.assertEqual(data, AppData(Preferences(), ()))
        self.assertEqual(data.preferences.refresh_seconds, 30)
        self.assertFalse(self.path.parent.exists())
        with self.assertRaises(FrozenInstanceError):
            data.preferences.language = 'de'

    def test_round_trip_duplicate_frequencies_and_private_permissions(self):
        self.store.save(self.data)
        self.assertEqual(self.store.load(), self.data)
        self.assertEqual(sorted(item.name for item in self.path.parent.iterdir()), ['preferences.json'])
        self.assertEqual(stat.S_IMODE(self.path.stat().st_mode), 0o600)
        self.assertEqual(stat.S_IMODE(self.path.parent.stat().st_mode), 0o700)
        changed = replace(self.data, preferences=replace(self.data.preferences, language='de', last_device_address='AA:BB:CC:DD:EE:FF'))
        self.store.save(changed)
        self.assertEqual(self.store.load(), changed)

    def test_exact_model_order_duplicate_and_band_counts(self):
        self.assertTrue(self.preset.matches('M4AEBT Black', (90, 90, 1500)))
        for model, frequencies in (('M5AEBT Black', (90, 90, 1500)), ('M4AEBT Black', (90, 1500, 90)), ('M4AEBT Black', (90, 1500, 1500)), ('M4AEBT Black', (90, 1500))):
            with self.subTest(model=model, frequencies=frequencies):
                self.assertFalse(self.preset.matches(model, frequencies))

    def test_invalid_json_preserved_on_load_and_save(self):
        document = self.document()
        candidates = [b'{', b'[]', b'{"version":1,"version":1}', b'[' * 2000, b' ' * (256 * 1024 + 1)]
        for key, value in (('version', True), ('version', 2), ('unknown', 1), ('presets', {})):
            changed = dict(document)
            changed[key] = value
            candidates.append(json.dumps(changed).encode())
        for content in candidates:
            with self.subTest(content=content[:70]):
                self.path.write_bytes(content)
                with self.assertRaises(PreferenceError):
                    self.store.load()
                with self.assertRaises(PreferenceError):
                    self.store.save(AppData(Preferences(), ()))
                self.assertEqual(self.path.read_bytes(), content)

    def test_bounded_read_precedes_parse(self):
        self.path.parent.mkdir()
        self.path.write_bytes(b' ' * (256 * 1024 + 1))
        with patch('momentum_control.preferences.json.loads', side_effect=AssertionError('parsed oversized input')):
            with self.assertRaises(PreferenceError):
                self.store.load()

    def test_stream_read_is_bounded_before_allocation(self):
        self.store.save(self.data)
        real_fdopen = os.fdopen
        requested = []
        case = self

        class CheckedReader:
            def __init__(self, descriptor, mode):
                self.stream = real_fdopen(descriptor, mode)

            def __enter__(self):
                return self

            def __exit__(self, *args):
                self.stream.close()

            def fileno(self):
                return self.stream.fileno()

            def read(self, size=-1):
                requested.append(size)
                case.assertGreater(size, 0)
                case.assertLessEqual(size, 262145)
                return self.stream.read(size)

        with patch('momentum_control.preferences.os.fdopen', CheckedReader):
            self.assertEqual(self.store.load(), self.data)
        self.assertEqual(requested, [262145])

    def test_invalid_preferences_are_rejected_without_writes(self):
        for field, value in (('refresh_seconds', True), ('refresh_seconds', 1), ('refresh_seconds', 30.0), ('tray_enabled', 1), ('language', 'fr'), ('last_device_address', 'aa:bb:cc:dd:ee:ff'), ('last_device_address', 'private@example.org')):
            with self.subTest(field=field, value=value):
                with self.assertRaises(PreferenceError):
                    self.store.save(AppData(replace(Preferences(), **{field: value}), ()))
                self.assertFalse(self.path.exists())

    def test_invalid_presets_are_rejected(self):
        cases = [('name', ''), ('name', 'x' * 41), ('name', 'a\nb'), ('model', 'x' * 129), ('model', ''), ('frequencies', (0, 90, 1500)), ('frequencies', (True, 90, 1500)), ('frequencies', (48001, 90, 1500)), ('frequencies', (90,) * 17), ('gains', (0.0,)), ('gains', (float('nan'), 0, 0)), ('gains', (float('inf'), 0, 0)), ('gains', (True, 0, 0)), ('gains', (6.1, 0, 0)), ('gains', (0.11, 0, 0))]
        for field, value in cases:
            with self.subTest(field=field, value=value):
                with self.assertRaises(PreferenceError):
                    self.store.save(AppData(Preferences(), (replace(self.preset, **{field: value}),)))
                self.assertFalse(self.path.exists())

    def test_json_rejects_unknown_missing_duplicate_and_nonfinite_fields(self):
        original = self.document()
        candidates = []
        for target, field, value in (('preferences', 'tray_enabled', 1), ('preferences', 'unknown', True), ('preset', 'gains', [float('nan'), 0, 0]), ('preset', 'gains', [True, 0, 0]), ('preset', 'unknown', 'private')):
            data = json.loads(json.dumps(original))
            (data['presets'][0] if target == 'preset' else data['preferences'])[field] = value
            candidates.append(json.dumps(data))
        data = json.loads(json.dumps(original))
        del data['preferences']['language']
        candidates.append(json.dumps(data))
        candidates.append(json.dumps(original).replace('"language": "en"', '"language": "en", "language": "de"'))
        for content in candidates:
            with self.subTest(content=content[:90]):
                self.path.write_text(content)
                with self.assertRaises(PreferenceError):
                    self.store.load()

    def test_preset_count_and_normalized_identity(self):
        presets = tuple(replace(self.preset, name=str(index)) for index in range(32))
        self.store.save(AppData(Preferences(), presets))
        self.assertEqual(len(self.store.load().presets), 32)
        for rejected in (presets + (replace(self.preset, name='overflow'),), (replace(self.preset, name=' Warm '), replace(self.preset, name='warm')), (replace(self.preset, name='Café'), replace(self.preset, name='Cafe\u0301'))):
            with self.assertRaises(PreferenceError):
                self.store.save(AppData(Preferences(), rejected))
        accepted = (self.preset, replace(self.preset, model='M5AEBT'), replace(self.preset, frequencies=(90, 1500, 90)))
        self.store.save(AppData(Preferences(), accepted))
        self.assertEqual(self.store.load().presets, accepted)

    def test_maximum_band_topology_and_preference_boundaries(self):
        preset = EqPreset('x' * 40, 'x' * 128, (48000,) * 16, (6.0,) * 16)
        for interval in (0, 10, 30, 60):
            data = AppData(Preferences(refresh_seconds=interval), (preset,))
            self.store.save(data)
            self.assertEqual(self.store.load(), data)
        with self.assertRaises(PreferenceError):
            self.store.save(AppData(Preferences(), (replace(preset, frequencies=(48000,) * 17, gains=(6.0,) * 17),)))

    def test_valid_json_symlink_is_not_read_or_replaced(self):
        self.store.save(self.data)
        target = self.path.with_name('target.json')
        self.path.rename(target)
        self.path.symlink_to(target)
        original = target.read_bytes()
        with self.assertRaises(PreferenceError):
            self.store.load()
        with self.assertRaises(PreferenceError):
            self.store.save(self.data)
        self.assertEqual(target.read_bytes(), original)
        self.assertTrue(self.path.is_symlink())

    def test_symlink_target_parent_and_nonregular_refused(self):
        self.path.parent.mkdir()
        other = Path(self.temporary.name) / 'other'
        other.write_text('private')
        self.path.symlink_to(other)
        for operation in (self.store.load, lambda: self.store.save(self.data)):
            with self.assertRaises(PreferenceError):
                operation()
        self.assertEqual(other.read_text(), 'private')
        self.path.unlink()
        os.mkfifo(self.path)
        with self.assertRaises(PreferenceError):
            self.store.load()
        self.path.unlink()
        self.path.parent.rmdir()
        self.path.parent.symlink_to(Path(self.temporary.name), target_is_directory=True)
        with self.assertRaises(PreferenceError):
            self.store.save(self.data)
        with self.assertRaises(PreferenceError):
            self.store.load()

    def test_atomic_replace_failure_preserves_original_and_cleans_temporary(self):
        self.store.save(self.data)
        original = self.path.read_bytes()
        with patch('momentum_control.preferences.os.replace', side_effect=OSError('disk failure')):
            with self.assertRaises(PreferenceError):
                self.store.save(AppData(Preferences(language='de'), ()))
        self.assertEqual(self.path.read_bytes(), original)
        self.assertEqual([item.name for item in self.path.parent.iterdir()], ['preferences.json'])


if __name__ == '__main__':
    unittest.main()
