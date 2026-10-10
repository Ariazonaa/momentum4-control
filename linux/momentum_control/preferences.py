from contextlib import contextmanager
from dataclasses import asdict, dataclass, fields
import json
import math
import os
from pathlib import Path
import re
import secrets
import stat
import unicodedata


class PreferenceError(ValueError):
    pass


@dataclass(frozen=True)
class Preferences:
    refresh_seconds: int = 30
    auto_reconnect: bool = False
    tray_enabled: bool = False
    close_to_tray: bool = False
    start_minimized: bool = False
    restore_transparency: bool = False
    check_app_updates: bool = False
    language: str = 'en'
    last_device_address: str = ''


@dataclass(frozen=True)
class EqPreset:
    name: str
    model: str
    frequencies: tuple[int, ...]
    gains: tuple[float, ...]

    def matches(self, model: str, frequencies: tuple[int, ...]) -> bool:
        return (type(frequencies) is tuple and all(type(value) is int for value in frequencies)
                and self.model == model and self.frequencies == frequencies)


@dataclass(frozen=True)
class AppData:
    preferences: Preferences
    presets: tuple[EqPreset, ...]


_MAX_BYTES = 256 * 1024
_PREFERENCE_FIELDS = frozenset(field.name for field in fields(Preferences))
_BOOLEAN_FIELDS = _PREFERENCE_FIELDS - {'refresh_seconds', 'language', 'last_device_address'}
_PRESET_FIELDS = frozenset(('name', 'model', 'frequencies', 'gains'))


def _require(condition: bool, message: str) -> None:
    if not condition:
        raise PreferenceError(message)


def _printable(value, limit: int) -> bool:
    return type(value) is str and 1 <= len(value) <= limit and value.isprintable() and bool(value.strip())


def _validate_preferences(preferences: Preferences) -> None:
    _require(type(preferences) is Preferences, 'Invalid preferences')
    _require(type(preferences.refresh_seconds) is int and preferences.refresh_seconds in (0, 10, 30, 60), 'Invalid refresh interval')
    for name in _BOOLEAN_FIELDS:
        _require(type(getattr(preferences, name)) is bool, 'Invalid boolean preference')
    _require(type(preferences.language) is str and preferences.language in ('en', 'de'), 'Invalid language')
    address = preferences.last_device_address
    _require(type(address) is str and (address == '' or re.fullmatch(r'(?:[0-9A-F]{2}:){5}[0-9A-F]{2}', address) is not None), 'Invalid device address')


def _validate_bands(frequencies, gains, allow_empty: bool = False) -> None:
    _require(type(frequencies) is tuple and type(gains) is tuple, 'Invalid EQ sequences')
    _require((0 if allow_empty else 1) <= len(frequencies) <= 16 and len(frequencies) == len(gains), 'Invalid EQ band count')
    for frequency in frequencies:
        _require(type(frequency) is int and 1 <= frequency <= 48000, 'Invalid EQ frequency')
    for gain in gains:
        _require(type(gain) in (int, float) and -6 <= gain <= 6 and math.isfinite(gain), 'Invalid EQ gain')
        _require(math.isclose(gain * 10, round(gain * 10), abs_tol=1e-9, rel_tol=0), 'Invalid EQ gain step')


def _validate_data(data: AppData) -> None:
    _require(type(data) is AppData, 'Invalid application data')
    _validate_preferences(data.preferences)
    _require(type(data.presets) is tuple and len(data.presets) <= 32, 'Invalid preset count')
    identities = set()
    for preset in data.presets:
        _require(type(preset) is EqPreset, 'Invalid preset')
        _require(_printable(preset.name, 40), 'Invalid preset name')
        _require(_printable(preset.model, 128), 'Invalid preset model')
        _validate_bands(preset.frequencies, preset.gains)
        identity = (unicodedata.normalize('NFKC', preset.name).strip().casefold(), preset.model, preset.frequencies)
        _require(identity not in identities, 'Duplicate preset identity')
        identities.add(identity)


def _unique_object(pairs):
    result = {}
    for key, value in pairs:
        _require(key not in result, 'Duplicate JSON field')
        result[key] = value
    return result


def _invalid_constant(value):
    raise PreferenceError('Invalid JSON number')


