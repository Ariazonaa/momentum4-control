from dataclasses import asdict
import threading

from .bluez import BlueZLease, discover
from .core_proxy import CoreProcess
from .errors import ControlError
from .models import Device, PeerDevice, Snapshot, snapshot_from_dict


class BluetoothClient:
    def __init__(self, device: Device, cancelled: threading.Event | None = None, *, _lease_factory=BlueZLease, _process_factory=CoreProcess):
        self.device = device
        self.cancelled = cancelled if cancelled is not None else threading.Event()
        self._lease_factory = _lease_factory
        self._process_factory = _process_factory
        self._lease = None
        self._process = None
        self._cached_snapshot = None
        self._operation_lock = threading.Lock()

    def connect(self):
        if self._lease is not None or self._process is not None:
            raise ControlError('Headset is already connected')
        if self.cancelled.is_set():
            raise ControlError('Operation cancelled')
        try:
            self._lease = self._lease_factory(self.device, cancelled=self.cancelled, terminal=self._terminal)
            self._lease.connect()
            self._process = self._process_factory('headset', fd=self._lease.fileno(), cancelled=self.cancelled, pump=self._lease.pump_idle)
        except BaseException:
            self.close()
            raise

    def _terminal(self):
        process = self._process
        if process is not None:
            process.cancel()
        self._cached_snapshot = None

    def _call(self, operation, arguments):
        if not self._operation_lock.acquire(blocking=False):
            raise ControlError('A headset operation is already pending')
        try:
            if self._process is None:
                raise ControlError('Headset is not connected')
            state = snapshot_from_dict(self._process.call(operation, arguments))
            self._cached_snapshot = state
            return state
        except BaseException:
            self.close()
            raise
        finally:
            self._operation_lock.release()

    def snapshot(self):
        return self._call('snapshot', {})

    def set_value(self, name, value, band=None, expected_topology=None):
        if name == 'eq':
            model, frequencies = self._topology(expected_topology)
            return self._call('eq-band', dict(band=band, gain=value, model=model, frequencies=frequencies))
        operation = 'set-boolean' if type(value) is bool else 'set-integer'
        return self._call(operation, {'control':name,'value':value})

    def _topology(self, expected_topology):
        state = self._cached_snapshot if self._cached_snapshot is not None else self.snapshot()
        frequencies = expected_topology[1] if expected_topology is not None else state.eq_frequencies
        if expected_topology is not None and expected_topology[0] != len(frequencies):
            raise ControlError('Invalid submitted equalizer topology')
        return state.model, tuple(frequencies)

    def set_equalizer(self, gains, expected_model, expected_topology):
        if expected_topology[0] != len(expected_topology[1]):
            raise ControlError('Invalid submitted equalizer topology')
        return self._call('eq-curve', dict(gains=tuple(gains), model=expected_model, frequencies=tuple(expected_topology[1])))

    def rename(self, name):
        return self._call('rename', {'name':name})

    def connect_peer(self, peer: PeerDevice):
        value = dict(index=peer.index, name=peer.name, priority=peer.priority, is_this_pc=peer.is_this_pc)
        return self._call('peer', value)

    def set_noise_mode(self, mode):
        return self._call('noise-mode', {'mode':mode})

    def pump_idle(self):
        if self._lease is not None:
            self._lease.pump_idle()
        if self._process is not None:
            self._process.poll_idle()

    def cancel(self):
        self.cancelled.set()
        self._terminal()
        lease = self._lease
        if lease is not None:
            lease.cancel()

    def close(self):
        self._cached_snapshot = None
        process, self._process = self._process, None
        if process is not None:
            process.close()
        lease, self._lease = self._lease, None
        if lease is not None:
            lease.close()
