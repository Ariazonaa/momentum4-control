import os
os.environ.setdefault('QT_QPA_PLATFORM', 'offscreen')
from copy import deepcopy
from unittest.mock import patch
import unittest
import test_gui as fixtures


class SoundCheckGuiTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.app = fixtures.QApplication.instance() or fixtures.QApplication([])
    wait = fixtures.GuiTests.wait
    tearDown = fixtures.GuiTests.tearDown
    connect = fixtures.GuiTests.connect

    def setUp(self):
        fixtures.GuiTests.setUp(self)
        integrated = hasattr(self.window, 'open_sound_check')
        if not integrated:
            self.window.close()
            self.wait(lambda: not self.window.thread.isRunning())
        self.assertTrue(integrated, 'Main-window Sound check integration is required')
        self.device_snapshot = fixtures.snapshot(frozenset({'anc_enabled', 'anc_strength', 'eq', 'sound_mode'}), 'M5AEBT Black')
        self.device_snapshot.firmware = '6.21.5'
        self.device_snapshot.settings['sound_mode'] = 1
        self.device_snapshot.eq_frequencies = (100, 1000, 8000)
        self.device_snapshot.eq_bands = (0., 1., -1.)
        self.connect()
        self.curves = []
        self.preview_block = False
        self.preview_error = ''
        def forbidden_location(cancelled=None, now=None):
            raise AssertionError('Real location must never be constructed in Sound check tests')
        self.window.worker.location_provider_factory = forbidden_location
        def set_equalizer(gains, expected_model, expected_topology):
            self.curves.append((gains, expected_model, expected_topology))
            if self.preview_block:
                self.started.set()
                self.block.wait(3)
            if self.preview_error:
                raise RuntimeError(self.preview_error)
            self.device_snapshot.eq_bands = gains
            return deepcopy(self.device_snapshot)
        self.window.worker.client.set_equalizer = set_equalizer

    def test_sound_check_constructor_render_and_close_send_no_writes(self):
        self.assertTrue(self.window.open_sound_check())
        dialog = self.window.sound_check_dialog
        self.app.processEvents()
        self.assertEqual(self.curves, [])
        self.assertIn('last applied', dialog.notice_label.text().lower())
        dialog.close()
        self.app.processEvents()
        self.assertIsNone(self.window.sound_check_dialog)
        self.assertEqual(self.curves, [])

    def test_sound_check_requires_verified_mode_idle_and_exact_target(self):
        self.device_snapshot.settings['sound_mode'] = 0
        self.window.apply_snapshot(deepcopy(self.device_snapshot))
        self.assertFalse(self.window.open_sound_check())
        self.device_snapshot.settings['sound_mode'] = 1
        self.window.apply_snapshot(deepcopy(self.device_snapshot))
        self.window.eq_controls[0].begin_edit()
        self.assertFalse(self.window.open_sound_check())
        self.window.eq_controls[0].cancel_edit()
        self.assertTrue(self.window.open_sound_check())
        dialog = self.window.sound_check_dialog
        binding = self.window.sound_check_binding
        self.window.generation += 1
        self.window.update_enabled()
        self.assertFalse(dialog.listen_a_button.isEnabled())
        self.assertFalse(self.window.apply_sound_check(dialog, binding, self.window.preset_picker.currentData()))
        self.assertEqual(self.curves, [])
        dialog.close()

    def test_preview_is_one_preset_and_only_exact_operation_acknowledges(self):
        self.assertTrue(self.window.open_sound_check())
        dialog = self.window.sound_check_dialog
        self.preview_block = True
        dialog.listen_a_button.click()
        self.wait(self.started.is_set)
        request = self.window.active_request
        self.assertIs(self.window.sound_check_operation[0], request)
        self.assertFalse(dialog.next_button.isEnabled())
        ordinary = deepcopy(self.device_snapshot)
        ordinary.eq_bands = (2., 1., -1.)
        self.window.apply_snapshot(ordinary)
        self.window.update_enabled()
        self.assertFalse(dialog.next_button.isEnabled())
        from dataclasses import replace
        with patch.object(dialog, 'operation_finished', wraps=dialog.operation_finished) as acknowledged:
            self.window.complete_sound_check(replace(request), '')
            self.assertFalse(acknowledged.called)
            self.assertIs(self.window.sound_check_operation[0], request)
        self.block.set()
        self.wait(lambda: not self.window.busy)
        self.assertEqual(self.curves, [((2., 1., -1.), 'M5AEBT Black', (3, (100, 1000, 8000)))])
        self.assertTrue(dialog.next_button.isEnabled())
        self.assertIsNone(self.window.sound_check_operation)
        dialog.close()

    def test_preview_close_does_not_restore_or_acknowledge_later_dialog(self):
        self.assertTrue(self.window.open_sound_check())
        retired = self.window.sound_check_dialog
        self.preview_block = True
        retired.listen_a_button.click()
        self.wait(self.started.is_set)
        with patch.object(retired, 'operation_finished', wraps=retired.operation_finished) as acknowledge:
            retired.close()
            self.block.set()
            self.wait(lambda: not self.window.busy)
            self.assertFalse(acknowledge.called)
        self.assertEqual(len(self.curves), 1)
        self.assertTrue(self.window.open_sound_check())
        self.assertIsNot(self.window.sound_check_dialog, retired)
        self.window.sound_check_dialog.close()
        self.assertEqual(len(self.curves), 1)

    def test_explicit_restore_and_confirmed_save_use_existing_preset_store(self):
        self.assertTrue(self.window.open_sound_check())
        dialog = self.window.sound_check_dialog
        dialog.listen_a_button.click()
        self.wait(lambda: not self.window.busy)
        dialog.restore_button.click()
        self.wait(lambda: not self.window.busy)
        self.assertEqual([curve[0] for curve in self.curves], [(2., 1., -1.), (0., 1., -1.)])
        dialog.listen_b_button.click()
        self.wait(lambda: not self.window.busy)
        for _ in range(3):
            dialog.next_button.click()
        dialog.name_edit.setText('Checked')
        self.assertTrue(dialog.save_button.isEnabled())
        dialog.save_button.click()
        self.assertEqual(self.window.app_data.presets[-1].name, 'Checked')
        self.assertEqual(self.window.app_data.presets[-1].gains, (-2., 1., -1.))
        self.assertEqual(self.window.app_data.presets[-1].frequencies, (100, 1000, 8000))
        dialog.close()

    def test_manual_dialog_suppresses_background_and_consumes_pending_zone_entry(self):
        from momentum_control.sound_zones import ZoneData, SoundZone, LocationFix
        from momentum_control.preferences import EqPreset
        zone = SoundZone('Local', self.device.address, 52., 13., 500,
            EqPreset('Zone', 'M5AEBT Black', (100, 1000, 8000), (1., 2., 3.)))
        self.window.zone_data = ZoneData(True, (zone,))
        now = [100.]
        self.window.clock = lambda: now[0]
        self.window.automatic_location_intent = self.window.location_intent()
        revision = self.window.user_edit_revision
        self.assertTrue(self.window.open_sound_check())
        self.assertGreater(self.window.user_edit_revision, revision)
        self.window.last_refresh = 0
        self.window.last_location_sample = 0
        self.window.background_tick()
        self.assertFalse(self.window.busy)
        intent = self.window.location_intent()
        self.window.complete_location(intent, LocationFix(52., 13., 0., 100.), '')
        self.assertEqual(self.curves, [])
        self.assertIn('skipped', self.window.zone_notice.text().lower())
        self.window.sound_check_dialog.close()
        self.window.complete_location(self.window.location_intent(), LocationFix(52., 13., 0., 100.), '')
        self.assertFalse(self.window.busy)
        self.assertEqual(self.curves, [])

    def test_preview_failure_keeps_dialog_open_without_claiming_saved_result(self):
        self.assertTrue(self.window.open_sound_check())
        dialog = self.window.sound_check_dialog
        self.preview_error = 'Rejected preview'
        dialog.listen_a_button.click()
        self.wait(lambda: not self.window.busy)
        self.assertIs(self.window.sound_check_dialog, dialog)
        self.assertIsNone(self.window.snapshot)
        self.assertIn('failed', dialog.status_label.text().lower())
        self.assertFalse(dialog.next_button.isEnabled())
        self.assertIsNone(self.window.sound_check_operation)
        self.assertEqual(self.window.app_data.presets, ())
        dialog.close()

    def test_save_failure_preserves_result_and_current_verified_gain_guard(self):
        from momentum_control.preferences import PreferenceError, EqPreset
        self.assertTrue(self.window.open_sound_check())
        dialog = self.window.sound_check_dialog
        binding = self.window.sound_check_binding
        for _ in range(3):
            dialog.next_button.click()
        dialog.name_edit.setText('Selected')
        foreign_gains = EqPreset('Selected', 'M5AEBT Black', (100, 1000, 8000), (1., 2., 3.))
        self.assertFalse(self.window.save_sound_check(dialog, binding, 'Selected', foreign_gains))
        class Store:
            def save(self, data):
                raise PreferenceError('Disk refused')
        self.window.preference_store = Store()
        dialog.save_button.click()
        self.assertIs(self.window.sound_check_dialog, dialog)
        self.assertTrue(dialog.save_button.isEnabled())
        self.assertEqual(self.window.app_data.presets, ())
        self.assertIn('Disk refused', self.window.settings_notice.text())
        dialog.close()

    def test_pending_zone_after_preview_and_close_is_consumed_without_overwrite(self):
        from momentum_control.sound_zones import ZoneData, SoundZone, LocationFix
        from momentum_control.preferences import EqPreset
        zone = SoundZone('Local', self.device.address, 52., 13., 500,
            EqPreset('Zone', 'M5AEBT Black', (100, 1000, 8000), (1., 2., 3.)))
        self.window.zone_data = ZoneData(True, (zone,))
        self.window.clock = lambda: 100.
        self.window.automatic_location_intent = self.window.location_intent()
        self.assertTrue(self.window.open_sound_check())
        self.window.sound_check_dialog.listen_a_button.click()
        self.wait(lambda: not self.window.busy)
        self.window.sound_check_dialog.close()
        self.window.complete_location(self.window.location_intent(), LocationFix(52., 13., 0., 100.), '')
        self.assertFalse(self.window.busy)
        self.assertEqual(len(self.curves), 1)
        self.assertIn('skipped', self.window.zone_notice.text().lower())
        self.window.complete_location(self.window.location_intent(), LocationFix(52., 13., 0., 100.), '')
        self.assertFalse(self.window.busy)
        self.assertEqual(len(self.curves), 1)
