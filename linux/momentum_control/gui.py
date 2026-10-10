from __future__ import annotations

import os
import threading
import time
import sys
from collections import deque
from pathlib import Path
from dataclasses import dataclass, replace, field
from typing import Callable

from PySide6.QtCore import QObject, QPointF, QRectF, QSignalBlocker, QThread, Qt, Signal, Slot, QTimer, QStandardPaths
from PySide6.QtGui import QCloseEvent, QColor, QIcon, QPainter, QPalette, QPen, QAction
from PySide6.QtWidgets import (
    QApplication, QButtonGroup, QCheckBox, QComboBox, QFrame, QGridLayout,
    QHBoxLayout, QLabel, QMainWindow, QPushButton, QRadioButton, QScrollArea,
    QSizePolicy, QSlider, QVBoxLayout, QWidget, QMessageBox, QLineEdit, QInputDialog,
    QFileDialog, QSystemTrayIcon, QMenu, QDoubleSpinBox, QSpinBox,
)


from .preferences import AppData, Preferences, EqPreset, PreferenceStore, PreferenceError, _validate_data
from .desktop import set_autostart, autostart_enabled, export_diagnostics, _OPERATION_NAMES
from .updates import check_project_release
from .i18n import translate
from .sound_zones import ZoneData, ZoneStore, SoundZone, ZoneTracker, ZoneError


class TranslatedLabel(QLabel):
    def __init__(self, text, language):
        self.language = language
        super().__init__(translate(language, text))

    def setText(self, text):
        super().setText(translate(self.language, text))


@dataclass(frozen=True)
class Request:
    action: str
    generation: int
    device: object = None
    name: str = ''
    value: bool | int | float = False
    band: int | None = None
    payload: object = None
    eq_topology: tuple | None = None


@dataclass(frozen=True)
class LocationIntent:
    generation: int
    location_generation: int
    edit_revision: int
    config_revision: int
    enable_generation: int
    editor_request: int
    address: str
    model: str
    frequencies: tuple[int, ...]
    started_at: float
    manual: bool = False
    cancelled: threading.Event = field(default_factory=threading.Event, compare=False, repr=False)


class DongleServices:
    def discover(self):
        from .dongle import discover_dongles
        return discover_dongles()

    def status(self, device, cancelled: Callable[[], bool] | None = None):
        from .dongle import read_status
        return read_status(device, cancelled=cancelled)

    def codec(self, device, codec, cancelled=None):
        from .dongle import set_codec
        return set_codec(device, codec, cancelled=cancelled)

    def check(self, device, cancelled: Callable[[], bool] | None = None):
        from .firmware import check_for_update
        return check_for_update(device, cancelled=cancelled)

    def apply(self, plan, progress, cancelled: Callable[[], bool] | None = None):
        from .firmware_update import apply_update
        return apply_update(plan, progress, cancelled=cancelled)

    def cancel(self):
        from .dongle import cancel
        cancel()

    def close(self):
        from .dongle import close
        close()


def dongle_audio_state(state):
    return {0: 'Disconnected', 1: 'Disconnected', 2: 'Connected',
            3: 'Streaming audio', 4: 'Voice streaming'}.get(state, 'Unknown audio state')


class Worker(QObject):
    completed = Signal(object, object, str)
    progress = Signal(str, int)
    connection_lost = Signal(int, str)

    def __init__(self, discover: Callable, client_factory: Callable, dongle_services=None, release_checker=None, location_provider_factory=None, clock=None):
        super().__init__()
        self.discover = discover
        self.client_factory = client_factory
        self.dongle_services = dongle_services
        self.release_checker = release_checker or check_project_release
        self.location_provider_factory = location_provider_factory
        self.location_provider = None
        self.location_provider_generation = 0
        self.location_closed_generation = 0
        self.clock = clock or time.monotonic
        self.shutdown = threading.Event()
        self.session_cancelled = None
        self.client = None
        self.client_lock = threading.Lock()
        self.idle_timer = None
        self.headset_generation = 0

    @Slot()
    def start_idle_pump(self):
        self.idle_timer = QTimer(self)
        self.idle_timer.setInterval(50)
        self.idle_timer.timeout.connect(self.pump_idle)
        self.idle_timer.start()

    @Slot()
    def pump_idle(self):
        client = self.client
        pump = getattr(client, 'pump_idle', None)
        if pump is not None and not self.shutdown.is_set():
            try:
                pump()
            except Exception as error:
                client.cancel()
                self.close_client()
                self.connection_lost.emit(self.headset_generation, str(error) or 'Headset connection ended')

    def cancel(self):
        self.shutdown.set()
        cancel_dongle = getattr(self.dongle_services, 'cancel', None)
        if cancel_dongle is not None:
            cancel_dongle()
        with self.client_lock:
            if self.session_cancelled is not None:
                self.session_cancelled.set()
            client = self.client
        if client is not None:
            client.cancel()

    def close_client(self):
        with self.client_lock:
            client, self.client = self.client, None
        if client is not None:
            client.close()

    def close_location_provider(self):
        provider, self.location_provider = self.location_provider, None
        self.location_provider_generation = 0
        if provider is not None:
            provider.close()

    @Slot(int)
    def close_location(self, generation):
        self.location_closed_generation = max(self.location_closed_generation, generation)
        error = ''
        try:
            if self.location_provider_generation <= generation:
                self.close_location_provider()
        except Exception:
            error = 'Location cleanup failed; location remains disabled.'
        self.completed.emit(Request('location-close', -1, payload=generation), None, error)

    @Slot(object)
    def execute(self, request: Request):
        result = None
        error = ''
        try:
            if request.action == 'close':
                try:
                    self.close_location_provider()
                finally:
                    self.close_client()
                    close_dongle = getattr(self.dongle_services, 'close', None)
                    if close_dongle is not None:
                        close_dongle()
                    if self.idle_timer is not None:
                        self.idle_timer.stop()
            elif self.shutdown.is_set():
                raise RuntimeError('Operation cancelled')
            elif request.action == 'location-sample':
                intent = request.payload
                if intent.cancelled.is_set() or intent.location_generation <= self.location_closed_generation:
                    raise RuntimeError('Location request cancelled')
                if self.location_provider is not None and self.location_provider_generation != intent.location_generation:
                    self.close_location_provider()
                if self.location_provider is None:
                    factory = self.location_provider_factory
                    if factory is None:
                        from .sound_zones import GeoClueProvider
                        factory = GeoClueProvider
                    self.location_provider = factory(cancelled=lambda: self.shutdown.is_set() or intent.cancelled.is_set(), now=self.clock)
                    self.location_provider_generation = intent.location_generation
                result = self.location_provider.sample()
            elif request.action == 'project-release':
                result = self.release_checker(cancelled=self.shutdown.is_set)
            elif request.action == 'reconnect':
                self.headset_generation = request.generation
                devices = self.discover(cancelled=self.shutdown)
                matches = [device for device in devices if device.address == request.name]
                if len(matches) != 1:
                    raise RuntimeError('Chosen headset not available')
                self.close_client()
                with self.client_lock:
                    cancelled = threading.Event()
                    self.session_cancelled = cancelled
                    if self.shutdown.is_set():
                        cancelled.set()
                client = self.client_factory(matches[0], cancelled=cancelled)
                with self.client_lock:
                    self.client = client
                if self.shutdown.is_set():
                    client.cancel()
                client.connect()
                result = {'device': matches[0], 'snapshot': client.snapshot()}
            elif request.action == 'discover':
                result = {'headsets': (), 'headset_error': '', 'dongles': (), 'dongle_status': None, 'dongle_error': ''}
                try:
                    result['headsets'] = self.discover(cancelled=self.shutdown)
                except Exception as exception:
                    result['headset_error'] = str(exception) or type(exception).__name__
                if self.dongle_services is not None and not self.shutdown.is_set():
                    try:
                        result['dongles'] = self.dongle_services.discover()
                        if result['dongles']:
                            result['dongle_status'] = self.dongle_services.status(result['dongles'][0], cancelled=self.shutdown.is_set)
                    except Exception as exception:
                        result['dongle_error'] = str(exception) or type(exception).__name__
            elif request.action == 'dongle-status':
                result = self.dongle_services.status(request.device, cancelled=self.shutdown.is_set)
            elif request.action == 'dongle-codec':
                result = self.dongle_services.codec(request.device, request.payload, cancelled=self.shutdown.is_set)
            elif request.action == 'dongle-check':
                result = self.dongle_services.check(request.device, cancelled=self.shutdown.is_set)
            elif request.action == 'dongle-update':
                result = self.dongle_services.apply(request.payload, self.progress.emit, cancelled=self.shutdown.is_set)
            elif request.action == 'connect':
                self.headset_generation = request.generation
                self.close_client()
                with self.client_lock:
                    cancelled = threading.Event()
                    self.session_cancelled = cancelled
                    if self.shutdown.is_set():
                        cancelled.set()
                client = self.client_factory(request.device, cancelled=cancelled)
                with self.client_lock:
                    self.client = client
                if self.shutdown.is_set():
                    client.cancel()
                client.connect()
                result = client.snapshot()
            elif request.action in ('refresh', 'poll'):
                if self.client is None:
                    raise RuntimeError('Connect to a headset first')
                result = self.client.snapshot()
            elif request.action == 'mode':
                if self.client is None:
                    raise RuntimeError('Connect to a headset first')
                result = self.client.set_noise_mode(request.name)
            elif request.action in ('preset', 'rename', 'connect-peer'):
                if self.client is None:
                    raise RuntimeError('Connect to a headset first')
                if request.action == 'preset':
                    preset = request.payload
                    result = self.client.set_equalizer(preset.gains, expected_model=preset.model,
                        expected_topology=(len(preset.frequencies), preset.frequencies))
                elif request.action == 'rename':
                    result = self.client.rename(request.payload)
                else:
                    result = self.client.connect_peer(request.payload)
            elif request.action == 'write':
                if self.client is None:
                    raise RuntimeError('Connect to a headset first')
                if request.eq_topology is not None:
                    result = self.client.set_value(request.name, request.value, band=request.band,
                        expected_topology=(len(request.eq_topology), request.eq_topology))
                else:
                    result = self.client.set_value(request.name, request.value, band=request.band)
            else:
                raise RuntimeError('Unknown operation')
        except Exception as exception:
            error = str(exception) or type(exception).__name__
            if request.action == 'location-sample':
                error = 'Location unavailable. Check GeoClue permission and service.'
                try:
                    self.close_location_provider()
                except Exception:
                    error = 'Location unavailable; cleanup failed.'
            if request.action == 'mode':
                error = f'Noise mode incomplete: {error}'
            try:
                if not request.action.startswith('dongle-') and request.action not in ('project-release', 'location-sample'):
                    self.close_client()
            except Exception as close_error:
                error += f'; closing connection: {close_error}'
        self.completed.emit(request, result, error)


class CommitSlider(QSlider):
    committed = Signal()
    editing_changed = Signal()
    edit_cancelled = Signal()
    ADJUSTMENT_KEYS = frozenset((
        Qt.Key.Key_Left, Qt.Key.Key_Right, Qt.Key.Key_Up, Qt.Key.Key_Down,
        Qt.Key.Key_Home, Qt.Key.Key_End, Qt.Key.Key_PageUp, Qt.Key.Key_PageDown,
    ))

    def __init__(self, orientation, parent=None):
        super().__init__(orientation, parent)
        self.editing = False
        self.held_keys = set()
        self.mouse_editing = False
        self.gesture_dirty = False
        self.gesture_committed = False
        self.sliderReleased.connect(self.commit_mouse)
        self.valueChanged.connect(self.mark_edit_changed)

    def begin_edit(self):
        if not self.editing:
            self.gesture_dirty = False
            self.gesture_committed = False
            self.editing = True
            self.editing_changed.emit()

    def mark_edit_changed(self):
        if self.editing:
            self.gesture_dirty = True

    def commit(self):
        if (self.mouse_editing or self.held_keys) and self.gesture_dirty and not self.gesture_committed:
            self.gesture_committed = True
            self.committed.emit()

    def commit_mouse(self):
        if self.mouse_editing and not self.held_keys:
            self.commit()

    def finish_edit(self):
        self.editing = bool(self.mouse_editing or self.held_keys)
        if not self.editing:
            committed = self.gesture_committed
            self.gesture_dirty = False
            self.gesture_committed = False
            if not committed:
                self.edit_cancelled.emit()
        self.editing_changed.emit()

    def cancel_edit(self):
        editing = self.editing
        blocker = QSignalBlocker(self)
        self.editing = False
        self.held_keys.clear()
        self.mouse_editing = False
        self.gesture_dirty = False
        self.gesture_committed = False
        self.setSliderDown(False)
        del blocker
        if editing:
            self.edit_cancelled.emit()
            self.editing_changed.emit()

    def focusOutEvent(self, event):
        blocker = QSignalBlocker(self)
        super().focusOutEvent(event)
        del blocker
        self.cancel_edit()

    def wheelEvent(self, event):
        event.ignore()

    def paintEvent(self, event):
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        painter.setOpacity(1 if self.isEnabled() else .35)
        span = self.maximum() - self.minimum()
        fraction = (QSlider.value(self) - self.minimum()) / span if span else 0
        if self.orientation() == Qt.Orientation.Vertical:
            center = self.width() / 2
            position = 9 + (self.height() - 18) * (1 - fraction)
            start, end = QPointF(center, 9), QPointF(center, self.height() - 9)
            knob = QPointF(center, position)
            accent_start, accent_end = knob, end
            painter.setPen(QPen(QColor('#555555'), 1))
            for tick in (0, .25, .5, .75, 1):
                height = 9 + (self.height() - 18) * tick
                painter.drawLine(QPointF(center - 7, height), QPointF(center - 4, height))
        else:
            center = self.height() / 2
            position = 9 + (self.width() - 18) * fraction
            start, end = QPointF(9, center), QPointF(self.width() - 9, center)
            knob = QPointF(position, center)
            accent_start, accent_end = start, knob
        painter.setPen(QPen(QColor('#444444'), 3))
        painter.drawLine(start, end)
        painter.setPen(QPen(QColor('#dddddd'), 3))
        painter.drawLine(accent_start, accent_end)
        painter.setPen(Qt.PenStyle.NoPen)
        painter.setBrush(QColor('#666666'))
        painter.drawEllipse(knob, 9, 9)
        painter.setBrush(QColor('#eeeeee'))
        painter.drawEllipse(knob, 5, 5)
        if self.hasFocus():
            painter.setPen(QPen(QColor('#eeeeee'), 1))
            painter.setBrush(Qt.BrushStyle.NoBrush)
            painter.drawEllipse(knob, 10, 10)

    def mousePressEvent(self, event):
        self.mouse_editing = True
        self.begin_edit()
        super().mousePressEvent(event)

    def mouseReleaseEvent(self, event):
        if not self.mouse_editing:
            blocker = QSignalBlocker(self)
            super().mouseReleaseEvent(event)
            del blocker
            return
        self.editing = bool(self.held_keys)
        dragging = self.isSliderDown()
        super().mouseReleaseEvent(event)
        if not dragging:
            self.commit_mouse()
        self.mouse_editing = False
        self.finish_edit()

    def keyPressEvent(self, event):
        if event.key() in self.ADJUSTMENT_KEYS:
            if event.isAutoRepeat() and event.key() not in self.held_keys:
                event.ignore()
                return
            self.begin_edit()
            self.held_keys.add(event.key())
        super().keyPressEvent(event)

    def keyReleaseEvent(self, event):
        tracked = not event.isAutoRepeat() and event.key() in self.held_keys
        super().keyReleaseEvent(event)
        if tracked:
            if len(self.held_keys) == 1 and not self.mouse_editing:
                self.editing = False
                self.commit()
            self.held_keys.remove(event.key())
            self.finish_edit()


