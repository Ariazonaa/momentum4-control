from dataclasses import asdict, dataclass
import json
import math
from pathlib import Path
import re
import time
import unicodedata
from typing import Callable

from .preferences import (AppData, EqPreset, PreferenceError, Preferences,
                          _atomic_write, _invalid_constant, _parent_directory,
                          _printable, _read_bounded, _unique_object, _validate_data)


class ZoneError(ValueError):
    pass


@dataclass(frozen=True)
class LocationFix:
    latitude: float
    longitude: float
    accuracy_m: float
    received_at: float


@dataclass(frozen=True)
class SoundZone:
    name: str
    device_address: str
    latitude: float
    longitude: float
    radius_m: int
    preset: EqPreset


@dataclass(frozen=True)
class ZoneData:
    enabled: bool = False
    zones: tuple[SoundZone, ...] = ()


_MAX_BYTES = 256 * 1024
_SERVICE = 'org.freedesktop.GeoClue2'
_MANAGER = _SERVICE + '.Manager'
_CLIENT = _SERVICE + '.Client'
_LOCATION = _SERVICE + '.Location'
_PROPERTIES = 'org.freedesktop.DBus.Properties'
_MANAGER_PATH = '/org/freedesktop/GeoClue2/Manager'


def _require(condition: bool, message: str) -> None:
    if not condition:
        raise ZoneError(message)


def _number(value, minimum: float, maximum: float) -> bool:
    return type(value) in (int, float) and minimum <= value <= maximum and math.isfinite(value)


def _coordinates(latitude, longitude) -> bool:
    return _number(latitude, -90, 90) and _number(longitude, -180, 180)


def _distance(latitude: float, longitude: float, zone: SoundZone) -> float:
    lat_delta = math.radians(zone.latitude - latitude)
    lon_delta = math.radians(zone.longitude - longitude)
    haversine = (math.sin(lat_delta / 2) ** 2 + math.cos(math.radians(latitude))
                 * math.cos(math.radians(zone.latitude)) * math.sin(lon_delta / 2) ** 2)
    return 12742000 * math.asin(math.sqrt(max(0, min(1, haversine))))


def _validate_zones(data: ZoneData) -> None:
    _require(type(data) is ZoneData and type(data.enabled) is bool, 'Invalid zone settings')
    _require(type(data.zones) is tuple and len(data.zones) <= 16, 'Invalid zone count')
    names = set()
    for index, zone in enumerate(data.zones):
        _require(type(zone) is SoundZone, 'Invalid sound zone')
        _require(_printable(zone.name, 40), 'Invalid zone name')
        name = unicodedata.normalize('NFKC', zone.name).strip().casefold()
        _require(name not in names, 'Duplicate zone name')
        names.add(name)
        _require(type(zone.device_address) is str and re.fullmatch(r'(?:[0-9A-F]{2}:){5}[0-9A-F]{2}', zone.device_address) is not None,
                 'Invalid zone device')
        _require(_coordinates(zone.latitude, zone.longitude), 'Invalid zone coordinates')
        _require(type(zone.radius_m) is int and 100 <= zone.radius_m <= 50000, 'Invalid zone radius')
        try:
            _validate_data(AppData(Preferences(), (zone.preset,)))
        except PreferenceError:
            raise ZoneError('Invalid zone preset') from None
        for previous in data.zones[:index]:
            if zone.device_address == previous.device_address:
                _require(_distance(zone.latitude, zone.longitude, previous) >= zone.radius_m + previous.radius_m,
                         'Zones for the same device overlap')


def _decode(content: bytes) -> ZoneData:
    try:
        _require(len(content) <= _MAX_BYTES, 'Zone data exceeds size limit')
        document = json.loads(content, object_pairs_hook=_unique_object, parse_constant=_invalid_constant)
        _require(type(document) is dict and document.keys() == {'version', 'enabled', 'zones'}, 'Invalid zone schema')
        _require(type(document['version']) is int and document['version'] == 1, 'Unsupported zone version')
        _require(type(document['zones']) is list and len(document['zones']) <= 16, 'Invalid zone count')
        zones = []
        for raw in document['zones']:
            _require(type(raw) is dict and raw.keys() == {'name', 'device_address', 'latitude', 'longitude', 'radius_m', 'preset'},
                     'Invalid zone fields')
            preset = raw['preset']
            _require(type(preset) is dict and preset.keys() == {'name', 'model', 'frequencies', 'gains'}, 'Invalid zone preset fields')
            _require(type(preset['frequencies']) is list and type(preset['gains']) is list, 'Invalid zone EQ sequences')
            zones.append(SoundZone(raw['name'], raw['device_address'], raw['latitude'], raw['longitude'], raw['radius_m'],
                                   EqPreset(preset['name'], preset['model'], tuple(preset['frequencies']), tuple(preset['gains']))))
        data = ZoneData(document['enabled'], tuple(zones))
        _validate_zones(data)
        return data
    except (ValueError, UnicodeError, RecursionError, OverflowError):
        raise ZoneError('Invalid sound zones document') from None


