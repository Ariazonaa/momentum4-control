import json
import os
from pathlib import Path
import select
import subprocess
import threading
import time

from .errors import ControlError, CoreOperationError, DongleDisconnectedError, UpdateError

REQUEST_LIMIT = 65536
REPLY_LIMIT = 262144
DEPTH_LIMIT = 16


def _unique(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError('Duplicate field')
        result[key] = value
    return result


def _decode(data):
    depth = 0
    quoted = False
    escaped = False
    for byte in data:
        if quoted:
            if escaped:
                escaped = False
            elif byte == 92:
                escaped = True
            elif byte == 34:
                quoted = False
        elif byte == 34:
            quoted = True
        elif byte in (91, 123):
            depth += 1
            if depth > DEPTH_LIMIT:
                raise ValueError('Nesting limit')
        elif byte in (93, 125):
            depth -= 1
    return json.loads(data, object_pairs_hook=_unique, parse_constant=lambda _: (_ for _ in ()).throw(ValueError('Nonfinite number')))


def _schema(value, fields):
    if type(value) is not dict or set(value) != set(fields):
        raise ValueError('Invalid fields')


def _cancelled(value):
    return value.is_set() if isinstance(value, threading.Event) else bool(value and value())


def _request_size(value, depth=0):
    if depth > DEPTH_LIMIT:
        raise ValueError('Request nesting')
    if type(value) is str:
        if len(value) > REQUEST_LIMIT:
            raise ValueError('Request string limit')
        size = 2
        for character in value:
            point = ord(character)
            size += 2 if character in '\"\\\b\f\n\r\t' else 1 if 32 <= point < 127 else 6 if point <= 65535 else 12
            if size > REQUEST_LIMIT:
                raise ValueError('Request string limit')
        return size
    if type(value) in (dict,list,tuple):
        if len(value) > REQUEST_LIMIT:
            raise ValueError('Request collection limit')
        size = 2
        items = value.items() if type(value) is dict else ((None,item) for item in value)
        for key,item in items:
            if key is not None:
                if type(key) is not str:
                    raise ValueError('Request key type')
                size += _request_size(key,depth+1)+1
            size += _request_size(item,depth+1)+1
            if size > REQUEST_LIMIT:
                raise ValueError('Request limit')
        return size
    if value is None or type(value) is bool:
        return 5
    if type(value) is int:
        if value.bit_length() > 256:
            raise ValueError('Request integer limit')
        return 80
    if type(value) is float:
        return 32
    raise ValueError('Request type')


class CoreProcess:
    def __init__(self, mode='headset', *, fd=None, cancelled=None, pump=None, os_call=None, _command=None):
        self.cancelled = cancelled
        self.pump = pump
        self.os_call = os_call
        self._request_lock = threading.Lock()
        self._lifecycle_lock = threading.Lock()
        self._next_id = 0
        self._last_os_id = 0
        self._buffer = bytearray()
        self._stderr_bytes = 0
        self._stderr_open = True
        self._closed = False
        self._cancelled = threading.Event()
        if _cancelled(cancelled):
            raise ControlError('Operation cancelled')
        executable = Path(__file__).resolve().parent.parent / 'core' / 'momentum-core-host'
        command = _command if _command is not None else [str(executable), mode] + ([] if fd is None else ['--fd', str(fd)])
        try:
            self.process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                            close_fds=True, pass_fds=() if fd is None else (fd,), bufsize=0)
        except OSError as error:
            raise ControlError('Shared device core is unavailable; reinstall Momentum Control') from error
        for pipe in (self.process.stdin, self.process.stdout, self.process.stderr):
            os.set_blocking(pipe.fileno(), False)

    def _check(self, deadline):
        if self._cancelled.is_set() or _cancelled(self.cancelled):
            raise ControlError('Operation cancelled')
        if time.monotonic() >= deadline:
            raise ControlError('Shared device core operation timed out')
        if self._closed:
            raise ControlError('Shared device core is closed')
        if self.pump is not None:
            self.pump()

    def _send(self, message, deadline):
        if _request_size(message) > REQUEST_LIMIT:
            raise ControlError('Shared device core request exceeds limit')
        data = json.dumps(message, ensure_ascii=True, allow_nan=False, separators=(',', ':')).encode() + b'\n'
        if len(data) - 1 > REQUEST_LIMIT:
            raise ControlError('Shared device core request exceeds limit')
        offset = 0
        while offset < len(data):
            self._check(deadline)
            _, writable, _ = select.select([], [self.process.stdin], [], min(.05, max(0, deadline-time.monotonic())))
            if not writable:
                continue
            try:
                written = os.write(self.process.stdin.fileno(), data[offset:])
            except (BlockingIOError, InterruptedError):
                continue
            if written <= 0:
                raise ControlError('Shared device core input closed')
            offset += written

    def _receive(self, deadline):
        while True:
            self._check(deadline)
            newline = self._buffer.find(b'\n')
            if newline >= 0:
                data = bytes(self._buffer[:newline])
                del self._buffer[:newline+1]
                if not data or len(data) > REPLY_LIMIT:
                    raise ValueError('Invalid reply length')
                return _decode(data)
            ready, _, _ = select.select([self.process.stdout] + ([self.process.stderr] if self._stderr_open else []), [], [], min(.05, max(0, deadline-time.monotonic())))
            for pipe in ready:
                try:
                    data = os.read(pipe.fileno(), 4096)
                except (BlockingIOError, InterruptedError):
                    continue
                if pipe is self.process.stderr:
                    if not data:
                        self._stderr_open = False
                    self._stderr_bytes += len(data)
                    if self._stderr_bytes > 8192:
                        raise ValueError('Diagnostic limit')
                else:
                    if not data:
                        raise ControlError('Shared device core exited during operation')
                    self._buffer.extend(data)
                    line_end = self._buffer.find(b'\n')
                    if (line_end < 0 and len(self._buffer) > REPLY_LIMIT) or line_end > REPLY_LIMIT:
                        raise ValueError('Reply limit')

    def call(self, operation, arguments, *, timeout=32, progress=None, cancelled=None):
        if not self._request_lock.acquire(blocking=False):
            raise ControlError('A shared device core operation is already pending')
        previous_cancelled = self.cancelled
        if cancelled is not None:
            self.cancelled = cancelled
        deadline = time.monotonic() + timeout
        try:
            self._next_id += 1
            request_id = self._next_id
            self._send(dict(v=1, kind='request', id=request_id, op=operation, args=arguments), deadline)
            os_count = 0
            progress_count = 0
            while True:
                message = self._receive(deadline)
                if type(message) is not dict or type(message.get('v')) is not int or message['v'] != 1 or type(message.get('id')) is not int or message['id'] <= 0:
                    raise ValueError('Invalid envelope')
                kind = message.get('kind')
                if kind == 'os-call':
                    _schema(message, ('v', 'kind', 'id', 'request', 'op', 'args'))
                    if type(message['request']) is not int or message['request'] != request_id or message['id'] != self._last_os_id+1 or type(message['op']) is not str or type(message['args']) is not dict or self.os_call is None:
                        raise ValueError('Invalid OS call')
                    self._last_os_id = message['id']
                    os_count += 1
                    if os_count > 102000:
                        raise ValueError('Callback limit')
                    try:
                        value = self.os_call(message['op'], message['args'], deadline, lambda: self._cancelled.is_set() or _cancelled(self.cancelled))
                        response = dict(v=1, kind='os-result', id=message['id'], request=request_id, value=value)
                    except ControlError as error:
                        response = dict(v=1, kind='os-error', id=message['id'], request=request_id, code='removed' if isinstance(error, DongleDisconnectedError) else 'io')
                    self._send(response, deadline)
                elif kind == 'progress':
                    _schema(message, ('v', 'kind', 'id', 'stage', 'percent'))
                    if message['id'] != request_id or type(message['stage']) is not str or len(message['stage']) > 32 or type(message['percent']) is not int or not 0 <= message['percent'] <= 100:
                        raise ValueError('Invalid progress')
                    progress_count += 1
                    if progress_count > 1024:
                        raise ValueError('Callback limit')
                    if progress is not None:
                        progress(message['stage'], message['percent'])
                elif kind == 'result':
                    _schema(message, ('v', 'kind', 'id', 'value'))
                    if message['id'] != request_id:
                        raise ValueError('Wrong response ID')
                    return message['value']
                elif kind == 'error':
                    _schema(message, ('v', 'kind', 'id', 'code', 'stage', 'recovery_required', 'interrupted'))
                    if message['id'] != request_id or message['code'] not in ('unsupported', 'invalid', 'io', 'cancelled', 'partial', 'firmware') or type(message['stage']) is not str or len(message['stage']) > 32 or type(message['recovery_required']) is not bool or type(message['interrupted']) is not bool:
                        raise ValueError('Invalid error')
                    self.close()
                    if message['code'] == 'firmware':
                        raise UpdateError(message['stage'], 'Shared firmware operation failed', message['recovery_required'], message['interrupted'])
                    raise CoreOperationError(message['code'])
                else:
                    raise ValueError('Unknown message kind')
        except (ValueError, TypeError, KeyError, UnicodeError) as error:
            self.close()
            if 'Request' in str(error):
                raise ControlError('Shared device core request protocol failure') from error
            if self._cancelled.is_set() or _cancelled(self.cancelled):
                raise ControlError('Operation cancelled') from error
            raise ControlError('Shared device core protocol failure') from error
        except BaseException:
            self.close()
            raise
        finally:
            self.cancelled = previous_cancelled
            self._request_lock.release()

    def poll_idle(self):
        if self._buffer:
            self.close()
            raise ControlError('Shared device core idle protocol failure')
        if self._closed or self.process.poll() is not None:
            self.close()
            raise ControlError('Shared device core connection ended')
        ready, _, _ = select.select([self.process.stdout] + ([self.process.stderr] if self._stderr_open else []), [], [], 0)
        for pipe in ready:
            if pipe is self.process.stdout:
                self.close()
                raise ControlError('Shared device core idle protocol failure')
            data = os.read(pipe.fileno(), 4096)
            self._stderr_bytes += len(data)
            if not data:
                self._stderr_open = False
            if self._stderr_bytes > 8192:
                self.close()
                raise ControlError('Shared device core diagnostic protocol failure')

    def cancel(self):
        self._cancelled.set()
        self.close()

    def close(self):
        with self._lifecycle_lock:
            if self._closed:
                return
            self._closed = True
            if self.process.poll() is None:
                self.process.terminate()
                try:
                    self.process.wait(timeout=.25)
                except subprocess.TimeoutExpired:
                    self.process.kill()
                    self.process.wait(timeout=1)
            else:
                self.process.wait()
            for pipe in (self.process.stdin, self.process.stdout, self.process.stderr):
                pipe.close()
