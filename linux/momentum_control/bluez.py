import errno
import logging
import os
import re
import socket
import threading
import time

from .errors import ControlError
from .models import Device

GAIA_UUID = 'a2129ff3-081b-4c45-8afe-469d9c4842ec'
_ADDRESS = re.compile(r'(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}')
_PATH = re.compile(r'/org/bluez/hci[0-9]+/dev_(?:[0-9A-Fa-f]{2}_){5}[0-9A-Fa-f]{2}')


def _check_cancel(cancelled: threading.Event):
    if cancelled.is_set():
        raise ControlError('Operation cancelled')


def _devices(objects) -> list[Device]:
    devices = []
    for path, interfaces in objects.items():
        props = interfaces.get('org.bluez.Device1', {})
        address = str(props.get('Address', ''))
        if not props.get('Paired') or GAIA_UUID not in [str(item).lower() for item in props.get('UUIDs', [])]:
            continue
        if not _ADDRESS.fullmatch(address) or not _PATH.fullmatch(str(path)):
            continue
        if str(path).rsplit('/dev_', 1)[1].replace('_', ':').upper() != address.upper():
            continue
        devices.append(Device(address.upper(), str(props.get('Alias', props.get('Name', address)))[:128],
                              str(path), bool(props.get('Connected', False))))
    return sorted(devices, key=lambda device: (device.name, device.address))


class _DBus:
    def __init__(self, cancelled: threading.Event):
        _check_cancel(cancelled)
        try:
            import dbus
            import dbus.service
            from dbus.mainloop.glib import DBusGMainLoop
            from gi.repository import GLib
            self.dbus = dbus
            self.context = GLib.MainContext.default()
            self.bus = dbus.SystemBus(private=True, mainloop=DBusGMainLoop())
        except Exception as error:
            raise ControlError('Cannot access BlueZ. Check Bluetooth and install dbus-python and PyGObject.') from error
        self.cancelled = cancelled

    def interface(self, path: str, interface: str):
        return self.dbus.Interface(self.bus.get_object('org.bluez', path, introspect=False), interface)

    def pump(self, deadline: float, cancelled: threading.Event | None = None):
        event = cancelled if cancelled is not None else self.cancelled
        _check_cancel(event)
        if time.monotonic() >= deadline:
            raise ControlError('Bluetooth operation timed out')
        for _ in range(32):
            if not self.context.pending():
                break
            self.context.iteration(False)
        event.wait(0.01)

    def call(self, method, *args, cancelled: threading.Event | None = None):
        event = cancelled if cancelled is not None else self.cancelled
        _check_cancel(event)
        completed = []
        errors = []
        pending = method(*args, reply_handler=lambda *values: completed.append(values),
                         error_handler=errors.append, timeout=3)
        deadline = time.monotonic() + 3
        try:
            while not completed and not errors:
                self.pump(deadline, event)
            _check_cancel(event)
            if errors:
                raise ControlError('BlueZ request failed; check the adapter and headset connection') from errors[0]
            return completed[0][0] if len(completed[0]) == 1 else completed[0]
        finally:
            if pending is not None:
                pending.cancel()

    def close(self):
        self.bus.close()


def discover(cancelled: threading.Event | None = None) -> list[Device]:
    connection = _DBus(cancelled if cancelled is not None else threading.Event())
    try:
        manager = connection.interface('/', 'org.freedesktop.DBus.ObjectManager')
        objects = connection.call(manager.GetManagedObjects)
        return _devices(objects)
    finally:
        connection.close()


class _DeviceLock:
    def __init__(self, address: str):
        self.connection = socket.socket(socket.AF_UNIX, socket.SOCK_DGRAM)
        try:
            self.connection.bind(f'\0momentum-control-{os.getuid()}-{address.upper()}')
        except OSError as error:
            self.connection.close()
            if error.errno == errno.EADDRINUSE:
                raise ControlError('This headset is already controlled by another Momentum Control instance') from error
            raise ControlError('Could not acquire headset session lock') from error

    def close(self):
        self.connection.close()