class ZoneStore:
    def __init__(self, path: Path):
        self.path = Path(path)

    def load(self) -> ZoneData:
        try:
            with _parent_directory(self.path) as directory:
                content = _read_bounded(directory, self.path.name, _MAX_BYTES)
            return ZoneData() if content is None else _decode(content)
        except FileNotFoundError:
            return ZoneData()
        except (OSError, PreferenceError):
            raise ZoneError('Cannot read sound zones') from None

    def save(self, data: ZoneData) -> None:
        _validate_zones(data)
        content = json.dumps({'version': 1, **asdict(data)}, ensure_ascii=True, allow_nan=False, separators=(',', ':')).encode('utf-8')
        _require(len(content) <= _MAX_BYTES, 'Zone data exceeds size limit')
        try:
            with _parent_directory(self.path, create=True) as directory:
                existing = _read_bounded(directory, self.path.name, _MAX_BYTES)
                if existing is not None:
                    _decode(existing)
                _atomic_write(directory, self.path.name, content)
        except (OSError, PreferenceError):
            raise ZoneError('Cannot save sound zones') from None


class ZoneTracker:
    def __init__(self):
        self._active: SoundZone | None = None

    def reset(self) -> None:
        self._active = None

    def update(self, fix: LocationFix, zones: tuple[SoundZone, ...], device_address: str,
               model: str, frequencies: tuple[int, ...], now: float) -> SoundZone | None:
        if (type(fix) is not LocationFix or not _coordinates(fix.latitude, fix.longitude)
                or not _number(fix.accuracy_m, 0, 1000000)
                or not _number(fix.received_at, -math.inf, math.inf)
                or not _number(now, -math.inf, math.inf) or not 0 <= now - fix.received_at <= 120):
            return None
        try:
            _validate_zones(ZoneData(True, zones))
        except ZoneError:
            return None
        matching = tuple(zone for zone in zones if zone.device_address == device_address and zone.preset.matches(model, frequencies))
        if self._active is not None and self._active in matching:
            if _distance(fix.latitude, fix.longitude, self._active) - fix.accuracy_m <= self._active.radius_m + 50:
                return None
        candidates = tuple(zone for zone in matching if _distance(fix.latitude, fix.longitude, zone) + fix.accuracy_m <= zone.radius_m)
        if len(candidates) > 1:
            return None
        if not candidates:
            if any(_distance(fix.latitude, fix.longitude, zone) - fix.accuracy_m <= zone.radius_m for zone in matching):
                return None
            self._active = None
            return None
        self._active = candidates[0]
        return self._active


