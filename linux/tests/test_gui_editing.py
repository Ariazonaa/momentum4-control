"""Real Qt focus/events with delayed fake headset peers; no Bluetooth/USB access."""
import os
os.environ.setdefault('QT_QPA_PLATFORM', 'offscreen')
from copy import deepcopy
import threading
import unittest
from PySide6.QtCore import QEventLoop, QPoint, QTimer, Qt
from PySide6.QtTest import QTest
from PySide6.QtWidgets import QApplication
import test_gui as fixtures


class EditingGuiTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.app = QApplication.instance() or QApplication([])

    wait = fixtures.GuiTests.wait
    connect = fixtures.GuiTests.connect

    def setUp(self):
        fixtures.GuiTests.setUp(self)
        self.write_gates = []
        self.device_snapshot = fixtures.snapshot(frozenset({'anc_enabled', 'adaptive_anc', 'transparency', 'anc_strength', 'smart_pause', 'touch_controls', 'bass_boost', 'sound_mode', 'eq'}))
        self.device_snapshot.settings.update(adaptive_anc=False, transparency=False, smart_pause=True, touch_controls=True, bass_boost=False, sound_mode=1)
        self.device_snapshot.eq_bands = (0.,) * 8
        self.device_snapshot.eq_frequencies = (90, 325, 1500, 2500, 4500, 8000, 13000, 16000)
        self.connect()
        self.window.show_section('equalizer')
        self.window.resize(500, 820)
        self.settle()

    def tearDown(self):
        for gate in self.write_gates:
            gate.set()
        fixtures.GuiTests.tearDown(self)

    def settle(self, milliseconds=30):
        loop = QEventLoop()
        QTimer.singleShot(milliseconds, loop.quit)
        loop.exec()

    def writes(self):
        return [call[1:4] for call in self.calls if call[0] == 'write']

    def start_delayed(self, name='smart_pause', value=False):
        self.mode = 'writeblock'
        self.assertTrue(self.window.write(name, value))
        self.wait(self.started.is_set)

    def test_real_slider_click_keeps_focus_scroll_and_other_editors_while_worker_waits(self):
        window = self.window
        window.show_section('noise')
        slider = window.controls['anc_strength']
        window.scroll.ensureWidgetVisible(slider)
        self.settle()
        slider.setFocus()
        before = window.scroll.verticalScrollBar().value()
        self.mode = 'writeblock'
        ticks = []
        timer = QTimer()
        timer.setInterval(5)
        timer.timeout.connect(lambda: ticks.append(True))
        timer.start()
        try:
            QTest.mouseClick(slider, Qt.MouseButton.LeftButton, pos=QPoint(slider.width() // 3, slider.height() // 2))
            self.wait(self.started.is_set)
            self.settle(50)
            self.assertTrue(window.busy)
            self.assertTrue(slider.isEnabled())
            self.assertIs(self.app.focusWidget(), slider)
            self.assertEqual(window.scroll.verticalScrollBar().value(), before)
            self.assertGreater(len(ticks), 2)
            self.assertTrue(window.controls['smart_pause'].isEnabled())
            self.assertFalse(window.refresh_button.isEnabled())
            self.block.set()
            self.wait(lambda: not window.busy)
            self.settle()
            self.assertEqual(window.scroll.verticalScrollBar().value(), before)
            self.assertIs(self.app.focusWidget(), slider)
        finally:
            timer.stop()

    def test_equalizer_readback_keeps_widget_identity_and_real_keyboard_focus(self):
        slider = self.window.eq_controls[0]
        self.window.scroll.ensureWidgetVisible(slider)
        self.settle()
        slider.setFocus()
        before = self.window.scroll.verticalScrollBar().value()
        QTest.keyClick(slider, Qt.Key.Key_Up)
        self.wait(lambda: not self.window.busy)
        self.settle()
        self.assertIs(self.window.eq_controls[0], slider)
        self.assertIs(self.app.focusWidget(), slider)
        self.assertEqual(self.window.scroll.verticalScrollBar().value(), before)
        self.assertEqual(slider.value(), .1)

    def test_different_controls_are_retained_and_same_key_moves_to_last_intent(self):
        self.calls.clear()
        self.start_delayed()
        self.assertTrue(self.window.write('touch_controls', False))
        self.assertTrue(self.window.write('bass_boost', True))
        self.assertTrue(self.window.write('touch_controls', True))
        self.assertEqual(len(self.window.pending_edits), 2)
        self.block.set()
        self.wait(lambda: not self.window.busy)
        self.assertEqual(self.writes(), [('smart_pause', False, None), ('bass_boost', True, None), ('touch_controls', True, None)])
        self.assertEqual(self.window.snapshot.settings['touch_controls'], True)
        self.assertEqual(self.window.snapshot.settings['bass_boost'], True)

    def test_stale_ack_preserves_latest_desired_overlay_without_changing_verified_snapshot(self):
        first, second = threading.Event(), threading.Event()
        self.write_gates.extend((first, second))
        second_started = threading.Event()
        original = self.clients[0].set_value
        calls = []
        def delayed(name, value, band=None, expected_topology=None):
            calls.append((name, value, band))
            if len(calls) == 1:
                self.started.set()
                first.wait(3)
            else:
                second_started.set()
                second.wait(3)
            return original(name, value, band, expected_topology=expected_topology)
        self.clients[0].set_value = delayed
        self.assertTrue(self.window.write('touch_controls', False))
        self.wait(self.started.is_set)
        self.assertTrue(self.window.write('touch_controls', True))
        first.set()
        self.wait(second_started.is_set)
        self.assertFalse(self.window.snapshot.settings['touch_controls'])
        self.assertTrue(self.window.controls['touch_controls'].isChecked())
        self.assertTrue(self.window.busy)
        second.set()
        self.wait(lambda: not self.window.busy)
        self.assertTrue(self.window.snapshot.settings['touch_controls'])
        self.assertEqual(calls, [('touch_controls', False, None), ('touch_controls', True, None)])

    def test_pending_cap_is32_and_replacements_do_not_grow_it(self):
        for index in range(34):
            self.window.snapshot.settings[f'future_{index}'] = False
        self.window.snapshot.writable = self.window.snapshot.writable | {f'future_{index}' for index in range(34)}
        self.start_delayed()
        for index in range(32):
            self.assertTrue(self.window.write(f'future_{index}', True))
        self.assertEqual(len(self.window.pending_edits), 32)
        self.assertFalse(self.window.write('future_32', True))
        self.assertTrue(self.window.write('future_0', False))
        self.assertEqual(len(self.window.pending_edits), 32)
        self.window.selection_changed()
        self.block.set()
        self.wait(lambda: not self.window.busy)

    def test_user_scroll_during_write_is_retained_at_completion(self):
        self.window.resize(460, 360)
        self.settle()
        self.start_delayed()
        bar = self.window.scroll.verticalScrollBar()
        desired = bar.maximum() // 2
        bar.setValue(desired)
        self.settle()
        self.block.set()
        self.wait(lambda: not self.window.busy)
        self.settle()
        self.assertEqual(bar.value(), desired)

    def test_key_held_draft_survives_other_control_readback_until_release(self):
        self.start_delayed()
        slider = self.window.eq_controls[0]
        self.window.scroll.ensureWidgetVisible(slider)
        self.settle()
        slider.setFocus()
        QTest.keyPress(slider, Qt.Key.Key_Up)
        self.assertEqual(slider.value(), .1)
        self.block.set()
        self.wait(lambda: not self.window.busy)
        self.settle()
        self.assertEqual(self.window.eq_controls[0].value(), .1)
        self.assertEqual(self.window.snapshot.eq_bands[0], 0.)
        QTest.keyRelease(self.window.eq_controls[0], Qt.Key.Key_Up)
        self.wait(lambda: not self.window.busy)
        self.assertEqual(self.window.snapshot.eq_bands[0], .1)

    def test_mouse_drag_draft_survives_other_control_readback_until_release(self):
        self.start_delayed()
        slider = self.window.eq_controls[0]
        self.window.scroll.ensureWidgetVisible(slider)
        self.settle()
        QTest.mousePress(slider, Qt.MouseButton.LeftButton, pos=QPoint(slider.width() // 2, slider.height() // 2))
        QTest.mouseMove(slider, QPoint(slider.width() // 2, slider.height() // 3))
        desired = slider.value()
        self.assertNotEqual(desired, 0.)
        self.block.set()
        self.wait(lambda: not self.window.busy)
        self.settle()
        self.assertEqual(self.window.eq_controls[0].value(), desired)
        self.assertEqual(self.window.snapshot.eq_bands[0], 0.)
        QTest.mouseRelease(self.window.eq_controls[0], Qt.MouseButton.LeftButton, pos=QPoint(slider.width() // 2, slider.height() // 3))
        self.wait(lambda: not self.window.busy)
        self.assertEqual(self.window.snapshot.eq_bands[0], desired)

    def test_queued_work_rechecks_latest_capabilities_and_cancels_unsafe_drafts(self):
        self.start_delayed()
        self.assertTrue(self.window.write('touch_controls', False))
        slider = self.window.eq_controls[0]
        slider.setFocus()
        QTest.keyPress(slider, Qt.Key.Key_Up)
        self.device_snapshot.writable = self.device_snapshot.writable - {'touch_controls', 'eq'}
        self.block.set()
        self.wait(lambda: not self.window.busy)
        self.settle()
        self.assertEqual(self.writes(), [('smart_pause', False, None)])
        self.assertFalse(self.window.controls['touch_controls'].isEnabled())
        self.assertFalse(self.window.eq_controls[0].isEnabled())
        self.assertEqual(self.window.eq_controls[0].value(), 0.)
        QTest.keyRelease(self.window.eq_controls[0], Qt.Key.Key_Up)
        self.assertEqual(self.writes(), [('smart_pause', False, None)])

    def test_failure_clears_pending_and_does_not_replay_after_reconnect(self):
        self.calls.clear()
        self.write_failure = 'smart_pause'
        self.assertTrue(self.window.write('smart_pause', False))
        self.assertTrue(self.window.write('touch_controls', False))
        self.wait(lambda: not self.window.busy)
        self.assertIsNone(self.window.snapshot)
        self.assertEqual(len(self.window.pending_edits), 0)
        self.assertEqual(self.writes(), [('smart_pause', False, None)])
        self.write_failure = ''
        self.connect()
        self.assertEqual(self.writes(), [('smart_pause', False, None)])

    def test_generation_change_and_close_clear_pending_without_replay(self):
        self.calls.clear()
        self.start_delayed()
        self.assertTrue(self.window.write('touch_controls', False))
        self.window.selection_changed()
        self.assertEqual(len(self.window.pending_edits), 0)
        self.block.set()
        self.wait(lambda: not self.window.busy)
        self.assertEqual(self.writes(), [('smart_pause', False, None)])
        self.mode = ''
        self.connect()
        self.calls.clear();self.block.clear();self.started.clear()
        self.start_delayed()
        self.assertTrue(self.window.write('touch_controls', False))
        self.window.close()
        self.wait(lambda: not self.window.thread.isRunning())
        self.assertEqual(len(self.window.pending_edits), 0)
        self.assertEqual(self.writes(), [('smart_pause', False, None)])

    def test_exclusive_refresh_click_uses_neutral_focus_without_scroll_jump(self):
        self.window.show_section('info')
        self.window.scroll.verticalScrollBar().setValue(0)
        self.settle()
        before = self.window.scroll.verticalScrollBar().value()
        self.mode = 'block'
        QTest.mouseClick(self.window.refresh_button, Qt.MouseButton.LeftButton)
        self.wait(self.started.is_set)
        self.settle()
        self.assertEqual(self.window.scroll.verticalScrollBar().value(), before)
        self.assertFalse(self.window.controls['smart_pause'].isEnabled())
        self.assertFalse(self.window.write('smart_pause', False))
        self.block.set()
        self.wait(lambda: not self.window.busy)

    def test_connection_and_firmware_actions_stay_exclusive_while_edits_are_pending(self):
        self.start_delayed()
        self.assertTrue(self.window.write('touch_controls', False))
        for action in ('connect', 'refresh', 'discover', 'dongle-status', 'dongle-check', 'dongle-update'):
            self.assertFalse(self.window.submit(action), action)
        self.assertFalse(self.window.start_dongle_update())
        self.block.set()
        self.wait(lambda: not self.window.busy)

    def assert_old_topology_edit_dropped(self, change):
        self.calls.clear()
        self.start_delayed()
        self.assertTrue(self.window.write('eq', 1.0, band=0))
        change(self.device_snapshot)
        self.block.set()
        self.wait(lambda: not self.window.busy)
        self.settle()
        self.assertEqual(self.writes(), [('smart_pause', False, None)])
        self.assertEqual(self.window.eq_controls[0].value(), 0.)
        self.assertEqual(len(self.window.pending_edits), 0)

    def test_frequency_mapping_change_discards_old_queued_band_edit(self):
        def change(snapshot):
            snapshot.eq_frequencies = (100, *snapshot.eq_frequencies[1:])
        self.assert_old_topology_edit_dropped(change)

    def test_band_count_change_discards_old_queued_band_edit_even_when_index_still_exists(self):
        def change(snapshot):
            snapshot.eq_bands = snapshot.eq_bands[:-1]
            snapshot.eq_frequencies = snapshot.eq_frequencies[:-1]
        self.assert_old_topology_edit_dropped(change)

    def test_queued_noise_modes_share_one_key_and_keep_verified_mode_until_readback(self):
        self.calls.clear()
        self.start_delayed()
        self.assertTrue(self.window.select_noise_mode('adaptive'))
        self.assertTrue(self.window.write('anc_strength', 40))
        self.assertTrue(self.window.select_noise_mode('off'))
        self.assertEqual(len(self.window.pending_edits), 2)
        self.assertTrue(self.window.noise_buttons['anc'].isChecked())
        self.assertIn('pending', self.window.edit_status.text())
        self.block.set()
        self.wait(lambda: not self.window.busy)
        self.assertEqual(self.writes(), [('smart_pause', False, None), ('anc_strength', 40, None)])
        self.assertEqual([call[1] for call in self.calls if call[0] == 'mode'], ['off'])
        self.assertTrue(self.window.noise_buttons['off'].isChecked())

    def test_latest_intent_is_queued_even_when_it_equals_inflight_value(self):
        self.calls.clear()
        self.start_delayed()
        self.assertTrue(self.window.write('smart_pause', True))
        self.assertEqual(len(self.window.pending_edits), 1)
        self.assertTrue(self.window.write('smart_pause', False))
        self.assertEqual(len(self.window.pending_edits), 1)
        self.block.set()
        self.wait(lambda: not self.window.busy)
        self.assertEqual(self.writes(), [('smart_pause', False, None), ('smart_pause', False, None)])
        self.assertFalse(self.window.snapshot.settings['smart_pause'])

    def test_lost_focus_cancels_uncommitted_key_draft_without_phantom_write(self):
        slider = self.window.eq_controls[0]
        self.window.scroll.ensureWidgetVisible(slider)
        self.settle()
        slider.setFocus()
        height = self.window.edit_status.height()
        QTest.keyPress(slider, Qt.Key.Key_Up)
        self.assertEqual(slider.value(), .1)
        self.assertIn('Editing', self.window.edit_status.text())
        self.assertEqual(self.window.edit_status.height(), height)
        QTest.keyClick(slider, Qt.Key.Key_Tab)
        self.settle()
        self.assertFalse(slider.editing)
        self.assertEqual(slider.value(), 0.)
        QTest.keyRelease(slider, Qt.Key.Key_Up)
        self.settle()
        self.assertEqual(self.writes(), [])
        self.assertEqual(self.window.edit_status.text(), '')

    def test_equal_inflight_strength_intent_executes_after_queued_mode_side_effect(self):
        original = self.clients[0].set_noise_mode
        def mode_side_effect(mode):
            if mode == 'transparency':
                self.device_snapshot.settings['anc_strength'] = 100
            return original(mode)
        self.clients[0].set_noise_mode = mode_side_effect
        self.calls.clear()
        self.start_delayed('anc_strength', 0)
        self.assertTrue(self.window.select_noise_mode('transparency'))
        self.assertTrue(self.window.write('anc_strength', 0))
        self.assertEqual(len(self.window.pending_edits), 2)
        self.block.set()
        self.wait(lambda: not self.window.busy)
        self.assertEqual(self.writes(), [('anc_strength', 0, None), ('anc_strength', 0, None)])
        self.assertEqual([call[1] for call in self.calls if call[0] == 'mode'], ['transparency'])
        self.assertEqual(self.window.snapshot.settings['anc_strength'], 0)

    def test_release_on_different_slider_does_not_commit_an_untracked_gesture(self):
        self.device_snapshot.eq_bands = (0., 1., *self.device_snapshot.eq_bands[2:])
        self.window.apply_snapshot(deepcopy(self.device_snapshot))
        first, second = self.window.eq_controls[:2]
        first.setFocus()
        QTest.keyPress(first, Qt.Key.Key_Up)
        QTest.keyClick(first, Qt.Key.Key_Tab)
        self.settle()
        self.assertIs(self.app.focusWidget(), second)
        QTest.keyRelease(second, Qt.Key.Key_Up)
        self.settle()
        self.assertEqual(self.writes(), [])

    def test_modifier_release_does_not_cancel_held_adjustment_draft(self):
        slider = self.window.eq_controls[0]
        slider.setFocus()
        QTest.keyPress(slider, Qt.Key.Key_Up)
        QTest.keyRelease(slider, Qt.Key.Key_Shift)
        self.assertTrue(slider.editing)
        self.window.apply_snapshot(deepcopy(self.device_snapshot))
        self.assertEqual(slider.value(), .1)
        QTest.keyRelease(slider, Qt.Key.Key_Up)
        self.wait(lambda: not self.window.busy)
        self.assertEqual(self.writes(), [('eq', .1, 0)])

    def test_cancelled_key_release_never_writes_into_reconnected_session(self):
        slider = self.window.eq_controls[0]
        slider.setFocus()
        QTest.keyPress(slider, Qt.Key.Key_Up)
        self.window.selection_changed()
        self.device_snapshot.eq_bands = (2., *self.device_snapshot.eq_bands[1:])
        self.connect()
        QTest.keyRelease(self.window.eq_controls[0], Qt.Key.Key_Up)
        self.settle()
        self.assertEqual(self.writes(), [])

    def test_window_deactivation_cancels_key_draft_without_phantom_write(self):
        from PySide6.QtWidgets import QPushButton, QVBoxLayout, QWidget
        slider = self.window.eq_controls[0]
        slider.setFocus()
        QTest.keyPress(slider, Qt.Key.Key_Up)
        probe = QWidget()
        layout = QVBoxLayout(probe)
        button = QPushButton('Focus probe')
        layout.addWidget(button)
        try:
            probe.show()
            probe.activateWindow()
            QTest.mouseClick(button, Qt.MouseButton.LeftButton)
            self.settle()
            self.assertFalse(self.window.isActiveWindow())
            self.assertFalse(slider.editing)
            self.assertEqual(slider.value(), 0.)
            QTest.keyRelease(slider, Qt.Key.Key_Up)
            self.settle()
            self.assertEqual(self.writes(), [])
        finally:
            probe.close()
            self.window.activateWindow()

    def test_cancelled_mouse_release_never_writes_into_reconnected_session(self):
        slider = self.window.eq_controls[0]
        self.window.scroll.ensureWidgetVisible(slider)
        self.settle()
        QTest.mousePress(slider, Qt.MouseButton.LeftButton, pos=QPoint(slider.width() // 2, slider.height() // 2))
        QTest.mouseMove(slider, QPoint(slider.width() // 2, slider.height() // 3))
        self.window.selection_changed()
        self.device_snapshot.eq_bands = (2., *self.device_snapshot.eq_bands[1:])
        self.connect()
        slider = self.window.eq_controls[0]
        QTest.mouseRelease(slider, Qt.MouseButton.LeftButton, pos=QPoint(slider.width() // 2, slider.height() // 3))
        self.settle()
        self.assertEqual(self.writes(), [])

    def test_dirty_keyboard_gesture_commits_final_intent_when_value_returns_to_start(self):
        self.start_delayed()
        slider = self.window.eq_controls[0]
        slider.setFocus()
        QTest.keyPress(slider, Qt.Key.Key_Up)
        QTest.keyPress(slider, Qt.Key.Key_Down)
        self.assertEqual(slider.value(), 0.)
        self.device_snapshot.eq_bands = (2., *self.device_snapshot.eq_bands[1:])
        self.block.set()
        self.wait(lambda: not self.window.busy)
        self.assertEqual(self.window.snapshot.eq_bands[0], 2.)
        self.assertEqual(slider.value(), 0.)
        QTest.keyRelease(slider, Qt.Key.Key_Up)
        QTest.keyRelease(slider, Qt.Key.Key_Down)
        self.wait(lambda: not self.window.busy)
        self.assertEqual(self.window.snapshot.eq_bands[0], 0.)
        self.assertIn(('eq', 0., 0), self.writes())

    def test_late_slider_released_signal_without_live_mouse_gesture_does_not_write(self):
        slider = self.window.eq_controls[0]
        slider.setFocus()
        QTest.mousePress(slider, Qt.MouseButton.LeftButton, pos=QPoint(slider.width() // 2, slider.height() // 2))
        QTest.mouseMove(slider, QPoint(slider.width() // 2, slider.height() // 3))
        QTest.keyClick(slider, Qt.Key.Key_Tab)
        self.settle()
        self.device_snapshot.eq_bands = (2., *self.device_snapshot.eq_bands[1:])
        self.window.apply_snapshot(deepcopy(self.device_snapshot))
        slider.sliderReleased.emit()
        self.settle()
        self.assertEqual(self.writes(), [])

    def test_untouched_limit_gesture_restores_later_readback_without_sending_write(self):
        self.device_snapshot.eq_bands = (6., *self.device_snapshot.eq_bands[1:])
        self.window.apply_snapshot(deepcopy(self.device_snapshot))
        self.start_delayed()
        slider = self.window.eq_controls[0]
        slider.setFocus()
        QTest.keyPress(slider, Qt.Key.Key_Up)
        self.assertEqual(slider.value(), 6.)
        self.device_snapshot.eq_bands = (5., *self.device_snapshot.eq_bands[1:])
        self.block.set()
        self.wait(lambda: not self.window.busy)
        self.assertEqual(slider.value(), 6.)
        QTest.keyRelease(slider, Qt.Key.Key_Up)
        self.settle()
        self.assertEqual(slider.value(), 5.)
        self.assertEqual(self.window.snapshot.eq_bands[0], 5.)
        self.assertEqual(self.writes(), [('smart_pause', False, None)])

    def test_orphan_auto_repeat_after_reconnect_cannot_start_a_new_edit(self):
        from PySide6.QtCore import QEvent
        from PySide6.QtGui import QKeyEvent
        slider = self.window.eq_controls[0]
        slider.setFocus()
        QTest.keyPress(slider, Qt.Key.Key_Up)
        self.window.selection_changed()
        self.device_snapshot.eq_bands = (2., *self.device_snapshot.eq_bands[1:])
        self.connect()
        slider = self.window.eq_controls[0]
        repeat = QKeyEvent(QEvent.Type.KeyPress, Qt.Key.Key_Up, Qt.KeyboardModifier.NoModifier, '', True, 1)
        self.app.sendEvent(slider, repeat)
        QTest.keyRelease(slider, Qt.Key.Key_Up)
        self.settle()
        self.assertEqual(slider.value(), 2.)
        self.assertEqual(self.writes(), [])
