import os
os.environ.setdefault('QT_QPA_PLATFORM', 'offscreen')
import threading
import time
import unittest
from types import SimpleNamespace
from unittest.mock import patch
from PySide6.QtWidgets import QApplication, QMessageBox
from PySide6.QtCore import QCoreApplication, QEvent
from momentum_control.gui import MainWindow
from momentum_control.dongle import DongleDevice


class DongleGuiTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.app = QApplication.instance() or QApplication([])

    def setUp(self):
        self.device = SimpleNamespace(control_path='/fake', upgrade_path='/fake-upgrade', usb_path='/usb', identity='private')
        self.state = 3
        self.supported = 15
        self.audio_mode = 0
        self.codec = 4
        self.calls = []
        self.devices = [self.device]
        self.block = threading.Event()
        self.started = threading.Event()
        self.failure = ''
        self.available = True
        owner = self
        class Services:
            def discover(self):
                owner.calls.append('discover')
                return owner.devices
            def status(self, device, cancelled=None):
                owner.calls.append('status')
                if owner.failure == 'status':
                    raise RuntimeError('USB permission denied')
                return SimpleNamespace(device=device, firmware='3.11.0', state=owner.state,
                    audio_mode=owner.audio_mode, transport=1, codec_mask=owner.codec, supported_codec_mask=owner.supported, sample_bits=24, sample_rate=96000)
            def codec(self, device, codec, cancelled=None):
                owner.calls.append(('codec', device, codec))
                if not callable(cancelled):
                    raise RuntimeError('Cancellation must be callable')
                owner.started.set()
                owner.block.wait(3)
                if owner.failure == 'codec':
                    raise RuntimeError('Codec verification uncertain')
                owner.codec = int(codec)
                return self.status(device, cancelled=cancelled)
            def check(self, device, cancelled=None):
                owner.calls.append('check')
                if owner.failure == 'check':
                    raise RuntimeError('Official service unavailable')
                return SimpleNamespace(device=device, current=SimpleNamespace(firmware='3.11.0', config=1),
                    target='3.12.0' if owner.available else '3.11.0', image=object() if owner.available else None,
                    available=owner.available, reason='No newer firmware available' if not owner.available else 'New firmware available')
            def apply(self, plan, progress, cancelled=None):
                owner.calls.append('apply')
                progress('transfer', 25)
                owner.started.set()
                owner.block.wait(3)
                if owner.failure == 'apply':
                    raise RuntimeError('commit: uncertain response; recovery required')
                return SimpleNamespace(firmware=plan.target, config=2)
        self.services = Services()
        self.headset = SimpleNamespace(address='AA', name='Headset')
        self.window = MainWindow(discover=lambda cancelled=None: [self.headset],
            client_factory=lambda *args, **kwargs: None, dongle_services=self.services)
        self.window.show()
        self.wait(lambda: not self.window.busy)

    def wait(self, predicate):
        deadline = time.monotonic() + 4
        while not predicate() and time.monotonic() < deadline:
            self.app.processEvents()
            time.sleep(.005)
        self.assertTrue(predicate())

    def tearDown(self):
        self.block.set()
        self.wait(lambda: not self.window.busy)
        self.window.close()
        self.wait(lambda: not self.window.thread.isRunning())
        self.window.deleteLater()
        QCoreApplication.sendPostedEvents(None, QEvent.Type.DeferredDelete)

    def check(self):
        self.window.dongle_check_button.click()
        self.wait(lambda: not self.window.busy)

    def test_streaming_audio_does_not_claim_headset_controls_or_contact_service(self):
        self.assertIn('Streaming audio', self.window.dongle_status_label.text())
        self.assertIn('disconnected', self.window.status.text().lower())
        self.assertEqual(self.window.connect_button.text(), 'Connect controls')
        self.assertFalse(self.window.noise_buttons['anc'].isEnabled())
        self.assertIn('3.11.0', self.window.dongle_firmware_label.text())
        self.assertNotIn('check', self.calls)
        self.assertNotIn('apply', self.calls)
        self.assertTrue(self.window.dongle_check_button.isEnabled())

    def test_audio_without_paired_control_headset_explains_direct_bluetooth_requirement(self):
        self.window.worker.discover = lambda cancelled=None: []
        self.window.submit('discover')
        self.wait(lambda: not self.window.busy)
        self.assertIn('Bluetooth', self.window.detail.text())
        self.assertIn('Streaming audio', self.window.dongle_status_label.text())
        self.assertFalse(self.window.connect_button.isEnabled())

    def test_usb_status_failure_preserves_headset_discovery_and_bluetooth_failure_preserves_usb(self):
        self.failure = 'status'
        self.window.submit('discover')
        self.wait(lambda: not self.window.busy)
        self.assertEqual(self.window.device_picker.count(), 1)
        self.assertIn('permission denied', self.window.dongle_status_label.text())
        self.failure = ''
        def unavailable(cancelled=None):
            raise RuntimeError('BlueZ unavailable')
        self.window.worker.discover = unavailable
        self.window.submit('discover')
        self.wait(lambda: not self.window.busy)
        self.assertIn('Streaming audio', self.window.dongle_status_label.text())
        self.assertIn('BlueZ unavailable', self.window.message.text())

    def test_check_reports_latest_and_streaming_blocks_update(self):
        self.check()
        self.assertIn('3.12.0', self.window.dongle_update_label.text())
        self.assertFalse(self.window.dongle_update_button.isEnabled())
        self.assertFalse(self.window.start_dongle_update())
        self.assertNotIn('apply', self.calls)
        self.state = 2
        self.window.submit('dongle-status')
        self.wait(lambda: not self.window.busy)
        self.assertIsNone(self.window.update_plan)
        self.check()
        self.assertTrue(self.window.dongle_update_button.isEnabled())

    def test_narrow_window_keeps_connection_and_dongle_actions_visible(self):
        self.window.resize(360, 900)
        self.app.processEvents()
        self.assertLessEqual(self.window.scroll.widget().width(), self.window.scroll.viewport().width())

    def test_codec_and_quality_only_show_reported_values_and_unknown_state_denies_update(self):
        self.assertIn('aptX Adaptive', self.window.dongle_quality_label.text())
        self.assertIn('24 bit', self.window.dongle_quality_label.text())
        self.assertIn('96000 Hz', self.window.dongle_quality_label.text())
        self.state = 99
        self.window.submit('dongle-status')
        self.wait(lambda: not self.window.busy)
        self.check()
        self.assertFalse(self.window.dongle_update_button.isEnabled())
        self.assertFalse(self.window.start_dongle_update())

    def test_switching_dongles_invalidates_plan_and_reads_selected_device(self):
        self.state = 2
        self.window.submit('dongle-status')
        self.wait(lambda: not self.window.busy)
        self.check()
        other = SimpleNamespace(control_path='/other', upgrade_path='/other-upgrade', usb_path='/usb2', identity='other-private')
        self.window.dongle_picker.addItem('BTD 700 2', other)
        self.window.dongle_picker.setCurrentIndex(1)
        self.wait(lambda: not self.window.busy)
        self.assertIsNone(self.window.update_plan)
        self.assertEqual(self.window.dongle_status.device, other)
        self.assertFalse(self.window.dongle_update_button.isEnabled())

    def test_update_errors_preserve_preflight_vs_started_recovery_status(self):
        from momentum_control.firmware_update import UpdateError
        self.state = 2
        for stage, recovery_required in (('preflight', False), ('transfer', True)):
            with self.subTest(stage=stage):
                self.window.submit('dongle-status')
                self.wait(lambda: not self.window.busy)
                self.check()
                def reject(plan, progress, cancelled=None):
                    raise UpdateError(stage, 'Playback resumed' if not recovery_required else 'Reply lost', recovery_required)
                self.services.apply = reject
                with patch.object(QMessageBox, 'question', return_value=QMessageBox.StandardButton.Yes):
                    self.assertTrue(self.window.start_dongle_update())
                self.wait(lambda: not self.window.busy)
                text = self.window.dongle_update_label.text()
                self.assertIn(stage, text)
                self.assertIn('failed', text.lower())
                if recovery_required:
                    self.assertIn('recovery required', text)
                else:
                    self.assertNotIn('recovery required', text)
                self.assertIsNone(self.window.update_plan)
                self.assertIsNone(self.window.dongle_status)

    def test_update_success_requires_worker_result_then_allows_close(self):
        self.state = 2
        self.window.submit('dongle-status')
        self.wait(lambda: not self.window.busy)
        self.check()
        with patch.object(QMessageBox, 'question', return_value=QMessageBox.StandardButton.Yes):
            self.window.start_dongle_update()
        self.wait(self.started.is_set)
        self.assertNotIn('verified:', self.window.dongle_update_label.text())
        self.block.set()
        self.wait(lambda: not self.window.busy)
        self.assertIn('verified: 3.12.0', self.window.dongle_update_label.text())
        self.assertFalse(self.window.updating)
        self.assertIsNone(self.window.update_plan)
        self.window.close()
        self.wait(lambda: not self.window.thread.isRunning())

    def test_latest_or_check_error_never_enables_update(self):
        self.available = False
        self.check()
        self.assertIn('3.11.0', self.window.dongle_update_label.text())
        self.assertFalse(self.window.dongle_update_button.isEnabled())
        self.failure = 'check'
        self.check()
        self.assertIsNone(self.window.update_plan)
        self.assertIn('Official service unavailable', self.window.dongle_update_label.text())

    def test_confirmation_required_and_refresh_invalidates_plan(self):
        self.state = 2
        self.window.submit('dongle-status')
        self.wait(lambda: not self.window.busy)
        self.check()
        with patch.object(QMessageBox, 'question', return_value=QMessageBox.StandardButton.No) as dialog:
            self.assertFalse(self.window.start_dongle_update())
        text = dialog.call_args.args[2]
        for term in ('3.11.0', '3.12.0', 'rollback', 'recovery', 'audio'):
            self.assertIn(term, text.lower())
        self.assertNotIn('apply', self.calls)
        self.window.submit('discover')
        self.wait(lambda: not self.window.busy)
        self.assertIsNone(self.window.update_plan)

    def test_update_refuses_close_keeps_worker_until_verified_result_and_reports_failure(self):
        self.state = 2
        self.window.submit('dongle-status')
        self.wait(lambda: not self.window.busy)
        self.check()
        self.failure = 'apply'
        with patch.object(QMessageBox, 'question', return_value=QMessageBox.StandardButton.Yes):
            self.assertTrue(self.window.start_dongle_update())
        self.wait(self.started.is_set)
        self.window.close()
        self.assertTrue(self.window.thread.isRunning())
        self.assertFalse(self.window.closing)
        self.assertFalse(self.window.worker.shutdown.is_set())
        self.assertIn('firmware', self.window.message.text().lower())
        self.assertFalse(self.window.submit('discover'))
        self.block.set()
        self.wait(lambda: not self.window.busy)
        self.assertIn('commit', self.window.dongle_update_label.text())
        self.assertIn('recovery required', self.window.dongle_update_label.text())
        self.assertIsNone(self.window.update_plan)
        self.assertIsNone(self.window.dongle_status)

    def test_codec_render_and_selector_never_write_and_unknown_bits_are_not_offered(self):
        self.window.open_dongle(self.device)
        self.wait(lambda: not self.window.busy)
        status = self.window.dongle_status
        status.supported_codec_mask = 15 | 128
        self.window.apply_dongle_status(status)
        self.window.update_enabled()
        self.assertEqual([self.window.codec_picker.itemText(i) for i in range(self.window.codec_picker.count())],
            ['SBC', 'aptX Classic', 'aptX Adaptive', 'aptX Lossless'])
        self.window.codec_picker.setCurrentIndex(3)
        self.app.processEvents()
        self.assertFalse(any(isinstance(call, tuple) and call[0] == 'codec' for call in self.calls))
        self.assertIn('aptX Adaptive', self.window.dongle_quality_label.text())
        self.assertTrue(self.window.codec_apply_button.isEnabled())

    def test_codec_requested_and_verified_active_are_distinct_and_error_clears_claims(self):
        self.window.open_dongle(self.device)
        self.wait(lambda: not self.window.busy)
        self.window.codec_picker.setCurrentIndex(3)
        self.failure = 'codec'
        self.assertTrue(self.window.apply_codec())
        self.wait(self.started.is_set)
        self.assertIn('Requested: aptX Lossless', self.window.codec_notice.text())
        self.assertIn('aptX Adaptive', self.window.dongle_quality_label.text())
        self.assertFalse(self.window.codec_apply_button.isEnabled())
        self.assertFalse(self.window.open_headset(self.headset))
        self.window.back_button.click()
        self.block.set()
        self.wait(lambda: not self.window.busy)
        self.assertEqual(self.window.page_kind, 'home')
        self.assertIsNone(self.window.dongle_status)
        self.assertEqual(self.window.dongle_quality_label.text(), 'Active codec unavailable')
        self.assertFalse(self.window.codec_apply_button.isEnabled())

    def test_codec_caps_and_state_loss_disable_apply(self):
        for state, mode, mask in ((0, 0, 15), (4, 0, 15), (99, 0, 15), (3, 1, 15), (3, 0, 0), (3, 0, 128)):
            with self.subTest(state=state, mode=mode, mask=mask):
                self.state, self.audio_mode, self.supported = state, mode, mask
                self.window.submit('dongle-status')
                self.wait(lambda: not self.window.busy)
                self.assertFalse(self.window.codec_apply_button.isEnabled())
                self.assertFalse(self.window.apply_codec())
        self.state, self.audio_mode, self.supported = 2, 0, 8
        self.window.submit('dongle-status')
        self.wait(lambda: not self.window.busy)
        self.assertTrue(self.window.codec_apply_button.isEnabled())
        self.assertEqual(self.window.codec_picker.count(), 1)
        self.assertEqual(self.window.codec_picker.currentText(), 'aptX Lossless')

    def test_missing_dongle_on_refresh_does_not_retarget_and_stable_identity_is_preserved(self):
        other = SimpleNamespace(control_path='/other', upgrade_path='/other-upgrade', usb_path='/usb2', identity='other')
        self.devices.append(other)
        self.window.submit('discover')
        self.wait(lambda: not self.window.busy)
        self.window.open_dongle(other)
        self.wait(lambda: not self.window.busy)
        changed = SimpleNamespace(control_path='/new-node', upgrade_path='/new-upgrade', usb_path='/usb2', identity='other')
        self.devices = [changed, self.device]
        self.window.submit('discover')
        self.wait(lambda: not self.window.busy)
        self.assertIs(self.window.dongle_picker.currentData(), changed)
        self.devices = [self.device]
        self.window.submit('discover')
        self.wait(lambda: not self.window.busy)
        self.assertIsNone(self.window.dongle_picker.currentData())
        self.assertIsNone(self.window.dongle_status)
        self.assertFalse(self.window.codec_apply_button.isEnabled())

    def test_firmware_operation_blocks_back_and_section_switch_until_completion(self):
        self.state = 2
        self.window.open_dongle(self.device)
        self.wait(lambda: not self.window.busy)
        self.check()
        with patch.object(QMessageBox, 'question', return_value=QMessageBox.StandardButton.Yes):
            self.assertTrue(self.window.start_dongle_update())
        self.wait(self.started.is_set)
        self.assertEqual(self.window.section, 'firmware')
        self.assertFalse(self.window.show_home())
        self.assertFalse(self.window.show_section('audio'))
        self.assertTrue(self.window.dongle_firmware_card.isVisible())
        self.block.set()
        self.wait(lambda: not self.window.busy)
        self.assertTrue(self.window.show_home())

    def test_status_for_wrong_physical_target_clears_active_audio_claims(self):
        other = SimpleNamespace(control_path='/other', upgrade_path='/other-upgrade', usb_path='/usb2', identity='other')
        wrong = self.services.status(other)
        self.services.status = lambda device, cancelled=None: wrong
        self.window.submit('dongle-status')
        self.wait(lambda: not self.window.busy)
        self.assertIsNone(self.window.dongle_status)
        self.assertEqual(self.window.dongle_quality_label.text(), 'Active codec unavailable')
        self.assertIn('unavailable', self.window.dongle_status_label.text())
        self.assertFalse(self.window.codec_apply_button.isEnabled())
        self.assertFalse(self.window.dongle_update_button.isEnabled())

    def test_codec_reenumeration_refreshes_followup_request_metadata(self):
        self.state = 2
        self.window.open_dongle(self.device)
        self.wait(lambda: not self.window.busy)
        replacement = DongleDevice('/dev/hidraw8', '/dev/hidraw9', self.device.usb_path, self.device.identity)
        original_status = self.services.status
        seen = []
        def codec(device, codec, cancelled=None):
            seen.append(('codec', device))
            self.codec = int(codec)
            self.devices = [replacement]
            return original_status(replacement, cancelled=cancelled)
        def status(device, cancelled=None):
            seen.append(('status', device))
            if device != replacement:
                raise RuntimeError('BTD 700 identity or USB path changed; refresh devices')
            return original_status(device, cancelled=cancelled)
        original_check = self.services.check
        def check(device, cancelled=None):
            seen.append(('check', device))
            if device != replacement:
                raise RuntimeError('BTD 700 identity or USB path changed; refresh devices')
            return original_check(device, cancelled=cancelled)
        self.services.codec = codec
        self.services.status = status
        self.services.check = check
        generation = self.window.generation
        self.window.codec_picker.setCurrentIndex(3)
        self.assertTrue(self.window.apply_codec())
        self.wait(lambda: not self.window.busy)
        self.assertIs(self.window.dongle_status.device, replacement)
        self.assertIn('aptX Lossless', self.window.dongle_quality_label.text())
        self.window.dongle_refresh_button.click()
        self.wait(lambda: not self.window.busy)
        self.assertIs(seen[-1][1], replacement, 'Refresh audio used obsolete hidraw paths')
        self.assertIs(self.window.dongle_picker.currentData(), replacement)
        self.assertIsNotNone(self.window.dongle_status)
        self.window.codec_picker.setCurrentIndex(2)
        self.assertTrue(self.window.apply_codec())
        self.wait(lambda: not self.window.busy)
        self.assertIs(seen[-1][1], replacement)
        self.check()
        self.assertEqual(seen[-1][0], 'check')
        self.assertIs(seen[-1][1], replacement)
        self.assertTrue(self.window.prepared_update_ready())
        self.assertEqual(self.window.generation, generation)
        self.assertEqual(self.window.page_kind, 'dongle')

    def test_status_render_adopts_same_target_metadata_without_actions_and_invalidates_old_plan(self):
        self.state = 2
        self.window.open_dongle(self.device)
        self.wait(lambda: not self.window.busy)
        self.check()
        self.assertTrue(self.window.prepared_update_ready())
        replacement = DongleDevice('/dev/hidraw8', '/dev/hidraw9', self.device.usb_path, self.device.identity)
        status = self.services.status(replacement)
        calls = list(self.calls)
        navigation = (self.window.page_kind, self.window.section, self.window.generation)
        selection_events = []
        self.window.dongle_picker.currentIndexChanged.connect(selection_events.append)
        self.window.dongle_picker.currentTextChanged.connect(selection_events.append)
        self.window.apply_dongle_status(status)
        self.assertIs(self.window.dongle_picker.currentData(), replacement)
        self.assertIs(self.window.dongle_status, status)
        self.assertIsNone(self.window.update_plan)
        self.assertFalse(self.window.prepared_update_ready())
        self.assertEqual(self.calls, calls)
        self.assertEqual(selection_events, [])
        self.assertEqual((self.window.page_kind, self.window.section, self.window.generation), navigation)
        self.assertFalse(self.window.busy)
        self.assertFalse(self.window.worker.shutdown.is_set())

    def test_status_render_does_not_adopt_another_physical_target(self):
        original = self.window.dongle_status
        for usb_path, identity in (('/other-usb', self.device.identity), (self.device.usb_path, 'other')):
            with self.subTest(usb_path=usb_path, identity=identity):
                other = DongleDevice('/dev/hidraw8', '/dev/hidraw9', usb_path, identity)
                self.window.apply_dongle_status(self.services.status(other))
                self.assertIs(self.window.dongle_picker.currentData(), self.device)
                self.assertIs(self.window.dongle_status, original)

    def test_audio_refresh_failure_shows_diagnostic_on_audio_page(self):
        self.window.open_dongle(self.device)
        self.wait(lambda: not self.window.busy)
        self.assertIsNotNone(self.window.dongle_status)
        self.failure = 'status'
        self.window.dongle_refresh_button.click()
        self.wait(lambda: not self.window.busy)
        self.app.processEvents()
        self.assertTrue(self.window.codec_notice.isVisible())
        self.assertIn('USB permission denied', self.window.codec_notice.text())
        self.assertIsNone(self.window.dongle_status)
        self.assertEqual(self.window.dongle_quality_label.text(), 'Active codec unavailable')
        self.assertFalse(self.window.codec_apply_button.isEnabled())