class GeoClueProvider:
    def __init__(self, cancelled: Callable[[], bool] | None = None, now: Callable = time.monotonic,
                 wall_now: Callable = time.time, bus_factory=None, context=None):
        self._cancelled = cancelled or (lambda: False)
        self._now = now
        self._wall_now = wall_now
        self._bus_factory = bus_factory
        self._context = context
        self._bus = None
        self._manager = None
        self._client = None
        self._client_path = None
        self._closed = False
        self._session = object()
        self._started = False
        self._start_failed = False
        self._start_deadline = None
        self._sample_deadline = None
        self._source_timestamp = None
        self._fix = None

    def _timeout(self) -> float:
        _require(not self._closed and not self._cancelled(), 'Location request cancelled')
        remaining = self._sample_deadline - self._now()
        _require(remaining > 0, 'Location service timed out; try again')
        return min(1.0, remaining)

    def _path(self, value, kind: str) -> str:
        _require(isinstance(value, str) and len(value) <= 256 and
                 re.fullmatch('/org/freedesktop/GeoClue2/' + kind + r'/[A-Za-z0-9_]+(?:/[A-Za-z0-9_]+)*', value) is not None,
                 'Location service returned an invalid object')
        return str(value)

    def _proxy(self, path: str):
        self._timeout()
        return self._bus.get_object(_SERVICE, path, introspect=False, follow_name_owner_changes=True)

    def _setup(self) -> None:
        import dbus
        self._timeout()
        if self._bus_factory is None:
            from dbus.mainloop.glib import DBusGMainLoop
            from gi.repository import GLib
            self._context = self._context or GLib.MainContext.default()
            self._bus = dbus.SystemBus(private=True, mainloop=DBusGMainLoop())
        else:
            self._bus = self._bus_factory()
        self._timeout()
        _require(self._context is not None, 'Location event context is unavailable')
        self._manager = self._proxy(_MANAGER_PATH)
        client_path = self._manager.GetClient(dbus_interface=_MANAGER, timeout=self._timeout())
        self._client_path = self._path(client_path, 'Client')
        self._client = self._proxy(self._client_path)
        for name, value in (('DesktopId', dbus.String('momentum-control')),
                            ('RequestedAccuracyLevel', dbus.UInt32(6)),
                            ('DistanceThreshold', dbus.UInt32(0)), ('TimeThreshold', dbus.UInt32(15))):
            self._client.Set(_CLIENT, name, value, dbus_interface=_PROPERTIES, timeout=self._timeout())
        self._timeout()
        self._start_deadline = self._now() + 30
        session = self._session

        def started():
            if not self._closed and self._session is session and not self._cancelled() and self._now() < self._start_deadline:
                self._started = True

        def failed(error):
            if not self._closed and self._session is session:
                self._start_failed = True

        self._client.Start(dbus_interface=_CLIENT, timeout=30, reply_handler=started, error_handler=failed)

    def _get(self, proxy, interface: str, name: str):
        return proxy.Get(interface, name, dbus_interface=_PROPERTIES, timeout=self._timeout())

    def _scalar(self, value, minimum: float, maximum: float) -> float:
        import dbus
        _require(isinstance(value, (int, float)) and not isinstance(value, (bool, dbus.Boolean)),
                 'Location service returned an invalid number')
        _require(minimum <= value <= maximum and math.isfinite(value), 'Location service returned an invalid number')
        return float(value)

    def _timestamp(self, value) -> float:
        import dbus
        _require(isinstance(value, (tuple, list)) and len(value) == 2, 'Location timestamp is invalid')
        for item in value:
            _require(isinstance(item, int) and not isinstance(item, (bool, dbus.Boolean)) and 0 <= item < 2**64,
                     'Location timestamp is invalid')
        _require(value[1] < 1000000, 'Location timestamp is invalid')
        return int(value[0]) + int(value[1]) / 1000000

    def sample(self) -> LocationFix | None:
        _require(not self._closed, 'Location request is closed')
        self._sample_deadline = self._now() + 3
        try:
            self._timeout()
            if self._bus is None:
                self._setup()
                return None
            if not self._started:
                _require(self._now() < self._start_deadline, 'Location permission timed out; check location permissions')
            for iteration in range(32):
                self._timeout()
                if not self._context.pending():
                    break
                self._context.iteration(False)
            self._timeout()
            _require(not self._start_failed, 'Location access denied; check location permissions')
            if not self._started:
                return None
            location_path = self._get(self._client, _CLIENT, 'Location')
            if location_path == '/':
                return None
            location = self._proxy(self._path(location_path, 'Location'))
            latitude = self._scalar(self._get(location, _LOCATION, 'Latitude'), -90, 90)
            longitude = self._scalar(self._get(location, _LOCATION, 'Longitude'), -180, 180)
            accuracy = self._scalar(self._get(location, _LOCATION, 'Accuracy'), 0, 1000000)
            source_timestamp = self._timestamp(self._get(location, _LOCATION, 'Timestamp'))
            self._timeout()
            age = self._wall_now() - source_timestamp
            _require(-10 <= age <= 120, 'Location fix is stale; wait for a new fix')
            if self._source_timestamp is not None:
                _require(source_timestamp >= self._source_timestamp, 'Location timestamp moved backwards')
                if source_timestamp == self._source_timestamp:
                    return self._fix
            fix = LocationFix(latitude, longitude, accuracy, self._now() - max(0, age))
            self._source_timestamp = source_timestamp
            self._fix = fix
            return fix
        except ZoneError:
            self.close()
            raise
        except Exception:
            self.close()
            raise ZoneError('Location service unavailable; check location services and permissions') from None
        finally:
            self._sample_deadline = None

    def close(self) -> None:
        if self._closed:
            return
        self._closed = True
        self._session = None
        bus, self._bus = self._bus, None
        try:
            for proxy, method, args, interface in ((self._client, 'Stop', (), _CLIENT),
                                                   (self._manager, 'DeleteClient', (self._client_path,), _MANAGER)):
                remaining = 1.0 if self._sample_deadline is None else self._sample_deadline - self._now()
                if proxy is not None and self._client_path is not None and remaining > 0:
                    try:
                        getattr(proxy, method)(*args, dbus_interface=interface, timeout=min(1.0, remaining))
                    except Exception:
                        continue
        finally:
            self._client = None
            self._manager = None
            self._client_path = None
            if bus is not None:
                try:
                    bus.close()
                except Exception:
                    pass