class GainSlider(CommitSlider):
    def __init__(self):
        super().__init__(Qt.Orientation.Vertical)
        self.setRange(-60, 60)
        self.setSingleStep(1)
        self.setPageStep(10)
        self.setFixedSize(28, 150)

    def value(self):
        return super().value() / 10

    def setValue(self, value):
        super().setValue(round(value * 10))


class ToggleSwitch(QCheckBox):
    def __init__(self, label, language='en'):
        super().__init__()
        self.language = language
        self.setAccessibleName(label)
        self.setFixedSize(98, 28)
        self.setCursor(Qt.CursorShape.PointingHandCursor)

    def hitButton(self, position):
        return self.rect().contains(position)

    def paintEvent(self, event):
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        painter.setOpacity(1 if self.isEnabled() else .38)
        track = QRectF(1, 4, 38, 20)
        painter.setPen(QPen(QColor('#969696'), 1))
        painter.setBrush(QColor('#dedede') if self.isChecked() else QColor('#0d0d0d'))
        painter.drawRoundedRect(track, 10, 10)
        painter.setPen(Qt.PenStyle.NoPen)
        painter.setBrush(QColor('#111111') if self.isChecked() else QColor('#b9b9b9'))
        painter.drawEllipse(QRectF(23 if self.isChecked() else 4, 7, 14, 14))
        painter.setPen(QColor('#eeeeee'))
        painter.drawText(QRectF(51, 0, 45, 28), Qt.AlignmentFlag.AlignVCenter, translate(self.language, 'On' if self.isChecked() else 'Off'))
        if self.hasFocus():
            painter.setPen(QPen(QColor('#aaaaaa'), 1))
            painter.setBrush(Qt.BrushStyle.NoBrush)
            painter.drawRoundedRect(QRectF(.5, .5, 96, 27), 4, 4)


CODEC_NAMES = {1: 'SBC', 2: 'aptX Classic', 4: 'aptX Adaptive', 8: 'aptX Lossless', 16: 'aptX Lite', 32: 'LC3'}


MAX_PENDING_EDITS = 32


NOISE_MODES = {
    'transparency': frozenset(('anc_enabled', 'transparency')),
    'anc': frozenset(('anc_enabled', 'adaptive_anc', 'transparency')),
    'adaptive': frozenset(('anc_enabled', 'adaptive_anc', 'transparency')),
    'off': frozenset(('anc_enabled',)),
}


def noise_mode(snapshot):
    if snapshot is None:
        return None
    settings = snapshot.settings
    if settings.get('anc_enabled') is False:
        return 'off'
    if settings.get('transparency') is True:
        return 'transparency'
    if settings.get('anc_enabled') is not True:
        return None
    if settings.get('adaptive_anc') is True:
        return 'adaptive'
    return 'anc'


def mode_available(snapshot, mode):
    return snapshot is not None and mode in NOISE_MODES and all(
        name in snapshot.writable and name in snapshot.settings
        for name in NOISE_MODES[mode]
    )


