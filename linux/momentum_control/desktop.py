import io
import json
import math
import os
from pathlib import Path
import re
import zipfile

from . import __version__
from .preferences import PreferenceError, _atomic_write, _parent_directory, _read_bounded, _require, _validate_bands


_AUTOSTART_PREFIX = '[Desktop Entry]\nType=Application\nName=Momentum Control\n'
_AUTOSTART_SUFFIX = '\nTerminal=false\nX-Momentum-Control-Managed=true\n'
_BOOLEAN_SETTINGS = frozenset(('anc_enabled', 'transparency', 'adaptive_anc', 'touch_controls', 'smart_pause', 'on_head_detection', 'auto_answer', 'comfort_call', 'bass_boost',
                               'pause_on_transparency', 'immersive_audio', 'head_tracking', 'battery_protection'))
_NUMERIC_SETTINGS = {'anc_strength': 100, 'transparency_level': 100, 'antiwind': 2, 'audio_prompts': 2, 'sound_mode': 3,
                     'auto_power_off': 65535, 'sidetone': 5}
_SETTING_NAMES = _BOOLEAN_SETTINGS | _NUMERIC_SETTINGS.keys()
_OPERATION_NAMES = frozenset(('discover', 'connect', 'refresh', 'write', 'mode', 'dongle-status', 'dongle-codec', 'dongle-check', 'dongle-update', 'close',
                              'poll', 'preset', 'rename', 'connect-peer', 'project-release', 'location-sample', 'reconnect'))
_STATUS_NAMES = frozenset(('ok', 'failed', 'cancelled'))


def _managed_autostart(content: bytes) -> None:
    try:
        text = content.decode('utf-8')
    except UnicodeError as error:
        raise PreferenceError('Invalid autostart document') from error
    _require(text.startswith(_AUTOSTART_PREFIX) and text.endswith(_AUTOSTART_SUFFIX), 'Autostart file is not managed by Momentum Control')
    command = text[len(_AUTOSTART_PREFIX):-len(_AUTOSTART_SUFFIX)]
    _require(command.startswith('Exec="/') and '\n' not in command and '\r' not in command and '\0' not in command, 'Invalid managed autostart command')


def _quoted_launcher(launcher: Path) -> str:
    launcher = Path(launcher)
    value = str(launcher)
    _require(launcher.is_absolute() and 1 <= len(value) <= 4096 and value.isprintable(), 'Invalid launcher path')
    escaped = value.replace('\\', '\\\\\\\\')
    for character in ('"', '`', '$'):
        escaped = escaped.replace(character, '\\\\' + character)
    return '"' + escaped.replace('%', '%%') + '"'


def set_autostart(path: Path, launcher: Path, enabled: bool, minimized: bool = False) -> None:
    _require(type(enabled) is bool and type(minimized) is bool, 'Invalid autostart flags')
    command = _quoted_launcher(launcher)
    content = (_AUTOSTART_PREFIX + 'Exec=' + command + (' --minimized' if minimized else '') + _AUTOSTART_SUFFIX).encode('utf-8')
    _require(len(content) <= 8192, 'Autostart file exceeds size limit')
    path = Path(path)
    try:
        with _parent_directory(path, create=enabled) as directory:
            existing = _read_bounded(directory, path.name, 8192)
            if existing is not None:
                _managed_autostart(existing)
            if enabled:
                _atomic_write(directory, path.name, content)
            elif existing is not None:
                os.unlink(path.name, dir_fd=directory)
    except FileNotFoundError as error:
        if enabled:
            raise PreferenceError('Cannot enable autostart') from error
    except OSError as error:
        raise PreferenceError('Cannot change autostart') from error


def autostart_enabled(path: Path) -> bool:
    path = Path(path)
    try:
        with _parent_directory(path) as directory:
            content = _read_bounded(directory, path.name, 8192)
        if content is None:
            return False
        _managed_autostart(content)
        return True
    except FileNotFoundError:
        return False
    except OSError as error:
        raise PreferenceError('Cannot read autostart') from error


