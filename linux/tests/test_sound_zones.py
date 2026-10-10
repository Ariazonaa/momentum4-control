from dataclasses import asdict, replace
import json
import math
from pathlib import Path
import tempfile
import unittest

import dbus

from momentum_control.preferences import EqPreset
from momentum_control.sound_zones import GeoClueProvider, LocationFix, SoundZone, ZoneData, ZoneError, ZoneStore, ZoneTracker


ADDRESS = 'AA:BB:CC:DD:EE:FF'
PRESET = EqPreset('Calm', 'M4', (100, 100, 1000), (1.0, 2.0, -1.0))
ZONE = SoundZone('Home', ADDRESS, 0.0, 0.0, 100, PRESET)


class StoreTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.path = Path(self.temporary.name) / 'private' / 'zones.json'
        self.store = ZoneStore(self.path)

    def test_private_roundtrip_and_default(self):
        self.assertEqual(self.store.load(), ZoneData())
        data = ZoneData(True, (ZONE,))
        self.store.save(data)
        self.assertEqual(self.store.load(), data)
        self.assertEqual(self.path.stat().st_mode & 0o777, 0o600)
        self.assertEqual(self.path.parent.stat().st_mode & 0o777, 0o700)

    def test_reject_invalid_fields(self):
        cases = [replace(ZONE, name=''), replace(ZONE, name='x' * 41), replace(ZONE, name='\n'),
                 replace(ZONE, device_address=ADDRESS.lower()), replace(ZONE, latitude=True),
                 replace(ZONE, latitude=91), replace(ZONE, longitude=-181), replace(ZONE, latitude=math.nan),
                 replace(ZONE, radius_m=99), replace(ZONE, radius_m=50001), replace(ZONE, radius_m=True),
                 replace(ZONE, preset=replace(PRESET, gains=(0, 0, 6.1))),
                 replace(ZONE, preset=replace(PRESET, frequencies=(0, 100, 1000)))]
        for zone in cases:
            with self.subTest(zone=cases.index(zone)), self.assertRaises(ZoneError):
                self.store.save(ZoneData(True, (zone,)))
        for data in [ZoneData(1), ZoneData(False, [ZONE]), ZoneData(False, (ZONE,) * 17),
                     ZoneData(False, (ZONE, replace(ZONE, name=' home ', latitude=30))),
                     ZoneData(False, (ZONE, replace(ZONE, name='Second', longitude=0.001)))]:
            with self.subTest(data_type=type(data)), self.assertRaises(ZoneError):
                self.store.save(data)

    def test_boundaries_and_different_devices(self):
        zones = tuple(replace(ZONE, name=str(index), latitude=-75 + index * 10,
                              radius_m=50000 if index else 100) for index in range(16))
        self.store.save(ZoneData(False, zones))
        self.assertEqual(self.store.load().zones, zones)
        other = replace(ZONE, name='Other', device_address='00:11:22:33:44:55')
        self.store.save(ZoneData(True, (ZONE, other)))
        self.assertEqual(len(self.store.load().zones), 2)

    def test_seventeen_individually_valid_zones_rejected(self):
        zones = tuple(replace(ZONE, name=str(index), latitude=-80 + index * 10) for index in range(17))
        with self.assertRaises(ZoneError):
            self.store.save(ZoneData(False, zones))

    def test_finite_out_of_range_coordinates_rejected(self):
        for zone in (replace(ZONE, latitude=91), replace(ZONE, longitude=181)):
            with self.assertRaises(ZoneError):
                self.store.save(ZoneData(False, (zone,)))

    def test_corrupt_preserved_and_bounded(self):
        self.path.parent.mkdir()
        valid = {'version': 1, **asdict(ZoneData(True, (ZONE,)))}
        unknown = dict(valid, surprise=True)
        malformed = [b'{', b'{"version":1,"version":1,"enabled":false,"zones":[]}',
                     json.dumps(unknown).encode(), json.dumps(dict(valid, version=True)).encode(),
                     json.dumps(valid).replace('0.0', 'NaN', 1).encode(), b' ' * (256 * 1024 + 1)]
        for content in malformed:
            self.path.write_bytes(content)
            with self.assertRaises(ZoneError):
                self.store.load()
            with self.assertRaises(ZoneError):
                self.store.save(ZoneData())
            self.assertEqual(self.path.read_bytes(), content)

    def test_valid_document_at_byte_limit_and_one_over(self):
        self.path.parent.mkdir()
        payload = b'{"version":1,"enabled":false,"zones":[]}'
        content = payload + b' ' * (262144 - len(payload))
        self.path.write_bytes(content)
        self.assertEqual(self.store.load(), ZoneData())
        self.path.write_bytes(content + b' ')
        with self.assertRaises(ZoneError):
            self.store.load()

    def test_unsafe_paths(self):
        self.path.parent.mkdir()
        target = Path(self.temporary.name) / 'target'
        target.write_text('preserved')
        self.path.symlink_to(target)
        for operation in [self.store.load, lambda: self.store.save(ZoneData())]:
            with self.assertRaises(ZoneError):
                operation()
        self.assertEqual(target.read_text(), 'preserved')
        other = Path(self.temporary.name) / 'link'
        other.symlink_to(self.path.parent, target_is_directory=True)
        with self.assertRaises(ZoneError):
            ZoneStore(other / 'new').save(ZoneData())