class BlueZLease:
    def __init__(self, device: Device, cancelled: threading.Event | None = None, terminal=None):
        if not _ADDRESS.fullmatch(device.address) or not _PATH.fullmatch(device.path):
            raise ControlError('Invalid Bluetooth device address or path')
        self.device = device
        self.cancelled = cancelled if cancelled is not None else threading.Event()
        self._socket: socket.socket | None = None
        self._socket_lock = threading.Lock()
        self._accepting = False
        self._operation_lock = threading.Lock()
        self._dbus: _DBus | None = None
        self._profile = None
        self._profile_path = f'/org/momentum_control/profile/p{os.getpid()}_{id(self)}'
        self._registered = False
        self._device_lock: _DeviceLock | None = None
        self._connected = False
        self._terminal = terminal

    def _accept_connection(self, path, fd):
        descriptor = fd.take()
        try:
            with self._socket_lock:
                if str(path) != self.device.path or self.cancelled.is_set() or not self._accepting:
                    raise ControlError('Unexpected Bluetooth profile device')
                if self._socket is not None:
                    raise ControlError('Duplicate Bluetooth profile connection')
                self._socket = socket.socket(fileno=descriptor)
                descriptor = -1
        finally:
            if descriptor >= 0:
                os.close(descriptor)

    def connect(self) -> None:
        if not self._operation_lock.acquire(blocking=False):
            raise ControlError('A headset operation is already pending')
        try:
            _check_cancel(self.cancelled)
            if self._connected:
                raise ControlError('Headset is already connected')
            self._device_lock = _DeviceLock(self.device.address)
            self._dbus = connection = _DBus(self.cancelled)
            manager = connection.interface('/', 'org.freedesktop.DBus.ObjectManager')
            candidates = _devices(connection.call(manager.GetManagedObjects))
            if not any(item.path == self.device.path and item.address == self.device.address.upper() for item in candidates):
                raise ControlError('Select a paired headset advertising the GAIA service')
            owner = self
            dbus = connection.dbus

            class Profile(dbus.service.Object):
                @dbus.service.method('org.bluez.Profile1', in_signature='', out_signature='')
                def Release(self):
                    owner.cancel()

                @dbus.service.method('org.bluez.Profile1', in_signature='oha{sv}', out_signature='')
                def NewConnection(self, path, descriptor, properties):
                    try:
                        owner._accept_connection(path, descriptor)
                    except ControlError as error:
                        raise dbus.exceptions.DBusException(str(error), name='org.bluez.Error.Rejected')

                @dbus.service.method('org.bluez.Profile1', in_signature='o', out_signature='')
                def RequestDisconnection(self, path):
                    if str(path) == owner.device.path:
                        owner.cancel()

            with self._socket_lock:
                _check_cancel(self.cancelled)
                self._accepting = True
            self._profile = Profile(connection.bus, self._profile_path)
            profile_manager = connection.interface('/org/bluez', 'org.bluez.ProfileManager1')
            connection.call(profile_manager.RegisterProfile, dbus.ObjectPath(self._profile_path), GAIA_UUID,
                            dbus.Dictionary({'Role': 'client', 'RequireAuthentication': dbus.Boolean(True),
                                             'RequireAuthorization': dbus.Boolean(True)}, signature='sv'))
            self._registered = True
            deadline = time.monotonic() + 10
            device = connection.interface(self.device.path, 'org.bluez.Device1')
            connection.call(device.ConnectProfile, GAIA_UUID)
            while self._socket is None:
                connection.pump(deadline)
            _check_cancel(self.cancelled)
            self._connected = True
        except Exception as error:
            self.close()
            if isinstance(error, ControlError):
                raise
            raise ControlError('Could not open headset control connection') from error
        finally:
            self._operation_lock.release()

    def cancel(self) -> None:
        with self._socket_lock:
            self.cancelled.set()
            self._accepting = False
            if self._socket is not None:
                try:
                    self._socket.shutdown(socket.SHUT_RDWR)
                except OSError as error:
                    if error.errno not in (errno.ENOTCONN, errno.EBADF):
                        raise ControlError('Could not cancel Bluetooth connection') from error
        if self._terminal is not None:
            self._terminal()

    def close(self) -> None:
        self._connected = False
        with self._socket_lock:
            self._accepting = False
            if self._socket is not None:
                self._socket.close()
                self._socket = None
        try:
            if self._dbus is not None:
                try:
                    if self._registered:
                        manager = self._dbus.interface('/org/bluez', 'org.bluez.ProfileManager1')
                        self._dbus.call(manager.UnregisterProfile, self._dbus.dbus.ObjectPath(self._profile_path),
                                        cancelled=threading.Event())
                except ControlError:
                    logging.getLogger(__name__).warning('Profile unregistration failed; closing its private D-Bus connection')
                finally:
                    self._registered = False
                    try:
                        if self._profile is not None:
                            profile, self._profile = self._profile, None
                            profile.remove_from_connection()
                    finally:
                        connection, self._dbus = self._dbus, None
                        connection.close()
        finally:
            if self._device_lock is not None:
                self._device_lock.close()
                self._device_lock = None

    def fileno(self):
        if self._socket is None or not self._connected:
            raise ControlError('Headset is not connected')
        return self._socket.fileno()

    def pump_idle(self):
        if self._dbus is not None:
            _check_cancel(self.cancelled)
            for _ in range(32):
                if not self._dbus.context.pending():
                    break
                self._dbus.context.iteration(False)
