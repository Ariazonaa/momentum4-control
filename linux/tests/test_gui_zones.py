import os
os.environ.setdefault('QT_QPA_PLATFORM', 'offscreen')
from copy import deepcopy
from dataclasses import replace
from types import SimpleNamespace
from unittest.mock import patch
import threading
import unittest
import test_gui as fixtures
from momentum_control.preferences import EqPreset
from momentum_control.sound_zones import ZoneData, SoundZone, LocationFix


class ZoneGuiTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.app = fixtures.QApplication.instance() or fixtures.QApplication([])
    wait = fixtures.GuiTests.wait
    tearDown = fixtures.GuiTests.tearDown
    connect = fixtures.GuiTests.connect

    def setUp(self):
        fixtures.GuiTests.setUp(self)
        integrated = hasattr(self.window, 'zone_data')
        if not integrated:
            self.window.close()
            self.wait(lambda: not self.window.thread.isRunning())
        self.assertTrue(integrated, 'Local Sound Zones integration is required')
        self.device_snapshot = fixtures.snapshot(frozenset({'anc_enabled', 'anc_strength', 'eq', 'smart_pause'}), 'M5AEBT Black')
        self.device_snapshot.settings['smart_pause'] = True
        self.device_snapshot.firmware = '6.21.5'
        self.connect()
        self.now = [100.]
        self.window.clock = lambda: self.now[0]
        self.curves = []
        def set_equalizer(gains, expected_model, expected_topology):
            self.curves.append((gains, expected_model, expected_topology))
            self.device_snapshot.eq_bands = gains
            return deepcopy(self.device_snapshot)
        self.window.worker.client.set_equalizer = set_equalizer
        self.preset = EqPreset('Zone EQ', 'M5AEBT Black', (100, 1000), (2., 3.))
        self.zone = SoundZone('Local', self.device.address, 52., 13., 500, self.preset)
        self.samples = []
        self.providers = []
        owner = self
        class Provider:
            def __init__(self, cancelled=None, now=None):
                self.closed = False
                owner.providers.append(self)
            def sample(self):
                owner.samples.append(threading.get_ident())
                return LocationFix(52., 13., 5., owner.now[0])
            def close(self):
                self.closed = True
                owner.samples.append(('close', threading.get_ident()))
        self.window.worker.location_provider_factory = Provider
        self.window.zone_data = ZoneData(True, (self.zone,))
        self.window.last_location_sample = float('-inf')

    def tick(self):
        self.window.background_tick()
        self.wait(lambda: not self.window.busy)

    def test_optin_entry_uses_single_guarded_preset_and_no_repeat(self):
        self.tick()
        self.assertEqual(self.curves, [((2., 3.), 'M5AEBT Black', (2, (100, 1000)))])
        self.assertEqual(len(self.providers), 1)
        self.assertNotEqual(self.samples[0], threading.get_ident())
        self.now[0] = 159.9
        self.tick()
        self.assertEqual(len([sample for sample in self.samples if isinstance(sample, int)]), 1)
        self.now[0] = 160.
        self.tick()
        self.assertEqual(len([sample for sample in self.samples if isinstance(sample, int)]), 2)
        self.assertEqual(len(self.curves), 1)

    def test_pending_sample_15s_and_edit_revision_consumes_entry(self):
        provider = self.window.worker.location_provider_factory
        count = [0]
        class Pending(provider):
            def sample(self):
                count[0] += 1
                return None if count[0] == 1 else super().sample()
        self.window.worker.location_provider_factory = Pending
        self.tick()
        self.assertEqual(count[0], 1)
        self.assertTrue(self.window.write('smart_pause', False))
        self.wait(lambda: not self.window.busy)
        self.now[0] = 114.9
        self.tick()
        self.assertEqual(count[0], 1)
        self.now[0] = 115.
        self.tick()
        self.assertEqual(count[0], 2)
        self.assertEqual(self.curves, [])
        self.assertIn('skipped', self.window.zone_notice.text().lower())
        self.now[0] = 175.
        self.tick()
        self.assertEqual(self.curves, [])

    def test_location_failure_keeps_client_snapshot_and_page(self):
        provider = self.window.worker.location_provider_factory
        class Failed(provider):
            def sample(self):
                raise RuntimeError('Private location failure')
        self.window.worker.location_provider_factory = Failed
        client, snapshot = self.window.worker.client, self.window.snapshot
        self.window.show_section('controls')
        self.tick()
        self.assertIs(self.window.worker.client, client)
        self.assertIs(self.window.snapshot, snapshot)
        self.assertEqual(self.window.section, 'controls')
        self.assertTrue(self.providers[0].closed)
        self.assertNotIn('Private', self.window.zone_notice.text())

    def test_no_location_for_disabled_foreign_unverified_or_editing_target(self):
        for data in (ZoneData(False, (self.zone,)), ZoneData(True, (replace(self.zone, device_address='11:22:33:44:55:66'),)), ZoneData(True, (replace(self.zone, preset=replace(self.preset, frequencies=(101, 1000))),))):
            self.window.zone_data = data
            self.tick()
            self.assertEqual(self.providers, [])
        self.window.zone_data = ZoneData(True, (self.zone,))
        self.window.eq_controls[0].begin_edit()
        self.tick()
        self.assertEqual(self.providers, [])
        self.window.eq_controls[0].cancel_edit()
        self.window.snapshot = None
        self.tick()
        self.assertEqual(self.providers, [])

    def test_manual_one_shot_fills_open_editor_then_closes(self):
        self.window.zone_data = ZoneData(False, ())
        self.window.show_zones()
        self.assertTrue(self.window.use_current_location())
        self.wait(lambda: not self.window.busy)
        self.assertEqual(self.window.zone_latitude.value(), 52.)
        self.assertEqual(self.window.zone_longitude.value(), 13.)
        self.assertTrue(self.providers[0].closed)
        self.assertIsNone(self.window.manual_location_intent)
        self.assertEqual(self.curves, [])

    def test_late_manual_result_after_cancel_never_populates_editor_or_reopens(self):
        provider = self.window.worker.location_provider_factory
        class Delayed(provider):
            def sample(self):
                self.owner_started = True
                self_outer.started.set()
                self_outer.block.wait(3)
                return super().sample()
        self_outer = self
        self.window.worker.location_provider_factory = Delayed
        self.window.zone_data = ZoneData(False, ())
        self.window.show_zones()
        self.assertTrue(self.window.use_current_location())
        self.wait(self.started.is_set)
        self.window.show_settings()
        self.block.set()
        self.wait(lambda: not self.window.busy)
        self.wait(lambda: self.providers[0].closed)
        self.assertEqual(self.window.zone_latitude.value(), 0.)
        self.assertIsNone(self.window.manual_location_intent)
        self.tick()
        self.assertEqual(len(self.providers), 1)

    def test_cleanup_has_priority_over_queued_edits_and_firmware(self):
        base = self.window.worker.location_provider_factory
        class Pending(base):
            def sample(self):
                return None
        self.window.worker.location_provider_factory = Pending
        self.tick()
        self.curves.clear()
        order = []
        provider = self.providers[0]
        close = provider.close
        provider.close = lambda: order.append('close-location') or close()
        original = self.window.worker.client.set_value
        def delayed(name, value, band=None, expected_topology=None):
            if name == 'smart_pause':
                order.append('first-write')
                self.started.set()
                self.block.wait(3)
            else:
                order.append('second-write')
            return original(name, value, band, expected_topology)
        self.window.worker.client.set_value = delayed
        self.assertTrue(self.window.write('smart_pause', False))
        self.wait(self.started.is_set)
        self.assertTrue(self.window.write('anc_strength', 25))
        self.assertTrue(self.window.set_zones_enabled(False))
        self.block.set()
        self.wait(lambda: not self.window.busy)
        self.assertIn('close-location', order)
        self.assertIn('second-write', order)
        self.assertLess(order.index('close-location'), order.index('second-write'))

    def test_manual_pending_timeout_queues_cleanup_and_no_location_during_firmware(self):
        provider = self.window.worker.location_provider_factory
        class Pending(provider):
            def sample(self):
                self_outer.samples.append(threading.get_ident())
                return None
        self_outer = self
        self.window.worker.location_provider_factory = Pending
        self.window.zone_data = ZoneData(False, ())
        self.window.show_zones()
        self.assertTrue(self.window.use_current_location())
        self.wait(lambda: not self.window.busy)
        self.now[0] = 130.
        self.tick()
        self.wait(lambda: self.providers[0].closed)
        self.assertIsNone(self.window.manual_location_intent)
        self.window.zone_data = ZoneData(True, (self.zone,))
        self.window.updating = True
        self.tick()
        self.assertEqual(len(self.providers), 1)
        self.window.updating = False

    def test_default_constructor_and_stored_optin_do_not_acquire_location(self):
        self.window.close()
        self.wait(lambda: not self.window.thread.isRunning())
        from momentum_control.gui import MainWindow
        class Store:
            def load(self):
                return ZoneData(True, (self_outer.zone,))
        self_outer = self
        with patch('momentum_control.sound_zones.GeoClueProvider', side_effect=AssertionError('Real location provider created')):
            self.window = MainWindow(discover=lambda cancelled=None: [self.device], client_factory=lambda device, cancelled=None: None, zone_store=Store())
            self.wait(lambda: not self.window.busy)
            self.window.background_tick()
            self.assertIsNone(self.window.worker.location_provider)
            self.assertFalse(self.window.busy)

    def test_long_exclusive_refresh_closes_provider_before_headset_operation(self):
        provider = self.window.worker.location_provider_factory
        class Pending(provider):
            def sample(self):
                return None
        self.window.worker.location_provider_factory = Pending
        self.tick()
        self.assertFalse(self.providers[0].closed)
        checked = []
        original = self.window.worker.client.snapshot
        def snapshot():
            checked.append(self.providers[0].closed)
            return original()
        self.window.worker.client.snapshot = snapshot
        self.assertTrue(self.window.submit('refresh'))
        self.wait(lambda: not self.window.busy)
        self.assertEqual(checked, [True])

    def test_manual_editor_user_input_cancels_request_before_result(self):
        provider = self.window.worker.location_provider_factory
        self_outer = self
        class Delayed(provider):
            def sample(self):
                self_outer.started.set()
                self_outer.block.wait(3)
                return super().sample()
        self.window.worker.location_provider_factory = Delayed
        self.window.zone_data = ZoneData(False, ())
        self.window.show_zones()
        self.assertTrue(self.window.use_current_location())
        self.wait(self.started.is_set)
        self.window.zone_latitude.setValue(40.)
        self.window.zone_latitude.editingFinished.emit()
        self.block.set()
        self.wait(lambda: not self.window.busy)
        self.assertEqual(self.window.zone_latitude.value(), 40.)
        self.wait(lambda: self.providers[0].closed)

    def test_zone_capture_save_rejects_drafts_and_embeds_current_verified_curve(self):
        self.window.zone_data = ZoneData(False, ())
        self.window.show_zones()
        self.window.zone_name.setText('Home')
        self.window.zone_latitude.setValue(52.)
        self.window.zone_longitude.setValue(13.)
        self.window.eq_controls[0].begin_edit()
        self.assertFalse(self.window.save_zone())
        self.window.eq_controls[0].cancel_edit()
        self.assertTrue(self.window.save_zone())
        saved = self.window.zone_data.zones[0]
        self.assertEqual(saved.device_address, self.device.address)
        self.assertEqual(saved.preset.gains, (1., -2.))
        self.assertEqual(saved.preset.frequencies, (100, 1000))
        self.assertEqual(saved.latitude, 52.)
        self.assertTrue(self.window.delete_zone())
        self.assertEqual(self.window.zone_data.zones, ())

    def test_blocked_location_keeps_editors_and_user_edit_consumes_entry(self):
        provider = self.window.worker.location_provider_factory
        self_outer = self
        class Delayed(provider):
            def sample(self):
                self_outer.started.set()
                self_outer.block.wait(3)
                return super().sample()
        self.window.worker.location_provider_factory = Delayed
        self.window.background_tick()
        self.wait(self.started.is_set)
        self.assertTrue(self.window.controls['smart_pause'].isEnabled())
        self.assertTrue(self.window.write('smart_pause', False))
        self.assertFalse(self.window.controls['smart_pause'].isChecked())
        self.assertTrue(self.window.snapshot.settings['smart_pause'])
        self.block.set()
        self.wait(lambda: not self.window.busy)
        self.assertFalse(self.window.snapshot.settings['smart_pause'])
        self.assertEqual(self.curves, [])
        self.assertIn('skipped', self.window.zone_notice.text().lower())

    def test_cancelled_queued_sample_never_constructs_provider(self):
        from momentum_control.gui import Request
        self.window.zone_data = ZoneData(False, ())
        intent = self.window.location_intent(manual=True)
        self.assertTrue(hasattr(intent, 'cancelled'), 'Location intents require an immediate cancellation token')
        intent.cancelled.set()
        self.window.busy = True
        self.window.requested.emit(Request('location-sample', self.window.generation, payload=intent))
        self.wait(lambda: not self.window.busy)
        self.assertEqual(self.providers, [])

    def test_existing_zone_noop_radius_and_name_edits_preserve_embedded_preset(self):
        self.window.timer.stop()
        self.window.show_zones()
        changes = []
        self.window.zone_preset_picker.currentIndexChanged.connect(changes.append)
        self.window.zone_picker.setCurrentIndex(1)
        self.assertEqual(changes, [])
        self.assertEqual(self.window.zone_preset_picker.currentData(), self.preset)
        self.assertTrue(self.window.save_zone())
        self.assertEqual(self.window.zone_data.zones[0].preset, self.preset)
        self.window.zone_radius.setValue(600)
        self.assertTrue(self.window.save_zone())
        self.assertEqual(self.window.zone_data.zones[0].preset, self.preset)
        self.assertEqual(self.window.zone_data.zones[0].radius_m, 600)
        self.window.zone_name.setText('Renamed')
        self.assertTrue(self.window.save_zone())
        self.assertEqual(self.window.zone_data.zones[0].preset, self.preset)
        self.assertEqual(self.window.zone_data.zones[0].name, 'Renamed')
        self.assertEqual(self.providers, [])
        self.assertEqual(self.curves, [])

    def test_switching_zones_resets_preset_but_same_zone_render_preserves_choice(self):
        other_preset = replace(self.preset, name='Other curve', gains=(-4., 5.))
        other_zone = replace(self.zone, name='Other', longitude=14., preset=other_preset)
        self.window.timer.stop()
        self.window.zone_data = ZoneData(False, (self.zone, other_zone))
        self.window.app_data = replace(self.window.app_data, presets=(other_preset,))
        self.window.show_zones()
        self.window.zone_picker.setCurrentIndex(1)
        self.assertEqual(self.window.zone_preset_picker.currentData(), self.preset)
        self.window.zone_preset_picker.setCurrentIndex(0)
        self.window.render_zones()
        self.assertIsNone(self.window.zone_preset_picker.currentData())
        self.window.zone_preset_picker.setCurrentIndex(self.window.zone_preset_picker.findData(other_preset))
        self.window.render_zones()
        self.assertEqual(self.window.zone_preset_picker.currentData(), other_preset)
        self.window.zone_picker.setCurrentIndex(2)
        self.assertEqual(self.window.zone_preset_picker.currentData(), other_preset)
        self.window.zone_picker.setCurrentIndex(1)
        self.assertEqual(self.window.zone_preset_picker.currentData(), self.preset)
        self.window.zone_picker.setCurrentIndex(0)
        self.assertIsNone(self.window.zone_preset_picker.currentData())
        self.assertEqual(self.providers, [])
        self.assertEqual(self.curves, [])

    def test_explicit_current_eq_choice_survives_refresh_and_replaces_zone_curve(self):
        self.window.timer.stop()
        self.window.show_zones()
        self.window.zone_picker.setCurrentIndex(1)
        self.window.zone_preset_picker.setCurrentIndex(0)
        self.assertTrue(self.window.submit('refresh'))
        self.wait(lambda: not self.window.busy)
        self.window.render_zones()
        self.assertIsNone(self.window.zone_preset_picker.currentData())
        self.assertTrue(self.window.save_zone())
        self.assertEqual(self.window.zone_data.zones[0].preset.gains, (1., -2.))
        self.assertEqual(self.providers, [])
        self.assertEqual(self.curves, [])

    def test_deleted_selection_defaults_new_zone_and_snapshot_loss_keeps_embedded_preset(self):
        self.window.timer.stop()
        self.window.show_zones()
        self.window.zone_picker.setCurrentIndex(1)
        snapshot = self.window.snapshot
        self.window.snapshot = None
        self.window.render_zones()
        self.assertEqual(self.window.zone_preset_picker.currentData(), self.preset)
        self.window.snapshot = snapshot
        self.assertTrue(self.window.delete_zone())
        self.assertIsNone(self.window.zone_picker.currentData())
        self.assertIsNone(self.window.zone_preset_picker.currentData())
