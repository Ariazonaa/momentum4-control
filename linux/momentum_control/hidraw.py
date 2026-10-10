from dataclasses import dataclass
import base64
import errno
import fcntl
import hashlib
import os
from pathlib import Path
import re
import select
import socket
import stat
import struct
import time

from .errors import DongleDisconnectedError, DongleError
from .models import DongleDevice

_SYSFS = Path("/sys/class/hidraw")


def _small_text(path):
    with path.open('rb') as source:
        data = source.read(4097)
    if len(data) > 4096:
        raise DongleError('Oversized USB identity metadata')
    return data.decode('utf-8', errors='strict').strip()


def discover_dongles():
    grouped = {}
    try:
        entries = tuple(_SYSFS.iterdir())
    except FileNotFoundError:
        return ()
    except OSError as error:
        raise DongleError('Cannot enumerate USB dongles') from error
    for entry in entries:
        if not re.fullmatch(r'hidraw[0-9]+', entry.name):
            continue
        try:
            node = (entry / 'device').resolve(strict=True)
            interface = next((parent for parent in (node, *node.parents) if (parent / 'bInterfaceNumber').is_file()), None)
            if interface is None:
                continue
            usb = interface.parent
            if _small_text(usb / 'idVendor').lower() != '3542' or _small_text(usb / 'idProduct').lower() != '3001':
                continue
            number = _small_text(interface / 'bInterfaceNumber')
            if number not in ('00', '01'):
                continue
            serial = _small_text(usb / 'serial') if (usb / 'serial').exists() else ''
            identity = hashlib.sha256((str(usb) + '\0' + serial).encode()).hexdigest()
            key = (str(usb), identity)
            interfaces = grouped.setdefault(key, {})
            if number in interfaces:
                raise DongleError('Ambiguous BTD 700 USB interfaces')
            interfaces[number] = '/dev/' + entry.name
        except (FileNotFoundError, NotADirectoryError):
            continue
        except (OSError, UnicodeError) as error:
            raise DongleError('Cannot inspect BTD 700 USB identity') from error
    return tuple(DongleDevice(interfaces['00'], interfaces['01'], usb, identity)
                 for (usb, identity), interfaces in sorted(grouped.items())
                 if '00' in interfaces and '01' in interfaces)


@dataclass(frozen=True)
class _USBInstance:
    devnum: int
    node: tuple[int, int]
    ctime_ns: int


def _usb_instance(device):
    usb = Path(device.usb_path)
    try:
        before = usb.stat()
        devnum = _small_text(usb / 'devnum')
        after = usb.stat()
    except (OSError, UnicodeError) as error:
        raise DongleError('Cannot inspect BTD 700 USB instance') from error
    if not re.fullmatch(r'[0-9]{1,3}', devnum) or not 1 <= int(devnum) <= 127:
        raise DongleError('Invalid BTD 700 USB instance number')
    generation = (before.st_dev, before.st_ino, before.st_ctime_ns)
    if generation != (after.st_dev, after.st_ino, after.st_ctime_ns):
        raise DongleError('BTD 700 USB instance changed during inspection')
    return _USBInstance(int(devnum), generation[:2], generation[2])


def _ioctl_read(number, size):
    return 0x80000000 | size << 16 | ord('H') << 8 | number


