import atexit
from dataclasses import asdict
from enum import IntEnum
import threading

from .core_proxy import CoreProcess
from .errors import ControlError, CoreOperationError, DongleError, UpdateError
from .hidraw import HidAdapter, discover_dongles
from .models import DongleDevice, DongleStatus, FirmwareVersion, UpgradeVersion, status_from_dict, plan_from_dict, upgrade_from_dict


class Codec(IntEnum):
    SBC = 1
    APTX = 2
    APTX_ADAPTIVE = 4
    APTX_LOSSLESS = 8
    APTX_LITE = 16
    LC3 = 32


class DongleProxy:
    def __init__(self, *, _process_factory=CoreProcess, _adapter_factory=HidAdapter):
        self._process_factory = _process_factory
        self._adapter_factory = _adapter_factory
        self._process = None
        self._adapter = None
        self._device = None
        self._plan = None
        self._operation_lock = threading.Lock()

    def _connect(self, device, cancelled):
        if cancelled is not None and cancelled():
            raise DongleError('BTD 700 operation cancelled')
        if self._device != device or self._process is None:
            self.close()
            self._device = device
            self._adapter = self._adapter_factory(device)
            self._process = self._process_factory('dongle', os_call=self._adapter.call)
            self._process.call('dongle-bind', asdict(device), cancelled=cancelled)

    def _call(self, device, operation, args, mapper, *, cancelled=None, progress=None, timeout=32):
        if not self._operation_lock.acquire(blocking=False):
            raise DongleError('A BTD 700 operation is already pending')
        try:
            self._connect(device, cancelled)
            value = self._process.call(operation, args, cancelled=cancelled, progress=progress, timeout=timeout)
            return mapper(value)
        except BaseException:
            self.close()
            raise
        finally:
            self._operation_lock.release()

    def status(self, device, cancelled=None):
        return self._call(device,'dongle-status',{},lambda value:status_from_dict(device,value),cancelled=cancelled)

    def codec(self, device, codec, cancelled=None):
        if type(codec) is not Codec:
            raise DongleError('Invalid codec choice type')
        self._plan = None
        return self._call(device,'dongle-codec',{'codec':int(codec)},lambda value:status_from_dict(device,value),cancelled=cancelled,timeout=17)

    def check(self, device, cancelled=None):
        self._plan = None
        plan = self._call(device,'dongle-check',{},lambda value:plan_from_dict(device,value),cancelled=cancelled,timeout=92)
        self._plan = plan
        return plan

    def apply(self, plan, progress, cancelled=None):
        if cancelled is not None and cancelled():
            self.close()
            raise DongleError('BTD 700 operation cancelled')
        if self._process is None or plan is not self._plan or plan.device != self._device:
            self.close()
            raise DongleError('BTD 700 update plan is stale or foreign')
        self._plan = None
        try:
            return self._call(plan.device,'dongle-apply',{'handle':plan.handle},upgrade_from_dict,cancelled=cancelled,progress=progress,timeout=602)
        except UpdateError:
            raise
        except CoreOperationError as error:
            if error.code in ('invalid','unsupported','cancelled'):
                raise UpdateError('preflight','Shared core refused the prepared update',False,error.code=='cancelled') from error
            raise UpdateError('transport','Update completion is unknown; do not replay the update',True) from error
        except KeyboardInterrupt as error:
            raise UpdateError('transport','Update completion is unknown; do not replay the update',True,True) from error
        except (ControlError,OSError,ValueError,TypeError) as error:
            raise UpdateError('transport','Update completion is unknown; do not replay the update',True,bool(cancelled and cancelled())) from error

    def cancel(self):
        process = self._process
        if process is not None:
            process.cancel()
        self._plan = None

    def close(self):
        self._plan = None
        process, self._process = self._process, None
        if process is not None:
            process.close()
        adapter, self._adapter = self._adapter, None
        if adapter is not None:
            adapter.close()
        self._device = None


_proxy = DongleProxy()
atexit.register(_proxy.close)


def read_status(device, cancelled=None):
    return _proxy.status(device, cancelled)


def set_codec(device, codec, cancelled=None):
    return _proxy.codec(device, codec, cancelled)


def close():
    _proxy.close()


def cancel():
    _proxy.cancel()