class TrackerTests(unittest.TestCase):
    def setUp(self):
        self.tracker = ZoneTracker()

    def update(self, fix=None, zones=(ZONE,), **kwargs):
        return self.tracker.update(fix or LocationFix(0, 0, 0, 100), zones,
                                   kwargs.get('address', ADDRESS), kwargs.get('model', 'M4'),
                                   kwargs.get('frequencies', (100, 100, 1000)), kwargs.get('now', 100))

    def test_once_per_entry_and_confident_exit(self):
        self.assertEqual(self.update(), ZONE)
        self.assertIsNone(self.update())
        self.assertIsNone(self.update(LocationFix(0, 0.0012, 0, 100)))
        self.assertIsNone(self.update())
        self.assertIsNone(self.update(LocationFix(0, 0.002, 100, 100)))
        self.assertIsNone(self.update())
        self.assertIsNone(self.update(LocationFix(0, 0.002, 0, 100)))
        self.assertEqual(self.update(), ZONE)
        self.tracker.reset()
        self.assertEqual(self.update(), ZONE)

    def test_invalid_stale_uncertain_and_target_guards(self):
        invalid = [LocationFix(0, 0, 101, 100), LocationFix(0, 0, -1, 100),
                   LocationFix(0, 0, 1000001, 100), LocationFix(91, 0, 0, 100),
                   LocationFix(0, 181, 0, 100), LocationFix(math.nan, 0, 0, 100),
                   LocationFix(0, 0, 0, -21), LocationFix(0, 0, 0, 101), LocationFix(True, 0, 0, 100)]
        for fix in invalid:
            with self.subTest(fix_index=invalid.index(fix)):
                self.assertIsNone(self.update(fix))
        for kwargs in [{'address': '00:11:22:33:44:55'}, {'model': 'M5'},
                       {'frequencies': (100, 1000, 100)}, {'frequencies': (100, 1000)},
                       {'frequencies': (True, 100, 1000)}]:
            self.assertIsNone(self.update(**kwargs))
        self.assertEqual(self.update(LocationFix(0, 0, 100, -20)), ZONE)

    def test_active_precedes_touching_zone_and_invalid_preserves_state(self):
        offset = math.degrees(200 / 6371000)
        second = replace(ZONE, name='Next', longitude=offset)
        zones = (ZONE, second)
        self.assertEqual(self.update(zones=zones), ZONE)
        self.assertIsNone(self.update(LocationFix(0, offset * 0.6, 0, 100), zones))
        self.assertIsNone(self.update(LocationFix(0, offset, 0, -30), zones))
        self.assertIsNone(self.update(zones=zones))
        self.assertEqual(self.update(LocationFix(0, offset, 0, 100), zones), second)
        self.assertIsNone(self.update(LocationFix(0, offset, 0, 100), zones))

    def test_ambiguous_overlap_denied(self):
        self.assertIsNone(self.update(zones=(ZONE, replace(ZONE, name='Other'))))
        self.assertEqual(self.update(), ZONE)


class FakeContext:
    def __init__(self):
        self.callbacks = []
        self.iterations = 0
        self.always_pending = False

    def pending(self):
        return self.always_pending or bool(self.callbacks)

    def iteration(self, block):
        if block:
            raise AssertionError('blocking context iteration')
        self.iterations += 1
        if self.callbacks:
            self.callbacks.pop(0)()