def _diagnostic_settings(snapshot) -> dict:
    _require(type(snapshot.settings) is dict, 'Invalid diagnostic settings')
    settings = {}
    for name in sorted(_SETTING_NAMES):
        if name not in snapshot.settings:
            continue
        value = snapshot.settings[name]
        if name in _BOOLEAN_SETTINGS:
            _require(type(value) is bool, 'Invalid diagnostic boolean setting')
        else:
            _require(type(value) is int and 0 <= value <= _NUMERIC_SETTINGS[name], 'Invalid diagnostic numeric setting')
        settings[name] = value
    return settings


def _diagnostic_events(events: tuple[dict, ...]) -> list[dict]:
    _require(type(events) is tuple and len(events) <= 2000, 'Invalid diagnostic event count')
    result = []
    for event in events:
        _require(type(event) is dict and {'operation', 'status', 'elapsed_ms'} <= event.keys(), 'Invalid diagnostic event')
        operation, status, elapsed = event['operation'], event['status'], event['elapsed_ms']
        _require(type(operation) is str and operation in _OPERATION_NAMES, 'Invalid diagnostic operation')
        _require(type(status) is str and status in _STATUS_NAMES, 'Invalid diagnostic status')
        _require(type(elapsed) in (int, float) and 0 <= elapsed <= 300000 and math.isfinite(elapsed), 'Invalid diagnostic elapsed time')
        result.append({'operation': operation, 'status': status, 'elapsed_ms': elapsed})
    return result


def _diagnostic_document(snapshot, events: tuple[dict, ...]) -> dict:
    model = snapshot.model
    family = 'unknown'
    if type(model) is str and len(model) <= 128:
        match = re.fullmatch(r'(M[45])AEBT(?: [A-Za-z0-9 -]+)?', model)
        if match is not None:
            family = match[1]
    firmware = snapshot.firmware
    _require(type(firmware) is str and len(firmware) <= 17 and re.fullmatch(r'[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}', firmware) is not None, 'Invalid diagnostic firmware')
    components = [int(component) for component in firmware.split('.')]
    _require(all(component <= 65535 for component in components), 'Invalid diagnostic firmware')
    battery = snapshot.battery
    _require(battery is None or type(battery) is int and 0 <= battery <= 100, 'Invalid diagnostic battery')
    _validate_bands(snapshot.eq_frequencies, snapshot.eq_bands, allow_empty=True)
    settings = _diagnostic_settings(snapshot)
    _require(type(snapshot.writable) is frozenset, 'Invalid diagnostic writable settings')
    writable_names = set(settings)
    if snapshot.eq_bands:
        writable_names.add('eq')
    writable = sorted(name for name in writable_names if name in snapshot.writable)
    return {'app': {'platform': 'linux', 'version': __version__}, 'model_family': family,
            'firmware': components, 'battery': battery, 'settings': settings,
            'eq_gains': snapshot.eq_bands, 'eq_frequencies': snapshot.eq_frequencies,
            'writable': writable, 'events': _diagnostic_events(events)}


def export_diagnostics(path: Path, snapshot, events: tuple[dict, ...] = ()) -> None:
    document = _diagnostic_document(snapshot, events)
    content = json.dumps(document, allow_nan=False, ensure_ascii=True, separators=(',', ':')).encode('utf-8')
    _require(len(content) <= 1024 * 1024, 'Diagnostics exceed size limit')
    archive_bytes = io.BytesIO()
    with zipfile.ZipFile(archive_bytes, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
        archive.writestr('diagnostics.json', content)
    path = Path(path)
    try:
        with _parent_directory(path, create=True) as directory:
            _atomic_write(directory, path.name, archive_bytes.getvalue())
    except OSError as error:
        raise PreferenceError('Cannot export diagnostics') from error
