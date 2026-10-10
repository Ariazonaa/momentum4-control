from pathlib import Path
import os
import tempfile
import time
import unittest
from unittest.mock import patch
from types import SimpleNamespace


class HidAdapterTests(unittest.TestCase):
    def setUp(self):
        from momentum_control import hidraw
        from momentum_control.models import DongleDevice
        self.adapter_module = hidraw
        self.device = DongleDevice('/dev/hidraw10','/dev/hidraw11','/sys/devices/usb1/1-2','identity')

    def test_unknown_and_foreign_endpoint_denied(self):
        adapter = self.adapter_module.HidAdapter(self.device)
        self.addCleanup(adapter.close)
        with self.assertRaises(RuntimeError):
            adapter.call('write', {'endpoint':99,'data':'AA=='}, time.monotonic()+1, lambda:False)
        with self.assertRaises(RuntimeError):
            adapter.call('open', {'kind':'control','target':9,'timeout_ms':100}, time.monotonic()+1, lambda:False)
        with self.assertRaises(RuntimeError):
            adapter.call('ioctl', {'path':'/etc/passwd'}, time.monotonic()+1, lambda:False)

    def test_single_report_read_bound_and_partial_write(self):
        module = self.adapter_module
        with patch.object(module.select,'select',side_effect=lambda r,w,e,t:(r,w,[])):
            with patch.object(module.os,'read',return_value=b'a'*256):
                with self.assertRaisesRegex(RuntimeError,'report'):
                    module._read(42,time.monotonic()+1,lambda:False)
            with patch.object(module.os,'read',return_value=b'a'*255):
                self.assertEqual(len(module._read(42,time.monotonic()+1,lambda:False)),255)
            with patch.object(module.os,'write',return_value=2):
                with self.assertRaisesRegex(RuntimeError,'Incomplete'):
                    module._write(42,b'abc',time.monotonic()+1,lambda:False)

    def test_feature_uses_one_fixed_ioctl_and_preserves_raw_buffer(self):
        adapter = self.adapter_module.HidAdapter(self.device)
        adapter._lease = SimpleNamespace(close=lambda:None)
        adapter._endpoints[1]=(42,b'descriptor')
        raw = bytes((3,1,2))+bytes(60)
        import base64
        args={'endpoint':1,'data':base64.b64encode(raw).decode(),'timeout_ms':100}
        with patch.object(self.adapter_module.fcntl,'ioctl',return_value=63) as ioctl:
            result=adapter.call('feature',args,time.monotonic()+1,lambda:False)
            self.assertEqual(result,{'written':63})
            self.assertEqual(ioctl.call_args.args[:2],(42,0xc03f4806))
            self.assertEqual(bytes(ioctl.call_args.args[2]),raw)
        with patch.object(self.adapter_module.fcntl,'ioctl',return_value=1):
            with self.assertRaisesRegex(RuntimeError,'feature'):
                adapter.call('feature',args,time.monotonic()+1,lambda:False)
        args['data']=base64.b64encode(b'\x99'*63).decode()
        with patch.object(self.adapter_module.fcntl,'ioctl',return_value=63) as ioctl:
            self.assertEqual(adapter.call('feature',args,time.monotonic()+1,lambda:False),{'written':63})
            self.assertEqual(bytes(ioctl.call_args.args[2]),b'\x99'*63)
        for length in (62,64):
            args['data']=base64.b64encode(b'a'*length).decode()
            with self.subTest(length=length),patch.object(self.adapter_module.fcntl,'ioctl',return_value=63) as ioctl:
                with self.assertRaises(RuntimeError):
                    adapter.call('feature',args,time.monotonic()+1,lambda:False)
                ioctl.assert_not_called()

    def test_instance_observation_reads_devnum_and_node_generation(self):
        with tempfile.TemporaryDirectory() as directory:
            usb=Path(directory)/'usb'; usb.mkdir(); (usb/'devnum').write_text('4')
            device=SimpleNamespace(usb_path=str(usb))
            first=self.adapter_module._usb_instance(device)
            self.assertEqual(first.devnum,4)
            self.assertEqual(first.node,(usb.stat().st_dev,usb.stat().st_ino))
            (usb/'devnum').write_text('5')
            self.assertEqual(self.adapter_module._usb_instance(device).devnum,5)
            (usb/'devnum').write_text('128')
            with self.assertRaises(RuntimeError):
                self.adapter_module._usb_instance(device)

    def test_open_rejects_wrong_vendor_regular_file_and_device_number(self):
        import stat
        import struct
        module=self.adapter_module
        for fault in ('vendor','regular','number','instance'):
            closed=[]; instances=iter((module._USBInstance(4,(1,20),300),module._USBInstance(5 if fault=='instance' else 4,(1,20),300)))
            def ioctl(fd, operation, buffer, mutate):
                number=operation&255
                if number==3: buffer[:]=struct.pack('Ihh',3,0x1234 if fault=='vendor' else 0x3542,0x3001)
                elif number==1: buffer[:]=struct.pack('I',1)
                elif number==2: buffer[4]=0
                return len(buffer)
            with self.subTest(fault=fault), patch.object(module,'discover_dongles',return_value=(self.device,)), patch.object(module,'_usb_instance',side_effect=lambda _:next(instances)), patch.object(module,'_small_text',return_value='240:99' if fault=='number' else '240:11'), patch.object(module.os,'open',return_value=42), patch.object(module.os,'fstat',return_value=SimpleNamespace(st_mode=stat.S_IFREG if fault=='regular' else stat.S_IFCHR,st_rdev=os.makedev(240,11))), patch.object(module.os,'close',side_effect=closed.append), patch.object(module.fcntl,'ioctl',side_effect=ioctl):
                with self.assertRaises(RuntimeError): module._open(self.device,'upgrade')
                self.assertEqual(closed,[42])

    def test_permission_error_is_actionable_and_open_flags_deny_symlinks(self):
        module=self.adapter_module
        with patch.object(module,'discover_dongles',return_value=(self.device,)), patch.object(module,'_usb_instance',return_value=module._USBInstance(4,(1,20),300)), patch.object(module.os,'open',side_effect=PermissionError()) as opened:
            with self.assertRaisesRegex(RuntimeError,'permission|uaccess'): module._open(self.device,'upgrade')
            flags=opened.call_args.args[1]
            self.assertEqual(flags&(os.O_NOFOLLOW|os.O_CLOEXEC|os.O_NONBLOCK),os.O_NOFOLLOW|os.O_CLOEXEC|os.O_NONBLOCK)
        wrong=SimpleNamespace(control_path='/tmp/device',upgrade_path='/tmp/device',usb_path=self.device.usb_path,identity=self.device.identity)
        with patch.object(module,'discover_dongles',return_value=(wrong,)), patch.object(module.os,'open',side_effect=AssertionError('Wrong path')):
            with self.assertRaises(RuntimeError): module._open(wrong,'control')

    def test_session_lock_rejects_concurrent_owner_and_releases(self):
        module=self.adapter_module
        first=module._lock(self.device)
        try:
            with self.assertRaisesRegex(RuntimeError,'busy'): module._lock(self.device)
        finally: first.close()
        module._lock(self.device).close()

    def test_missing_and_changing_instance_are_rejected(self):
        module=self.adapter_module
        with tempfile.TemporaryDirectory() as directory:
            usb=Path(directory)/'usb'; usb.mkdir()
            device=SimpleNamespace(usb_path=str(usb))
            with self.assertRaises(RuntimeError): module._usb_instance(device)
            (usb/'devnum').write_text('4')
            before=SimpleNamespace(st_dev=1,st_ino=20,st_ctime_ns=300)
            after=SimpleNamespace(st_dev=1,st_ino=21,st_ctime_ns=301)
            with patch.object(Path,'stat',side_effect=(before,after)):
                with self.assertRaisesRegex(RuntimeError,'changed'): module._usb_instance(device)

    def test_blocked_fixed_ioctl_retains_fd_until_cancelled_call_returns(self):
        import base64
        import threading
        module=self.adapter_module
        adapter=module.HidAdapter(self.device)
        released=[]; entered=threading.Event(); unblock=threading.Event(); cancelled=threading.Event(); errors=[]
        adapter._lease=SimpleNamespace(close=lambda:released.append('lease'))
        adapter._endpoints[1]=(42,b'descriptor')
        def ioctl(*args): entered.set(); unblock.wait(2); return 63
        def operate():
            try: adapter.call('feature',dict(endpoint=1,data=base64.b64encode(bytes((3,1,2))+bytes(60)).decode(),timeout_ms=1000),time.monotonic()+1,cancelled.is_set)
            except RuntimeError as error: errors.append(error)
            finally: adapter.close()
        with patch.object(module.fcntl,'ioctl',side_effect=ioctl), patch.object(module.os,'close',side_effect=lambda fd:released.append(fd)):
            thread=threading.Thread(target=operate); thread.start()
            self.assertTrue(entered.wait(1)); cancelled.set()
            self.assertEqual(released,[])
            unblock.set(); thread.join(1)
            self.assertFalse(thread.is_alive()); self.assertEqual(len(errors),1)
            self.assertEqual(released,[42,'lease'])
            adapter.close(); self.assertEqual(released,[42,'lease'])

    def test_sysfs_discovery_groups_interfaces_and_fingerprints_serial(self):
        d=self.adapter_module
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            usb = base / 'devices' / '1-2'
            usb.mkdir(parents=True)
            (usb / 'idVendor').write_text('3542\n')
            (usb / 'idProduct').write_text('3001\n')
            (usb / 'serial').write_text('synthetic-private-serial\n')
            classes = base / 'class'
            classes.mkdir()
            for index, number in ((31, '00'), (42, '01')):
                interface = usb / ('1-2:1.' + str(index))
                interface.mkdir()
                (interface / 'bInterfaceNumber').write_text(number)
                hid = interface / 'synthetic-hid'
                hid.mkdir()
                entry = classes / ('hidraw' + str(index))
                entry.mkdir()
                (entry / 'device').symlink_to(hid)
            with patch.object(d, '_SYSFS', classes):
                devices = d.discover_dongles()
                self.assertEqual(len(devices), 1)
                self.assertEqual((devices[0].control_path, devices[0].upgrade_path), ('/dev/hidraw31', '/dev/hidraw42'))
                self.assertNotIn('synthetic-private-serial', repr(devices))
                (usb / 'serial').write_text('replacement')
                self.assertNotEqual(d.discover_dongles()[0].identity, devices[0].identity)
                (usb / 'idProduct').write_text('3002')
                self.assertEqual(d.discover_dongles(), ())