class FakeBus:
    def __init__(self, context):
        self.context = context
        self.calls = []
        self.proxies = []
        self.closed = 0
        self.start_reply = None
        self.start_error = None
        self.client_path = dbus.ObjectPath('/org/freedesktop/GeoClue2/Client/1')
        self.location_path = dbus.ObjectPath('/org/freedesktop/GeoClue2/Location/1')
        self.values = {'Latitude': dbus.Double(0), 'Longitude': dbus.Double(0),
                       'Accuracy': dbus.Double(5), 'Timestamp': dbus.Struct((dbus.UInt64(1000), dbus.UInt64(0)))}
        self.failure = None
        self.after_call = None

    def get_object(self, service, path, introspect=True, follow_name_owner_changes=False):
        self.proxies.append((service, path, introspect, follow_name_owner_changes))
        if service != 'org.freedesktop.GeoClue2' or introspect:
            raise AssertionError('wrong D-Bus connection contract')
        bus = self

        class Proxy:
            def __getattr__(self, method):
                def call(*args, **kwargs):
                    bus.calls.append((str(path), method, args, kwargs))
                    if bus.after_call:
                        bus.after_call()
                    if bus.failure == method:
                        raise RuntimeError('private location data must not escape')
                    if method == 'GetClient':
                        return bus.client_path
                    if method == 'Start':
                        bus.start_reply = kwargs['reply_handler']
                        bus.start_error = kwargs['error_handler']
                    if method == 'Get':
                        return bus.location_path if args[1] == 'Location' else bus.values[args[1]]
                return call
        return Proxy()

    def close(self):
        self.closed += 1


