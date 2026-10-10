from dataclasses import replace
import os
import unittest

os.environ.setdefault('QT_QPA_PLATFORM', 'offscreen')
from PySide6.QtWidgets import QApplication, QDialog

from momentum_control.backend import Snapshot
from momentum_control.preferences import EqPreset, PreferenceError
from momentum_control.sound_check import SoundCheckDialog, comparison_curves


def snapshot(gains=(0.0, 1.0, -1.0, 2.0), frequencies=(90, 90, 1500, 4500)):
    return Snapshot('M4AEBT Black', '3.38.3', 80, {'sound_mode': 1}, gains, frequencies, frozenset({'eq'}), ())


class ComparisonCurveTests(unittest.TestCase):
    def test_frequency_groups_preserve_order_and_duplicate_frequencies(self):
        gains = (0.0,) * 6
        frequencies = (3000, 500, 501, 90, 2999, 500)
        self.assertEqual(comparison_curves(gains, frequencies, 0), ((0.0, 2.0, 0.0, 2.0, 0.0, 2.0), (0.0, -2.0, 0.0, -2.0, 0.0, -2.0)))
        self.assertEqual(comparison_curves(gains, frequencies, 1), ((0.0, 0.0, 2.0, 0.0, 2.0, 0.0), (0.0, 0.0, -2.0, 0.0, -2.0, 0.0)))
        self.assertEqual(comparison_curves(gains, frequencies, 2), ((2.0, 0.0, 0.0, 0.0, 0.0, 0.0), (-2.0, 0.0, 0.0, 0.0, 0.0, 0.0)))

    def test_clamping_tenths_and_empty_group(self):
        self.assertEqual(comparison_curves((5.9, -5.9, 0.1), (90, 90, 500), 0), ((6.0, -3.9, 2.1), (3.9, -6.0, -1.9)))
        self.assertEqual(comparison_curves((5.9, -5.9), (90, 90), 1), ((5.9, -5.9), (5.9, -5.9)))
        self.assertEqual(len(comparison_curves((0.0,) * 16, (48000,) * 16, 2)[0]), 16)

    def test_invalid_bounds_types_and_topology_rejected(self):
        cases = [((float('nan'),), (90,), 0), ((float('inf'),), (90,), 0), ((True,), (90,), 0), ((6.1,), (90,), 0), ((-6.1,), (90,), 0), ((0.11,), (90,), 0), ((), (), 0), ((0.0,), (True,), 0), ((0.0,), (0,), 0), ((0.0,), (48001,), 0), ((0.0,) * 17, (90,) * 17, 0), ((0.0,), (90, 90), 0), ([0.0], (90,), 0), ((0.0,), [90], 0), ((0.0,), (90,), True), ((0.0,), (90,), -1), ((0.0,), (90,), 3)]
        for gains, frequencies, stage in cases:
            with self.subTest(gains=gains, frequencies=frequencies, stage=stage):
                with self.assertRaises(PreferenceError):
                    comparison_curves(gains, frequencies, stage)


class SoundCheckDialogTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.app = QApplication.instance() or QApplication([])

    def setUp(self):
        self.applied = []
        self.saved = []
        self.accept_apply = True
        self.accept_save = True
        self.ready = True
        self.translated = []
        self.initial = snapshot()
        self.dialogs = []
        self.dialog = self.make_dialog()

    def tearDown(self):
        for dialog in self.dialogs:
            dialog.close()
        self.app.processEvents()

    def apply(self, preset):
        self.applied.append(preset)
        return self.accept_apply

    def save(self, name, preset):
        self.saved.append((name, preset))
        return self.accept_save

    def translate(self, text):
        self.translated.append(text)
        return text

    def make_dialog(self, state=None, **overrides):
        callbacks = {'apply': self.apply, 'save': self.save, 'ready': lambda: self.ready, 'translate': self.translate}
        callbacks.update(overrides)
        dialog = SoundCheckDialog(self.initial if state is None else state, **callbacks)
        self.dialogs.append(dialog)
        return dialog

    def finish(self, dialog=None, state=None, error=''):
        dialog = dialog or self.dialog
        if state is None and not error:
            state = replace(self.initial, eq_bands=self.applied[-1].gains)
        dialog.update_snapshot(state, False)
        dialog.operation_finished(state, error)

    def go_to_result(self, dialog=None):
        dialog = dialog or self.dialog
        for _ in range(3):
            dialog.next_button.click()
        dialog.name_edit.setText('My music')

    def test_constructor_and_updates_never_apply_or_save(self):
        self.dialog.show()
        self.app.processEvents()
        self.dialog.update_snapshot(self.initial, False)
        self.dialog.update_snapshot(self.initial, True)
        self.dialog.update_snapshot(self.initial, False)
        self.assertEqual(self.applied, [])
        self.assertEqual(self.saved, [])
        self.assertIn('Closing keeps the last applied EQ.', self.dialog.notice_label.text())
        self.assertIn('Listen A', self.translated)

    def test_invalid_initial_snapshot_is_rejected_without_writes(self):
        states = [replace(self.initial, writable=frozenset()), replace(self.initial, settings={'sound_mode': 3}), replace(self.initial, settings={'sound_mode': True}), replace(self.initial, eq_bands=(True,) * 4), replace(self.initial, eq_frequencies=(90,)), replace(self.initial, model=''), replace(self.initial, model='x' * 129)]
        for state in states:
            with self.subTest(state=state):
                with self.assertRaises(PreferenceError):
                    self.make_dialog(state)
        self.assertEqual(self.applied, [])

    def test_preview_preserves_exact_preset_model_order_and_duplicate_count(self):
        self.dialog.listen_a_button.click()
        self.assertEqual(len(self.applied), 1)
        self.assertIsInstance(self.applied[0], EqPreset)
        self.assertEqual((self.applied[0].model, self.applied[0].frequencies, self.applied[0].gains), ('M4AEBT Black', (90, 90, 1500, 4500), (2.0, 3.0, -1.0, 2.0)))
        for button in (self.dialog.listen_a_button, self.dialog.listen_b_button, self.dialog.next_button, self.dialog.restore_button, self.dialog.save_button):
            self.assertFalse(button.isEnabled())
        self.assertEqual(self.saved, [])

    def test_a_b_a_candidates_never_drift(self):
        for button in (self.dialog.listen_a_button, self.dialog.listen_b_button, self.dialog.listen_a_button):
            button.click()
            self.finish()
        self.assertEqual([preset.gains for preset in self.applied], [(2.0, 3.0, -1.0, 2.0), (-2.0, -1.0, -1.0, 2.0), (2.0, 3.0, -1.0, 2.0)])

    def test_ordinary_snapshot_does_not_acknowledge_preview_or_allow_save(self):
        self.dialog.listen_a_button.click()
        wanted = replace(self.initial, eq_bands=self.applied[-1].gains)
        self.dialog.update_snapshot(wanted, False)
        self.go_to_result()
        self.dialog.save_button.click()
        self.assertFalse(self.dialog.next_button.isEnabled())
        self.assertFalse(self.dialog.save_button.isEnabled())
        self.assertEqual(self.saved, [])
        self.dialog.operation_finished(wanted, '')
        self.go_to_result()
        self.dialog.save_button.click()
        self.assertEqual(self.saved[0][1].gains, (2.0, 3.0, -1.0, 2.0))

    def test_next_uses_last_confirmed_curve_for_new_candidates(self):
        self.dialog.listen_b_button.click()
        self.finish()
        self.dialog.next_button.click()
        self.dialog.listen_a_button.click()
        self.assertEqual(self.applied[-1].gains, (-2.0, -1.0, 1.0, 2.0))
        self.finish()
        self.go_to_result()
        self.dialog.save_button.click()
        self.assertEqual(self.saved[-1][1].gains, (-2.0, -1.0, 1.0, 2.0))

    def test_failed_or_mismatched_readback_never_becomes_saved_result(self):
        cases = [(self.initial, 'Failed'), (replace(self.initial, eq_bands=(2.0, 3.0, -1.0, 1.0)), ''), (replace(self.initial, model='M5AEBT Black', eq_bands=(2.0, 3.0, -1.0, 2.0)), ''), (replace(self.initial, eq_frequencies=(90, 1500, 90, 4500), eq_bands=(2.0, 3.0, -1.0, 2.0)), ''), (replace(self.initial, settings={'sound_mode': 3}, eq_bands=(2.0, 3.0, -1.0, 2.0)), '')]
        for state, error in cases:
            with self.subTest(state=state, error=error):
                dialog = self.make_dialog()
                dialog.listen_a_button.click()
                dialog.operation_finished(state, error)
                self.assertTrue(dialog.status_label.text())
                dialog.update_snapshot(self.initial, False)
                self.go_to_result(dialog)
                dialog.save_button.click()
                self.assertEqual(self.saved[-1][1].gains, self.initial.eq_bands)

    def test_submission_rejection_preserves_confirmed_curve(self):
        self.accept_apply = False
        self.dialog.listen_a_button.click()
        self.assertTrue(self.dialog.next_button.isEnabled())
        self.assertTrue(self.dialog.status_label.text())
        self.go_to_result()
        self.dialog.save_button.click()
        self.assertEqual(self.saved[-1][1].gains, self.initial.eq_bands)

    def test_busy_disconnected_stale_model_topology_mode_and_capability_gate_actions(self):
        states = [(self.initial, True), (None, False), (replace(self.initial, model='M5AEBT Black'), False), (replace(self.initial, eq_frequencies=(90, 1500, 90, 4500)), False), (replace(self.initial, eq_frequencies=(90, 1500, 1500, 4500)), False), (replace(self.initial, eq_frequencies=(90, 1500, 4500), eq_bands=(0.0, -1.0, 2.0)), False), (replace(self.initial, settings={'sound_mode': 3}), False), (replace(self.initial, writable=frozenset()), False)]
        for state, busy in states:
            with self.subTest(state=state, busy=busy):
                self.dialog.update_snapshot(state, busy)
                for button in (self.dialog.listen_a_button, self.dialog.listen_b_button, self.dialog.next_button, self.dialog.restore_button, self.dialog.save_button):
                    self.assertFalse(button.isEnabled())
                    button.click()
                self.assertEqual(self.applied, [])
                self.assertEqual(self.saved, [])
        self.dialog.update_snapshot(self.initial, False)
        self.assertTrue(self.dialog.listen_a_button.isEnabled())
        self.ready = False
        self.dialog.listen_a_button.click()
        self.dialog.next_button.click()
        self.dialog.restore_button.click()
        self.assertEqual(self.applied, [])
        self.ready = True
        self.dialog.update_snapshot(self.initial, False)
        self.assertIn('Bass', self.dialog.stage_label.text())

    def test_final_save_rechecks_readiness_and_snapshot(self):
        self.go_to_result()
        self.ready = False
        self.dialog.save_button.click()
        self.assertEqual(self.saved, [])
        self.ready = True
        self.dialog.update_snapshot(None, False)
        self.assertFalse(self.dialog.save_button.isEnabled())
        self.dialog.save_button.click()
        self.assertEqual(self.saved, [])

    def test_restore_is_one_explicit_operation_and_close_never_restores(self):
        self.dialog.listen_a_button.click()
        self.finish()
        self.dialog.restore_button.click()
        self.assertEqual(len(self.applied), 2)
        self.assertEqual(self.applied[-1].gains, self.initial.eq_bands)
        self.assertFalse(self.dialog.next_button.isEnabled())
        self.finish()
        self.dialog.close()
        self.assertEqual(len(self.applied), 2)
        self.assertEqual(self.saved, [])

    def test_close_during_pending_ignores_late_completion_and_direct_actions(self):
        self.dialog.listen_a_button.click()
        self.dialog.close()
        self.dialog.operation_finished(replace(self.initial, eq_bands=self.applied[-1].gains), '')
        self.dialog.update_snapshot(self.initial, False)
        self.go_to_result()
        self.dialog.listen_a_button.click()
        self.dialog.restore_button.click()
        self.dialog.save_button.click()
        self.assertFalse(self.dialog.isVisible())
        self.assertEqual(len(self.applied), 1)
        self.assertEqual(self.saved, [])

    def test_empty_stages_skip_without_writing_or_claiming_difference(self):
        dialog = self.make_dialog(snapshot((0.0, 1.0), (4500, 8000)))
        self.assertIn('Treble', dialog.stage_label.text())
        self.assertEqual(self.applied, [])
        dialog.next_button.click()
        dialog.name_edit.setText('Treble only')
        dialog.save_button.click()
        self.assertEqual(self.saved[-1][1].gains, (0.0, 1.0))
        self.assertEqual(self.applied, [])

    def test_persistence_failure_keeps_result_and_allows_retry(self):
        self.dialog.show()
        self.go_to_result()
        self.accept_save = False
        self.dialog.save_button.click()
        self.assertTrue(self.dialog.isVisible())
        self.assertTrue(self.dialog.save_button.isEnabled())
        self.assertTrue(self.dialog.status_label.text())
        self.accept_save = True
        self.dialog.save_button.click()
        self.assertEqual(len(self.saved), 2)
        self.assertEqual(self.saved[0], self.saved[1])
        self.assertEqual(self.dialog.result(), QDialog.DialogCode.Accepted)
        self.assertFalse(self.dialog.isVisible())

    def test_pending_is_set_before_apply_callback_can_reenter(self):
        holder = []
        observed = []

        def apply(preset):
            dialog = holder[0]
            observed.append(dialog.next_button.isEnabled())
            dialog.update_snapshot(replace(self.initial, eq_bands=preset.gains), False)
            dialog.next_button.click()
            self.applied.append(preset)
            return True

        dialog = self.make_dialog(apply=apply)
        holder.append(dialog)
        dialog.listen_a_button.click()
        self.assertEqual(observed, [False])
        self.assertIn('Bass', dialog.stage_label.text())
        self.assertFalse(dialog.next_button.isEnabled())
        self.finish(dialog)
        self.assertTrue(dialog.next_button.isEnabled())

    def test_unsolicited_snapshot_and_late_duplicate_completion_do_not_replace_result(self):
        self.dialog.listen_a_button.click()
        self.finish()
        unrelated = replace(self.initial, eq_bands=(-6.0,) * 4)
        self.dialog.update_snapshot(unrelated, False)
        self.dialog.operation_finished(unrelated, '')
        self.go_to_result()
        self.dialog.save_button.click()
        self.assertEqual(self.saved[-1][1].gains, (2.0, 3.0, -1.0, 2.0))

    def test_rejected_dialog_ignores_pending_completion(self):
        self.dialog.listen_b_button.click()
        self.dialog.reject()
        self.finish()
        self.go_to_result()
        self.dialog.save_button.click()
        self.assertFalse(self.dialog.next_button.isEnabled())
        self.assertFalse(self.dialog.restore_button.isEnabled())
        self.assertEqual(len(self.applied), 1)
        self.assertEqual(self.saved, [])

    def test_callback_failure_keeps_confirmed_result_and_dialog_usable(self):
        def fail(*args):
            raise PreferenceError('Local failure')

        dialog = self.make_dialog(apply=fail, save=fail)
        dialog.show()
        dialog.listen_a_button.click()
        self.assertTrue(dialog.next_button.isEnabled())
        self.assertTrue(dialog.status_label.text())
        self.go_to_result(dialog)
        dialog.save_button.click()
        self.assertTrue(dialog.save_button.isEnabled())
        self.assertTrue(dialog.isVisible())
        self.assertEqual(self.applied, [])
        self.assertEqual(self.saved, [])

    def test_name_validation_and_unrequested_completion(self):
        self.dialog.operation_finished(replace(self.initial, eq_bands=(6.0,) * 4), '')
        self.go_to_result()
        for name in ('', '   ', 'bad\x01name'):
            self.dialog.name_edit.setText(name)
            self.dialog.save_button.click()
            self.assertEqual(self.saved, [])
        self.dialog.name_edit.setText('x' * 41)
        self.assertEqual(len(self.dialog.name_edit.text()), 40)
        self.dialog.save_button.click()
        self.assertEqual(self.saved[-1][0], 'x' * 40)
        self.assertEqual(self.saved[-1][1].gains, self.initial.eq_bands)


if __name__ == '__main__':
    unittest.main()