def _open(device, kind):
    if device not in discover_dongles():
        raise DongleError('BTD 700 identity or USB path changed; refresh devices')
    path = device.control_path if kind == 'control' else device.upgrade_path
    if not re.fullmatch(r'/dev/hidraw[0-9]+', path):
        raise DongleError('Invalid BTD 700 hidraw path')
    before_instance = _usb_instance(device)
    descriptor_fd = None
    validated = False
    try:
        descriptor_fd = os.open(path, os.O_RDWR | os.O_NONBLOCK | os.O_CLOEXEC | os.O_NOFOLLOW)
        opened = os.fstat(descriptor_fd)
        if not stat.S_ISCHR(opened.st_mode):
            raise DongleError('BTD 700 path is not a character device')
        expected_number = _small_text(_SYSFS / Path(path).name / 'dev')
        if expected_number != f'{os.major(opened.st_rdev)}:{os.minor(opened.st_rdev)}':
            raise DongleError('BTD 700 opened device number does not match USB identity')
        info = bytearray(8)
        fcntl.ioctl(descriptor_fd, _ioctl_read(3, 8), info, True)
        bus, vendor, product = struct.unpack('Ihh', info)
        if (bus, vendor & 65535, product & 65535) != (3, 0x3542, 0x3001):
            raise DongleError('Opened HID device is not a BTD 700 USB device')
        size_buffer = bytearray(4)
        fcntl.ioctl(descriptor_fd, _ioctl_read(1, 4), size_buffer, True)
        size = struct.unpack('I', size_buffer)[0]
        if not 0 < size <= 4096:
            raise DongleError('Invalid HID descriptor size')
        descriptor = bytearray(struct.pack('I', size) + bytes(4096))
        fcntl.ioctl(descriptor_fd, _ioctl_read(2, 4100), descriptor, True)
        if struct.unpack('I', descriptor[:4])[0] != size:
            raise DongleError('HID descriptor size changed')
        if device not in discover_dongles():
            raise DongleError('BTD 700 identity changed while opening')
        if _usb_instance(device) != before_instance:
            raise DongleError('BTD 700 USB instance changed while opening')
        validated = True
        return descriptor_fd, bytes(descriptor[4:4 + size])
    except PermissionError as error:
        raise DongleError('BTD 700 permission denied: install the scoped uaccess rule, then unplug and reconnect the dongle') from error
    except OSError as error:
        raise DongleError('Cannot open or validate BTD 700 HID interface') from error
    finally:
        if descriptor_fd is not None and not validated:
            _close_fd(descriptor_fd)


def _close_fd(descriptor_fd):
    try:
        os.close(descriptor_fd)
    except OSError:
        pass


def _check(cancelled, deadline):
    if cancelled is not None and cancelled():
        raise DongleError('BTD 700 operation cancelled')
    if time.monotonic() >= deadline:
        raise DongleError('BTD 700 operation timed out')


def _read(descriptor_fd, deadline, cancelled):
    while True:
        _check(cancelled, deadline)
        try:
            ready, _, _ = select.select([descriptor_fd], [], [], min(0.05, max(0, deadline - time.monotonic())))
            if not ready:
                continue
            data = os.read(descriptor_fd, 256)
            if not data:
                raise DongleDisconnectedError('BTD 700 disconnected')
            if len(data) > 255:
                raise DongleError('Oversized BTD 700 report')
            return data
        except (BlockingIOError, InterruptedError):
            continue
        except OSError as error:
            if error.errno in (errno.ENODEV, errno.ENXIO, errno.EIO, errno.EPIPE):
                raise DongleDisconnectedError('BTD 700 disconnected') from error
            raise DongleError('BTD 700 read failed') from error


def _write(descriptor_fd, data, deadline, cancelled):
    if not 0 < len(data) <= 255:
        raise DongleError('Invalid BTD 700 report size')
    while True:
        _check(cancelled, deadline)
        try:
            _, ready, _ = select.select([], [descriptor_fd], [], min(0.05, max(0, deadline - time.monotonic())))
            if not ready:
                continue
            if os.write(descriptor_fd, data) != len(data):
                raise DongleError('Incomplete BTD 700 report write')
            return
        except (BlockingIOError, InterruptedError):
            continue
        except OSError as error:
            if error.errno in (errno.ENODEV, errno.ENXIO, errno.EIO, errno.EPIPE):
                raise DongleDisconnectedError('BTD 700 disconnected') from error
            raise DongleError('BTD 700 write failed') from error


def _lock(device):
    owner = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    try:
        owner.bind('\0momentum-btd700-' + hashlib.sha256(device.usb_path.encode()).hexdigest())
        return owner
    except OSError as error:
        owner.close()
        if error.errno == errno.EADDRINUSE:
            raise DongleError('BTD 700 is busy in another operation or application instance') from error
        raise DongleError('Cannot acquire BTD 700 session lock') from error


def _fields(arguments, expected):
    if type(arguments) is not dict or set(arguments) != set(expected):
        raise DongleError('Invalid HID operation fields')


def _number(arguments, name):
    value = arguments.get(name)
    if type(value) is not int or value <= 0:
        raise DongleError('Invalid HID operation number')
    return value