def _decode(content: bytes) -> AppData:
    _require(len(content) <= _MAX_BYTES, 'Preferences exceed size limit')
    try:
        document = json.loads(content, object_pairs_hook=_unique_object, parse_constant=_invalid_constant)
        _require(type(document) is dict and document.keys() == {'version', 'preferences', 'presets'}, 'Invalid preferences schema')
        _require(type(document['version']) is int and document['version'] == 1, 'Unsupported preferences version')
        raw_preferences = document['preferences']
        _require(type(raw_preferences) is dict and raw_preferences.keys() == _PREFERENCE_FIELDS, 'Invalid preference fields')
        raw_presets = document['presets']
        _require(type(raw_presets) is list and len(raw_presets) <= 32, 'Invalid preset count')
        presets = []
        for preset in raw_presets:
            _require(type(preset) is dict and preset.keys() == _PRESET_FIELDS, 'Invalid preset fields')
            _require(type(preset['frequencies']) is list and type(preset['gains']) is list, 'Invalid EQ sequences')
            presets.append(EqPreset(preset['name'], preset['model'], tuple(preset['frequencies']), tuple(preset['gains'])))
        data = AppData(Preferences(**raw_preferences), tuple(presets))
        _validate_data(data)
        return data
    except (UnicodeError, ValueError, RecursionError, OverflowError) as error:
        raise PreferenceError('Invalid preferences document') from error


@contextmanager
def _parent_directory(path: Path, create: bool = False):
    path = Path(path)
    _require(path.name not in ('', '.', '..') and '..' not in path.parts, 'Unsafe output path')
    directory = os.open('/' if path.is_absolute() else '.', os.O_RDONLY | os.O_DIRECTORY)
    try:
        parts = path.parent.parts[1:] if path.is_absolute() else path.parent.parts
        for part in parts:
            if create:
                try:
                    os.mkdir(part, 0o700, dir_fd=directory)
                except FileExistsError:
                    pass
            child = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=directory)
            os.close(directory)
            directory = child
        yield directory
    finally:
        os.close(directory)


def _regular_target(directory: int, name: str):
    try:
        metadata = os.stat(name, dir_fd=directory, follow_symlinks=False)
    except FileNotFoundError:
        return None
    _require(stat.S_ISREG(metadata.st_mode), 'Target must be a regular file')
    return metadata


def _read_bounded(directory: int, name: str, maximum: int):
    if _regular_target(directory, name) is None:
        return None
    descriptor = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=directory)
    with os.fdopen(descriptor, 'rb') as stream:
        _require(stat.S_ISREG(os.fstat(stream.fileno()).st_mode), 'Target must be a regular file')
        content = stream.read(maximum + 1)
    _require(len(content) <= maximum, 'File exceeds size limit')
    return content


def _atomic_write(directory: int, name: str, content: bytes) -> None:
    _regular_target(directory, name)
    temporary = '.momentum-' + secrets.token_hex(16) + '.tmp'
    descriptor = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600, dir_fd=directory)
    try:
        with os.fdopen(descriptor, 'wb') as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        _regular_target(directory, name)
        os.replace(temporary, name, src_dir_fd=directory, dst_dir_fd=directory)
    finally:
        try:
            os.unlink(temporary, dir_fd=directory)
        except FileNotFoundError:
            pass


class PreferenceStore:
    def __init__(self, path: Path):
        self.path = Path(path)

    def load(self) -> AppData:
        try:
            with _parent_directory(self.path) as directory:
                content = _read_bounded(directory, self.path.name, _MAX_BYTES)
            return AppData(Preferences(), ()) if content is None else _decode(content)
        except FileNotFoundError:
            return AppData(Preferences(), ())
        except OSError as error:
            raise PreferenceError('Cannot read preferences') from error

    def save(self, data: AppData) -> None:
        _validate_data(data)
        content = json.dumps({'version': 1, **asdict(data)}, ensure_ascii=True, allow_nan=False, separators=(',', ':')).encode('utf-8')
        _require(len(content) <= _MAX_BYTES, 'Preferences exceed size limit')
        try:
            with _parent_directory(self.path, create=True) as directory:
                existing = _read_bounded(directory, self.path.name, _MAX_BYTES)
                if existing is not None:
                    _decode(existing)
                _atomic_write(directory, self.path.name, content)
        except OSError as error:
            raise PreferenceError('Cannot save preferences') from error
