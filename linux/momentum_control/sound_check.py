from collections.abc import Callable

from PySide6.QtCore import Qt
from PySide6.QtWidgets import QDialog, QHBoxLayout, QLabel, QLineEdit, QPushButton, QVBoxLayout

from .preferences import EqPreset, PreferenceError, _printable, _require, _validate_bands


def comparison_curves(gains: tuple[float, ...], frequencies: tuple[int, ...], stage: int) -> tuple[tuple[float, ...], tuple[float, ...]]:
    _validate_bands(frequencies, gains)
    _require(type(stage) is int and 0 <= stage <= 2, 'Invalid comparison stage')
    selected = tuple(frequency <= 500 if stage == 0 else 500 < frequency < 3000 if stage == 1 else frequency >= 3000 for frequency in frequencies)
    return tuple(tuple(round(max(-6.0, min(6.0, gain + change)), 1) if chosen else gain for gain, chosen in zip(gains, selected)) for change in (2.0, -2.0))


def _snapshot_curve(snapshot) -> EqPreset:
    _require(snapshot is not None, 'Sound check requires a connected headset')
    try:
        _require(_printable(snapshot.model, 128), 'Invalid sound check model')
        _require(type(snapshot.writable) is frozenset and 'eq' in snapshot.writable, 'Sound check requires verified EQ capability')
        _require(type(snapshot.settings) is dict and type(snapshot.settings.get('sound_mode')) is int and snapshot.settings['sound_mode'] == 1, 'Select Equalizer mode before sound check')
        _validate_bands(snapshot.eq_frequencies, snapshot.eq_bands)
        return EqPreset('Sound check', snapshot.model, snapshot.eq_frequencies, snapshot.eq_bands)
    except AttributeError as error:
        raise PreferenceError('Invalid sound check snapshot') from error


