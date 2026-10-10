import os
import subprocess
import sys
from copy import deepcopy
import threading
import time
import unittest
from types import SimpleNamespace

os.environ.setdefault('QT_QPA_PLATFORM', 'offscreen')
from PySide6.QtWidgets import QApplication
from PySide6.QtCore import QCoreApplication, QEvent, QPoint, QPointF, Qt
from PySide6.QtGui import QWheelEvent
try:
    from momentum_control.gui import MainWindow
except ImportError:
    MainWindow = None


def snapshot(writable=frozenset({'anc_enabled', 'anc_strength', 'eq'}), model='MOMENTUM 4'):
    return SimpleNamespace(model=model, firmware='3.38.3', battery=71,
        settings={'anc_enabled': True, 'anc_strength': 20}, eq_bands=(1.0, -2.0),
        eq_frequencies=(100, 1000), writable=writable, notes=(), choices={})


class GuiTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.app = QApplication.instance() or QApplication([])

    def setUp(self):
        self.assertIsNotNone(MainWindow, 'native GUI implementation is missing')
        self.calls = []
        self.clients = []
        self.block = threading.Event()
        self.started = threading.Event()
        self.mode = ''
        self.device_snapshot = None
        self.write_failure = ''
        self.force_wrong_mode = False
        self.device = SimpleNamespace(address='AA:BB:CC:DD:EE:FF', name='Headset', path='/device', connected=True)
        owner = self
        class Client:
            def __init__(self, device, cancelled=None):
                self.cancelled = cancelled
                owner.clients.append(self)
                owner.calls.append(('create', threading.get_ident()))
            def connect(self):
                owner.calls.append(('connect', threading.get_ident()))
                if self.cancelled.is_set():
                    raise RuntimeError('Operation cancelled')
                if owner.mode == 'connectblock':
                    owner.started.set()
                    owner.block.wait(3)
            def snapshot(self):
                owner.calls.append(('snapshot', threading.get_ident()))
                if self.cancelled.is_set():
                    raise RuntimeError('Operation cancelled')
                if owner.mode == 'block':
                    owner.started.set()
                    owner.block.wait(3)
                if owner.mode == 'error':
                    raise RuntimeError('Headset unavailable')
                if owner.device_snapshot is not None:
                    return deepcopy(owner.device_snapshot)
                result = snapshot(frozenset() if owner.mode == 'readonly' else frozenset({'anc_enabled', 'anc_strength', 'eq'}))
                if owner.mode == 'personalization':
                    result.settings['sound_mode'] = 3
                    result.writable = result.writable | {'sound_mode'}
                return result
            def set_value(self, name, value, band=None, expected_topology=None):
                owner.calls.append(('write', name, value, band, threading.get_ident()))
                if owner.write_failure == name:
                    raise RuntimeError('Headset rejected setting')
                if owner.mode == 'writeblock':
                    owner.started.set()
                    owner.block.wait(3)
                if owner.device_snapshot is not None:
                    owner.device_snapshot.settings[name] = value
                    if name == 'transparency_level' and getattr(owner, 'transparency_coupled', False):
                        owner.device_snapshot.settings['anc_strength'] = value
                        owner.device_snapshot.settings['transparency'] = value == 100
                    if owner.force_wrong_mode and name == 'transparency' and value is False:
                        owner.device_snapshot.settings['adaptive_anc'] = False
                    if name == 'eq':
                        gains = list(owner.device_snapshot.eq_bands)
                        gains[band] = value
                        owner.device_snapshot.eq_bands = tuple(gains)
                    return deepcopy(owner.device_snapshot)
                return snapshot()
            def set_noise_mode(self, mode):
                from momentum_control.gui import mode_available
                owner.calls.append(('mode', mode, threading.get_ident()))
                if not mode_available(owner.device_snapshot, mode):
                    raise RuntimeError('Noise mode unavailable')
                if owner.write_failure or owner.force_wrong_mode:
                    raise RuntimeError('Noise mode readback failed')
                if owner.mode == 'writeblock':
                    owner.started.set()
                    owner.block.wait(3)
                settings = owner.device_snapshot.settings
                settings['anc_enabled'] = mode != 'off'
                if mode != 'off':
                    settings['transparency'] = mode == 'transparency'
                    settings['adaptive_anc'] = mode == 'adaptive'
                return deepcopy(owner.device_snapshot)
            def cancel(self):
                owner.calls.append(('cancel', threading.get_ident()))
                owner.block.set()
                if owner.mode == 'cancelerror':
                    raise RuntimeError('Cancellation failed')
            def close(self):
                owner.calls.append(('close', threading.get_ident()))
        def discover(cancelled=None):
            self.calls.append(('discover', threading.get_ident()))
            return [self.device]
        self.window = MainWindow(discover=discover, client_factory=Client)
        self.window.show()
        self.wait(lambda: self.window.device_picker.count() == 1 and not self.window.busy)

    def wait(self, predicate):
        deadline = time.monotonic() + 4
        while not predicate() and time.monotonic() < deadline:
            self.app.processEvents()
            time.sleep(.005)
        self.assertTrue(predicate(), 'GUI operation did not complete')

    def tearDown(self):
        if hasattr(self, 'window'):
            self.block.set()
            self.window.close()
            self.wait(lambda: not self.window.thread.isRunning())
            self.window.deleteLater()
            QCoreApplication.sendPostedEvents(None, QEvent.Type.DeferredDelete)
            self.app.processEvents()

    def connect(self):
        self.window.connect_button.click()
        self.wait(lambda: not self.window.busy)

    def test_main_uses_non_glib_dispatcher_before_entering_event_loop(self):
        script = """
from PySide6.QtCore import QTimer
from PySide6.QtWidgets import QApplication, QWidget
from momentum_control import gui
class ProbeWindow(QWidget):
    def show_startup(self, minimized=False):
        assert not minimized
        self.show()
    def show(self):
        super().show()
        QTimer.singleShot(0, self.inspect_dispatcher)
    def inspect_dispatcher(self):
        app = QApplication.instance()
        dispatcher = app.eventDispatcher().metaObject().className()
        print('dispatcher:', dispatcher, flush=True)
        app.exit(31 if 'Glib' in dispatcher else 0)
gui.MainWindow = ProbeWindow
raise SystemExit(gui.main(['momentum-control']))
"""
        environment = dict(os.environ, QT_QPA_PLATFORM='offscreen')
        environment.pop('QT_NO_GLIB', None)
        result = subprocess.run([sys.executable, '-c', script], env=environment,
            capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('dispatcher:', result.stdout)
        self.assertNotIn('Glib', result.stdout)

    def configure_noise(self, writable=None):
        value = snapshot(frozenset({'anc_enabled', 'adaptive_anc', 'transparency', 'anc_strength', 'eq'}) if writable is None else writable)
        value.settings.update(transparency=False, adaptive_anc=False)
        self.device_snapshot = value
        self.connect()
        return value

    def test_visible_noise_modes_forward_one_shared_operation(self):
        self.configure_noise()
        for mode in ('transparency', 'anc', 'adaptive', 'off'):
            with self.subTest(mode=mode):
                self.calls.clear()
                self.window.noise_buttons[mode].click()
                self.assertTrue(self.window.busy)
                self.assertFalse(self.window.submit('refresh'))
                self.wait(lambda: not self.window.busy)
                self.assertEqual([call[1] for call in self.calls if call[0] == 'mode'], [mode])
                self.assertFalse(any(call[0] == 'write' for call in self.calls))
                self.assertTrue(self.window.noise_buttons[mode].isChecked())

    def test_off_remains_off_when_transparency_flag_is_still_true(self):
        value = self.configure_noise(frozenset({'anc_enabled'}))
        value.settings.update(transparency=True, anc_enabled=True)
        self.window.apply_snapshot(deepcopy(value))
        self.window.update_enabled()
        self.assertTrue(self.window.noise_buttons['transparency'].isChecked())
        self.calls.clear()
        self.window.noise_buttons['off'].click()
        self.wait(lambda: not self.window.busy)
        self.assertTrue(self.window.snapshot.settings['transparency'])
        self.assertTrue(self.window.noise_buttons['off'].isChecked())
        self.assertEqual([call[1] for call in self.calls if call[0] == 'mode'], ['off'])

    def test_final_composed_mode_must_match_requested_mode(self):
        self.configure_noise()
        self.force_wrong_mode = True
        self.window.noise_buttons['adaptive'].click()
        self.wait(lambda: not self.window.busy)
        self.assertIsNone(self.window.snapshot)
        self.assertIn('incomplete', self.window.message.text().lower())
        self.assertFalse(self.window.noise_buttons['adaptive'].isChecked())
        self.assertTrue(any(call[0] == 'close' for call in self.calls))

    def test_slider_wheel_does_not_change_gain_or_issue_writes(self):
        self.configure_noise()
        self.window.show_section('equalizer')
        self.calls.clear()
        control = self.window.eq_controls[0]
        event = QWheelEvent(QPointF(14, 70), QPointF(14, 70), QPoint(), QPoint(0, 120),
            Qt.MouseButton.NoButton, Qt.KeyboardModifier.NoModifier, Qt.ScrollPhase.NoScrollPhase, False)
        self.app.sendEvent(control, event)
        self.assertEqual(control.value(), 1.0)
        self.assertFalse(any(call[0] == 'write' for call in self.calls))
        self.assertFalse(event.isAccepted())

    def test_eq_keyboard_changes_commit_once_on_key_release(self):
        from PySide6.QtTest import QTest
        self.configure_noise()
        self.window.show_section('equalizer')
        self.calls.clear()
        control = self.window.eq_controls[0]
        for _ in range(4):
            QTest.keyPress(control, Qt.Key.Key_Up)
        self.assertFalse(any(call[0] == 'write' for call in self.calls))
        self.assertFalse(self.window.busy)
        QTest.keyRelease(control, Qt.Key.Key_Up)
        self.wait(lambda: not self.window.busy)
        self.assertEqual([call[1:4] for call in self.calls if call[0] == 'write'], [('eq', 1.4, 0)])

    def test_mode_refresh_checks_all_capabilities_before_any_write(self):
        value = self.configure_noise()
        self.assertTrue(hasattr(self.window, 'noise_buttons'), 'noise mode controls are missing')
        self.assertTrue(self.window.noise_buttons['adaptive'].isEnabled())
        value.writable = frozenset({'anc_enabled', 'transparency'})
        self.calls.clear()
        self.window.noise_buttons['adaptive'].click()
        self.wait(lambda: not self.window.busy)
        self.assertFalse(any(call[0] == 'write' for call in self.calls))
        self.assertIsNone(self.window.snapshot)
        self.assertIn('unavailable', self.window.message.text().lower())
        self.assertTrue(any(call[0] == 'close' for call in self.calls))

    def test_missing_mode_capability_disables_mode_before_submission(self):
        self.configure_noise(frozenset({'anc_enabled'}))
        self.assertTrue(hasattr(self.window, 'noise_buttons'), 'noise mode controls are missing')
        self.assertFalse(self.window.noise_buttons['transparency'].isEnabled())
        self.assertFalse(self.window.select_noise_mode('transparency'))
        self.assertFalse(any(call[0] == 'write' for call in self.calls))

    def test_mode_failure_closes_connection_and_never_claims_requested_mode(self):
        self.configure_noise()
        self.assertTrue(hasattr(self.window, 'noise_buttons'), 'noise mode controls are missing')
        self.write_failure = 'anc_enabled'
        self.window.noise_buttons['adaptive'].click()
        self.wait(lambda: not self.window.busy)
        self.assertIsNone(self.window.snapshot)
        self.assertIn('incomplete', self.window.message.text().lower())
        self.assertFalse(self.window.noise_buttons['adaptive'].isChecked())
        self.assertTrue(any(call[0] == 'close' for call in self.calls))

    def test_mode_selection_waits_for_verified_readback(self):
        self.configure_noise()
        self.assertTrue(hasattr(self.window, 'noise_buttons'), 'noise mode controls are missing')
        self.mode = 'writeblock'
        self.window.noise_buttons['adaptive'].click()
        self.wait(self.started.is_set)
        self.assertTrue(self.window.noise_buttons['anc'].isChecked())
        self.assertFalse(self.window.noise_buttons['adaptive'].isChecked())
        self.block.set()
        self.wait(lambda: not self.window.busy)
        self.assertTrue(self.window.noise_buttons['adaptive'].isChecked())

    def test_strength_display_100_maps_to_device_level_zero_on_release(self):
        from PySide6.QtTest import QTest
        self.configure_noise()
        control = self.window.controls['anc_strength']
        self.assertEqual(control.value(), 80)
        self.calls.clear()
        QTest.mousePress(control, Qt.MouseButton.LeftButton, pos=QPoint(control.width() * 4 // 5, control.height() // 2))
        control.setValue(100)
        self.assertFalse(any(call[0] == 'write' for call in self.calls))
        self.assertFalse(self.window.busy)
        QTest.mouseRelease(control, Qt.MouseButton.LeftButton, pos=QPoint(control.width() * 4 // 5, control.height() // 2))
        self.wait(lambda: not self.window.busy)
        self.assertIn(('write', 'anc_strength', 0, None), [call[:-1] for call in self.calls])

    def test_eq_drag_and_render_do_not_write_until_release(self):
        from PySide6.QtTest import QTest
        self.configure_noise()
        self.window.show_section('equalizer')
        self.calls.clear()
        self.window.apply_snapshot(deepcopy(self.device_snapshot))
        self.window.update_enabled()
        self.assertFalse(any(call[0] == 'write' for call in self.calls))
        control = self.window.eq_controls[0]
        self.assertTrue(hasattr(control, 'sliderReleased'), 'EQ slider is missing')
        QTest.mousePress(control, Qt.MouseButton.LeftButton, pos=QPoint(control.width() // 2, control.height() // 2))
        for value in (1.1, 1.5, 2.0, 2.5):
            control.setValue(value)
        self.assertFalse(any(call[0] == 'write' for call in self.calls))
        self.assertFalse(self.window.busy)
        QTest.mouseRelease(control, Qt.MouseButton.LeftButton, pos=QPoint(control.width() // 2, control.height() // 2))
        QTest.mouseRelease(control, Qt.MouseButton.LeftButton, pos=QPoint(control.width() // 2, control.height() // 2))
        self.wait(lambda: not self.window.busy)
        writes = [call[1:4] for call in self.calls if call[0] == 'write']
        self.assertEqual(writes, [('eq', 2.5, 0)])

    def test_peer_disconnect_then_retry_uses_fresh_session_cancellation(self):
        self.connect()
        old = self.clients[0]
        old.cancelled.set()
        self.window.refresh_button.click()
        self.wait(lambda: not self.window.busy)
        self.assertIsNone(self.window.snapshot)
        self.assertIn('cancelled', self.window.message.text())
        self.connect()
        self.assertIsNotNone(self.window.snapshot)
        self.assertTrue(self.window.refresh_button.isEnabled())
        self.assertEqual(len(self.clients), 2)
        self.assertIsNot(old.cancelled, self.clients[1].cancelled)
        self.assertTrue(old.cancelled.is_set())

    def test_snapshot_choices_disable_unsupported_values_and_reset_for_next_device(self):
        self.connect()
        value = snapshot(frozenset({'antiwind'}))
        value.settings = {'antiwind': 1}
        value.choices = {'antiwind': (0, 1)}
        self.window.apply_snapshot(value)
        self.window.update_enabled()
        control = self.window.controls['antiwind']
        self.assertTrue(control.isEnabled())
        self.assertTrue(control.model().item(0).isEnabled())
        self.assertTrue(control.model().item(1).isEnabled())
        self.assertFalse(control.model().item(2).isEnabled())
        self.assertFalse(self.window.write('antiwind', 2))
        self.assertFalse(any(call[0] == 'write' for call in self.calls))
        self.window.selection_changed()
        value.choices = {}
        self.window.apply_snapshot(value)
        self.window.update_enabled()
        self.assertTrue(control.model().item(2).isEnabled())
        self.assertFalse(self.window.controls['sound_mode'].model().item(3).isEnabled())
        self.assertTrue(self.window.write('antiwind', 2))
        self.wait(lambda: not self.window.busy)
        self.assertIn(('write', 'antiwind', 2, None), [call[:-1] for call in self.calls])

    def test_eq_gains_remain_visible_when_frequencies_are_missing(self):
        value = snapshot()
        value.eq_frequencies = (100,)
        self.window.apply_snapshot(value)
        self.window.update_enabled()
        self.assertEqual(len(self.window.eq_controls), 2)
        self.assertEqual([control.value() for control in self.window.eq_controls], [1.0, -2.0])
        self.assertEqual(self.window.eq_labels[1].text(), 'Band 2')
        self.assertTrue(self.window.eq_controls[1].isEnabled())

    def test_discovery_connection_refresh_and_close_use_one_worker_thread(self):
        self.connect()
        self.window.refresh_button.click()
        self.wait(lambda: not self.window.busy)
        self.window.close()
        self.wait(lambda: not self.window.thread.isRunning())
        identifiers = {call[-1] for call in self.calls if call[0] != 'cancel'}
        self.assertEqual(len(identifiers), 1)
        self.assertNotIn(threading.get_ident(), identifiers)
        self.assertTrue(any(call[0] == 'close' for call in self.calls))

    def test_busy_disables_controls_and_rejects_additional_work(self):
        self.mode = 'block'
        self.window.connect_button.click()
        self.wait(self.started.is_set)
        self.assertFalse(self.window.device_picker.isEnabled())
        self.assertFalse(self.window.refresh_button.isEnabled())
        self.assertFalse(self.window.submit('refresh'))
        self.block.set()
        self.wait(lambda: not self.window.busy)
        self.assertTrue(self.window.refresh_button.isEnabled())

    def test_readonly_and_missing_settings_cannot_emit_writes(self):
        self.mode = 'readonly'
        self.connect()
        self.assertFalse(self.window.controls['anc_enabled'].isEnabled())
        self.assertFalse(self.window.controls['smart_pause'].isEnabled())
        self.assertFalse(self.window.write('anc_enabled', False))
        self.assertFalse(any(call[0] == 'write' for call in self.calls))

    def test_write_forwards_raw_anc_scale_and_eq_band(self):
        self.connect()
        self.assertTrue(self.window.write('anc_strength', 80))
        self.wait(lambda: not self.window.busy)
        self.assertTrue(self.window.write('eq', 2.5, band=1))
        self.wait(lambda: not self.window.busy)
        writes = [call[1:4] for call in self.calls if call[0] == 'write']
        self.assertEqual(writes, [('anc_strength', 80, None), ('eq', 2.5, 1)])

    def test_error_recovers_and_clears_controls(self):
        self.mode = 'error'
        self.connect()
        self.assertIn('Headset unavailable', self.window.message.text())
        self.assertTrue(self.window.connect_button.isEnabled())
        self.assertFalse(self.window.controls['anc_enabled'].isEnabled())
        self.mode = ''
        self.connect()
        self.assertTrue(self.window.controls['anc_enabled'].isEnabled())

    def test_stale_snapshot_is_not_applied_after_selection_changes(self):
        self.mode = 'block'
        self.window.connect_button.click()
        self.wait(self.started.is_set)
        self.window.selection_changed()
        self.block.set()
        self.wait(lambda: not self.window.busy)
        self.assertIsNone(self.window.snapshot)
        self.assertFalse(self.window.refresh_button.isEnabled())

    def test_headset_text_cannot_be_interpreted_as_rich_text(self):
        value = snapshot(model='<img src="file:///tmp/untrusted-image">')
        value.notes = ('<a href="file:///tmp/untrusted">Peer-controlled text</a>',)
        self.window.apply_snapshot(value)
        self.assertEqual(self.window.status.textFormat(), Qt.TextFormat.PlainText)
        self.assertEqual(self.window.capabilities.textFormat(), Qt.TextFormat.PlainText)
        self.assertEqual(self.window.message.textFormat(), Qt.TextFormat.PlainText)

    def test_sound_mode_reads_personalization_without_writing_it(self):
        self.mode = 'personalization'
        self.connect()
        self.assertIn('sound_mode', self.window.controls)
        control = self.window.controls['sound_mode']
        self.assertEqual(control.currentIndex(), 3)
        self.assertFalse(any(call[0] == 'write' for call in self.calls))
        self.assertFalse(self.window.write('sound_mode', 3))
        control.setCurrentIndex(1)
        self.wait(lambda: not self.window.busy)
        self.assertIn(('write', 'sound_mode', 1, None), [call[:-1] for call in self.calls])

    def test_close_during_connect_cancels_before_closing_owned_client(self):
        self.mode = 'connectblock'
        self.window.connect_button.click()
        self.wait(self.started.is_set)
        self.window.close()
        self.wait(lambda: not self.window.thread.isRunning())
        events = [call[0] for call in self.calls]
        self.assertLess(events.index('cancel'), events.index('close'))
        self.wait(lambda: not self.window.isVisible())

    def test_close_during_discovery_sets_cancellation_and_waits_for_worker(self):
        factory = self.window.worker.client_factory
        self.window.close()
        self.wait(lambda: not self.window.thread.isRunning())
        def discovery(cancelled=None):
            self.started.set()
            cancelled.wait(3)
            self.assertTrue(cancelled.is_set())
            return []
        self.window = MainWindow(discover=discovery, client_factory=factory)
        self.window.show()
        self.wait(self.started.is_set)
        self.window.close()
        self.wait(lambda: not self.window.thread.isRunning())
        self.wait(lambda: not self.window.isVisible())

    def test_cancel_error_still_closes_client_and_stops_worker(self):
        self.connect()
        self.mode = 'cancelerror'
        try:
            self.window.close()
        except RuntimeError:
            self.mode = ''
            self.window.closing = False
            self.window.close()
            self.wait(lambda: not self.window.thread.isRunning())
            self.fail('Cancellation error interrupted shutdown')
        self.wait(lambda: not self.window.thread.isRunning())
        self.assertTrue(any(call[0] == 'close' for call in self.calls))
        self.wait(lambda: not self.window.isVisible())

    def test_close_cancels_inflight_io_before_worker_close(self):
        self.mode = 'block'
        self.window.connect_button.click()
        self.wait(self.started.is_set)
        self.window.close()
        self.wait(lambda: not self.window.thread.isRunning())
        events = [call[0] for call in self.calls]
        self.assertLess(events.index('cancel'), events.index('close'))
        self.wait(lambda: not self.window.isVisible())

    def test_idle_connection_loss_revokes_snapshot_without_clearing_active_request(self):
        self.connect()
        request=SimpleNamespace(action='write')
        self.window.busy=True;self.window.active_request=request
        self.window.connection_lost(self.window.generation,'Headset connection ended')
        self.assertIsNone(self.window.snapshot)
        self.assertTrue(self.window.busy)
        self.assertIs(self.window.active_request,request)
        self.assertFalse(self.window.controls['anc_strength'].isEnabled())
        self.window.busy=False;self.window.active_request=None

    def test_idle_connection_loss_from_old_generation_preserves_current_snapshot(self):
        self.connect()
        current=self.window.snapshot
        self.window.connection_lost(self.window.generation-1,'Old connection ended')
        self.assertIs(self.window.snapshot,current)


if __name__ == '__main__':
    unittest.main()
