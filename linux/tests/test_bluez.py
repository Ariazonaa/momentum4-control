import os
import socket
import threading
import unittest
from types import SimpleNamespace
from unittest.mock import patch
from momentum_control import bluez as backend


class BlueZTests(unittest.TestCase):
    def setUp(self):
        self.device = backend.Device('00:11:22:33:44:55', 'Headset',
                                     '/org/bluez/hci0/dev_00_11_22_33_44_55', True)

    def test_unexpected_profile_fd_closed_and_valid_fd_owned(self):
        import os
        class Descriptor:
            def __init__(self, descriptor):
                self.descriptor = descriptor
            def take(self):
                return self.descriptor
        client = backend.BlueZLease(self.device)
        client._accepting = True
        self.addCleanup(client.close)
        local, peer = socket.socketpair()
        self.addCleanup(peer.close)
        descriptor = local.detach()
        with self.assertRaises(backend.ControlError):
            client._accept_connection('/org/bluez/hci0/dev_AA_BB_CC_DD_EE_FF', Descriptor(descriptor))
        with self.assertRaises(OSError):
            os.fstat(descriptor)
        client._accepting = True
        local, peer2 = socket.socketpair()
        self.addCleanup(peer2.close)
        client._accept_connection(self.device.path, Descriptor(local.detach()))
        self.assertIsNotNone(client._socket)
        client.close()
        self.assertEqual(peer2.recv(1), b'')

    def test_cancellation_checked_atomically_with_profile_acceptance(self):
        client = backend.BlueZLease(self.device)
        client._accepting = True
        self.addCleanup(client.close)
        local, peer = socket.socketpair()
        self.addCleanup(peer.close)
        descriptor = local.detach()
        entering = threading.Event()
        mutex = threading.Lock()
        class ObservedLock:
            def __enter__(self):
                entering.set()
                mutex.acquire()
            def __exit__(self, *args):
                mutex.release()
        client._socket_lock = ObservedLock()
        errors = []
        def take():
            return descriptor
        def accept():
            try:
                client._accept_connection(client.device.path, SimpleNamespace(take=take))
            except backend.ControlError as error:
                errors.append(error)
        with client._socket_lock:
            entering.clear()
            worker = threading.Thread(target=accept)
            worker.start()
            self.assertTrue(entering.wait(1))
            client.cancelled.set()
        worker.join(1)
        self.assertFalse(worker.is_alive())
        self.assertEqual(len(errors), 1)
        self.assertIsNone(client._socket)
        with self.assertRaises(OSError):
            os.fstat(descriptor)

    def test_dbus_call_is_async_bounded_and_cancelled(self):
        connection = backend._DBus.__new__(backend._DBus)
        connection.cancelled = threading.Event()
        callbacks = {}
        class Pending:
            cancelled = False
            def cancel(self):
                self.cancelled = True
        pending = Pending()
        def method(*args, **kwargs):
            callbacks.update(kwargs)
            return pending
        def pump(deadline, event):
            self.assertLessEqual(deadline - backend.time.monotonic(), 3)
            callbacks['reply_handler']('resolved')
        connection.pump = pump
        self.assertEqual(connection.call(method), 'resolved')
        self.assertEqual(callbacks['timeout'], 3)
        self.assertTrue(pending.cancelled)
        pending.cancelled = False
        def cancel_pump(deadline, event):
            event.set()
            backend._check_cancel(event)
        connection.pump = cancel_pump
        with self.assertRaisesRegex(backend.ControlError, 'cancelled'):
            connection.call(method)
        self.assertTrue(pending.cancelled)

    def test_profile_removal_failure_still_closes_private_bus_and_lock(self):
        client = backend.BlueZLease(self.device)
        released = []
        class Profile:
            def remove_from_connection(self):
                raise RuntimeError('profile already removed')
        class Bus:
            def close(self):
                released.append('bus')
        class Lock:
            def close(self):
                released.append('lock')
        client._profile = Profile()
        client._dbus = Bus()
        client._device_lock = Lock()
        with self.assertRaises(RuntimeError):
            client.close()
        self.assertEqual(released, ['bus', 'lock'])
        self.assertIsNone(client._dbus)

    def test_profile_lifecycle_marshals_real_dbus_signatures_without_introspection(self):
        import dbus
        import dbus.service
        from dbus.lowlevel import MethodCallMessage
        client = backend.BlueZLease(self.device)
        profiles = []
        calls = []
        peer_sockets = []
        device = self.device
        class ServiceObject:
            def __init__(self, bus, path):
                profiles.append(self)
            def remove_from_connection(self):
                calls.append(('removed', ''))
        class Descriptor:
            def __init__(self, descriptor):
                self.descriptor = descriptor
            def take(self):
                return self.descriptor
        class Interface:
            def GetManagedObjects(self):
                return {device.path: {'org.bluez.Device1': {
                    'Address': device.address, 'Paired': True, 'UUIDs': [backend.GAIA_UUID]}}}
            def RegisterProfile(self, *args):
                message = MethodCallMessage('org.bluez', '/org/bluez', 'org.bluez.ProfileManager1', 'RegisterProfile')
                message.append(*args)
                calls.append(('register', message.get_signature()))
            def ConnectProfile(self, *args):
                local, peer = socket.socketpair()
                peer_sockets.append(peer)
                profiles[0].NewConnection(device.path, Descriptor(local.detach()), {})
            def UnregisterProfile(self, *args):
                message = MethodCallMessage('org.bluez', '/org/bluez', 'org.bluez.ProfileManager1', 'UnregisterProfile')
                message.append(*args)
                calls.append(('unregister', message.get_signature()))
        class Bus:
            def __init__(self, cancelled):
                self.dbus = dbus
                self.bus = object()
            def interface(self, path, interface):
                return Interface()
            def call(self, method, *args, **kwargs):
                return method(*args)
            def close(self):
                calls.append(('closed', ''))
        try:
            with patch.object(backend, '_DBus', Bus), patch.object(dbus.service, 'Object', ServiceObject):
                client.connect()
                self.assertIsNotNone(client._socket)
                client.close()
            self.assertEqual(calls, [('register', 'osa{sv}'), ('unregister', 'o'), ('removed', ''), ('closed', '')])
        finally:
            client.close()
            for peer in peer_sockets:
                peer.close()

    def test_discovery_requires_paired_service_ignores_name(self):
        props = {'Address': self.device.address, 'Alias': 'Custom name', 'Paired': True,
                 'UUIDs': [backend.GAIA_UUID], 'Connected': False}
        objects = {self.device.path: {'org.bluez.Device1': props}}
        found = backend._devices(objects)
        self.assertEqual(found[0].name, 'Custom name')
        for field, value in [('Paired', False), ('UUIDs', [])]:
            with self.subTest(field=field):
                changed = dict(props, **{field: value})
                self.assertEqual(backend._devices({self.device.path: {'org.bluez.Device1': changed}}), [])

    def test_device_lock_fails_fast_and_is_released(self):
        first = backend._DeviceLock(self.device.address)
        try:
            with self.assertRaisesRegex(backend.ControlError, 'another'):
                backend._DeviceLock(self.device.address)
        finally:
            first.close()
        backend._DeviceLock(self.device.address).close()

    def test_cancel_shutdown_is_thread_safe(self):
        client = backend.BlueZLease(self.device)
        self.addCleanup(client.close)
        local, peer = socket.socketpair()
        self.addCleanup(peer.close)
        client._socket = local
        thread = threading.Thread(target=client.cancel)
        thread.start(); thread.join(1)
        self.assertFalse(thread.is_alive())
        self.assertTrue(client.cancelled.is_set())
        self.assertEqual(peer.recv(1), b'')

    def test_cleanup_rejects_late_profile_fd(self):
        for cancelled in (False, True):
            with self.subTest(cancelled=cancelled):
                client = backend.BlueZLease(self.device)
                client._accepting = True
                self.addCleanup(client.close)
                local, peer = socket.socketpair()
                self.addCleanup(peer.close)
                descriptor = local.detach()
                rejected = []
                released = []
                class Bus:
                    dbus = SimpleNamespace(ObjectPath=str)
                    def interface(self, *args):
                        return SimpleNamespace(UnregisterProfile=object())
                    def call(self, *args, **kwargs):
                        try:
                            client._accept_connection(client.device.path, SimpleNamespace(take=lambda: descriptor))
                        except backend.ControlError:
                            rejected.append(True)
                    def close(self):
                        released.append('bus')
                client._dbus = Bus()
                client._registered = True
                client._device_lock = SimpleNamespace(close=lambda: released.append('lock'))
                if cancelled:
                    client.cancel()
                client.close()
                self.assertEqual(rejected, [True])
                self.assertIsNone(client._socket)
                with self.assertRaises(OSError):
                    os.fstat(descriptor)
                self.assertEqual(released, ['bus', 'lock'])
                self.assertEqual(peer.recv(1), b'')