class MainWindow(QMainWindow):
    requested = Signal(object)
    location_cleanup_requested = Signal(int)

    def __init__(self, discover=None, client_factory=None, dongle_services=None,
                 preference_store=None, config_root=None, autostart_path=None, launcher_path=None,
                 release_checker=None, clock=None, tray_available=None, zone_store=None, location_provider_factory=None):
        super().__init__()
        default_services = discover is None and client_factory is None
        fake_client = not default_services
        if dongle_services is None and default_services:
            dongle_services = DongleServices()
        if discover is None or client_factory is None:
            from .backend import BluetoothClient, discover as bluetooth_discover
            discover = discover or bluetooth_discover
            client_factory = client_factory or BluetoothClient
        self.clock = clock or time.monotonic
        self.tray_available = tray_available or QSystemTrayIcon.isSystemTrayAvailable
        self.preference_store = preference_store
        if self.preference_store is None and (not fake_client or config_root is not None):
            root = Path(config_root) if config_root is not None else Path(QStandardPaths.writableLocation(QStandardPaths.StandardLocation.AppConfigLocation)) / 'momentum-control'
            self.preference_store = PreferenceStore(root / 'preferences.json')
        self.app_data = AppData(Preferences(), ())
        self.storage_error = ''
        self.preferences_invalid = False
        if self.preference_store is not None:
            try:
                self.app_data = self.preference_store.load()
                _validate_data(self.app_data)
            except PreferenceError as error:
                self.storage_error = str(error)
                self.preferences_invalid = True
        self.preferences = self.app_data.preferences
        self.language = self.preferences.language
        self.autostart_path = Path(autostart_path) if autostart_path is not None else None
        self.launcher_path = Path(launcher_path) if launcher_path is not None else None
        if not fake_client and self.autostart_path is None:
            self.autostart_path = Path(QStandardPaths.writableLocation(QStandardPaths.StandardLocation.GenericConfigLocation)) / 'autostart' / 'momentum-control.desktop'
            self.launcher_path = Path.home() / '.local' / 'bin' / 'momentum-control'
        self.autostart_active = False
        if self.autostart_path is not None:
            try:
                self.autostart_active = autostart_enabled(self.autostart_path)
            except PreferenceError as error:
                self.storage_error = str(error)
        self.zone_store = zone_store
        if self.zone_store is None and (not fake_client or config_root is not None):
            zone_root = Path(config_root) if config_root is not None else self.preference_store.path.parent if isinstance(self.preference_store, PreferenceStore) else Path(QStandardPaths.writableLocation(QStandardPaths.StandardLocation.AppConfigLocation)) / 'momentum-control'
            self.zone_store = ZoneStore(zone_root / 'sound-zones.json')
        self.zone_data = ZoneData()
        self.zone_storage_error = ''
        self.zones_invalid = False
        if self.zone_store is not None:
            try:
                self.zone_data = self.zone_store.load()
            except ZoneError as error:
                self.zone_storage_error = str(error)
                self.zones_invalid = True
        self.zone_tracker = ZoneTracker()
        self.zone_config_revision = 0
        self.zone_enable_generation = 0
        self.user_edit_revision = 0
        self.location_generation = 1
        self.location_cancelled = threading.Event()
        self.zone_editor_request = 0
        self.manual_location_intent = None
        self.automatic_location_intent = None
        self.last_location_sample = float('-inf')
        self.location_interval = 15
        self.location_active = False
        self.explicit_address = self.preferences.last_device_address
        self.last_full_transparency = False
        self.last_refresh = self.clock()
        self.last_reconnect = float('-inf')
        self.project_checked = False
        self.force_quit = False
        self.events = deque(maxlen=2000)
        self.request_started = self.clock()
        self.busy = False
        self.active_request = None
        self.page_kind = 'home'
        self.section = 'noise'
        self.last_sections = {}
        self.device_cards = []
        self.discovered_once = False
        # Only the GUI thread owns pending edits and presentation desires.
        self.pending_edits = {}
        self.desired_edits = {}
        self.pending_notice = ''
        self.updating = False
        self.dongle_status = None
        self.update_plan = None
        self.closing = False
        self.generation = 0
        self.snapshot = None
        self.sound_check_dialog = None
        self.sound_check_binding = None
        self.sound_check_operation = None
        self.sound_check_zone_skip = False
        self.controls = {}
        self.eq_controls = []
        self.eq_labels = []
        self.eq_gain_labels = []
        self.eq_topology = None
        self.setWindowTitle('Momentum Control')
        self.setWindowIcon(QIcon.fromTheme('audio-headphones'))
        self.resize(500, 820)
        self.setMinimumWidth(360)
        app = QApplication.instance()
        app.setStyle('Fusion')
        palette = QPalette()
        for role, color in (
            (QPalette.ColorRole.Window, '#000000'), (QPalette.ColorRole.WindowText, '#eeeeee'),
            (QPalette.ColorRole.Base, '#1b1b1b'), (QPalette.ColorRole.AlternateBase, '#0d0d0d'),
            (QPalette.ColorRole.Text, '#eeeeee'), (QPalette.ColorRole.Button, '#1b1b1b'),
            (QPalette.ColorRole.ButtonText, '#eeeeee'), (QPalette.ColorRole.Highlight, '#dddddd'),
            (QPalette.ColorRole.HighlightedText, '#000000'), (QPalette.ColorRole.ToolTipBase, '#222222'),
            (QPalette.ColorRole.ToolTipText, '#eeeeee'),
        ):
            palette.setColor(role, QColor(color))
        palette.setColor(QPalette.ColorGroup.Disabled, QPalette.ColorRole.Text, QColor('#777777'))
        palette.setColor(QPalette.ColorGroup.Disabled, QPalette.ColorRole.ButtonText, QColor('#777777'))
        app.setPalette(palette)
        self.setStyleSheet('''
            QMainWindow, QWidget#page, QScrollArea { background: #000000; }
            QWidget { color: #eeeeee; font-size: 13px; }
            QScrollArea { border: none; }
            QFrame[card="true"] { background: #0d0d0d; border: 1px solid #1e1e1e; border-radius: 14px; }
            QLabel, QRadioButton, QCheckBox { background: transparent; border: none; }
            QLabel[muted="true"] { color: #a0a0a0; font-size: 12px; }
            QLabel[heading="true"] { font-weight: 600; }
            QCheckBox::indicator { width: 16px; height: 16px; border: 1px solid #888888; border-radius: 3px; background: #0d0d0d; }
            QCheckBox::indicator:checked { background: #dddddd; border-color: #dddddd; }
            QPushButton, QComboBox { background: #1d1d1d; border: 1px solid #292929; border-radius: 4px; padding: 6px 10px; }
            QPushButton:hover, QComboBox:hover { background: #292929; }
            QPushButton:checked { background: #292929; border-color: #777777; }
            QPushButton:disabled, QComboBox:disabled, QRadioButton:disabled { color: #666666; }
            QPushButton#gear { background: transparent; border: none; font-size: 23px; padding: 0; }
            QComboBox::drop-down { width: 22px; border: none; }
            QComboBox QAbstractItemView { background: #202020; color: #eeeeee; selection-background-color: #dddddd; selection-color: #000000; }
            QRadioButton { spacing: 9px; min-height: 28px; }
            QRadioButton::indicator { width: 18px; height: 18px; border: 1px solid #888888; border-radius: 10px; background: #0d0d0d; }
            QRadioButton::indicator:checked { width: 10px; height: 10px; border: 5px solid #eeeeee; }
            QRadioButton::indicator:disabled { border-color: #444444; }
            QSlider::groove:horizontal { height: 3px; background: #444444; }
            QSlider::sub-page:horizontal { background: #dddddd; }
            QSlider::handle:horizontal { background: #eeeeee; border: 4px solid #6b6b6b; width: 10px; height: 10px; margin: -7px 0; border-radius: 9px; }
            QSlider::groove:vertical { width: 3px; background: #444444; }
            QSlider::add-page:vertical { background: #dddddd; }
            QSlider::handle:vertical { background: #eeeeee; border: 4px solid #6b6b6b; width: 10px; height: 10px; margin: 0 -7px; border-radius: 9px; }
            QSlider::handle:disabled { background: #777777; border-color: #333333; }
            QScrollBar:vertical { background: #000000; width: 8px; margin: 0; }
            QScrollBar::handle:vertical { background: #333333; border-radius: 4px; min-height: 24px; }
            QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical { height: 0; }
            QScrollBar::add-page:vertical, QScrollBar::sub-page:vertical { background: transparent; }
        ''')
        self.scroll = QScrollArea()
        self.scroll.setWidgetResizable(True)
        self.scroll.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        self.setCentralWidget(self.scroll)
        page = QWidget()
        page.setObjectName('page')
        page.setMinimumWidth(0)
        self.scroll.setWidget(page)
        layout = QVBoxLayout(page)
        layout.setContentsMargins(24, 20, 24, 24)
        layout.setSpacing(12)
        header = QHBoxLayout()
        header_text = QVBoxLayout()
        header_text.setSpacing(4)
        self.title = self.label('Momentum Control')
        self.title.setStyleSheet('font-size: 28px; font-weight: 700;')
        self.title.setSizePolicy(QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Preferred)
        header_text.addWidget(self.title)
        self.status = self.label('Headset controls — disconnected', muted=True)
        header_text.addWidget(self.status)
        self.detail = self.label('Select a paired headset', muted=True)
        header_text.addWidget(self.detail)
        self.edit_status = self.label('', muted=True)
        self.edit_status.setWordWrap(False)
        self.edit_status.setFixedHeight(self.edit_status.fontMetrics().height())
        self.edit_status.setSizePolicy(QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Fixed)
        header_text.addWidget(self.edit_status)
        header.addLayout(header_text, 1)
        right = QVBoxLayout()
        self.battery = self.label('—')
        self.battery.setStyleSheet('font-size: 20px; font-weight: 600;')
        right.addWidget(self.battery, 0, Qt.AlignmentFlag.AlignRight)
        self.gear = QPushButton('⚙')
        self.gear.setObjectName('gear')
        self.gear.setAccessibleName('Connection and device settings')
        self.gear.setFixedSize(30, 30)
        self.gear.setCheckable(True)
        self.gear.clicked.connect(lambda checked: self.show_section('info'))
        right.addWidget(self.gear, 0, Qt.AlignmentFlag.AlignRight)
        right.addStretch()
        header.addLayout(right)
        self.header_bar = QWidget()
        self.header_bar.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Fixed)
        header.setContentsMargins(0, 0, 0, 0)
        self.header_bar.setLayout(header)
        layout.addWidget(self.header_bar)
        self.back_button = QPushButton('‹ My devices')
        self.back_button.clicked.connect(self.show_home)
        layout.addWidget(self.back_button)
        self.section_bar = QWidget()
        section_layout = QHBoxLayout(self.section_bar)
        section_layout.setContentsMargins(0, 0, 0, 0)
        section_layout.setSpacing(5)
        self.section_buttons = {}
        for key, text in (('noise', 'Noise'), ('equalizer', 'Equalizer'), ('controls', 'Controls'),
                          ('info', 'Info'), ('audio', 'Audio'), ('firmware', 'Firmware')):
            button = QPushButton(text)
            button.setCheckable(True)
            button.clicked.connect(lambda checked, key=key: self.show_section(key))
            section_layout.addWidget(button)
            self.section_buttons[key] = button
        layout.addWidget(self.section_bar)
        self.home_card, self.home_layout = self.card('My devices')
        self.home_hint = self.label('Choose a paired headset for Bluetooth controls or a BTD 700 for USB audio.', muted=True)
        self.home_layout.addWidget(self.home_hint)
        self.home_refresh = QPushButton('Refresh devices')
        self.home_refresh.clicked.connect(lambda: self.submit('discover'))
        self.home_layout.addWidget(self.home_refresh)
        self.settings_button = QPushButton(self.tr('App settings'))
        self.settings_button.clicked.connect(self.show_settings)
        self.home_layout.addWidget(self.settings_button)
        layout.addWidget(self.home_card)
        self.connection_card, connection = self.card('Headset controls · direct Bluetooth')
        self.device_picker = QComboBox()
        self.device_picker.setMinimumContentsLength(12)
        self.device_picker.setSizeAdjustPolicy(QComboBox.SizeAdjustPolicy.AdjustToMinimumContentsLengthWithIcon)
        self.device_picker.setSizePolicy(QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Fixed)
        self.device_picker.currentIndexChanged.connect(self.selection_changed)
        connection.addWidget(self.device_picker)
        buttons = QGridLayout()
        self.search_button = QPushButton('Find devices')
        self.search_button.clicked.connect(lambda: self.submit('discover'))
        self.connect_button = QPushButton('Connect controls')
        self.connect_button.clicked.connect(lambda: self.open_headset(self.device_picker.currentData()))
        self.refresh_button = QPushButton('Refresh')
        self.refresh_button.clicked.connect(lambda: self.submit('refresh'))
        buttons.addWidget(self.search_button, 0, 0)
        buttons.addWidget(self.refresh_button, 0, 1)
        buttons.addWidget(self.connect_button, 1, 0, 1, 2)
        connection.addLayout(buttons)
        self.name_label = self.label('Device name unavailable', muted=True)
        connection.addWidget(self.name_label)
        self.name_editor = QLineEdit()
        self.name_editor.setPlaceholderText(self.tr('New device name'))
        self.name_editor.setMaxLength(30)
        connection.addWidget(self.name_editor)
        self.rename_button = QPushButton(self.tr('Rename'))
        self.rename_button.clicked.connect(self.rename_device)
        connection.addWidget(self.rename_button)
        self.metadata_label = self.label('', muted=True)
        connection.addWidget(self.metadata_label)
        self.peers_heading = self.label('Paired devices')
        connection.addWidget(self.peers_heading)
        self.peer_picker = QComboBox()
        self.peer_picker.setSizePolicy(QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Fixed)
        connection.addWidget(self.peer_picker)
        self.peer_label = self.label('Peer information unavailable', muted=True)
        connection.addWidget(self.peer_label)
        self.peer_button = QPushButton(self.tr('Connect paired device'))
        self.peer_button.clicked.connect(self.connect_selected_peer)
        self.peer_picker.currentIndexChanged.connect(lambda: self.update_enabled())
        connection.addWidget(self.peer_button)
        layout.addWidget(self.connection_card)
        self.dongle_card, dongle_layout = self.card('BTD 700 · USB audio')
        self.dongle_picker = QComboBox()
        self.dongle_picker.setSizePolicy(QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Fixed)
        self.dongle_picker.currentIndexChanged.connect(self.dongle_selection_changed)
        dongle_layout.addWidget(self.dongle_picker)
        self.dongle_status_label = self.label('No BTD 700 detected', muted=True)
        self.dongle_firmware_label = self.label('Firmware unavailable', muted=True)
        self.dongle_quality_label = self.label('', muted=True)
        self.dongle_update_label = self.label('Manual official update check. Native transfer is experimental; hardware flashing and recovery are unverified.', muted=True)
        for label in (self.dongle_status_label, self.dongle_firmware_label, self.dongle_quality_label):
            dongle_layout.addWidget(label)
        dongle_layout.addWidget(self.label('USB audio does not expose headset ANC/EQ controls or identify the paired headset.', muted=True))
        self.codec_picker = QComboBox()
        self.codec_picker.setAccessibleName('Requested Bluetooth codec')
        dongle_layout.addWidget(self.codec_picker)
        self.codec_apply_button = QPushButton('Apply codec')
        self.codec_apply_button.clicked.connect(self.apply_codec)
        dongle_layout.addWidget(self.codec_apply_button)
        self.codec_notice = self.label('Switching codec can briefly reconnect audio.', muted=True)
        dongle_layout.addWidget(self.codec_notice)
        self.dongle_firmware_card, firmware_layout = self.card('BTD 700 · Firmware')
        firmware_layout.addWidget(self.dongle_update_label)
        dongle_buttons = QGridLayout()
        self.dongle_refresh_button = QPushButton('Refresh audio')
        self.dongle_refresh_button.clicked.connect(lambda: self.submit('dongle-status'))
        self.dongle_check_button = QPushButton('Check for updates')
        self.dongle_check_button.clicked.connect(lambda: self.submit('dongle-check'))
        self.dongle_update_button = QPushButton('Update firmware')
        self.dongle_update_button.clicked.connect(self.start_dongle_update)
        dongle_buttons.addWidget(self.dongle_check_button, 0, 0, 1, 2)
        dongle_buttons.addWidget(self.dongle_refresh_button, 1, 0)
        dongle_buttons.addWidget(self.dongle_update_button, 1, 1)
        firmware_layout.addLayout(dongle_buttons)
        dongle_layout.addWidget(self.dongle_refresh_button)
        layout.addWidget(self.dongle_card)
        layout.addWidget(self.dongle_firmware_card)

        self.gear.setChecked(True)
        self.message = self.label('Searching for paired headsets…', muted=True)
        self.message.hide()
        layout.addWidget(self.message)
        self.noise_card, noise = self.card('Noise Control')
        self.noise_buttons = {}
        self.noise_group = QButtonGroup(self)
        grid = QGridLayout()
        grid.setHorizontalSpacing(12)
        grid.setVerticalSpacing(4)
        for mode, label, row, column in (
            ('transparency', 'Full transparency', 0, 0), ('anc', 'ANC', 1, 0),
            ('adaptive', 'Adaptive', 0, 1), ('off', 'Off', 1, 1),
        ):
            button = QRadioButton(label)
            button.clicked.connect(lambda checked, mode=mode: self.select_noise_mode(mode))
            self.noise_buttons[mode] = button
            self.noise_group.addButton(button)
            grid.addWidget(button, row, column)
        noise.addLayout(grid)
        for key in ('anc_enabled', 'adaptive_anc', 'transparency'):
            control = QCheckBox(self)
            control.hide()
            self.controls[key] = control
        self.noise_hint = self.label('Connect controls to read noise settings.', muted=True)
        noise.addWidget(self.noise_hint)
        self.strength_box = QWidget()
        strength_layout = QVBoxLayout(self.strength_box)
        strength_layout.setContentsMargins(0, 0, 0, 0)
        strength_layout.setSpacing(8)
        self.strength_label = self.label('ANC strength')
        strength_layout.addWidget(self.strength_label)
        strength = CommitSlider(Qt.Orientation.Horizontal)
        strength.setRange(0, 100)
        strength.setAccessibleName('ANC strength, 100 is strongest')
        strength.valueChanged.connect(lambda value: self.strength_label.setText(f'ANC strength  ·  {value}'))
        strength.committed.connect(lambda: self.write('anc_strength', 100 - strength.value()))
        strength_layout.addWidget(strength)
        strength.editing_changed.connect(self.update_edit_status)
        strength.edit_cancelled.connect(self.apply_edit_values)
        self.controls['anc_strength'] = strength
        noise.addWidget(self.strength_box)
        self.transparency_box = QWidget()
        transparency_layout = QVBoxLayout(self.transparency_box)
        transparency_layout.setContentsMargins(0, 0, 0, 0)
        self.transparency_label = self.label('Transparency amount')
        transparency_layout.addWidget(self.transparency_label)
        transparency = CommitSlider(Qt.Orientation.Horizontal)
        transparency.setRange(0, 100)
        transparency.setAccessibleName('Transparency amount')
        transparency.valueChanged.connect(lambda value: self.transparency_label.setText(f'Transparency amount  ·  {value}'))
        transparency.committed.connect(lambda: self.write('transparency_level', transparency.value()))
        transparency.editing_changed.connect(self.update_edit_status)
        transparency.edit_cancelled.connect(self.apply_edit_values)
        transparency_layout.addWidget(transparency)
        self.transparency_hint = self.label('Changes briefly pass through full transparency.', muted=True)
        transparency_layout.addWidget(self.transparency_hint)
        self.controls['transparency_level'] = transparency
        noise.addWidget(self.transparency_box)
        self.add_combo(noise, 'antiwind' , 'Anti-wind', ('Off', 'Maximum', 'Automatic'))
        self.add_toggle(noise, 'pause_on_transparency', 'Pause music on transparency')
        layout.addWidget(self.noise_card)
        self.eq_card, eq = self.card('Equalizer')
        eq.setSpacing(8)
        self.add_combo(eq, 'sound_mode', 'Sound mode', ('Neutral', 'Equalizer', 'Podcast', 'Personalization (read-only)'))
        self.controls['sound_mode'].model().item(3).setEnabled(False)
        self.eq_hint = self.label('Connect controls to read equalizer bands.', muted=True)
        eq.addWidget(self.eq_hint)
        self.eq_grid = QGridLayout()
        self.eq_grid.setContentsMargins(0, 8, 0, 0)
        self.eq_grid.setHorizontalSpacing(2)
        eq.addLayout(self.eq_grid)
        self.add_toggle(eq, 'bass_boost', 'Bass boost')
        self.add_toggle(eq, 'immersive_audio', 'Immersive audio')
        self.add_toggle(eq, 'head_tracking', 'Head tracking')
        self.spatial_hint = self.label('Head tracking requires immersive audio. Personalization cannot be combined with immersive audio.', muted=True)
        eq.addWidget(self.spatial_hint)
        self.preset_picker = QComboBox()
        self.preset_picker.setSizePolicy(QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Fixed)
        eq.addWidget(self.preset_picker)
        preset_buttons = QHBoxLayout()
        self.preset_save_button = QPushButton(self.tr('Save'))
        self.preset_apply_button = QPushButton(self.tr('Apply'))
        self.preset_delete_button = QPushButton(self.tr('Delete'))
        self.preset_save_button.clicked.connect(lambda: self.prompt_preset())
        self.preset_apply_button.clicked.connect(lambda: self.apply_preset())
        self.preset_delete_button.clicked.connect(lambda: self.delete_preset())
        for button in (self.preset_save_button, self.preset_apply_button, self.preset_delete_button):
            preset_buttons.addWidget(button)
        eq.addLayout(preset_buttons)
        self.sound_check_button = QPushButton(self.tr('Sound check (local EQ)'))
        self.sound_check_button.clicked.connect(self.open_sound_check)
        eq.addWidget(self.sound_check_button)
        self.preset_notice = self.label('Presets use the verified model and exact band frequencies.', muted=True)
        self.preset_picker.setToolTip(self.preset_notice.text())
        self.preset_notice.hide()
        eq.addWidget(self.preset_notice)
        layout.addWidget(self.eq_card)

        self.behavior_card, behavior = self.card('Controls')
        for key, label in (
            ('on_head_detection', 'On-head detection'), ('smart_pause', 'Smart pause'),
            ('touch_controls', 'Touch controls'), ('auto_answer', 'Auto-answer calls'),
            ('comfort_call', 'Comfort call'), ('battery_protection', 'Battery protection'),
        ):
            self.add_toggle(behavior, key, label)
        self.add_combo(behavior, 'audio_prompts', 'Audio prompts', ('Off', 'Tones', 'Tones and voice'))
        self.add_combo(behavior, 'auto_power_off', 'Auto power off', ('Never', '15 minutes', '30 minutes', '60 minutes'), (0, 900, 1800, 3600))
        self.add_combo(behavior, 'sidetone', 'Call transparency', tuple(str(value) for value in range(6)))
        self.firmware_hint = self.label('Unavailable controls are disabled.', muted=True)
        behavior.addWidget(self.firmware_hint)
        layout.addWidget(self.behavior_card)
        self.details_button = QPushButton('Details and help  ▸')
        self.details_button.setCheckable(True)
        self.details_button.setStyleSheet('text-align: left; background: transparent; border: none; color: #999999; padding: 6px 0;')
        self.details_button.toggled.connect(self.toggle_details)
        layout.addWidget(self.details_button)
        self.capabilities = self.label('Only successfully read and verified controls are available.', muted=True)
        self.capabilities.hide()
        layout.addWidget(self.capabilities)
        self.build_settings(layout)
        self.build_zones(layout)
        layout.addStretch()
        self.thread = QThread(self)
        self.worker = Worker(discover, client_factory, dongle_services, release_checker, location_provider_factory, self.clock)
        self.worker.moveToThread(self.thread)
        self.requested.connect(self.worker.execute)
        self.location_cleanup_requested.connect(self.worker.close_location)
        self.worker.completed.connect(self.completed)
        self.worker.progress.connect(self.dongle_progress)
        self.worker.connection_lost.connect(self.connection_lost)
        self.thread.started.connect(self.worker.start_idle_pump)
        self.thread.finished.connect(self.worker.deleteLater)
        self.thread.finished.connect(self.worker_stopped)
        self.thread.start()
        self.render_navigation()
        self.update_enabled()
        self.tray = QSystemTrayIcon(self.windowIcon(), self)
        self.tray.activated.connect(lambda reason: self.open_window())
        self.tray_menu = QMenu(self)
        self.tray_menu.addAction(self.tr('Open'), self.open_window)
        self.tray_mode_actions = {}
        for mode, title in (('anc', 'ANC'), ('transparency', 'Full transparency'), ('adaptive', 'Adaptive'), ('off', 'Off')):
            action = self.tray_menu.addAction(self.tr(title), lambda checked=False, mode=mode: self.select_noise_mode(mode))
            self.tray_mode_actions[mode] = action
        self.tray_refresh_action = self.tray_menu.addAction(self.tr('Refresh'), lambda: self.submit('refresh'))
        self.tray_menu.addAction(self.tr('Quit'), self.quit_application)
        self.tray.setContextMenu(self.tray_menu)
        self.configure_tray()
        self.timer = QTimer(self)
        self.timer.setInterval(1000)
        self.timer.timeout.connect(self.background_tick)
        self.timer.start()
        self.translate_widgets()
        self.submit('discover')

    def sound_check_ready(self, dialog, binding):
        snapshot = self.snapshot
        return (dialog is self.sound_check_dialog and self.sound_check_binding == binding
                and binding[0] == self.generation and binding[1] == self.device_key(self.device_picker.currentData())
                and self.preset_ready() and snapshot.settings.get('sound_mode') == 1
                and snapshot.model == binding[2] and tuple(snapshot.eq_frequencies) == binding[3])

    def open_sound_check(self):
        if self.sound_check_dialog is not None:
            self.sound_check_dialog.raise_()
            self.sound_check_dialog.activateWindow()
            return False
        if not self.preset_ready() or self.snapshot.settings.get('sound_mode') != 1:
            return False
        from .sound_check import SoundCheckDialog
        binding = (self.generation, self.device_key(self.device_picker.currentData()),
                   self.snapshot.model, tuple(self.snapshot.eq_frequencies))
        dialog = None
        try:
            dialog = SoundCheckDialog(self.snapshot,
                apply=lambda preset: self.apply_sound_check(dialog, binding, preset),
                save=lambda name, preset: self.save_sound_check(dialog, binding, name, preset),
                ready=lambda: self.sound_check_ready(dialog, binding), translate=self.tr, parent=self)
        except PreferenceError as error:
            self.preset_notice.setText(str(error))
            self.preset_notice.show()
            return False
        self.sound_check_dialog = dialog
        self.sound_check_binding = binding
        self.sound_check_operation = None
        self.user_edit_revision += 1
        self.sound_check_zone_skip = self.zone_data.enabled
        dialog.finished.connect(lambda result: self.sound_check_closed(dialog))
        dialog.update_snapshot(self.snapshot, self.busy)
        dialog.show()
        self.update_enabled()
        return True

    def apply_sound_check(self, dialog, binding, preset):
        if not isinstance(preset, EqPreset) or not self.sound_check_ready(dialog, binding) or not preset.matches(binding[2], binding[3]):
            return False
        if not self.submit('preset', payload=preset):
            return False
        self.sound_check_operation = (self.active_request, dialog, binding)
        return True

    def save_sound_check(self, dialog, binding, name, preset):
        if (not isinstance(preset, EqPreset) or not self.sound_check_ready(dialog, binding) or not preset.matches(binding[2], binding[3])
                or tuple(self.snapshot.eq_bands) != preset.gains):
            return False
        data = replace(self.app_data, presets=self.app_data.presets + (replace(preset, name=name.strip()),))
        if not self.persist_data(data):
            self.preset_notice.setText(self.settings_notice.text())
            self.preset_notice.show()
            return False
        self.render_presets()
        return True

    def sound_check_closed(self, dialog):
        if self.sound_check_dialog is dialog:
            self.sound_check_dialog = None
            self.sound_check_binding = None
            self.sound_check_operation = None
            self.user_edit_revision += 1
            self.update_enabled()

    def complete_sound_check(self, request, error):
        operation = self.sound_check_operation
        if operation is None or operation[0] is not request:
            return
        self.sound_check_operation = None
        _, dialog, binding = operation
        if (self.sound_check_dialog is not dialog or self.sound_check_binding != binding
                or binding[0] != self.generation or binding[1] != self.device_key(self.device_picker.currentData())):
            return
        dialog.update_snapshot(self.snapshot, self.busy)
        dialog.operation_finished(self.snapshot, error)

    def build_zones(self, layout):
        self.zone_card, zone_layout = self.card('Local Sound Zones')
        zone_layout.addWidget(self.label('Local presets triggered by Linux location. Coordinates stay on this computer; no account or cloud synchronization.', muted=True))
        self.zone_enabled_control = QCheckBox(self.tr('Enable automatic Sound Zones'))
        self.zone_enabled_control.toggled.connect(self.set_zones_enabled)
        zone_layout.addWidget(self.zone_enabled_control)
        self.zone_picker = QComboBox()
        self.zone_picker.setSizePolicy(QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Fixed)
        self.zone_picker.currentIndexChanged.connect(self.zone_selection_changed)
        zone_layout.addWidget(self.zone_picker)
        zone_layout.addWidget(self.label('Zone name'))
        self.zone_name = QLineEdit()
        self.zone_name.setMaxLength(40)
        self.zone_name.textEdited.connect(lambda text: self.cancel_manual_location())
        zone_layout.addWidget(self.zone_name)
        coordinates = QGridLayout()
        self.zone_latitude = QDoubleSpinBox()
        self.zone_latitude.setRange(-90, 90)
        self.zone_latitude.setDecimals(6)
        self.zone_longitude = QDoubleSpinBox()
        self.zone_longitude.setRange(-180, 180)
        self.zone_longitude.setDecimals(6)
        self.zone_radius = QSpinBox()
        self.zone_radius.setRange(100, 50000)
        self.zone_radius.setValue(500)
        self.zone_radius.setSuffix(' m')
        for row, (name, control) in enumerate((('Latitude', self.zone_latitude), ('Longitude', self.zone_longitude), ('Radius', self.zone_radius))):
            coordinates.addWidget(self.label(name), row, 0)
            coordinates.addWidget(control, row, 1)
        for control in (self.zone_latitude, self.zone_longitude, self.zone_radius):
            control.editingFinished.connect(self.cancel_manual_location)
        zone_layout.addLayout(coordinates)
        self.zone_location_button = QPushButton(self.tr('Use current location'))
        self.zone_location_button.clicked.connect(self.use_current_location)
        zone_layout.addWidget(self.zone_location_button)
        self.zone_cancel_button = QPushButton(self.tr('Cancel location request'))
        self.zone_cancel_button.clicked.connect(self.cancel_manual_location)
        zone_layout.addWidget(self.zone_cancel_button)
        zone_layout.addWidget(self.label('Zone equalizer preset'))
        self.zone_preset_picker = QComboBox()
        self.zone_preset_picker.setSizePolicy(QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Fixed)
        zone_layout.addWidget(self.zone_preset_picker)
        buttons = QHBoxLayout()
        self.zone_save_button = QPushButton(self.tr('Save zone'))
        self.zone_save_button.clicked.connect(self.save_zone)
        self.zone_delete_button = QPushButton(self.tr('Delete zone'))
        self.zone_delete_button.clicked.connect(self.delete_zone)
        buttons.addWidget(self.zone_save_button)
        buttons.addWidget(self.zone_delete_button)
        zone_layout.addLayout(buttons)
        self.zone_notice = self.label(self.zone_storage_error or 'Select a verified headset and EQ before saving a zone. Leaving a zone keeps the current sound settings.', muted=True)
        zone_layout.addWidget(self.zone_notice)
        layout.addWidget(self.zone_card)
        self.render_zones()

    def show_zones(self):
        if self.updating or self.closing:
            return False
        self.cancel_manual_location()
        self.zone_editor_request += 1
        self.page_kind = 'zones'
        self.render_zones()
        self.render_navigation()
        return True

    def render_zones(self):
        selected = self.zone_picker.currentData()
        blocker = QSignalBlocker(self.zone_picker)
        self.zone_picker.clear()
        self.zone_picker.addItem(self.tr('New zone'), None)
        for index, zone in enumerate(self.zone_data.zones, 1):
            self.zone_picker.addItem(zone.name, zone)
            if selected == zone:
                self.zone_picker.setCurrentIndex(index)
        del blocker
        blocker = QSignalBlocker(self.zone_enabled_control)
        self.zone_enabled_control.setChecked(self.zone_data.enabled)
        del blocker
        selected_preset = self.zone_preset_picker.currentData()
        if selected != self.zone_picker.currentData():
            selected = None
            selected_preset = None
        blocker = QSignalBlocker(self.zone_preset_picker)
        self.zone_preset_picker.clear()
        self.zone_preset_picker.addItem(self.tr('Current verified EQ'), None)
        snapshot = self.snapshot
        if snapshot is not None:
            for preset in self.app_data.presets:
                if preset.matches(snapshot.model, tuple(snapshot.eq_frequencies)):
                    self.zone_preset_picker.addItem(preset.name, preset)
        if selected is not None:
            self.zone_preset_picker.addItem(self.tr('Stored zone preset'), selected.preset)
        index = self.zone_preset_picker.findData(selected_preset)
        self.zone_preset_picker.setCurrentIndex(max(0, index))
        del blocker

    def zone_selection_changed(self, index=None):
        self.cancel_manual_location()
        self.zone_editor_request += 1
        zone = self.zone_picker.currentData()
        if zone is None:
            self.zone_name.clear()
            self.zone_latitude.setValue(0)
            self.zone_longitude.setValue(0)
            self.zone_radius.setValue(500)
        else:
            self.zone_name.setText(zone.name)
            self.zone_latitude.setValue(zone.latitude)
            self.zone_longitude.setValue(zone.longitude)
            self.zone_radius.setValue(zone.radius_m)
        self.render_zones()
        blocker = QSignalBlocker(self.zone_preset_picker)
        preset_index = self.zone_preset_picker.findData(zone.preset if zone is not None else None)
        self.zone_preset_picker.setCurrentIndex(max(0, preset_index))
        del blocker

    def persist_zones(self, data):
        from .sound_zones import _validate_zones
        try:
            if self.zones_invalid:
                raise ZoneError('Existing Sound Zones data is invalid; repair it before saving.')
            _validate_zones(data)
            if self.zone_store is not None:
                self.zone_store.save(data)
        except (ZoneError, OSError) as error:
            self.zone_notice.setText(str(error))
            self.render_zones()
            return False
        self.zone_data = data
        self.zone_config_revision += 1
        self.zone_enable_generation += 1
        self.cancel_location()
        self.zone_tracker.reset()
        self.sound_check_zone_skip = False
        self.last_location_sample = float('-inf')
        self.location_interval = 15
        self.render_zones()
        self.zone_notice.setText(self.tr('Sound Zones saved.') if self.zone_store is not None else self.tr('Sound Zones applied for this session.'))
        return True

    def set_zones_enabled(self, enabled):
        if self.closing or self.updating:
            self.render_zones()
            return False
        return self.persist_zones(replace(self.zone_data, enabled=enabled))

    def save_zone(self):
        if not self.preset_ready():
            self.zone_notice.setText(self.tr('Saving a zone requires idle, verified EQ with no drafts or pending edits.'))
            return False
        device = self.device_picker.currentData()
        if device is None or device.address != self.explicit_address:
            return False
        selected = self.zone_picker.currentData()
        if selected is not None and selected.device_address != device.address:
            self.zone_notice.setText(self.tr('Select this zone’s configured headset before editing it.'))
            return False
        snapshot = self.snapshot
        preset = self.zone_preset_picker.currentData()
        if preset is None:
            preset = EqPreset('Zone EQ', snapshot.model, tuple(snapshot.eq_frequencies), tuple(snapshot.eq_bands))
        if not preset.matches(snapshot.model, tuple(snapshot.eq_frequencies)):
            self.zone_notice.setText(self.tr('Zone preset does not match this verified headset.'))
            return False
        zone = SoundZone(self.zone_name.text().strip(), device.address, self.zone_latitude.value(),
            self.zone_longitude.value(), self.zone_radius.value(), preset)
        zones = tuple(item for item in self.zone_data.zones if item != selected) + (zone,)
        if not self.persist_zones(replace(self.zone_data, zones=zones)):
            return False
        self.zone_picker.setCurrentIndex(self.zone_picker.findData(zone))
        return True

    def delete_zone(self):
        if self.busy or self.closing or self.updating or self.pending_edits or self.slider_drafts():
            return False
        selected = self.zone_picker.currentData()
        if selected is None:
            return False
        return self.persist_zones(replace(self.zone_data, zones=tuple(zone for zone in self.zone_data.zones if zone != selected)))

    def location_intent(self, manual=False):
        snapshot = self.snapshot
        return LocationIntent(self.generation, self.location_generation, self.user_edit_revision,
            self.zone_config_revision, self.zone_enable_generation, self.zone_editor_request,
            self.explicit_address, snapshot.model if snapshot is not None else '',
            tuple(snapshot.eq_frequencies) if snapshot is not None else (), self.clock(), manual, self.location_cancelled)

    def cancel_location(self):
        self.location_cancelled.set()
        self.location_cancelled = threading.Event()
        closed = self.location_generation
        self.location_generation += 1
        self.manual_location_intent = None
        self.automatic_location_intent = None
        self.location_active = False
        self.location_cleanup_requested.emit(closed)

    def cancel_manual_location(self):
        if self.manual_location_intent is not None:
            self.zone_editor_request += 1
            self.cancel_location()

    def use_current_location(self):
        if self.page_kind != 'zones' or self.busy or self.closing or self.updating or self.pending_edits or self.slider_drafts():
            return False
        self.cancel_location()
        self.zone_editor_request += 1
        self.manual_location_intent = self.location_intent(manual=True)
        self.last_location_sample = self.clock()
        self.location_interval = 15
        self.zone_notice.setText(self.tr('Waiting for location permission or a fresh fix…'))
        return self.submit('location-sample', payload=self.manual_location_intent)

    def zones_target_ready(self):
        device = self.device_picker.currentData()
        snapshot = self.snapshot
        return (self.zone_data.enabled and device is not None and snapshot is not None
                and device.address == self.explicit_address and 'eq' in snapshot.writable
                and len(snapshot.eq_frequencies) == len(snapshot.eq_bands) and bool(snapshot.eq_bands)
                and any(zone.device_address == device.address and zone.preset.matches(snapshot.model, tuple(snapshot.eq_frequencies))
                        for zone in self.zone_data.zones))

    def sample_location_if_due(self, now):
        intent = self.manual_location_intent
        if intent is None:
            if not self.zones_target_ready():
                if self.location_active:
                    self.cancel_location()
                return False
            intent = self.automatic_location_intent
            if intent is None:
                intent = self.location_intent()
        if now - self.last_location_sample < self.location_interval:
            return False
        self.last_location_sample = now
        if not intent.manual:
            self.automatic_location_intent = intent
        return self.submit('location-sample', payload=intent)

    def complete_location(self, intent, fix, error):
        if intent.location_generation != self.location_generation:
            return
        if (intent.generation != self.generation or intent.config_revision != self.zone_config_revision
                or intent.enable_generation != self.zone_enable_generation):
            self.cancel_location()
            return
        if error:
            self.zone_notice.setText(self.tr('Location unavailable. Check GeoClue permission and service.'))
            self.cancel_location()
            self.location_interval = 15
            return
        if fix is None:
            self.location_interval = 15
            self.zone_notice.setText(self.tr('Waiting for location permission or a fresh fix…'))
            return
        self.location_interval = 60
        if intent.manual:
            if (self.page_kind == 'zones' and intent.editor_request == self.zone_editor_request
                    and intent.edit_revision == self.user_edit_revision
                    and self.clock() - intent.started_at < 30):
                self.zone_latitude.setValue(fix.latitude)
                self.zone_longitude.setValue(fix.longitude)
                self.zone_notice.setText(self.tr('Current location filled. Review and save the zone.'))
            self.cancel_location()
            return
        self.automatic_location_intent = None
        if not self.zones_target_ready() or intent.address != self.explicit_address:
            self.cancel_location()
            return
        zone = self.zone_tracker.update(fix, self.zone_data.zones, intent.address, intent.model, intent.frequencies, self.clock())
        if zone is None:
            return
        skipped = self.sound_check_dialog is not None or self.sound_check_zone_skip
        self.sound_check_zone_skip = False
        if (skipped or intent.edit_revision != self.user_edit_revision or not self.preset_ready()
                or not zone.preset.matches(self.snapshot.model, tuple(self.snapshot.eq_frequencies))):
            self.zone_notice.setText(self.tr('Zone entry skipped because settings changed or work is pending.'))
            return
        self.zone_notice.setText(self.tr('Applying the entered zone’s verified EQ preset…'))
        self.submit('preset', payload=zone.preset, automatic=True)

    def tr(self, text):
        return translate(self.language, text)

    def translate_widgets(self):
        for widget in self.findChildren(QWidget):
            if isinstance(widget, (QLabel, QPushButton, QCheckBox)):
                widget.setText(self.tr(widget.text()))
            elif isinstance(widget, QComboBox):
                for index in range(widget.count()):
                    widget.setItemText(index, self.tr(widget.itemText(index)))

    def build_settings(self, layout):
        self.preference_controls = {}
        self.settings_card, settings = self.card('App settings')
        settings.addWidget(self.label('Language (applies after restart)'))
        language = QComboBox()
        language.addItem('English', 'en')
        language.addItem('Deutsch', 'de')
        language.currentIndexChanged.connect(lambda index: self.set_preference('language', language.itemData(index)))
        settings.addWidget(language)
        self.preference_controls['language'] = language
        settings.addWidget(self.label('Refresh interval'))
        refresh = QComboBox()
        for title, seconds in (('Manual', 0), ('10 seconds', 10), ('30 seconds', 30), ('60 seconds', 60)):
            refresh.addItem(self.tr(title), seconds)
        refresh.currentIndexChanged.connect(lambda index: self.set_preference('refresh_seconds', refresh.itemData(index)))
        settings.addWidget(refresh)
        self.preference_controls['refresh_seconds'] = refresh
        self.zones_button = QPushButton(self.tr('Local Sound Zones'))
        self.zones_button.clicked.connect(self.show_zones)
        settings.addWidget(self.zones_button)
        self.settings_notice = self.label(self.storage_error, muted=True)
        settings.addWidget(self.settings_notice)
        layout.addWidget(self.settings_card)
        self.background_card, background = self.card('Background behavior')
        for key, title in (('auto_reconnect', 'Reconnect the chosen headset'), ('tray_enabled', 'Show tray icon'),
                ('close_to_tray', 'Close window to tray'), ('start_minimized', 'Start minimized'),
                ('restore_transparency', 'Restore full transparency after reconnect')):
            checkbox = QCheckBox(self.tr(title))
            checkbox.toggled.connect(lambda value, key=key: self.set_preference(key, value))
            background.addWidget(checkbox)
            self.preference_controls[key] = checkbox
        self.autostart_control = QCheckBox(self.tr('Start at login'))
        self.autostart_control.toggled.connect(self.change_autostart)
        self.autostart_control.setEnabled(self.autostart_path is not None and self.launcher_path is not None)
        background.addWidget(self.autostart_control)
        background.addWidget(self.label('Reconnect uses only the explicitly chosen or stored headset. Restoration never enables ANC.', muted=True))
        layout.addWidget(self.background_card)
        self.maintenance_card, maintenance = self.card('Maintenance')
        checkbox = QCheckBox(self.tr('Check upstream project releases once per run'))
        checkbox.toggled.connect(lambda value: self.set_preference('check_app_updates', value))
        self.preference_controls['check_app_updates'] = checkbox
        maintenance.addWidget(checkbox)
        self.release_button = QPushButton(self.tr('Check project release'))
        self.release_button.clicked.connect(lambda: self.submit('project-release'))
        maintenance.addWidget(self.release_button)
        self.release_label = self.label('Manual upstream Windows project release information. These releases do not update this Linux companion.', muted=True)
        maintenance.addWidget(self.release_label)
        self.export_button = QPushButton(self.tr('Export redacted diagnostics'))
        self.export_button.clicked.connect(self.export_diagnostic_file)
        maintenance.addWidget(self.export_button)
        layout.addWidget(self.maintenance_card)
        self.render_preferences()

    def render_preferences(self):
        for name, control in self.preference_controls.items():
            blocker = QSignalBlocker(control)
            value = getattr(self.preferences, name)
            if isinstance(control, QComboBox):
                control.setCurrentIndex(control.findData(value))
            else:
                control.setChecked(value)
            del blocker
        blocker = QSignalBlocker(self.autostart_control)
        self.autostart_control.setChecked(self.autostart_active)
        del blocker

    def persist_data(self, data):
        try:
            if self.preferences_invalid:
                raise PreferenceError('Preferences are invalid; repair the existing file before saving.')
            _validate_data(data)
            if self.preference_store is not None:
                self.preference_store.save(data)
        except (PreferenceError, OSError) as error:
            self.settings_notice.setText(str(error))
            return False
        self.app_data = data
        self.preferences = data.preferences
        self.settings_notice.setText(self.tr('Saved.') if self.preference_store is not None else self.tr('Applied for this session.'))
        return True

    def set_preference(self, name, value):
        if self.closing or self.updating:
            self.render_preferences()
            return False
        try:
            preferences = replace(self.preferences, **{name: value})
        except TypeError:
            return False
        saved = self.persist_data(replace(self.app_data, preferences=preferences))
        self.render_preferences()
        if saved:
            if name == 'tray_enabled':
                self.configure_tray()
            if name == 'start_minimized' and self.autostart_active:
                self.change_autostart(True)
            if name == 'language':
                self.settings_notice.setText(self.tr('Language changes after restart.'))
        return saved

    def change_autostart(self, enabled):
        if self.updating or self.closing or self.autostart_path is None or self.launcher_path is None:
            self.render_preferences()
            return False
        try:
            set_autostart(self.autostart_path, self.launcher_path, enabled, minimized=self.preferences.start_minimized)
            self.autostart_active = autostart_enabled(self.autostart_path)
            self.settings_notice.setText(self.tr('Start at login enabled.') if self.autostart_active else self.tr('Start at login disabled.'))
        except PreferenceError as error:
            self.settings_notice.setText(str(error))
            self.render_preferences()
            return False
        self.render_preferences()
        return True

    def show_settings(self):
        self.cancel_manual_location()
        if self.updating or self.closing:
            return False
        self.page_kind = 'settings'
        self.render_navigation()
        return True

    def choose_address(self, address):
        if address != self.explicit_address:
            self.cancel_location()
            self.zone_tracker.reset()
            self.sound_check_zone_skip = False
            self.last_full_transparency = False
            self.explicit_address = address
        if address != self.preferences.last_device_address:
            self.set_preference('last_device_address', address)

    def slider_drafts(self):
        return any(control.editing for control in (*self.controls.values(), *self.eq_controls)
                   if isinstance(control, CommitSlider))

    def preset_ready(self):
        snapshot = self.snapshot
        return (not self.busy and not self.closing and not self.updating and not self.pending_edits
                and not self.desired_edits and not self.slider_drafts() and snapshot is not None
                and 'eq' in snapshot.writable and len(snapshot.eq_frequencies) == len(snapshot.eq_bands)
                and bool(snapshot.eq_bands))

    def render_presets(self):
        selected = self.preset_picker.currentData()
        blocker = QSignalBlocker(self.preset_picker)
        self.preset_picker.clear()
        snapshot = self.snapshot
        if snapshot is not None and snapshot.eq_frequencies and len(snapshot.eq_frequencies) == len(snapshot.eq_bands):
            flat = EqPreset('Flat', snapshot.model, tuple(snapshot.eq_frequencies), (0.,) * len(snapshot.eq_bands))
            presets = (flat,) + tuple(preset for preset in self.app_data.presets if preset.matches(snapshot.model, tuple(snapshot.eq_frequencies)))
            for index, preset in enumerate(presets):
                self.preset_picker.addItem(self.tr('Flat') if index == 0 else preset.name, preset)
                if preset == selected:
                    self.preset_picker.setCurrentIndex(index)
        del blocker

    def prompt_preset(self):
        if not self.preset_ready():
            return False
        name, accepted = QInputDialog.getText(self, self.tr('Save preset'), self.tr('Preset name'))
        return self.save_preset(name) if accepted else False

    def save_preset(self, name):
        if not self.preset_ready():
            return False
        snapshot = self.snapshot
        preset = EqPreset(name.strip(), snapshot.model, tuple(snapshot.eq_frequencies), tuple(snapshot.eq_bands))
        data = replace(self.app_data, presets=self.app_data.presets + (preset,))
        if not self.persist_data(data):
            self.preset_notice.setText(self.settings_notice.text())
            return False
        self.render_presets()
        self.preset_picker.setCurrentIndex(self.preset_picker.findData(preset))
        self.preset_notice.setText(self.tr('Preset saved from verified gains.'))
        self.preset_notice.show()
        return True

    def apply_preset(self):
        if not self.preset_ready():
            return False
        preset = self.preset_picker.currentData()
        if not isinstance(preset, EqPreset) or not preset.matches(self.snapshot.model, tuple(self.snapshot.eq_frequencies)):
            self.preset_notice.setText(self.tr('Preset does not match this headset and band frequencies.'))
            return False
        self.preset_notice.setText(self.tr('Applying and verifying the full curve…'))
        return self.submit('preset', payload=preset)

    def delete_preset(self):
        if not self.preset_ready():
            return False
        preset = self.preset_picker.currentData()
        if preset not in self.app_data.presets:
            return False
        data = replace(self.app_data, presets=tuple(item for item in self.app_data.presets if item != preset))
        if not self.persist_data(data):
            self.preset_notice.setText(self.settings_notice.text())
            return False
        self.render_presets()
        self.preset_notice.setText(self.tr('Preset deleted.'))
        return True

    def rename_device(self):
        if self.busy or self.closing or self.snapshot is None or 'rename' not in self.snapshot.writable:
            return False
        return self.submit('rename', payload=self.name_editor.text())

    def render_metadata(self):
        snapshot = self.snapshot
        name = getattr(snapshot, 'device_name', None)
        self.name_label.setText(self.tr('Device name unavailable') if name is None else self.tr('Device name:') + ' ' + name)
        for widget in (self.name_label, self.name_editor, self.rename_button):
            widget.setVisible(name is not None or 'rename' in snapshot.writable)
        self.metadata_label.setVisible(any(getattr(snapshot, key, None) is not None for key in ('charging', 'wear_state', 'personalization_state')))
        for widget in (self.peers_heading, self.peer_picker, self.peer_label, self.peer_button):
            widget.setVisible(getattr(snapshot, 'peers_available', False))
        self.name_editor.setMaxLength(16 if snapshot.model.startswith('M5AEBT') else 30)
        values = []
        for key, title in (('charging', 'Charging'), ('wear_state', 'Wear state'), ('personalization_state', 'Personalization state')):
            value = getattr(snapshot, key, None)
            values.append(self.tr(title) + ': ' + (self.tr('Unavailable') if value is None else str(value)))
        self.metadata_label.setText(' · '.join(values))
        selected = self.peer_picker.currentData()
        blocker = QSignalBlocker(self.peer_picker)
        self.peer_picker.clear()
        for peer in getattr(snapshot, 'peers', ()):
            status = self.tr('Connected') if peer.connected is True else self.tr('Disconnected') if peer.connected is False else self.tr('Unknown')
            self.peer_picker.addItem(peer.name + ' · ' + status + (' · ' + self.tr('This PC') if peer.is_this_pc else ''), peer)
            if selected == peer:
                self.peer_picker.setCurrentIndex(self.peer_picker.count() - 1)
        del blocker
        capacity = getattr(snapshot, 'max_connections', None)
        count = sum(peer.connected is True for peer in getattr(snapshot, 'peers', ()))
        reason = self.tr('M5 peer connection is read-only until hardware verification.') if snapshot.model.startswith('M5AEBT') and 'connect_peer' not in snapshot.writable else self.tr('No disconnect or pairing deletion is offered.')
        self.peer_label.setText((self.tr('Connections:') + f' {count}/{capacity}\n' if capacity is not None else self.tr('Peer capacity unavailable.') + '\n') + reason)

    def peer_ready(self):
        snapshot = self.snapshot
        peer = self.peer_picker.currentData()
        if snapshot is None or peer is None or 'connect_peer' not in snapshot.writable or not getattr(snapshot, 'peers_available', False):
            return False
        peers = snapshot.peers
        capacity = snapshot.max_connections
        return (peer in peers and not peer.is_this_pc and peer.connected is not None
                and all(item.connected is not None for item in peers)
                and sum(item.name == peer.name for item in peers) == 1
                and sum(item.is_this_pc and item.connected is True for item in peers) == 1
                and capacity is not None and (peer.connected is True or sum(item.connected is True for item in peers) < capacity))

    def connect_selected_peer(self):
        if self.busy or self.closing or not self.peer_ready():
            return False
        return self.submit('connect-peer', payload=self.peer_picker.currentData())

    def background_tick(self):
        now = self.clock()
        if self.manual_location_intent is not None and now - self.manual_location_intent.started_at >= 30:
            self.cancel_manual_location()
            self.zone_notice.setText(self.tr('Location request timed out.'))
        if self.closing or self.updating or self.busy or self.pending_edits or self.desired_edits or self.slider_drafts() or self.sound_check_dialog is not None:
            return
        if self.sample_location_if_due(now):
            return
        if self.preferences.check_app_updates and not self.project_checked:
            self.submit('project-release')
            return
        if self.snapshot is None:
            if self.preferences.auto_reconnect and self.explicit_address and now - self.last_reconnect >= 10:
                self.last_reconnect = now
                restore = self.preferences.restore_transparency and self.last_full_transparency
                self.start_request(Request('reconnect', self.generation, name=self.explicit_address, payload=restore))
            return
        if self.preferences.refresh_seconds and now - self.last_refresh >= self.preferences.refresh_seconds:
            self.submit('poll')

    def configure_tray(self):
        available = self.preferences.tray_enabled and self.tray_available()
        self.tray.setVisible(available)
        if not available and not self.isVisible() and not self.closing:
            self.show()
        app = QApplication.instance()
        app.setQuitOnLastWindowClosed(not available)

    def show_startup(self, minimized=False):
        if (minimized or self.preferences.start_minimized) and self.tray.isVisible() and self.tray_available():
            self.hide()
        else:
            self.show()

    def open_window(self):
        self.show()
        self.raise_()
        self.activateWindow()

    def quit_application(self):
        if self.updating:
            self.open_window()
            self.message.setText(self.tr('Firmware update is active. Keep the dongle connected; quit after it finishes.'))
            self.message.show()
            return False
        self.force_quit = True
        self.close()
        return True

    def record_event(self, action, error):
        if action in _OPERATION_NAMES:
            elapsed = min(300000, max(0, (self.clock() - self.request_started) * 1000))
            self.events.append({'operation': action, 'status': 'failed' if error else 'ok', 'elapsed_ms': elapsed})

    def export_diagnostic_file(self):
        if self.busy or self.closing or self.snapshot is None:
            return False
        filename, _ = QFileDialog.getSaveFileName(self, self.tr('Export redacted diagnostics'), 'momentum-diagnostics.zip', 'ZIP (*.zip)')
        if not filename:
            return False
        try:
            export_diagnostics(Path(filename), self.snapshot, tuple(self.events))
        except (PreferenceError, OSError) as error:
            self.settings_notice.setText(str(error))
            return False
        self.settings_notice.setText(self.tr('Redacted diagnostics exported.'))
        return True

    @staticmethod
    def device_key(device, kind='headset'):
        if device is None:
            return None
        if kind == 'dongle':
            return (device.identity, device.usb_path)
        return (device.address, getattr(device, 'path', None))

    def show_home(self):
        self.cancel_manual_location()
        if self.updating:
            return False
        self.page_kind = 'home'
        self.render_navigation()
        return True

    def show_section(self, section):
        if self.updating and section != 'firmware':
            return False
        valid = ('audio', 'firmware') if self.page_kind == 'dongle' else ('noise', 'equalizer', 'controls', 'info')
        if self.page_kind == 'home' or section not in valid:
            return False
        self.section = section
        device = self.dongle_picker.currentData() if self.page_kind == 'dongle' else self.device_picker.currentData()
        self.last_sections[(self.page_kind, self.device_key(device, self.page_kind))] = section
        self.render_navigation()
        return True

    def open_headset(self, device):
        self.cancel_manual_location()
        if self.busy or self.pending_edits or self.closing or device is None:
            return False
        key = self.device_key(device)
        index = next((i for i in range(self.device_picker.count())
                      if self.device_key(self.device_picker.itemData(i)) == key), -1)
        if index < 0:
            return False
        self.device_picker.setCurrentIndex(index)
        self.choose_address(device.address)
        self.page_kind = 'headset'
        self.section = self.last_sections.get(('headset', key), 'noise')
        self.render_navigation()
        if self.snapshot is None:
            return self.submit('connect')
        return True

    def open_dongle(self, device):
        self.cancel_manual_location()
        if self.busy or self.pending_edits or self.closing or device is None:
            return False
        key = self.device_key(device, 'dongle')
        index = next((i for i in range(self.dongle_picker.count())
                      if self.device_key(self.dongle_picker.itemData(i), 'dongle') == key), -1)
        if index < 0:
            return False
        blocker = QSignalBlocker(self.dongle_picker)
        changed = key != self.device_key(self.dongle_picker.currentData(), 'dongle')
        self.dongle_picker.setCurrentIndex(index)
        del blocker
        if changed:
            self.clear_dongle_status()
            self.update_plan = None
        self.page_kind = 'dongle'
        self.section = self.last_sections.get(('dongle', key), 'audio')
        self.render_navigation()
        return self.submit('dongle-status')

    def render_navigation(self):
        home = self.page_kind == 'home'
        app_settings = self.page_kind == 'settings'
        zones = self.page_kind == 'zones'
        self.zone_card.setVisible(zones)
        self.settings_card.setVisible(app_settings)
        self.background_card.setVisible(app_settings)
        self.maintenance_card.setVisible(app_settings)
        headset = self.page_kind == 'headset'
        self.title.setText(self.tr('Local Sound Zones') if zones else self.tr('App settings') if app_settings else self.tr('My devices') if home else 'BTD 700' if not headset else
            'MOMENTUM 5' if self.snapshot is not None and self.snapshot.model.startswith('M5AEBT') else
            'MOMENTUM 4' if self.snapshot is not None and (self.snapshot.model.startswith('M4AEBT') or self.snapshot.model == 'MOMENTUM 4') else 'Momentum Control')
        self.back_button.setVisible(not home)
        self.section_bar.setVisible(not home and not app_settings and not zones)
        self.home_card.setVisible(home)
        self.status.setVisible(headset)
        self.detail.setVisible(headset)
        self.battery.setVisible(headset)
        self.gear.hide()
        self.edit_status.setVisible(headset)
        for key, button in self.section_buttons.items():
            button.setVisible(key in (('noise', 'equalizer', 'controls', 'info') if headset else ('audio', 'firmware')))
            button.setChecked(key == self.section)
        for card, visible in ((self.noise_card, headset and self.section == 'noise'),
                (self.eq_card, headset and self.section == 'equalizer'),
                (self.behavior_card, headset and self.section == 'controls'),
                (self.connection_card, headset and self.section == 'info'),
                (self.dongle_card, self.page_kind == 'dongle' and self.section == 'audio'),
                (self.dongle_firmware_card, self.page_kind == 'dongle' and self.section == 'firmware')):
            card.setVisible(visible)
        self.details_button.hide()
        self.capabilities.setVisible(headset and self.section == 'info')
        self.scroll.verticalScrollBar().setValue(0)

    def rebuild_device_cards(self):
        for button in self.device_cards:
            self.home_layout.removeWidget(button)
            button.hide()
            button.deleteLater()
        self.device_cards = []
        for picker, kind in ((self.device_picker, 'headset'), (self.dongle_picker, 'dongle')):
            for index in range(picker.count()):
                device = picker.itemData(index)
                text = f'{device.name}\nBluetooth controls · {device.address}' if kind == 'headset' else f'BTD 700 {index + 1}\nUSB audio and firmware'
                button = QPushButton(text)
                button.setMinimumHeight(72)
                button.clicked.connect(lambda checked, device=device, kind=kind:
                    self.open_headset(device) if kind == 'headset' else self.open_dongle(device))
                self.home_layout.insertWidget(self.home_layout.count() - 1, button)
                self.device_cards.append(button)

    def clear_dongle_status(self):
        self.dongle_status = None
        self.dongle_status_label.setText('BTD 700 — status unavailable')
        self.dongle_firmware_label.setText('Firmware unavailable')
        self.codec_picker.clear()
        self.dongle_quality_label.setText('Active codec unavailable')
        self.codec_notice.setText('Refresh audio status before choosing a codec.')

    def transparency_level_available(self):
        snapshot = self.snapshot
        return (snapshot is not None and snapshot.settings.get('anc_enabled') is True
                and {'transparency_level', 'transparency'} <= snapshot.writable
                and all(key in snapshot.settings for key in ('anc_enabled', 'anc_strength',
                    'transparency', 'transparency_level', 'antiwind', 'adaptive_anc')))

    def codec_ready(self):
        status = self.dongle_status
        codec = self.codec_picker.currentData()
        return (status is not None and self.device_key(status.device, 'dongle') ==
                self.device_key(self.dongle_picker.currentData(), 'dongle')
                and status.state in (2, 3) and status.audio_mode == 0
                and codec is not None and bool(getattr(status, 'supported_codec_mask', 0) & int(codec)))

    def apply_codec(self):
        if self.busy or self.closing or not self.codec_ready():
            return False
        codec = self.codec_picker.currentData()
        self.codec_notice.setText(f'Requested: {CODEC_NAMES[int(codec)]} · verifying active codec…')
        return self.submit('dongle-codec', payload=codec)

    def label(self, text, muted=False):
        label = TranslatedLabel(text, self.language)
        label.setTextFormat(Qt.TextFormat.PlainText)
        label.setWordWrap(True)
        label.setMinimumWidth(0)
        label.setProperty('muted', muted)
        return label

    def card(self, heading):
        card = QFrame()
        card.setProperty('card', True)
        card.setMinimumWidth(0)
        layout = QVBoxLayout(card)
        layout.setContentsMargins(18, 18, 18, 18)
        layout.setSpacing(12)
        title = self.label(heading)
        title.setProperty('heading', True)
        layout.addWidget(title)
        return card, layout

    def add_toggle(self, layout, key, label):
        row = QWidget()
        row_layout = QHBoxLayout(row)
        row_layout.setContentsMargins(0, 0, 0, 0)
        row_layout.setSpacing(3)
        row_layout.addWidget(self.label(label))
        control = ToggleSwitch(self.tr(label), self.language)
        control.toggled.connect(lambda value: self.write(key, value))
        row_layout.addWidget(control)
        layout.addWidget(row)
        self.controls[key] = control

    def add_combo(self, layout, key, label, choices, values=None):
        row = QWidget()
        row_layout = QHBoxLayout(row)
        row_layout.setContentsMargins(0, 0, 0, 0)
        row_layout.addWidget(self.label(label), 1)
        combo = QComboBox()
        for index, text in enumerate(choices):
            combo.addItem(self.tr(text), values[index] if values is not None else index)
        combo.setMaximumWidth(220 if key == 'sound_mode' else 180)
        combo.setMinimumWidth(0)
        combo.setSizePolicy(QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Fixed)
        combo.setAccessibleName(label)
        combo.currentIndexChanged.connect(lambda index: self.write(key, combo.itemData(index)))
        row_layout.addWidget(combo, 1)
        layout.addWidget(row)
        self.controls[key] = combo

    def toggle_details(self, checked):
        self.capabilities.setVisible(checked)
        self.details_button.setText('Details and help  ▾' if checked else 'Details and help  ▸')

    @Slot()
    def selection_changed(self, index=None, *, explicit=True):
        if explicit:
            device = self.device_picker.currentData()
            self.choose_address(device.address if device is not None else '')
        self.clear_pending_edits()
        self.generation += 1
        self.snapshot = None
        self.status.setText('Headset controls — disconnected')
        self.detail.setText('Select Connect controls to read this headset.' if self.device_picker.currentData() is not None else 'Pair a headset with PC Bluetooth for controls.')
        self.battery.setText('—')
        self.update_enabled()

    @staticmethod
    def edit_key(request):
        return ('mode', '', None) if request.action == 'mode' else ('write', request.name, request.band)

    def edit_available(self, request):
        snapshot = self.snapshot
        if (snapshot is None or request.generation != self.generation
                or self.device_key(request.device) != self.device_key(self.device_picker.currentData())):
            return False
        if request.action == 'mode':
            return mode_available(snapshot, request.name)
        if request.name == 'head_tracking' and snapshot.settings.get('immersive_audio') is not True:
            return False
        if request.name in ('immersive_audio', 'head_tracking') and request.value is True and snapshot.settings.get('sound_mode') == 3:
            return False
        if request.name == 'transparency_level' and not self.transparency_level_available():
            return False
        if request.name not in snapshot.writable:
            return False
        if request.name in snapshot.choices and request.value not in snapshot.choices[request.name]:
            return False
        if request.name == 'sound_mode' and request.value not in (0, 1, 2):
            return False
        if request.name == 'eq':
            return (request.band is not None and 0 <= request.band < len(snapshot.eq_bands)
                    and request.eq_topology == self.snapshot_eq_topology(snapshot))
        return request.name in snapshot.settings

    def clear_pending_edits(self):
        self.pending_edits.clear()
        self.desired_edits.clear()
        self.pending_notice = ''
        for control in (*self.controls.values(), *self.eq_controls):
            if isinstance(control, CommitSlider):
                control.cancel_edit()
        self.apply_edit_values()

    def submit(self, action, name='', value=False, band=None, payload=None, automatic=False):
        if self.updating and action != 'dongle-update':
            return False
        if self.closing:
            return False
        device = self.device_picker.currentData()
        if action == 'connect' and device is None:
            return False
        if action in ('refresh', 'poll', 'write', 'mode', 'preset', 'rename', 'connect-peer') and self.snapshot is None:
            return False
        if action == 'location-sample' and (not isinstance(payload, LocationIntent) or payload.location_generation != self.location_generation):
            return False
        if action == 'preset' and (not self.preset_ready() or not isinstance(payload, EqPreset)
                or not payload.matches(self.snapshot.model, tuple(self.snapshot.eq_frequencies))):
            return False
        if action == 'rename' and 'rename' not in self.snapshot.writable:
            return False
        if action == 'connect-peer' and (not self.peer_ready() or payload != self.peer_picker.currentData()):
            return False
        if action == 'connect':
            self.choose_address(device.address)
        if action.startswith('dongle-'):
            device = self.dongle_picker.currentData()
            if device is None:
                return False
        request = Request(action, self.generation, device, name, value, band, payload,
                          self.snapshot_eq_topology(self.snapshot) if action == 'write' and name == 'eq' else None)
        editing = action in ('write', 'mode')
        if editing and not self.edit_available(request):
            return False
        if self.busy:
            if (not editing or self.active_request is None
                    or self.active_request.action not in ('write', 'mode', 'poll', 'location-sample')):
                return False
            key = self.edit_key(request)
            if key not in self.pending_edits and len(self.pending_edits) >= MAX_PENDING_EDITS:
                self.pending_notice = 'Pending edit limit reached.'
                self.apply_edit_values()
                self.update_enabled()
                return False
            if not automatic:
                self.user_edit_revision += 1
            self.pending_edits.pop(key, None)
            self.pending_edits[key] = request
            self.desired_edits[key] = request
            self.apply_edit_values()
            self.update_enabled()
            return True
        if action in ('discover', 'refresh', 'dongle-status', 'dongle-check', 'dongle-codec'):
            self.update_plan = None
        if not automatic and action in ('write', 'mode', 'preset', 'rename', 'connect-peer'):
            self.user_edit_revision += 1
        self.pending_notice = ''
        self.start_request(request)
        return True

    def start_request(self, request):
        if request.action not in ('write', 'mode', 'poll', 'location-sample'):
            self.cancel_location()
        if request.action == 'location-sample':
            self.location_active = True
        self.busy = True
        self.active_request = request
        self.request_started = self.clock()
        if request.action == 'project-release':
            self.project_checked = True
        if request.action in ('write', 'mode'):
            self.desired_edits[self.edit_key(request)] = request
        messages = {'discover': 'Searching for paired headsets…', 'connect': 'Connecting and reading settings…', 'refresh': 'Refreshing settings…', 'write': 'Applying and verifying setting…', 'mode': 'Applying and verifying noise mode…', 'dongle-codec': 'Applying and verifying Bluetooth codec…', 'dongle-status': 'Reading USB audio status…', 'dongle-check': 'Checking official dongle firmware…', 'dongle-update': 'Updating dongle firmware…'}
        self.message.setText(messages.get(request.action, self.tr('Working and verifying…')))
        self.message.setVisible(request.action in ('discover', 'connect'))
        self.update_enabled()
        self.requested.emit(request)

    def drain_pending_edits(self):
        while self.pending_edits and not self.busy and not self.closing:
            key = next(iter(self.pending_edits))
            request = self.pending_edits.pop(key)
            if self.edit_available(request):
                self.start_request(request)
                return
            if self.desired_edits.get(key) is request:
                self.desired_edits.pop(key)
            self.pending_notice = 'Queued setting unavailable; not applied.'
        self.apply_edit_values()

    def select_noise_mode(self, mode):
        self.apply_noise_state()
        return self.submit('mode', name=mode)

    def write(self, name, value, band=None):
        return self.submit('write', name, value, band)

    def presented_value(self, name, band, actual):
        request = self.desired_edits.get(('write', name, band))
        return request.value if request is not None and self.edit_available(request) else actual

    def apply_edit_values(self):
        if self.snapshot is None:
            return
        for key, control in self.controls.items():
            if key not in self.snapshot.settings:
                continue
            available = key in self.snapshot.writable
            if isinstance(control, CommitSlider) and control.editing and available:
                continue
            blocker = QSignalBlocker(control)
            if isinstance(control, CommitSlider) and not available:
                control.editing = False
                control.held_keys.clear()
                control.mouse_editing = False
                control.gesture_dirty = False
                control.gesture_committed = False
                control.setSliderDown(False)
            value = self.presented_value(key, None, self.snapshot.settings[key])
            if isinstance(control, QCheckBox):
                control.setChecked(bool(value))
            elif isinstance(control, QComboBox):
                control.setCurrentIndex(control.findData(value))
            else:
                control.setValue(100 - int(value) if key == 'anc_strength' else int(value))
            del blocker
        self.transparency_label.setText(f'Transparency amount  ·  {self.controls["transparency_level"].value()}')
        self.strength_label.setText(f'ANC strength  ·  {self.controls["anc_strength"].value()}')
        for band, control in enumerate(self.eq_controls):
            if band >= len(self.snapshot.eq_bands):
                continue
            available = 'eq' in self.snapshot.writable
            if not (control.editing and available):
                blocker = QSignalBlocker(control)
                if not available:
                    control.editing = False
                    control.held_keys.clear()
                    control.mouse_editing = False
                    control.gesture_dirty = False
                    control.gesture_committed = False
                    control.setSliderDown(False)
                control.setValue(self.presented_value('eq', band, self.snapshot.eq_bands[band]))
                del blocker
            self.eq_gain_labels[band].setText(f'{control.value():+.1f}')

    @Slot(int, str)
    def connection_lost(self, generation, error):
        if self.closing or generation != self.generation:
            return
        self.snapshot = None
        self.clear_pending_edits()
        self.message.setText(error)
        self.message.show()
        self.update_enabled()

    @Slot(object, object, str)
    def completed(self, request, result, error):
        if request.action == 'location-close':
            if error and not self.closing:
                self.zone_notice.setText(self.tr(error))
            return
        if request.action == 'close':
            self.record_event(request.action, error)
            if error:
                self.message.setText(error)
            self.thread.quit()
            return
        if self.closing:
            return
        self.record_event(request.action, error)
        self.busy = False
        self.active_request = None
        if request.action == 'location-sample':
            self.complete_location(request.payload, result, error)
            self.drain_pending_edits()
            self.update_enabled()
            return
        if request.action == 'preset':
            self.preset_notice.setText(self.tr('Preset change incomplete; refresh before continuing.') if error else self.tr('Full curve applied and verified.'))
            self.preset_notice.show()
        if request.action == 'project-release':
            self.release_label.setText(self.tr('Project release check failed.') if error else
                self.tr('Upstream Windows project release:') + f' {result.tag}\n{result.url}\n' + self.tr('These releases do not update this Linux companion.'))
            self.update_enabled()
            return
        if request.action in ('write', 'mode'):
            key = self.edit_key(request)
            if self.desired_edits.get(key) is request:
                self.desired_edits.pop(key)
        if request.action.startswith('dongle-'):
            self.updating = False
            if self.device_key(request.device, 'dongle') != self.device_key(self.dongle_picker.currentData(), 'dongle'):
                self.update_plan = None
                self.dongle_update_label.setText('Dongle selection changed. Check again.')
            elif error:
                self.update_plan = None
                if request.action in ('dongle-status', 'dongle-update', 'dongle-codec'):
                    self.clear_dongle_status()
                    self.dongle_status_label.setText('BTD 700 — status unavailable')
                self.dongle_update_label.setText(error)
                if request.action in ('dongle-status', 'dongle-codec'):
                    self.codec_notice.setText(error)
            elif request.action in ('dongle-status', 'dongle-codec'):
                if self.device_key(result.device, 'dongle') != self.device_key(request.device, 'dongle'):
                    self.clear_dongle_status()
                    self.dongle_update_label.setText('Status returned a different dongle; refresh again.')
                else:
                    self.apply_dongle_status(result)
            elif request.action == 'dongle-check':
                self.update_plan = result if self.device_key(result.device, 'dongle') == self.device_key(request.device, 'dongle') else None
                if self.update_plan is None:
                    self.dongle_update_label.setText('Update check returned a different dongle; check again.')
                else:
                    self.dongle_update_label.setText(f'Current {result.current.firmware} · Latest {result.target}\n{result.reason}\nNative transfer is experimental; hardware flashing and recovery are unverified.')
            else:
                self.update_plan = None
                self.clear_dongle_status()
                self.dongle_status_label.setText('BTD 700 — refresh audio status')
                self.dongle_update_label.setText(f'Firmware update verified: {result.firmware}. Refresh audio status.')
            self.update_enabled()
            return
        if request.generation != self.generation:
            self.clear_pending_edits()
            self.message.setText('Device selection changed. Connect controls to read the selected headset.')
            self.message.show()
        elif error:
            self.cancel_location()
            self.clear_pending_edits()
            self.snapshot = None
            self.status.setText('Headset controls — disconnected')
            self.detail.setText('Connection unavailable')
            self.battery.setText('—')
            self.message.setText(error)
            self.message.show()
            self.gear.setChecked(True)
        elif request.action == 'discover':
            old_headset = self.device_key(self.device_picker.currentData())
            old_dongle = self.device_key(self.dongle_picker.currentData(), 'dongle')
            for picker, kind, devices, old in ((self.device_picker, 'headset', result['headsets'], old_headset),
                    (self.dongle_picker, 'dongle', result['dongles'], old_dongle)):
                blocker = QSignalBlocker(picker)
                picker.clear()
                selected = -1
                for index, device in enumerate(devices):
                    picker.addItem(f'{device.name} · {device.address}' if kind == 'headset' else f'BTD 700 {index + 1}', device)
                    if self.device_key(device, kind) == old:
                        selected = index
                picker.setCurrentIndex(selected if self.discovered_once else 0 if devices else -1)
                del blocker
            if old_headset != self.device_key(self.device_picker.currentData()):
                self.selection_changed(explicit=False)
            self.discovered_once = True
            self.dongle_picker.setVisible(len(result['dongles']) > 1)
            self.clear_dongle_status()
            self.update_plan = None
            self.dongle_firmware_label.setText('Firmware unavailable')
            self.dongle_update_label.setText('Manual official update check. Native transfer is experimental; hardware flashing and recovery are unverified.')
            status = result['dongle_status']
            if status is not None and self.device_key(status.device, 'dongle') == self.device_key(self.dongle_picker.currentData(), 'dongle'):
                self.apply_dongle_status(status)
            else:
                self.dongle_status_label.setText('BTD 700 — ' + result['dongle_error'] if result['dongle_error'] else 'Select a BTD 700 to read audio status')
            self.rebuild_device_cards()
            self.message.setText(result['headset_error'] or ('Select a headset and Connect controls.' if result['headsets'] else 'No paired headset found. Enable Bluetooth, power on the headset, and pair it in system settings.'))
            self.message.setVisible(bool(result['headset_error']) or not result['headsets'])
        else:
            restore = request.action == 'reconnect' and request.payload is True
            if request.action == 'reconnect':
                if request.name != self.explicit_address:
                    self.update_enabled()
                    return
                device = result['device']
                blocker = QSignalBlocker(self.device_picker)
                index = next((index for index in range(self.device_picker.count()) if self.device_picker.itemData(index).address == device.address), -1)
                if index < 0:
                    self.device_picker.addItem(f'{device.name} · {device.address}', device)
                    index = self.device_picker.count() - 1
                self.device_picker.setItemData(index, device)
                self.device_picker.setCurrentIndex(index)
                del blocker
                result = result['snapshot']
            self.apply_snapshot(result)
            self.last_refresh = self.clock()
            if restore and self.preferences.restore_transparency and result.settings.get('anc_enabled') is True and mode_available(result, 'transparency'):
                self.submit('write', 'transparency', True)
            self.message.setText(self.tr('Settings read from the headset.') if request.action not in ('write', 'mode', 'preset', 'rename', 'connect-peer') else self.tr('Setting applied and verified.'))
            self.message.hide()
            if request.action == 'connect':
                self.gear.setChecked(False)
        self.complete_sound_check(request, error)
        if request.action in ('write', 'mode', 'poll') and not error and request.generation == self.generation:
            self.drain_pending_edits()
        self.update_enabled()

    def dongle_selection_changed(self):
        self.update_plan = None
        self.clear_dongle_status()
        self.dongle_firmware_label.setText('Firmware unavailable')
        self.dongle_quality_label.setText('')
        self.dongle_update_label.setText('Selection changed; check for updates again.')
        self.submit('dongle-status')
        self.update_enabled()

    def apply_dongle_status(self, status):
        from .dongle import Codec
        device = self.dongle_picker.currentData()
        if device is None or self.device_key(status.device, 'dongle') != self.device_key(device, 'dongle'):
            return
        blocker = QSignalBlocker(self.dongle_picker)
        self.dongle_picker.setItemData(self.dongle_picker.currentIndex(), status.device)
        del blocker
        if self.update_plan is not None and self.update_plan.device != status.device:
            self.update_plan = None
        requested = self.codec_picker.currentData()
        self.dongle_status = status
        blocker = QSignalBlocker(self.codec_picker)
        self.codec_picker.clear()
        for codec in Codec:
            if getattr(status, 'supported_codec_mask', 0) & int(codec):
                self.codec_picker.addItem(CODEC_NAMES[int(codec)], codec)
        wanted = requested if requested is not None else status.codec_mask
        index = self.codec_picker.findData(wanted)
        self.codec_picker.setCurrentIndex(index if index >= 0 else 0)
        del blocker
        self.codec_notice.setText('Switching codec can briefly reconnect audio.' if status.state in (2, 3) and status.audio_mode == 0 else
            'Codec changes require a standard connected audio mode; stop calls or broadcast first.')
        self.dongle_status_label.setText(f'BTD 700 — {dongle_audio_state(status.state)}')
        self.dongle_firmware_label.setText(f'Firmware {status.firmware}')
        codec = {1: 'SBC', 2: 'aptX Classic', 4: 'aptX Adaptive', 8: 'aptX Lossless',
                 16: 'aptX Lite (QMAP)', 32: 'LC3'}.get(status.codec_mask, 'Unknown codec')
        quality = [codec]
        if status.sample_bits:
            quality.append(f'{status.sample_bits} bit')
        if status.sample_rate:
            quality.append(f'{status.sample_rate} Hz')
        self.dongle_quality_label.setText(' · '.join(quality))

    def prepared_update_ready(self):
        device = self.dongle_picker.currentData()
        return (self.update_plan is not None and self.update_plan.available
                and self.update_plan.device == device
                and self.dongle_status is not None and self.dongle_status.device == device
                and self.dongle_status.state in (0, 1, 2))

    def start_dongle_update(self):
        if self.busy or self.closing or not self.prepared_update_ready():
            return False
        plan = self.update_plan
        answer = QMessageBox.question(self, 'Confirm experimental BTD 700 update',
            f'Update BTD 700 from {plan.current.firmware} to {plan.target}?\n\n'
            'The experimental native transfer interrupts audio and reboots the dongle. '
            'Rollback is not implemented. Interrupted-update recovery has not been hardware-verified. '
            'Keep the dongle connected and the app open until verification finishes.',
            QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No,
            QMessageBox.StandardButton.No)
        if answer != QMessageBox.StandardButton.Yes or not self.prepared_update_ready():
            return False
        self.page_kind = 'dongle'
        self.section = 'firmware'
        self.render_navigation()
        self.updating = True
        if not self.submit('dongle-update', payload=plan):
            self.updating = False
            return False
        return True

    @Slot(str, int)
    def dongle_progress(self, stage, percent):
        if self.updating:
            self.dongle_update_label.setText(f'Firmware update: {stage} · {percent}%')

    def apply_noise_state(self):
        mode = noise_mode(self.snapshot)
        self.noise_group.setExclusive(False)
        for key, button in self.noise_buttons.items():
            blocker = QSignalBlocker(button)
            button.setChecked(key == mode)
            del blocker
        self.noise_group.setExclusive(True)
        strength_visible = mode == 'anc' and self.snapshot is not None and 'anc_strength' in self.snapshot.settings
        focused = QApplication.focusWidget()
        if not strength_visible and focused is not None and self.strength_box.isAncestorOf(focused):
            self.scroll.setFocus(Qt.FocusReason.OtherFocusReason)
        self.strength_box.setVisible(strength_visible)
        transparency_visible = self.snapshot is not None and 'transparency_level' in self.snapshot.settings
        focused = QApplication.focusWidget()
        if not transparency_visible and focused is not None and self.transparency_box.isAncestorOf(focused):
            self.scroll.setFocus(Qt.FocusReason.OtherFocusReason)
        self.transparency_box.setVisible(transparency_visible)
        self.transparency_hint.setText('Enable noise first to change transparency level.' if
            self.snapshot is not None and self.snapshot.settings.get('anc_enabled') is not True else
            'Changes briefly pass through full transparency.' if self.transparency_level_available() else
            'Unavailable: complete verified noise controls are required.')
        self.noise_hint.setText({
            'transparency': 'Ambient sound is audible (Transparent Hearing).',
            'anc': 'Active noise cancellation. 100 is strongest.',
            'adaptive': 'Noise cancellation adapts to your surroundings.',
            'off': 'Noise cancellation and transparency are off.',
        }.get(mode, 'Connect controls to read noise settings.'))

    @staticmethod
    def snapshot_eq_topology(snapshot):
        return tuple(snapshot.eq_frequencies[band] if band < len(snapshot.eq_frequencies) else None
                     for band in range(len(snapshot.eq_bands)))

    def apply_snapshot(self, snapshot):
        self.snapshot = snapshot
        model = snapshot.model
        self.title.setText(self.tr('App settings') if self.page_kind == 'settings' else self.tr('Local Sound Zones') if self.page_kind == 'zones' else self.tr('My devices') if self.page_kind == 'home' else 'BTD 700' if self.page_kind == 'dongle' else 'MOMENTUM 5' if model.startswith('M5AEBT') else 'MOMENTUM 4' if model.startswith('M4AEBT') or model == 'MOMENTUM 4' else 'Momentum Control')
        self.status.setText('Headset controls — connected')
        self.detail.setText(f'{model} · Firmware {snapshot.firmware}')
        self.battery.setText('—' if snapshot.battery is None else f'{snapshot.battery} %')
        for key, control in self.controls.items():
            if isinstance(control, QComboBox):
                defaults = (0, 1, 2) if key == 'sound_mode' else tuple(control.itemData(index) for index in range(control.count()))
                choices = snapshot.choices.get(key, defaults)
                for index in range(control.count()):
                    control.model().item(index).setEnabled(control.itemData(index) in defaults and control.itemData(index) in choices)
        topology = self.snapshot_eq_topology(snapshot)
        if topology != self.eq_topology:
            if any(control.hasFocus() for control in self.eq_controls):
                self.scroll.setFocus(Qt.FocusReason.OtherFocusReason)
            while self.eq_grid.count():
                item = self.eq_grid.takeAt(0)
                if item.widget() is not None:
                    item.widget().deleteLater()
            self.eq_controls.clear()
            self.eq_labels.clear()
            self.eq_gain_labels.clear()
            self.eq_topology = topology
            for band, gain in enumerate(snapshot.eq_bands):
                column = QWidget()
                band_layout = QVBoxLayout(column)
                band_layout.setContentsMargins(0, 0, 0, 0)
                band_layout.setSpacing(6)
                gain_label = self.label(f'{gain:+.1f}')
                gain_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
                gain_label.setStyleSheet('font-size: 11px;')
                gain_label.setSizePolicy(QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Preferred)
                band_layout.addWidget(gain_label)
                control = GainSlider()
                control.setAccessibleName(f'Equalizer band {band + 1} gain')
                control.setValue(gain)
                control.valueChanged.connect(lambda value, label=gain_label, control=control: label.setText(f'{control.value():+.1f}'))
                control.committed.connect(lambda band=band, control=control: self.write('eq', control.value(), band=band))
                control.editing_changed.connect(self.update_edit_status)
                control.edit_cancelled.connect(self.apply_edit_values)
                band_layout.addWidget(control, 0, Qt.AlignmentFlag.AlignHCenter)
                frequency = snapshot.eq_frequencies[band] if band < len(snapshot.eq_frequencies) else None
                text = f'{frequency / 1000:g}k' if frequency is not None and frequency >= 1000 else str(frequency) if frequency is not None else f'Band {band + 1}'
                frequency_label = self.label(text, muted=True)
                frequency_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
                frequency_label.setStyleSheet('color: #a0a0a0; font-size: 11px;')
                frequency_label.setSizePolicy(QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Preferred)
                band_layout.addWidget(frequency_label)
                column.setMinimumWidth(28)
                self.eq_grid.addWidget(column, band // 8, band % 8)
                self.eq_controls.append(control)
                self.eq_labels.append(frequency_label)
                self.eq_gain_labels.append(gain_label)
        for key in ('immersive_audio', 'head_tracking', 'battery_protection', 'auto_power_off', 'sidetone', 'pause_on_transparency'):
            self.controls[key].parentWidget().setVisible(key in snapshot.settings)
        self.spatial_hint.setVisible('immersive_audio' in snapshot.settings or 'head_tracking' in snapshot.settings)
        self.sound_check_button.setVisible('sound_mode' in snapshot.settings)
        self.render_presets()
        self.render_metadata()
        self.last_full_transparency = (noise_mode(snapshot) == 'transparency'
            and snapshot.settings.get('transparency_level', 100) == 100)
        if hasattr(self, 'tray'):
            self.tray.setToolTip('Momentum Control · ' + ('—' if snapshot.battery is None else f'{snapshot.battery}%'))
        self.apply_edit_values()
        self.eq_hint.setText('EQ gains are audible in Equalizer mode.' if self.eq_controls and snapshot.settings.get('sound_mode') != 1 else '' if self.eq_controls else 'Equalizer unavailable on this model or firmware.')
        self.eq_hint.setVisible(bool(self.eq_hint.text()))
        notes = list(snapshot.notes)
        if not snapshot.writable:
            notes.insert(0, 'Read-only: this model or firmware has no verified write commands.')
        notes.extend(('Unavailable controls require verified write support.', 'BTD 700 carries USB audio; the PC Bluetooth connection provides control.', 'CLI: momentumctl --help. Closing this window quits the app.'))
        self.capabilities.setText('\n'.join(notes))
        readonly = [label for key, label in (('on_head_detection', 'On-head detection'), ('auto_answer', 'auto-answer')) if key in snapshot.settings and key not in snapshot.writable]
        self.firmware_hint.setText(f'Read-only on firmware {snapshot.firmware}: {", ".join(readonly)}.' if readonly else 'Unavailable controls are disabled for this model or firmware.')
        self.apply_noise_state()

    def update_enabled(self):
        ready = not self.busy and not self.closing
        editable = not self.closing and (not self.busy or self.active_request is not None
                    and self.active_request.action in ('write', 'mode', 'poll', 'location-sample'))
        self.update_edit_status()
        self.set_enabled(self.back_button, not self.updating and not self.closing)
        self.set_enabled(self.home_refresh, ready)
        for button in self.device_cards:
            self.set_enabled(button, ready and not self.pending_edits)
        for button in self.section_buttons.values():
            self.set_enabled(button, not self.updating and not self.closing)
        self.set_enabled(self.codec_picker, ready and self.codec_ready())
        self.set_enabled(self.codec_apply_button, ready and self.codec_ready())
        self.set_enabled(self.dongle_picker, ready)
        detected = self.dongle_picker.currentData() is not None
        self.set_enabled(self.dongle_refresh_button, ready and detected)
        self.set_enabled(self.dongle_check_button, ready and detected)
        self.set_enabled(self.dongle_update_button, ready and self.prepared_update_ready())
        self.dongle_update_button.setToolTip('Stop playback/calls and check for a newer compatible official update.' if not self.prepared_update_ready() else 'Experimental native update; hardware flashing and recovery unverified.')
        self.set_enabled(self.device_picker, ready)
        self.set_enabled(self.search_button, ready)
        self.set_enabled(self.connect_button, ready and self.device_picker.currentData() is not None)
        self.set_enabled(self.refresh_button, ready and self.snapshot is not None)
        for key, control in self.controls.items():
            available = self.snapshot is not None and key in self.snapshot.settings and key in self.snapshot.writable
            if key == 'head_tracking':
                available = available and self.snapshot.settings.get('immersive_audio') is True and self.snapshot.settings.get('sound_mode') != 3
            if key == 'immersive_audio' and self.snapshot is not None:
                available = available and self.snapshot.settings.get('sound_mode') != 3
            if key == 'transparency_level':
                available = available and self.transparency_level_available()
            self.set_enabled(control, editable and available)
            control.setToolTip('' if available else 'Read-only or unavailable on this model and firmware.')
        for mode, button in self.noise_buttons.items():
            available = mode_available(self.snapshot, mode)
            self.set_enabled(button, editable and available)
            button.setToolTip('' if available else 'Unavailable: all settings needed for this mode must support verified writes.')
        for control in self.eq_controls:
            self.set_enabled(control, editable and self.snapshot is not None and 'eq' in self.snapshot.writable)
        preset_ready = self.preset_ready()
        for button in (self.preset_save_button, self.preset_apply_button, self.preset_delete_button):
            self.set_enabled(button, preset_ready)
        self.set_enabled(self.preset_picker, preset_ready)
        self.set_enabled(self.sound_check_button, preset_ready and self.snapshot.settings.get('sound_mode') == 1
            if self.snapshot is not None else False)
        if self.sound_check_dialog is not None:
            self.sound_check_dialog.update_snapshot(self.snapshot, self.busy)
        self.set_enabled(self.rename_button, ready and self.snapshot is not None and 'rename' in self.snapshot.writable)
        self.set_enabled(self.name_editor, ready and self.snapshot is not None and 'rename' in self.snapshot.writable)
        self.set_enabled(self.peer_button, ready and self.peer_ready())
        self.set_enabled(self.release_button, ready)
        self.set_enabled(self.export_button, ready and self.snapshot is not None)
        self.set_enabled(self.settings_button, not self.updating and not self.closing)
        self.set_enabled(self.zones_button, not self.updating and not self.closing)
        self.set_enabled(self.zone_enabled_control, not self.updating and not self.closing)
        self.set_enabled(self.zone_save_button, self.preset_ready())
        self.set_enabled(self.zone_delete_button, ready and not self.pending_edits)
        self.set_enabled(self.zone_location_button, ready and not self.updating)
        self.set_enabled(self.zone_cancel_button, self.manual_location_intent is not None and not self.closing)
        if hasattr(self, 'tray_mode_actions'):
            for mode, action in self.tray_mode_actions.items():
                action.setEnabled(editable and mode_available(self.snapshot, mode))
            self.tray_refresh_action.setEnabled(ready and self.snapshot is not None)
        self.apply_noise_state()

    def update_edit_status(self):
        drafts = any(control.editing for control in (*self.controls.values(), *self.eq_controls)
                     if isinstance(control, CommitSlider))
        applying = self.busy and self.active_request is not None and self.active_request.action in ('write', 'mode')
        if applying:
            text = f'Applying · {len(self.pending_edits)} pending' if self.pending_edits else 'Applying and verifying…'
            if drafts:
                text += ' · editing'
        elif drafts:
            text = 'Editing · release to apply'
        else:
            text = self.pending_notice
        self.edit_status.setText(self.tr(text))
        self.edit_status.setToolTip(f'{text} {self.pending_notice}'.strip())

    def set_enabled(self, widget, enabled):
        # Disabling a focused descendant otherwise makes QScrollArea follow tab focus.
        if not enabled and widget.hasFocus():
            self.scroll.setFocus(Qt.FocusReason.OtherFocusReason)
        widget.setEnabled(enabled)

    def closeEvent(self, event: QCloseEvent):
        if not self.thread.isRunning():
            event.accept()
            return
        event.ignore()
        if self.updating:
            self.message.setText('Firmware update is active. Keep the dongle connected; close after the operation finishes.')
            self.message.show()
            return
        if self.closing:
            return
        if not self.force_quit and self.preferences.close_to_tray and self.tray.isVisible() and self.tray_available():
            self.hide()
            return
        self.timer.stop()
        self.cancel_location()
        self.closing = True
        self.clear_pending_edits()
        self.message.setText('Closing Bluetooth connection…')
        self.update_enabled()
        try:
            self.worker.cancel()
        except Exception as error:
            self.message.setText(f'Cancellation failed: {error}. Closing connection…')
        self.requested.emit(Request('close', self.generation))

    @Slot()
    def worker_stopped(self):
        self.tray.hide()
        self.close()
        if self.force_quit or self.preferences.tray_enabled:
            QApplication.instance().quit()


def main(argv=None, minimized=False):
    # Keep Qt GUI events out of the default GLib context pumped by the Bluetooth worker.
    os.environ['QT_NO_GLIB'] = '1'
    app = QApplication(argv or [])
    app.setApplicationName('Momentum Control')
    app.setDesktopFileName('momentum-control')
    window = MainWindow()
    window.show_startup(minimized=minimized)
    return app.exec()