class HidAdapter:
    def __init__(self, device):
        self.device = device
        self._lease = None
        self._targets = {}
        self._endpoints = {}
        self._next_target = 0
        self._next_endpoint = 0

    def call(self, operation, arguments, deadline, cancelled):
        _check(cancelled, deadline)
        schemas = {
            'acquire': ('timeout_ms',), 'release': (), 'observe': ('timeout_ms',),
            'open': ('kind', 'target', 'timeout_ms'), 'descriptor': ('endpoint', 'timeout_ms'),
            'read': ('endpoint', 'timeout_ms'), 'write': ('endpoint', 'data', 'timeout_ms'),
            'feature': ('endpoint', 'data', 'timeout_ms'), 'close': ('endpoint',),
        }
        if operation not in schemas:
            raise DongleError('Unknown HID OS operation')
        _fields(arguments, schemas[operation])
        if 'timeout_ms' in arguments:
            timeout = _number(arguments, 'timeout_ms')
            if timeout > 600000:
                raise DongleError('Invalid HID operation timeout')
            deadline = min(deadline, time.monotonic() + timeout / 1000)
        if operation == 'acquire':
            if self._lease is not None:
                raise DongleError('Duplicate HID lease')
            if self.device not in discover_dongles():
                raise DongleError('BTD 700 target changed')
            self._lease = _lock(self.device)
            return {}
        if operation == 'release':
            self.close()
            return {}
        if self._lease is None:
            raise DongleError('HID lease is not active')
        if operation == 'observe':
            self._targets.clear()
            values = []
            for target in discover_dongles():
                if target.usb_path != self.device.usb_path:
                    continue
                if len(values) >= 32:
                    raise DongleError('Ambiguous HID targets')
                instance = _usb_instance(target)
                self._next_target += 1
                target_id = self._next_target
                self._targets[target_id] = target
                values.append(dict(id=target_id, control_path=target.control_path, upgrade_path=target.upgrade_path,
                                   usb_path=target.usb_path, identity=target.identity, device_number=instance.devnum,
                                   node_device=instance.node[0], node_inode=instance.node[1], change_stamp=instance.ctime_ns))
            return values
        if operation == 'open':
            target = self._targets.get(_number(arguments, 'target'))
            kind = arguments['kind']
            if target is None or target.usb_path != self.device.usb_path or target.identity != self.device.identity or kind not in ('control', 'upgrade') or len(self._endpoints) >= 2:
                raise DongleError('Invalid HID target or endpoint')
            descriptor, report_descriptor = _open(target, kind)
            self._next_endpoint += 1
            self._endpoints[self._next_endpoint] = (descriptor, report_descriptor)
            return {'endpoint':self._next_endpoint}
        endpoint = _number(arguments, 'endpoint')
        entry = self._endpoints.get(endpoint)
        if operation == 'close':
            if entry is not None:
                self._endpoints.pop(endpoint)
                _close_fd(entry[0])
            return {}
        if entry is None:
            raise DongleError('Unknown HID endpoint')
        descriptor, report_descriptor = entry
        if operation == 'descriptor':
            return {'data':base64.b64encode(report_descriptor).decode()}
        if operation == 'read':
            return {'data':base64.b64encode(_read(descriptor, deadline, cancelled)).decode()}
        encoded = arguments['data']
        if type(encoded) is not str or len(encoded) > 340:
            raise DongleError('Invalid HID report')
        try:
            data = base64.b64decode(encoded, validate=True)
        except (ValueError, TypeError) as error:
            raise DongleError('Invalid HID report encoding') from error
        if not 0 < len(data) <= 255:
            raise DongleError('Invalid HID report size')
        if operation == 'write':
            _write(descriptor, data, deadline, cancelled)
            return {'written':len(data)}
        if len(data) != 63:
            raise DongleError('Invalid fixed HID feature report')
        _check(cancelled, deadline)
        try:
            result = fcntl.ioctl(descriptor, 0xc03f4806, bytearray(data), True)
        except OSError as error:
            raise DongleError('BTD 700 feature operation failed') from error
        _check(cancelled, deadline)
        if result != 63:
            raise DongleError('Incomplete BTD 700 feature report')
        return {'written':result}

    def close(self):
        entries, self._endpoints = self._endpoints, {}
        self._targets.clear()
        for descriptor, _ in entries.values():
            _close_fd(descriptor)
        lease, self._lease = self._lease, None
        if lease is not None:
            lease.close()
