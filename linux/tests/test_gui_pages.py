import unittest
import threading
from copy import deepcopy
from types import SimpleNamespace
import test_gui as fixtures


class DevicePageTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.app = fixtures.QApplication.instance() or fixtures.QApplication([])
    setUp = fixtures.GuiTests.setUp
    tearDown = fixtures.GuiTests.tearDown
    wait = fixtures.GuiTests.wait
    connect = fixtures.GuiTests.connect

    def test_home_lists_actual_devices_and_opens_exact_card_target(self):
        self.assertTrue(hasattr(self.window, 'device_cards'), 'My devices cards are missing')
        other = SimpleNamespace(address='22', path='/other', name='Other', connected=False)
        self.window.worker.discover = lambda cancelled=None: [self.device, other]
        self.window.submit('discover')
        self.wait(lambda: not self.window.busy)
        self.assertEqual(len(self.window.device_cards), 2)
        self.window.device_cards[1].click()
        self.wait(lambda: not self.window.busy)
        self.assertIs(self.window.device_picker.currentData(), other)
        self.assertEqual(self.window.page_kind, 'headset')
        self.assertTrue(self.window.noise_card.isVisible())
        self.assertFalse(self.window.eq_card.isVisible())

    def test_refresh_keeps_identity_when_mutable_metadata_changes(self):
        self.connect()
        original = self.window.snapshot
        changed = SimpleNamespace(address=self.device.address, path=self.device.path, name='Renamed', connected=False)
        self.window.worker.discover = lambda cancelled=None: [changed]
        self.window.submit('discover')
        self.wait(lambda: not self.window.busy)
        self.assertIs(self.window.snapshot, original)
        self.assertIs(self.window.device_picker.currentData(), changed)

    def test_missing_selected_target_does_not_retarget_other_headset(self):
        self.connect()
        other = SimpleNamespace(address='22', path='/other', name='Other', connected=False)
        self.window.worker.discover = lambda cancelled=None: [other]
        self.window.submit('discover')
        self.wait(lambda: not self.window.busy)
        self.assertIsNone(self.window.device_picker.currentData())
        self.assertIsNone(self.window.snapshot)
        self.assertFalse(self.window.refresh_button.isEnabled())

    def test_back_and_tabs_preserve_queued_edits_and_late_reply_keeps_home(self):
        self.connect()
        self.assertTrue(hasattr(self.window, 'back_button'), 'Back navigation is missing')
        self.mode = 'writeblock'
        self.window.write('anc_strength', 30)
        self.wait(self.started.is_set)
        self.assertTrue(self.window.write('anc_strength', 40))
        self.window.show_section('equalizer')
        self.window.back_button.click()
        self.assertEqual(self.window.page_kind, 'home')
        self.assertEqual(len(self.window.pending_edits), 1)
        self.assertFalse(self.window.worker.shutdown.is_set())
        self.assertFalse(self.window.device_cards[0].isEnabled())
        self.block.set()
        self.wait(lambda: not self.window.busy)
        self.assertEqual(self.window.page_kind, 'home')
        self.assertEqual([call[1:4] for call in self.calls if call[0] == 'write'], [('anc_strength', 30, None), ('anc_strength', 40, None)])
        self.window.device_cards[0].click()
        self.assertEqual(self.window.section, 'equalizer')
        self.assertTrue(self.window.eq_card.isVisible())

    def test_sections_fit_default_and_compact_window_without_long_scroll(self):
        self.connect()
        self.assertTrue(hasattr(self.window, 'show_section'), 'Device sections are missing')
        for width, height in ((500, 820), (460, 780)):
            self.window.resize(width, height)
            for section in ('noise', 'equalizer', 'controls', 'info'):
                self.window.show_section(section)
                self.app.processEvents()
                self.assertLessEqual(self.window.scroll.widget().width(), self.window.scroll.viewport().width())
                self.assertEqual(self.window.scroll.verticalScrollBar().maximum(), 0, (width, height, section))

    def test_coupled_transparency_amount_stays_visible_after_repeated_partial_changes(self):
        value = fixtures.snapshot(frozenset({'anc_enabled', 'transparency', 'transparency_level', 'anc_strength'}))
        value.settings.update(transparency=True, transparency_level=100, anc_strength=100, adaptive_anc=True, antiwind=0)
        self.device_snapshot = value
        self.transparency_coupled = True
        self.connect()
        slider = self.window.controls['transparency_level']
        self.assertEqual(slider.value(), 100)
        self.assertTrue(slider.isEnabled())
        self.calls.clear()
        for amount in (75, 50):
            self.assertTrue(self.window.write('transparency_level', amount))
            self.wait(lambda: not self.window.busy)
            self.assertEqual(self.window.snapshot.settings['transparency_level'], amount)
            self.assertEqual(self.window.snapshot.settings['anc_strength'], amount)
            self.assertFalse(self.window.snapshot.settings['transparency'])
            self.assertTrue(self.window.snapshot.settings['adaptive_anc'])
            self.assertTrue(self.window.transparency_box.isVisible())
            self.assertTrue(slider.isEnabled())
            self.assertEqual(slider.value(), amount)
            self.assertTrue(self.window.noise_buttons['adaptive'].isChecked())
        writes = [call[1:4] for call in self.calls if call[0] == 'write']
        self.assertEqual(writes, [('transparency_level', 75, None), ('transparency_level', 50, None)])
        self.window.apply_snapshot(deepcopy(self.device_snapshot))
        self.window.update_enabled()
        self.assertEqual([call[1:4] for call in self.calls if call[0] == 'write'], writes)
        value.settings['anc_enabled'] = False
        self.window.apply_snapshot(deepcopy(value))
        self.window.update_enabled()
        self.assertTrue(self.window.transparency_box.isVisible())
        self.assertFalse(slider.isEnabled())
        self.assertFalse(self.window.write('transparency_level', 25))
        self.assertIn('enable noise', self.window.transparency_hint.text().lower())

    def test_transparency_pending_overlay_survives_partial_readback_and_applies_next_amount(self):
        from PySide6.QtCore import QPoint, Qt
        from PySide6.QtTest import QTest
        value = fixtures.snapshot(frozenset({'anc_enabled', 'transparency', 'transparency_level', 'anc_strength'}))
        value.settings.update(transparency=True, transparency_level=100, anc_strength=100, adaptive_anc=True, antiwind=0)
        self.device_snapshot = value
        self.transparency_coupled = True
        self.connect()
        self.mode = 'writeblock'
        second_gate = threading.Event()
        second_started = threading.Event()
        original = self.clients[0].set_value
        amounts = []
        def delayed(name, value, band=None):
            amounts.append(value)
            if len(amounts) == 2:
                second_started.set()
                second_gate.wait(3)
            return original(name, value, band)
        self.clients[0].set_value = delayed
        try:
            self.assertTrue(self.window.write('transparency_level', 75))
            self.wait(self.started.is_set)
            slider = self.window.controls['transparency_level']
            QTest.mousePress(slider, Qt.MouseButton.LeftButton, pos=QPoint(slider.width() // 2, slider.height() // 2))
            slider.setValue(50)
            QTest.mouseRelease(slider, Qt.MouseButton.LeftButton, pos=QPoint(slider.width() // 2, slider.height() // 2))
            self.assertEqual(slider.value(), 50)
            self.assertEqual(self.window.snapshot.settings['transparency_level'], 100)
            self.assertEqual(len(self.window.pending_edits), 1)
            self.block.set()
            self.wait(second_started.is_set)
            self.assertEqual(self.window.snapshot.settings['transparency_level'], 75)
            self.assertEqual(slider.value(), 50)
            self.assertTrue(slider.isEnabled())
            self.assertTrue(self.window.transparency_box.isVisible())
            second_gate.set()
            self.wait(lambda: not self.window.busy)
            self.assertEqual(self.window.snapshot.settings['transparency_level'], 50)
            self.assertEqual(slider.value(), 50)
            self.assertTrue(slider.isEnabled())
            self.assertTrue(self.window.transparency_box.isVisible())
            self.assertEqual([call[1:4] for call in self.calls if call[0] == 'write'], [('transparency_level', 75, None), ('transparency_level', 50, None)])
        finally:
            second_gate.set()

    def test_late_connection_failure_keeps_home_and_does_not_reveal_other_sections(self):
        self.mode = 'connectblock'
        self.window.device_cards[0].click()
        self.wait(self.started.is_set)
        self.window.back_button.click()
        self.mode = 'error'
        self.block.set()
        self.wait(lambda: not self.window.busy)
        self.assertEqual(self.window.page_kind, 'home')
        self.assertTrue(self.window.home_card.isVisible())
        self.assertFalse(self.window.connection_card.isVisible())
        self.assertFalse(self.window.noise_card.isVisible())
        self.assertIn('Headset unavailable', self.window.message.text())

    def test_transparency_amount_denies_missing_composite_capabilities_or_noise_state(self):
        value = fixtures.snapshot(frozenset({'anc_enabled', 'transparency', 'transparency_level', 'anc_strength'}))
        value.settings.update(transparency=False, transparency_level=75, anc_strength=75, adaptive_anc=True, antiwind=0)
        self.device_snapshot = value
        self.connect()
        self.assertTrue(self.window.controls['transparency_level'].isEnabled())
        for key in ('anc_enabled', 'anc_strength', 'transparency', 'antiwind', 'adaptive_anc'):
            with self.subTest(missing=key):
                incomplete = deepcopy(value)
                incomplete.settings.pop(key)
                self.window.apply_snapshot(incomplete)
                self.window.update_enabled()
                self.assertFalse(self.window.controls['transparency_level'].isEnabled())
                self.assertFalse(self.window.write('transparency_level', 50))
        for key in ('transparency', 'transparency_level'):
            with self.subTest(capability=key):
                incomplete = deepcopy(value)
                incomplete.writable = incomplete.writable - {key}
                self.window.apply_snapshot(incomplete)
                self.window.update_enabled()
                self.assertFalse(self.window.controls['transparency_level'].isEnabled())
                self.assertFalse(self.window.write('transparency_level', 50))
        self.assertFalse(any(call[0] == 'write' for call in self.calls))

    def test_header_back_position_is_stable_across_sections(self):
        self.connect()
        positions = []
        for section in ('noise', 'equalizer', 'controls', 'info'):
            self.window.show_section(section)
            self.app.processEvents()
            positions.append(self.window.back_button.y())
        self.assertEqual(positions, [positions[0]] * 4)

    def test_rebuilt_home_cards_hide_retired_widgets_before_deferred_delete(self):
        self.app.processEvents()
        retired = self.window.device_cards[0]
        self.assertTrue(retired.isVisible())
        self.window.rebuild_device_cards()
        self.assertFalse(retired.isVisible())
        self.app.processEvents()
        self.assertTrue(self.window.device_cards[0].isVisible())
