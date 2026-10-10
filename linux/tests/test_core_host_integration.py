import json
import os
import select
import socket
import subprocess
import threading
import time
import unittest

from momentum_control.core_proxy import CoreProcess
from momentum_control.models import snapshot_from_dict
try:
    from .core_host_peer import HeadsetPeer, host_command
except ImportError:
    from core_host_peer import HeadsetPeer, host_command


class RealCoreHostTests(unittest.TestCase):
    def headset(self, model=None):
        peer=HeadsetPeer(model)
        self.addCleanup(peer.close)
        command=host_command()+['headset','--fd',str(peer.parent.fileno())]
        process=CoreProcess(fd=peer.parent.fileno(),_command=command)
        self.addCleanup(process.close)
        return peer,process

    def test_real_shared_snapshot_setter_and_idle_do_not_read_serial_or_poll(self):
        peer,process=self.headset()
        state=snapshot_from_dict(process.call('snapshot',{}))
        self.assertTrue(state.model.startswith('M4AEBT'))
        self.assertTrue(state.verified_session)
        self.assertIn('touch_controls',state.writable)
        after=snapshot_from_dict(process.call('set-boolean',dict(control='touch_controls',value=False)))
        self.assertFalse(after.settings['touch_controls'])
        self.assertEqual(peer.writes,[(0x1606,b'\x01')])
        self.assertNotIn((0x001d,0x0003,b''),peer.requests)
        self.assertFalse(any(command==0x0007 for _,command,_ in peer.requests))
        count=len(peer.requests); time.sleep(.12); self.assertEqual(len(peer.requests),count)
        self.assertFalse(peer.errors)

    def test_unknown_identity_remains_readonly_and_write_is_not_replayed(self):
        peer,process=self.headset(b'Unknown')
        state=snapshot_from_dict(process.call('snapshot',{}))
        self.assertEqual(state.writable,frozenset())
        with self.assertRaisesRegex(RuntimeError,'unsupported'):
            process.call('set-boolean',dict(control='touch_controls',value=False))
        self.assertEqual(peer.writes,[])
        self.assertIsNotNone(process.process.poll())

    def test_invalid_typed_control_denies_before_setter(self):
        for args in (dict(control='raw',value=True),dict(control='touch_controls',value=1)):
            peer,process=self.headset()
            with self.assertRaisesRegex(RuntimeError,'invalid'):
                process.call('set-boolean',args)
            self.assertEqual(peer.writes,[])

    def test_parent_eof_cancels_blocked_shared_operation(self):
        peer=HeadsetPeer(); peer.stall=True
        self.addCleanup(peer.close)
        process=subprocess.Popen(host_command()+['headset','--fd',str(peer.parent.fileno())],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,pass_fds=(peer.parent.fileno(),),close_fds=True)
        self.addCleanup(lambda:process.kill() if process.poll() is None else None)
        process.stdin.write(b'{"v":1,"kind":"request","id":1,"op":"snapshot","args":{}}\n');process.stdin.flush()
        deadline=time.monotonic()+2
        while not peer.requests and time.monotonic()<deadline: time.sleep(.01)
        self.assertTrue(peer.requests)
        process.stdin.close()
        self.assertEqual(process.wait(timeout=2),0)
        process.stdout.close();process.stderr.close()

    def test_child_rejects_duplicate_id_and_busy_request(self):
        for payload in (
            b'{"v":1,"kind":"request","id":1,"op":"snapshot","args":{}}\n'*2,
            b'{"v":1,"kind":"request","id":1,"id":1,"op":"snapshot","args":{}}\n',
            b'x'*65537+b'\n',
        ):
            peer=HeadsetPeer(); peer.stall=True
            self.addCleanup(peer.close)
            process=subprocess.Popen(host_command()+['headset','--fd',str(peer.parent.fileno())],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,pass_fds=(peer.parent.fileno(),),close_fds=True)
            output,error=process.communicate(payload,timeout=2)
            self.assertNotEqual(process.returncode,0)
            self.assertLessEqual(len(error),128)
            self.assertNotIn(b'"args"',error)

    def test_version_is_offline(self):
        result=subprocess.run(host_command()+['--version'],capture_output=True,timeout=2)
        self.assertEqual(result.returncode,0)
        self.assertIn(b'protocol 1',result.stdout)


