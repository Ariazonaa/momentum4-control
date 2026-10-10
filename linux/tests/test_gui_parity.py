import os
os.environ.setdefault('QT_QPA_PLATFORM', 'offscreen')
from copy import deepcopy
from dataclasses import replace
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest.mock import patch
import unittest
from PySide6.QtCore import Qt
import test_gui as fixtures
from momentum_control.preferences import AppData, Preferences, PreferenceStore, EqPreset, PreferenceError
from momentum_control.updates import ProjectRelease


class ParityGuiTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        from PySide6.QtWidgets import QApplication
        cls.app = QApplication.instance() or QApplication([])
    wait = fixtures.GuiTests.wait
    connect = fixtures.GuiTests.connect
    tearDown = fixtures.GuiTests.tearDown
    def setUp(self):
        fixtures.GuiTests.setUp(self)
        self.device_snapshot = fixtures.snapshot(frozenset({'anc_enabled', 'adaptive_anc', 'transparency', 'anc_strength', 'smart_pause', 'touch_controls', 'eq', 'auto_power_off', 'sidetone', 'immersive_audio', 'head_tracking', 'rename'}), 'M5AEBT Black')
        self.device_snapshot.firmware = '6.21.5'
        self.device_snapshot.settings.update(adaptive_anc=False, transparency=False, smart_pause=True, touch_controls=True, auto_power_off=900, sidetone=1, immersive_audio=True, head_tracking=False, sound_mode=1)
        self.device_snapshot.device_name = '<b>Device</b>'
        self.device_snapshot.peers = ()
        self.device_snapshot.peers_available = True
        self.device_snapshot.max_connections = 2
        self.connect()

    def test_background_poll_keeps_editors_and_drains_real_worker_edits(self):
        self.mode = 'block'
        self.assertTrue(self.window.submit('poll'))
        self.wait(self.started.is_set)
        self.assertTrue(self.window.controls['smart_pause'].isEnabled())
        self.assertTrue(self.window.write('smart_pause', False))
        self.assertFalse(self.window.controls['smart_pause'].isChecked())
        self.assertTrue(self.window.snapshot.settings['smart_pause'])
        self.window.show_section('controls')
        self.mode = ''
        self.block.set()
        self.wait(lambda: not self.window.busy)
        self.assertIn(('write', 'smart_pause', False, None), [call[:-1] for call in self.calls if call[0] == 'write'])
        self.assertFalse(self.window.snapshot.settings['smart_pause'])
        self.assertEqual(self.window.section, 'controls')

    def test_auto_power_values_render_and_write_seconds(self):
        self.assertIn('auto_power_off', self.window.controls)
        control = self.window.controls['auto_power_off']
        self.assertEqual(control.currentData(), 900)
        for value in (0, 900, 1800, 3600):
            control.setCurrentIndex(control.findData(value))
            self.wait(lambda: not self.window.busy)
            self.assertEqual(self.window.snapshot.settings['auto_power_off'], value)
            self.assertEqual(control.currentData(), value)
        self.assertEqual([call[2] for call in self.calls if call[0:2] == ('write', 'auto_power_off')], [0, 900, 1800, 3600])

    def test_project_release_failure_and_success_keep_headset_and_page(self):
        self.window.worker.release_checker = lambda cancelled=None: ProjectRelease('v9', 'https://github.com/Ariazonaa/momentum4-control/releases/tag/v9')
        self.window.show_section('equalizer')
        client = self.window.worker.client
        snapshot = self.window.snapshot
        self.assertTrue(self.window.submit('project-release'))
        self.wait(lambda: not self.window.busy)
        self.assertIs(self.window.worker.client, client)
        self.assertIs(self.window.snapshot, snapshot)
        self.assertEqual(self.window.section, 'equalizer')
        self.assertIn('Windows', self.window.release_label.text())
        def failed(cancelled=None):
            raise RuntimeError('Service unavailable')
        self.window.worker.release_checker = failed
        self.assertTrue(self.window.submit('project-release'))
        self.wait(lambda: not self.window.busy)
        self.assertIs(self.window.worker.client, client)
        self.assertIs(self.window.snapshot, snapshot)
        self.assertIn('failed', self.window.release_label.text().lower())

    def test_preset_single_curve_and_exact_topology_guard(self):
        self.assertTrue(hasattr(self.window, 'preset_picker'))
        curves = []
        def set_equalizer(gains, expected_model, expected_topology):
            curves.append((gains, expected_model, expected_topology))
            self.device_snapshot.eq_bands = gains
            return deepcopy(self.device_snapshot)
        self.window.worker.client.set_equalizer = set_equalizer
        self.assertTrue(self.window.apply_preset())
        self.wait(lambda: not self.window.busy)
        self.assertEqual(curves, [((0., 0.), 'M5AEBT Black', (2, (100, 1000)))])
        foreign = EqPreset('Foreign', 'M5AEBT Black', (101, 1000), (1., 2.))
        self.window.preset_picker.addItem(foreign.name, foreign)
        self.window.preset_picker.setCurrentIndex(self.window.preset_picker.count() - 1)
        self.assertFalse(self.window.apply_preset())
        self.assertEqual(len(curves), 1)

    def test_presets_reject_active_request_drafts_and_pending(self):
        self.assertTrue(hasattr(self.window, 'save_preset'))
        self.mode = 'block'
        self.assertTrue(self.window.submit('poll'))
        self.wait(self.started.is_set)
        self.assertFalse(self.window.save_preset('Busy'))
        self.assertFalse(self.window.apply_preset())
        self.assertFalse(self.window.delete_preset())
        self.mode = ''
        self.block.set()
        self.wait(lambda: not self.window.busy)
        self.window.eq_controls[0].begin_edit()
        self.assertFalse(self.window.save_preset('Draft'))
        self.assertFalse(self.window.apply_preset())
        self.window.eq_controls[0].cancel_edit()
        self.assertTrue(self.window.save_preset('Verified'))
        self.assertEqual(self.window.app_data.presets[0].gains, (1., -2.))

    def test_spatial_conflict_and_off_tracking_disabled(self):
        self.assertIn('head_tracking', self.window.controls)
        self.device_snapshot.settings['immersive_audio'] = False
        self.window.apply_snapshot(deepcopy(self.device_snapshot))
        self.window.update_enabled()
        self.assertFalse(self.window.controls['head_tracking'].isEnabled())
        self.assertFalse(self.window.write('head_tracking', True))
        self.device_snapshot.settings['sound_mode'] = 3
        self.window.apply_snapshot(deepcopy(self.device_snapshot))
        self.window.update_enabled()
        self.assertFalse(self.window.write('immersive_audio', True))

    def test_fake_defaults_tripwire_and_no_automatic_release(self):
        self.window.close()
        self.wait(lambda: not self.window.thread.isRunning())
        from momentum_control.gui import MainWindow
        with patch.object(PreferenceStore, 'load', side_effect=AssertionError('real preferences read')), patch('momentum_control.desktop.autostart_enabled', side_effect=AssertionError('real autostart read')), patch('momentum_control.updates._open', side_effect=AssertionError('real transport accessed')), patch('pathlib.Path.open', side_effect=AssertionError('filesystem accessed')):
            self.window = MainWindow(discover=lambda cancelled=None: [self.device], client_factory=lambda device, cancelled=None: None, tray_available=lambda: False)
            self.wait(lambda: not self.window.busy)
            self.window.background_tick()
            self.assertFalse(self.window.busy)
            self.assertEqual(self.window.preferences, Preferences())

    def test_setting_failed_save_preserves_memory_and_disk(self):
        self.assertTrue(hasattr(self.window, 'set_preference'))
        class Store:
            def save(self, data):
                raise PreferenceError('Storage refused')
        self.window.preference_store = Store()
        before = self.window.preferences
        self.assertFalse(self.window.set_preference('refresh_seconds', 60))
        self.assertEqual(self.window.preferences, before)
        self.assertIn('Storage refused', self.window.settings_notice.text())

    def test_tray_unavailable_startup_is_visible_and_quit_bypasses_tray(self):
        self.assertTrue(hasattr(self.window, 'show_startup'))
        self.window.preferences = replace(self.window.preferences, start_minimized=True, close_to_tray=True)
        self.window.tray_available = lambda: False
        self.window.show_startup(minimized=True)
        self.assertTrue(self.window.isVisible())
        self.assertTrue(self.window.quit_application())
        self.wait(lambda: not self.window.thread.isRunning())

    def test_tray_quit_refuses_firmware_and_shows_progress(self):
        self.assertTrue(hasattr(self.window, 'quit_application'))
        self.window.updating = True
        self.window.hide()
        self.assertFalse(self.window.quit_application())
        self.assertTrue(self.window.isVisible())
        self.assertTrue(self.window.thread.isRunning())
        self.window.updating = False

    def test_reconnect_exact_fresh_target_backoff_and_no_navigation(self):
        from types import SimpleNamespace
        window = self.window
        now = [100.]
        window.clock = lambda: now[0]
        window.preferences = replace(window.preferences, auto_reconnect=True)
        window.snapshot = None
        other = SimpleNamespace(address='11:22:33:44:55:66', path='/other', name='Other', connected=True)
        seen = []
        window.worker.discover = lambda cancelled=None: seen.append(now[0]) or [other]
        window.show_home()
        self.calls.clear()
        window.background_tick()
        self.wait(lambda: not window.busy)
        self.assertIsNone(window.snapshot)
        self.assertFalse(any(call[0] == 'create' for call in self.calls))
        self.assertEqual(window.explicit_address, self.device.address)
        self.assertEqual(window.page_kind, 'home')
        now[0] = 109.9
        window.background_tick()
        self.assertFalse(window.busy)
        self.assertEqual(seen, [100.])
        fresh = SimpleNamespace(address=self.device.address, path=self.device.path, name='New metadata', connected=False)
        window.worker.discover = lambda cancelled=None: seen.append(now[0]) or [other, fresh]
        factory = window.worker.client_factory
        targets = []
        window.worker.client_factory = lambda device, cancelled=None: targets.append(device) or factory(device, cancelled)
        now[0] = 110.
        window.background_tick()
        self.wait(lambda: not window.busy)
        self.assertEqual(targets, [fresh])
        self.assertIs(window.device_picker.currentData(), fresh)
        self.assertIsNotNone(window.snapshot)
        self.assertEqual(window.page_kind, 'home')

    def test_picker_default_zero_is_not_reconnect_intent(self):
        window = self.window
        window.explicit_address = ''
        window.preferences = replace(window.preferences, auto_reconnect=True, last_device_address='')
        window.snapshot = None
        calls = []
        window.worker.discover = lambda cancelled=None: calls.append(True) or [self.device]
        window.background_tick()
        self.assertEqual(window.device_picker.currentIndex(), 0)
        self.assertFalse(window.busy)
        self.assertEqual(calls, [])

    def test_reconnect_restore_frozen_full_transparency_and_gates(self):
        window = self.window
        window.clock = lambda: 100.
        for option, full, anc, caps, expected in ((True, True, True, True, 1), (False, True, True, True, 0), (True, False, True, True, 0), (True, True, False, True, 0), (True, True, True, False, 0)):
            with self.subTest(option=option, full=full, anc=anc, caps=caps):
                window.preferences = replace(window.preferences, auto_reconnect=True, restore_transparency=option)
                window.last_full_transparency = full
                window.last_reconnect = float('-inf')
                window.snapshot = None
                self.device_snapshot.settings.update(anc_enabled=anc, transparency=False)
                writable = {'anc_enabled', 'transparency', 'smart_pause', 'eq'} if caps else {'anc_enabled', 'smart_pause', 'eq'}
                self.device_snapshot.writable = frozenset(writable)
                self.calls.clear()
                window.background_tick()
                self.wait(lambda: not window.busy)
                setters = [call[1:3] for call in self.calls if call[0] == 'write']
                self.assertEqual(setters, [('transparency', True)] * expected)
                self.assertFalse(any(name == 'anc_enabled' for name, value in setters))

    def test_partial_state_and_different_device_revoke_restore(self):
        self.device_snapshot.settings.update(anc_enabled=True, transparency=True, transparency_level=75)
        self.window.apply_snapshot(deepcopy(self.device_snapshot))
        self.assertFalse(self.window.last_full_transparency)
        self.device_snapshot.settings.update(transparency_level=100)
        self.window.apply_snapshot(deepcopy(self.device_snapshot))
        self.assertTrue(self.window.last_full_transparency)
        self.window.choose_address('11:22:33:44:55:66')
        self.assertFalse(self.window.last_full_transparency)

    def test_stale_reconnect_discovery_generation_does_not_restore(self):
        window = self.window
        window.preferences = replace(window.preferences, auto_reconnect=True, restore_transparency=True)
        window.last_full_transparency = True
        window.snapshot = None
        started = self.started
        gate = self.block
        def discover(cancelled=None):
            started.set()
            gate.wait(3)
            return [self.device]
        window.worker.discover = discover
        self.calls.clear()
        window.background_tick()
        self.wait(started.is_set)
        window.choose_address('11:22:33:44:55:66')
        window.generation += 1
        gate.set()
        self.wait(lambda: not window.busy)
        self.assertIsNone(window.snapshot)
        self.assertFalse(any(call[0] == 'write' for call in self.calls))

    def test_preferences_presets_persist_restart_and_corrupt_store_is_not_replaced(self):
        with TemporaryDirectory() as directory:
            store = PreferenceStore(Path(directory) / 'preferences.json')
            self.window.preference_store = store
            self.assertTrue(self.window.set_preference('language', 'de'))
            self.assertTrue(self.window.save_preset('Local'))
            self.assertEqual(store.load().preferences.language, 'de')
            self.assertEqual(store.load().presets[0].name, 'Local')
            self.window.close()
            self.wait(lambda: not self.window.thread.isRunning())
            from momentum_control.gui import MainWindow
            self.window = MainWindow(discover=lambda cancelled=None: [self.device], client_factory=lambda device, cancelled=None: None, preference_store=store)
            self.wait(lambda: not self.window.busy)
            self.assertEqual(self.window.language, 'de')
            self.assertEqual(self.window.settings_button.text(), 'App-Einstellungen')
            self.window.apply_snapshot(deepcopy(self.device_snapshot))
            self.assertEqual(self.window.status.text(), 'Kopfhörersteuerung — verbunden')
            self.assertEqual(self.window.app_data.presets[0].name, 'Local')
            self.window.close()
            self.wait(lambda: not self.window.thread.isRunning())
            store.path.write_text('{broken')
            self.window = MainWindow(discover=lambda cancelled=None: [self.device], client_factory=lambda device, cancelled=None: None, preference_store=store)
            self.wait(lambda: not self.window.busy)
            self.assertIn('Invalid preferences', self.window.settings_notice.text())
            self.assertFalse(self.window.set_preference('refresh_seconds', 60))
            self.assertEqual(store.path.read_text(), '{broken')

    def test_diagnostic_events_are_bounded_allowlisted_and_redacted(self):
        import json
        import zipfile
        window = self.window
        for index in range(2010):
            window.record_event('write', 'private-name AA:BB:CC:DD:EE:FF')
        window.record_event('rename private-name', '')
        self.assertEqual(len(window.events), 2000)
        self.assertEqual(window.events[-1]['operation'], 'write')
        self.assertEqual(window.events[-1]['status'], 'failed')
        with TemporaryDirectory() as directory:
            filename = str(Path(directory) / 'diagnostic.zip')
            with patch('momentum_control.gui.QFileDialog.getSaveFileName', return_value=(filename, 'ZIP')):
                self.assertTrue(window.export_diagnostic_file())
            with zipfile.ZipFile(filename) as archive:
                content = archive.read(archive.namelist()[0])
            self.assertNotIn(b'AA:BB', content)
            self.assertNotIn(b'private-name', content)
            self.assertNotIn(b'Device', content)
            document = json.loads(content)
            self.assertIn('settings', document)
            self.assertEqual(len(document['events']), 2000)

    def test_verified_metadata_plain_text_and_m5_peers_remain_read_only(self):
        from momentum_control.backend import PeerDevice
        self.device_snapshot.peers = (PeerDevice(0, '<b>Phone</b>', False, False), PeerDevice(1, 'This PC', True, True))
        self.window.apply_snapshot(deepcopy(self.device_snapshot))
        self.window.update_enabled()
        self.assertEqual(self.window.name_label.textFormat(), Qt.TextFormat.PlainText)
        self.assertIn('<b>Device</b>', self.window.name_label.text())
        self.assertIn('<b>Phone</b>', self.window.peer_picker.itemText(0))
        self.assertFalse(self.window.peer_button.isEnabled())
        self.assertFalse(self.window.connect_selected_peer())
        self.assertIn('read-only', self.window.peer_label.text())

    def test_background_poll_is_idle_only_and_optin_release_once(self):
        window = self.window
        now = [window.last_refresh]
        window.clock = lambda: now[0]
        window.preferences = replace(window.preferences, refresh_seconds=30)
        now[0] += 29.9
        window.background_tick()
        self.assertFalse(window.busy)
        window.eq_controls[0].begin_edit()
        now[0] += 1
        window.background_tick()
        self.assertFalse(window.busy)
        window.eq_controls[0].cancel_edit()
        window.background_tick()
        self.wait(lambda: not window.busy)
        checks = []
        window.worker.release_checker = lambda cancelled=None: checks.append(True) or ProjectRelease('v1', 'https://github.com/Ariazonaa/momentum4-control/releases/tag/v1')
        self.assertTrue(window.set_preference('check_app_updates', True))
        window.background_tick()
        self.wait(lambda: not window.busy)
        window.background_tick()
        self.assertEqual(checks, [True])

    def test_actual_m5_layout_has_no_horizontal_overflow(self):
        window = self.window
        self.device_snapshot.eq_bands = (0.,) * 8
        self.device_snapshot.eq_frequencies = (90, 325, 1500, 2500, 4500, 8000, 13000, 16000)
        window.apply_snapshot(deepcopy(self.device_snapshot))
        for width, height in ((500, 820), (460, 780)):
            window.resize(width, height)
            for section in ('noise', 'equalizer', 'controls', 'info'):
                window.show_section(section)
                self.app.processEvents()
                self.assertLessEqual(window.scroll.widget().width(), window.scroll.viewport().width(), (width, section))
                self.assertEqual(window.scroll.horizontalScrollBar().maximum(), 0)
            window.show_settings()
            self.app.processEvents()
            self.assertLessEqual(window.scroll.widget().width(), window.scroll.viewport().width())
            window.open_headset(self.device)

    def test_tray_hidden_quit_finishes_worker_then_quits_application(self):
        window = self.window
        window.preferences = replace(window.preferences, tray_enabled=True, close_to_tray=True)
        window.tray_available = lambda: True
        window.hide()
        with patch.object(window.tray, 'isVisible', return_value=True), patch.object(self.app, 'quit') as quit_app:
            window.close()
            self.assertTrue(window.thread.isRunning())
            self.assertFalse(window.isVisible())
            self.assertFalse(window.closing)
            self.assertTrue(window.quit_application())
            self.wait(lambda: not window.thread.isRunning())
            self.app.processEvents()
            self.assertTrue(quit_app.called)
        self.assertTrue(any(call[0] == 'close' for call in self.calls))
        self.assertLess([call[0] for call in self.calls].index('cancel'), [call[0] for call in self.calls].index('close'))

    def test_default_fake_constructor_never_resolves_real_config_or_os_paths(self):
        self.window.close()
        self.wait(lambda: not self.window.thread.isRunning())
        from momentum_control.gui import MainWindow
        with patch('momentum_control.gui.QStandardPaths.writableLocation', side_effect=AssertionError('real config resolved')), patch('momentum_control.gui.autostart_enabled', side_effect=AssertionError('real autostart accessed')), patch('momentum_control.preferences.os.open', side_effect=AssertionError('real filesystem accessed')), patch('momentum_control.updates._open', side_effect=AssertionError('network transport accessed')):
            self.window = MainWindow(discover=lambda cancelled=None: [self.device], client_factory=lambda device, cancelled=None: None)
            self.wait(lambda: not self.window.busy)
            self.window.background_tick()
            self.assertEqual(self.window.preferences, Preferences())
            self.window.close()
            self.wait(lambda: not self.window.thread.isRunning())

    def test_corrupt_injected_store_never_receives_default_replacement(self):
        self.window.close()
        self.wait(lambda: not self.window.thread.isRunning())
        from momentum_control.gui import MainWindow
        written = []
        class Store:
            def load(self):
                raise PreferenceError('Invalid existing preferences')
            def save(self, data):
                written.append(data)
        self.window = MainWindow(discover=lambda cancelled=None: [self.device], client_factory=lambda device, cancelled=None: None, preference_store=Store())
        self.wait(lambda: not self.window.busy)
        self.assertFalse(self.window.set_preference('refresh_seconds', 60))
        self.assertEqual(written, [])