class SoundCheckDialog(QDialog):
    def __init__(self, snapshot, apply: Callable[[EqPreset], bool], save: Callable[[str, EqPreset], bool], ready: Callable[[], bool], translate: Callable[[str], str], parent=None):
        initial = _snapshot_curve(snapshot)
        super().__init__(parent)
        self._initial = initial
        self._confirmed = initial.gains
        self._apply = apply
        self._save = save
        self._ready = ready
        self._translate = translate
        self._snapshot = snapshot
        self._busy = False
        self._pending = None
        self._retired = False
        self._stage = 0
        self._message = ''
        self._candidates = (initial.gains, initial.gains)
        self.setWindowTitle(translate('Local sound check'))
        self.setMinimumWidth(360)
        layout = QVBoxLayout(self)
        self.notice_label = QLabel(translate('Play your own music. This is a local EQ comparison, not the manufacturer’s fitting engine. Closing keeps the last applied EQ.'))
        self.notice_label.setWordWrap(True)
        self.notice_label.setTextFormat(Qt.TextFormat.PlainText)
        layout.addWidget(self.notice_label)
        self.stage_label = QLabel()
        self.stage_label.setTextFormat(Qt.TextFormat.PlainText)
        layout.addWidget(self.stage_label)
        preview_row = QHBoxLayout()
        self.listen_a_button = QPushButton(translate('Listen A'))
        self.listen_b_button = QPushButton(translate('Listen B'))
        preview_row.addWidget(self.listen_a_button)
        preview_row.addWidget(self.listen_b_button)
        layout.addLayout(preview_row)
        self.next_button = QPushButton(translate('Keep current and continue'))
        layout.addWidget(self.next_button)
        self.name_edit = QLineEdit()
        self.name_edit.setMaxLength(40)
        self.name_edit.setPlaceholderText(translate('Preset name'))
        self.name_edit.setAccessibleName(translate('Preset name'))
        layout.addWidget(self.name_edit)
        self.save_button = QPushButton(translate('Save preset'))
        layout.addWidget(self.save_button)
        self.status_label = QLabel()
        self.status_label.setWordWrap(True)
        self.status_label.setTextFormat(Qt.TextFormat.PlainText)
        layout.addWidget(self.status_label)
        bottom_row = QHBoxLayout()
        self.restore_button = QPushButton(translate('Restore starting curve'))
        self.close_button = QPushButton(translate('Close'))
        bottom_row.addWidget(self.restore_button)
        bottom_row.addWidget(self.close_button)
        layout.addLayout(bottom_row)
        for button in (self.listen_a_button, self.listen_b_button, self.next_button, self.restore_button, self.save_button, self.close_button):
            button.setAutoDefault(False)
        self.listen_a_button.clicked.connect(lambda: self._preview(0))
        self.listen_b_button.clicked.connect(lambda: self._preview(1))
        self.next_button.clicked.connect(self._next)
        self.restore_button.clicked.connect(self._restore)
        self.save_button.clicked.connect(self._persist)
        self.close_button.clicked.connect(self.reject)
        self.name_edit.textChanged.connect(self._render)
        self._prepare_stage()
        self._render()

    def _compatible(self, snapshot) -> bool:
        try:
            curve = _snapshot_curve(snapshot)
        except PreferenceError:
            return False
        return self._initial.matches(curve.model, curve.frequencies)

    def _can_act(self) -> bool:
        if self._retired or self._busy or self._pending is not None or not self._compatible(self._snapshot):
            return False
        return self._ready() is True

    def _prepare_stage(self) -> None:
        while self._stage < 3:
            self._candidates = comparison_curves(self._confirmed, self._initial.frequencies, self._stage)
            if self._candidates[0] != self._candidates[1]:
                return
            self._stage += 1

    def _render(self) -> None:
        enabled = self._can_act()
        finished = self._stage == 3
        if finished:
            self.stage_label.setText(self._translate('Save your selected curve'))
        else:
            group = self._translate(('Bass', 'Midrange', 'Treble')[self._stage])
            self.stage_label.setText(self._translate('Step {step} of 3: {group}').format(step=self._stage + 1, group=group))
        for button in (self.listen_a_button, self.listen_b_button, self.next_button):
            button.setVisible(not finished)
            button.setEnabled(enabled and not finished)
        self.restore_button.setEnabled(enabled)
        self.name_edit.setVisible(finished)
        self.name_edit.setEnabled(enabled and finished)
        self.save_button.setVisible(finished)
        self.save_button.setEnabled(enabled and finished and _printable(self.name_edit.text().strip(), 40))
        message = 'Applying EQ…' if self._pending is not None else self._message
        self.status_label.setText(self._translate(message) if message else '')

    def _submit(self, gains: tuple[float, ...]) -> None:
        if not self._can_act():
            self._render()
            return
        preset = EqPreset('Sound check', self._initial.model, self._initial.frequencies, gains)
        self._pending = preset
        self._render()
        try:
            accepted = self._apply(preset) is True
        except Exception:
            accepted = False
        if not accepted and not self._retired:
            self._pending = None
            self._message = 'Could not start EQ preview. The last confirmed curve is unchanged.'
        self._render()

    def _preview(self, candidate: int) -> None:
        if self._stage < 3:
            self._submit(self._candidates[candidate])

    def _restore(self) -> None:
        self._submit(self._initial.gains)

    def _next(self) -> None:
        if not self._can_act() or self._stage >= 3:
            self._render()
            return
        self._stage += 1
        self._prepare_stage()
        self._message = ''
        self._render()

    def _persist(self) -> None:
        name = self.name_edit.text().strip()
        if not self._can_act() or self._stage != 3 or not _printable(name, 40):
            self._render()
            return
        preset = EqPreset(name, self._initial.model, self._initial.frequencies, self._confirmed)
        try:
            saved = self._save(name, preset) is True
        except Exception:
            saved = False
        if saved:
            self.done(QDialog.DialogCode.Accepted)
        elif not self._retired:
            self._message = 'Could not save preset. Your selected curve is still available.'
            self._render()

    def update_snapshot(self, snapshot, busy: bool) -> None:
        if self._retired:
            return
        self._snapshot = snapshot
        self._busy = busy is not False
        self._render()

    def operation_finished(self, snapshot, error: str) -> None:
        if self._retired or self._pending is None:
            return
        pending = self._pending
        self._pending = None
        self._snapshot = snapshot
        if not error and self._compatible(snapshot) and snapshot.eq_bands == pending.gains:
            self._confirmed = snapshot.eq_bands
            self._message = 'EQ preview confirmed.'
        else:
            self._message = 'EQ preview failed. The last confirmed curve is unchanged.'
        self._render()

    def done(self, result: int) -> None:
        self._retired = True
        self._pending = None
        self._render()
        super().done(result)

    def closeEvent(self, event) -> None:
        self._retired = True
        self._pending = None
        self._render()
        super().closeEvent(event)