class ProviderTests(unittest.TestCase):
    def setUp(self):
        self.monotonic = 100.0
        self.wall = 1000.0
        self.cancelled = False
        self.context = FakeContext()
        self.bus = FakeBus(self.context)
        self.created = 0
        def factory():
            self.created += 1
            return self.bus
        self.provider = GeoClueProvider(cancelled=lambda: self.cancelled, now=lambda: self.monotonic,
                                        wall_now=lambda: self.wall, bus_factory=factory, context=self.context)
        self.addCleanup(self.provider.close)

    def ready(self):
        self.assertIsNone(self.provider.sample())
        self.assertTrue(callable(self.bus.start_reply))
        self.context.callbacks.append(self.bus.start_reply)

    def test_lazy_pending_success_explicit_timeouts_and_close(self):
        self.assertEqual(self.created, 0)
        self.ready()
        self.assertEqual(self.provider.sample(), LocationFix(0, 0, 5, 100))
        self.assertTrue(all(item[3] for item in self.bus.proxies))
        for path, method, args, kwargs in self.bus.calls:
            expected_interface = ('org.freedesktop.DBus.Properties' if method in ('Get', 'Set') else
                                  'org.freedesktop.GeoClue2.Manager' if method == 'GetClient' else
                                  'org.freedesktop.GeoClue2.Client')
            self.assertEqual(kwargs['dbus_interface'], expected_interface)
            self.assertGreater(kwargs['timeout'], 0)
            self.assertLessEqual(kwargs['timeout'], 30 if method == 'Start' else 1)
        self.provider.close()
        self.provider.close()
        self.assertEqual(self.bus.closed, 1)
        methods = [call[1] for call in self.bus.calls]
        self.assertEqual(methods.count('Stop'), 1)
        self.assertEqual(methods.count('DeleteClient'), 1)
        with self.assertRaises(ZoneError):
            self.provider.sample()
        self.assertEqual(self.created, 1)

    def test_pending_deadline_before_callbacks_and_late_callback(self):
        self.ready()
        self.monotonic = 130
        with self.assertRaises(ZoneError):
            self.provider.sample()
        self.assertEqual(self.context.iterations, 0)
        self.bus.start_reply()
        self.bus.start_error(RuntimeError('private'))
        self.assertEqual(self.bus.closed, 1)
        with self.assertRaises(ZoneError):
            self.provider.sample()

    def test_context_pump_is_bounded(self):
        self.ready()
        self.context.always_pending = True
        self.assertEqual(self.provider.sample(), LocationFix(0, 0, 5, 100))
        self.assertEqual(self.context.iterations, 32)

    def test_source_age_repeat_regression(self):
        self.bus.values['Timestamp'] = dbus.Struct((dbus.UInt64(881), dbus.UInt64(0)))
        self.ready()
        self.assertEqual(self.provider.sample().received_at, -19)
        self.wall += 2
        self.monotonic += 2
        with self.assertRaises(ZoneError):
            self.provider.sample()
        self.assertEqual(self.bus.closed, 1)

    def test_repeated_source_timestamp_does_not_renew(self):
        self.ready()
        first = self.provider.sample()
        self.monotonic += 10
        self.wall += 10
        self.bus.values['Latitude'] = dbus.Double(1)
        self.assertEqual(self.provider.sample(), first)
        self.bus.values['Timestamp'] = (999, 0)
        with self.assertRaises(ZoneError):
            self.provider.sample()

    def test_invalid_reply_paths_timestamp_and_privacy(self):
        cases = [('Latitude', True), ('Latitude', dbus.Boolean(True)), ('Latitude', 91),
                 ('Latitude', math.nan), ('Longitude', -181), ('Accuracy', -1), ('Accuracy', 1000001),
                 ('Timestamp', (True, 0)), ('Timestamp', (1000, 1000000)),
                 ('Timestamp', (879, 0)), ('Timestamp', (1011, 0)), ('Timestamp', (1000,)),
                 ('Timestamp', (2**64, 0))]
        for name, value in cases:
            with self.subTest(field=name, case_type=type(value)):
                context = FakeContext()
                bus = FakeBus(context)
                provider = GeoClueProvider(now=lambda: 100, wall_now=lambda: 1000, bus_factory=lambda: bus, context=context)
                provider.sample()
                self.assertTrue(callable(bus.start_reply))
                bus.start_reply()
                bus.values[name] = value
                with self.assertRaises(ZoneError) as raised:
                    provider.sample()
                self.assertNotIn('private', str(raised.exception))
                self.assertEqual(bus.closed, 1)
        for kind in ('client_path', 'location_path'):
            context = FakeContext()
            bus = FakeBus(context)
            setattr(bus, kind, '/org/foreign/1')
            provider = GeoClueProvider(now=lambda: 100, wall_now=lambda: 1000, bus_factory=lambda: bus, context=context)
            if kind == 'location_path':
                provider.sample()
                self.assertTrue(callable(bus.start_reply))
                bus.start_reply()
            with self.assertRaises(ZoneError):
                provider.sample()
            self.assertEqual(bus.closed, 1)

    def test_permission_error_cancellation_setup_failure_and_budget(self):
        self.ready()
        self.bus.start_error(RuntimeError('private'))
        with self.assertRaises(ZoneError):
            self.provider.sample()
        self.assertEqual(self.bus.closed, 1)
        for failure in ('cancelled', 'GetClient', 'Set', 'budget'):
            context = FakeContext()
            bus = FakeBus(context)
            self.monotonic = 100
            self.cancelled = failure == 'cancelled'
            bus.failure = failure
            if failure == 'budget':
                bus.after_call = lambda: setattr(self, 'monotonic', self.monotonic + 1)
            provider = GeoClueProvider(cancelled=lambda: self.cancelled, now=lambda: self.monotonic,
                                        wall_now=lambda: 1000, bus_factory=lambda: bus, context=context)
            with self.assertRaises(ZoneError):
                provider.sample()
            self.assertEqual(bus.closed, 0 if failure == 'cancelled' else 1)
            self.assertIsNone(bus.start_reply)

    def test_delivery_delay_preserves_source_age_and_tracker_rejects(self):
        self.bus.values['Timestamp'] = (881, 0)
        self.ready()
        fix = self.provider.sample()
        self.assertEqual(fix.received_at, -19)
        self.monotonic += 2
        self.assertIsNone(ZoneTracker().update(fix, (ZONE,), ADDRESS, 'M4', (100, 100, 1000), self.monotonic))

    def test_cancel_during_setup_and_after_start_closes_once(self):
        self.bus.after_call = lambda: setattr(self, 'cancelled', True)
        with self.assertRaises(ZoneError):
            self.provider.sample()
        self.assertIsNone(self.bus.start_reply)
        self.assertEqual(self.bus.closed, 1)
        self.provider.close()
        self.assertEqual(self.bus.closed, 1)

    def test_stop_failure_still_deletes_client_and_closes_bus(self):
        self.ready()
        self.bus.failure = 'Stop'
        self.provider.close()
        self.assertEqual([call[1] for call in self.bus.calls].count('DeleteClient'), 1)
        self.assertEqual(self.bus.closed, 1)
        self.bus.start_reply()
        self.provider.close()
        self.assertEqual(self.bus.closed, 1)

    def test_no_location_path_is_pending(self):
        self.ready()
        self.bus.location_path = dbus.ObjectPath('/')
        self.assertIsNone(self.provider.sample())
        self.assertEqual(self.bus.closed, 0)
        self.assertEqual(len(self.bus.proxies), 2)

    def test_future_tolerance_never_creates_future_receipt(self):
        self.bus.values['Timestamp'] = (1010, 0)
        self.ready()
        self.assertEqual(self.provider.sample().received_at, 100)


if __name__ == '__main__':
    unittest.main()