class RealDongleHostTests(unittest.TestCase):
    def _dongle_fixture(self):
        import base64
        from momentum_control.dongle import DongleProxy, Codec
        from momentum_control.models import DongleDevice
        device=DongleDevice('/dev/hidraw10','/dev/hidraw11','/sys/devices/usb1/1-2','identity')
        replacement=DongleDevice('/dev/hidraw12','/dev/hidraw13',device.usb_path,device.identity)
        calls=[]; selected=[4]; pending=[]; counter=[0]
        class Adapter:
            def __init__(self, target): self.target=target
            def close(self): calls.append(('adapter-close',{}))
            def call(self, operation, args, deadline, cancelled):
                calls.append((operation,args))
                if operation in ('acquire','release','close'): return {}
                if operation=='observe':
                    counter[0]+=1
                    actual=replacement if selected[0]==8 else device
                    return [dict(id=counter[0],control_path=actual.control_path,upgrade_path=actual.upgrade_path,usb_path=actual.usb_path,identity=actual.identity,device_number=5 if selected[0]==8 else 4,node_device=1,node_inode=21 if selected[0]==8 else 20,change_stamp=301 if selected[0]==8 else 300)]
                if operation=='open':
                    self.assert_kind=args['kind']; return {'endpoint':1}
                if operation=='descriptor': return {'data':base64.b64encode(bytes.fromhex('06a2ff0901a10185347508953f09028102953f09019102c0')).decode()}
                if operation=='write':
                    data=base64.b64decode(args['data']); command=data[2]
                    values={0x12:b'\x03\x0b\0',6:b'\x03',1:b'\0\x03\x01',3:b'\x0f',5:bytes([selected[0]]),7:b'\0',8:b'\x01\x01',0x15:b'\x01'}
                    if command==4: selected[0]=data[4]; payload=b'\0'
                    else: payload=values[command]
                    pending.append(bytes((0x34,0xff,command,len(payload)))+payload+bytes(60-len(payload)))
                    return {'written':len(data)}
                if operation=='read': return {'data':base64.b64encode(pending.pop(0)).decode()}
                raise AssertionError(operation)
        proxy=DongleProxy(_adapter_factory=Adapter,_process_factory=lambda mode,**kwargs:CoreProcess(mode,_command=host_command()+[mode],**kwargs))
        self.addCleanup(proxy.close)
        return proxy,device,replacement,calls

    def test_real_shared_status_and_codec_use_only_bound_os_calls(self):
        import base64
        from momentum_control.dongle import Codec
        proxy,device,replacement,calls=self._dongle_fixture()
        status=proxy.status(device)
        self.assertEqual(str(status.firmware),'3.11.0'); self.assertEqual(status.codec_mask,4)
        after=proxy.codec(device,Codec.APTX_LOSSLESS)
        self.assertEqual(after.codec_mask,8)
        self.assertEqual(after.device,replacement)
        self.assertEqual(proxy.status(after.device).device,replacement)
        writes=[base64.b64decode(args['data']) for op,args in calls if op=='write']
        self.assertEqual([report[4:6] for report in writes if report[2]==4],[b'\x08\0'])
        self.assertFalse(any('path' in args or 'ioctl' in args for _,args in calls))
        proxy.close(); self.assertEqual(calls[-1][0],'adapter-close')

    def test_unprepared_handle_denies_without_os_or_network(self):
        process=CoreProcess('dongle',_command=host_command()+['dongle'],os_call=lambda *args:(_ for _ in ()).throw(AssertionError('OS access')))
        self.addCleanup(process.close)
        process.call('dongle-bind',dict(control_path='/dev/hidraw10',upgrade_path='/dev/hidraw11',usb_path='/sys/devices/usb1/1-2',identity='identity'))
        with self.assertRaisesRegex(RuntimeError,'invalid'):
            process.call('dongle-apply',{'handle':1})
        self.assertIsNotNone(process.process.poll())

    def test_many_immediate_sequential_results_preserve_ids(self):
        peer=HeadsetPeer()
        self.addCleanup(peer.close)
        process=CoreProcess(fd=peer.parent.fileno(),_command=host_command()+['headset','--fd',str(peer.parent.fileno())])
        self.addCleanup(process.close)
        for index in range(1,21):
            with self.subTest(index=index):
                value=process.call('snapshot',{})
                self.assertTrue(value['verified_session'])

    def test_backpressured_terminal_result_still_detects_parent_eof(self):
        import fcntl
        peer=HeadsetPeer(); peer.large_peers=True
        self.addCleanup(peer.close)
        process=subprocess.Popen(host_command()+['headset','--fd',str(peer.parent.fileno())],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,pass_fds=(peer.parent.fileno(),),close_fds=True)
        def cleanup():
            if process.poll() is None: process.kill()
            process.wait()
            for stream in (process.stdin,process.stdout,process.stderr):
                if not stream.closed: stream.close()
        self.addCleanup(cleanup)
        fcntl.fcntl(process.stdout,fcntl.F_SETPIPE_SZ,4096)
        first=dict(v=1,kind='request',id=1,op='snapshot',args={})
        process.stdin.write(json.dumps(first).encode()+b'\n');process.stdin.flush()
        deadline=time.monotonic()+2
        while not any(command==0x1401 and payload==b'\x3f' for _,command,payload in peer.requests) and time.monotonic()<deadline:
            time.sleep(.01)
        self.assertTrue(any(command==0x1401 and payload==b'\x3f' for _,command,payload in peer.requests))
        time.sleep(.05)
        self.assertIsNone(process.poll())
        second=dict(first,id=2)
        process.stdin.write(json.dumps(second).encode()+b'\n');process.stdin.flush()
        process.stdin.close()
        self.assertEqual(process.wait(timeout=2),0)

    def test_direct_cancel_reaps_child_while_ioctl_remains_owned(self):
        from types import SimpleNamespace
        from unittest.mock import patch
        from momentum_control import hidraw
        from momentum_control.dongle import DongleProxy
        from momentum_control.models import DongleDevice
        device=DongleDevice('/dev/hidraw10','/dev/hidraw11','/sys/devices/usb1/1-2','identity')
        entered=threading.Event();unblock=threading.Event();released=[];errors=[];processes=[]
        def factory(mode,**kwargs):
            process=CoreProcess(mode,_command=host_command()+[mode],**kwargs);processes.append(process);return process
        proxy=DongleProxy(_process_factory=factory)
        self.addCleanup(proxy.close)
        def ioctl(*args): entered.set();unblock.wait(3);return 63
        def operate():
            try: proxy.check(device)
            except RuntimeError as error: errors.append(error)
        descriptor=bytes.fromhex('0600ff0901a10185037508953e0902b102850595fe090291028506950c09028102c0')
        with patch.object(hidraw,'discover_dongles',return_value=(device,)),patch.object(hidraw,'_usb_instance',return_value=hidraw._USBInstance(4,(1,20),300)),patch.object(hidraw,'_lock',return_value=SimpleNamespace(close=lambda:released.append('lock'))),patch.object(hidraw,'_open',return_value=(42,descriptor)),patch.object(hidraw,'_close_fd',side_effect=lambda fd:released.append(fd)),patch.object(hidraw.fcntl,'ioctl',side_effect=ioctl):
            thread=threading.Thread(target=operate);thread.start()
            try:
                self.assertTrue(entered.wait(2))
                started=time.monotonic();proxy.cancel()
                self.assertLess(time.monotonic()-started,.8)
                self.assertIsNotNone(processes[0].process.poll())
                self.assertEqual(released,[])
            finally:
                unblock.set();thread.join(2)
            self.assertFalse(thread.is_alive());self.assertEqual(len(errors),1)
            self.assertIn('cancelled',str(errors[0]).lower())
            self.assertEqual(released,[42,'lock'])
            proxy.close();self.assertEqual(released,[42,'lock'])

    def test_native_gui_refresh_after_real_codec_reenumeration_uses_new_paths(self):
        os.environ.setdefault('QT_QPA_PLATFORM','offscreen')
        from PySide6.QtWidgets import QApplication
        from momentum_control.gui import MainWindow
        from momentum_control.dongle import Codec
        app=QApplication.instance() or QApplication([])
        proxy,device,replacement,calls=self._dongle_fixture()
        seen=[]
        class Services:
            def discover(self): return (device,)
            def status(self,target,cancelled=None): seen.append(target);return proxy.status(target,cancelled)
            def codec(self,target,codec,cancelled=None): return proxy.codec(target,codec,cancelled)
            def cancel(self): proxy.cancel()
            def close(self): proxy.close()
        window=MainWindow(discover=lambda cancelled=None:(),client_factory=lambda *args,**kwargs:None,dongle_services=Services())
        def wait(predicate):
            deadline=time.monotonic()+3
            while not predicate() and time.monotonic()<deadline: app.processEvents();time.sleep(.005)
            self.assertTrue(predicate())
        try:
            window.show();wait(lambda:not window.busy)
            window.open_dongle(device);wait(lambda:not window.busy)
            window.codec_picker.setCurrentIndex(window.codec_picker.findData(Codec.APTX_LOSSLESS))
            self.assertTrue(window.apply_codec());wait(lambda:not window.busy)
            self.assertEqual(window.dongle_picker.currentData(),replacement)
            window.dongle_refresh_button.click();wait(lambda:not window.busy)
            self.assertEqual(seen[-1],replacement)
            self.assertEqual(window.dongle_status.device,replacement)
        finally:
            window.close();wait(lambda:not window.thread.isRunning())
            window.deleteLater();app.processEvents()

    def test_cli_command_reaps_retained_dongle_helper_on_return(self):
        import contextlib
        import io
        from unittest.mock import patch
        from momentum_control import cli, dongle
        proxy,device,_,_=self._dongle_fixture()
        original=proxy._process_factory;processes=[]
        def factory(*args,**kwargs):
            process=original(*args,**kwargs);processes.append(process);return process
        proxy._process_factory=factory
        with patch.object(dongle,'_proxy',proxy),patch.object(cli,'discover_dongles',return_value=[device]),contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(cli.main(['dongle-status']),0)
        self.assertIsNone(proxy._process)
        self.assertEqual(len(processes),1)
        self.assertIsNotNone(processes[0].process.poll())

    def test_m5_canonical_capture_maps_eight_bands_and_typed_capabilities(self):
        peer=HeadsetPeer(profile='m5')
        self.addCleanup(peer.close)
        process=CoreProcess(fd=peer.parent.fileno(),_command=host_command()+['headset','--fd',str(peer.parent.fileno())])
        self.addCleanup(process.close)
        state=snapshot_from_dict(process.call('snapshot',{}))
        self.assertEqual(state.model,'M5AEBT Black');self.assertEqual(state.firmware,'6.21.5')
        self.assertEqual(len(state.eq_bands),8)
        self.assertEqual(state.eq_frequencies,(90,325,1500,2500,4500,8000,13000,16000))
        self.assertEqual(state.choices['antiwind'],(0,1))
        self.assertIs(type(state.settings['battery_protection']),bool)
        after=snapshot_from_dict(process.call('set-boolean',dict(control='touch_controls',value=False)))
        self.assertFalse(after.settings['touch_controls'])
        with self.assertRaises(RuntimeError):process.call('set-integer',dict(control='antiwind',value=2))
        self.assertEqual(peer.writes,[(0x1606,b'\x01')])
