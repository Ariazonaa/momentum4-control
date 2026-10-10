import json
import os
from pathlib import Path
import socket
import sys
import tempfile
import threading
import time
import unittest


class CoreProxyTests(unittest.TestCase):
    def setUp(self):
        from momentum_control.core_proxy import CoreProcess
        self.process_type = CoreProcess
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)

    def child(self, body, **kwargs):
        path = Path(self.directory.name) / 'peer.py'
        path.write_text('import json, os, sys, time\n' + body)
        process = self.process_type(_command=[sys.executable, str(path)], **kwargs)
        self.addCleanup(process.close)
        return process

    def test_fragmented_result_and_monotonic_requests(self):
        process = self.child("for line in sys.stdin:\n r=json.loads(line)\n out=json.dumps(dict(v=1,kind='result',id=r['id'],value={'seen':r['op']})).encode()+b'\\n'\n for byte in out:\n  os.write(1,bytes([byte]))\n")
        self.assertEqual(process.call('snapshot', {}), {'seen': 'snapshot'})
        self.assertEqual(process.call('rename', {'name': 'A'}), {'seen': 'rename'})

    def test_invalid_replies_stop_and_reap(self):
        messages = [
            '{"v":1,"kind":"result","id":2,"value":{}}',
            '{"v":1,"kind":"result","id":1,"value":{},"extra":0}',
            '{"v":1,"kind":"result","id":1,"id":1,"value":{}}',
            '{"v":1,"kind":"result","id":true,"value":{}}',
            '{"v":2,"kind":"result","id":1,"value":{}}',
            '{"v":1,"kind":"result","id":1,"value":' + '[' * 17 + '0' + ']' * 17 + '}',
            'x' * 262145,
        ]
        for message in messages:
            with self.subTest(message=message[:60]):
                process = self.child("sys.stdin.readline()\nos.write(1," + repr(message.encode()+b'\n') + ")\ntime.sleep(5)\n")
                with self.assertRaisesRegex(RuntimeError, 'protocol'):
                    process.call('snapshot', {}, timeout=.8)
                self.assertIsNotNone(process.process.poll())

    def test_cancel_reaps_blocked_child(self):
        cancelled = threading.Event()
        process = self.child('sys.stdin.readline()\ntime.sleep(10)\n', cancelled=cancelled)
        timer = threading.Timer(.12, cancelled.set)
        timer.start()
        self.addCleanup(timer.cancel)
        started = time.monotonic()
        with self.assertRaisesRegex(RuntimeError, 'cancelled'):
            process.call('snapshot', {}, timeout=2)
        self.assertLess(time.monotonic()-started, .8)
        self.assertIsNotNone(process.process.poll())

    def test_deadline_and_eof_reap(self):
        for body in ('sys.stdin.readline()\ntime.sleep(5)\n', 'sys.stdin.readline()\n'):
            process = self.child(body)
            started=time.monotonic()
            with self.assertRaises(RuntimeError):
                process.call('snapshot', {}, timeout=.1)
            self.assertIsNotNone(process.process.poll())
            self.assertLess(time.monotonic()-started,.8)

    def test_request_limit_denies_before_child_input(self):
        process = self.child('sys.stdin.readline()\ntime.sleep(5)\n')
        with self.assertRaisesRegex(RuntimeError, 'request'):
            process.call('rename', {'name': 'a' * 65536})
        self.assertIsNotNone(process.process.poll())

    def test_os_callback_ids_and_progress_are_typed(self):
        calls = []
        progress = []
        process = self.child("r=json.loads(sys.stdin.readline())\nprint(json.dumps(dict(v=1,kind='os-call',id=1,request=r['id'],op='observe',args={})),flush=True)\na=json.loads(sys.stdin.readline())\nassert a==dict(v=1,kind='os-result',id=1,request=r['id'],value={'generation':7})\nprint(json.dumps(dict(v=1,kind='progress',id=r['id'],stage='transfer',percent=25)),flush=True)\nprint(json.dumps(dict(v=1,kind='result',id=r['id'],value={'ok':True})),flush=True)\n", os_call=lambda op,args,deadline,cancelled: calls.append((op,args)) or {'generation':7})
        self.assertEqual(process.call('dongle-status', {}, progress=lambda *v: progress.append(v)), {'ok':True})
        self.assertEqual(calls, [('observe', {})])
        self.assertEqual(progress, [('transfer',25)])

    def test_only_explicit_socket_is_inherited(self):
        inherited, peer = socket.socketpair()
        unrelated = os.open('/dev/null', os.O_RDONLY)
        os.set_inheritable(unrelated, True)
        self.addCleanup(os.close, unrelated)
        self.addCleanup(inherited.close)
        self.addCleanup(peer.close)
        body = "r=json.loads(sys.stdin.readline())\nresult={}\nfor name,fd in [('selected'," + str(inherited.fileno()) + "),('other'," + str(unrelated) + ")]:\n try:\n  os.fstat(fd)\n  result[name]=True\n except OSError:\n  result[name]=False\nprint(json.dumps(dict(v=1,kind='result',id=r['id'],value=result)),flush=True)\n"
        process = self.child(body, fd=inherited.fileno())
        self.assertEqual(process.call('snapshot', {}), {'selected':True,'other':False})

    def test_bounded_event_pump_runs_during_wait(self):
        pumps = []
        process = self.child("r=json.loads(sys.stdin.readline())\ntime.sleep(.16)\nprint(json.dumps(dict(v=1,kind='result',id=r['id'],value={})),flush=True)\n", pump=lambda: pumps.append(time.monotonic()))
        process.call('snapshot', {})
        self.assertGreaterEqual(len(pumps), 3)
        self.assertLess(max(b-a for a,b in zip(pumps,pumps[1:])), .075)

    def test_idle_unsolicited_result_revokes_process(self):
        process=self.child("r=json.loads(sys.stdin.readline())\nprint(json.dumps(dict(v=1,kind='result',id=r['id'],value={})),flush=True)\nprint(json.dumps(dict(v=1,kind='result',id=r['id'],value={})),flush=True)\ntime.sleep(2)\n")
        self.assertEqual(process.call('snapshot',{}),{})
        time.sleep(.02)
        with self.assertRaisesRegex(RuntimeError,'protocol'): process.poll_idle()
        self.assertIsNotNone(process.process.poll())

    def test_diagnostic_output_is_bounded_and_never_returned(self):
        process=self.child("sys.stdin.readline()\nos.write(2,b'x'*8193)\ntime.sleep(2)\n")
        with self.assertRaisesRegex(RuntimeError,'protocol') as result: process.call('snapshot',{})
        self.assertNotIn('xxx',str(result.exception))
        self.assertIsNotNone(process.process.poll())

    def test_oversized_request_denies_before_serialization(self):
        from unittest.mock import patch
        from momentum_control import core_proxy
        process=self.child('sys.stdin.readline()\ntime.sleep(2)\n')
        import builtins
        def character_code(character):
            if character=='z':
                raise AssertionError('Oversized string traversed before bound')
            return builtins.ord(character)
        with patch.object(core_proxy.json,'dumps',side_effect=AssertionError('Unbounded serialization')),patch.object(core_proxy,'ord',side_effect=character_code,create=True):
            with self.assertRaisesRegex(RuntimeError,'request'):
                process.call('rename',{'name':'z'*65537})

    def test_direct_cancel_can_interrupt_waiting_call(self):
        process=self.child('sys.stdin.readline()\ntime.sleep(5)\n')
        errors=[]
        thread=threading.Thread(target=lambda:self._capture_call(process,errors))
        thread.start();time.sleep(.1);process.cancel();thread.join(1)
        self.assertFalse(thread.is_alive());self.assertEqual(len(errors),1)
        self.assertIn('cancelled',str(errors[0]).lower());self.assertIsNotNone(process.process.poll())

    @staticmethod
    def _capture_call(process,errors):
        try: process.call('snapshot',{})
        except RuntimeError as error: errors.append(error)

    def test_no_dongle_descriptors_are_inherited(self):
        unrelated=os.open('/dev/null',os.O_RDONLY);os.set_inheritable(unrelated,True)
        self.addCleanup(os.close,unrelated)
        body="r=json.loads(sys.stdin.readline())\ntry:\n os.fstat("+str(unrelated)+")\n inherited=True\nexcept OSError:\n inherited=False\nprint(json.dumps(dict(v=1,kind='result',id=r['id'],value={'inherited':inherited})),flush=True)\n"
        process=self.child(body)
        self.assertEqual(process.call('dongle-status',{}),{'inherited':False})

    def test_progress_flood_has_a_fixed_work_limit(self):
        process=self.child("r=json.loads(sys.stdin.readline())\nfor index in range(1025):\n print(json.dumps(dict(v=1,kind='progress',id=r['id'],stage='transfer',percent=index%101)),flush=True)\nprint(json.dumps(dict(v=1,kind='result',id=r['id'],value={})),flush=True)\ntime.sleep(2)\n")
        observed=[]
        with self.assertRaisesRegex(RuntimeError,'protocol'):
            process.call('dongle-apply',{},progress=lambda *args:observed.append(args))
        self.assertEqual(len(observed),1024)
        self.assertIsNotNone(process.process.poll())

    def test_reordered_valid_reply_fields_are_accepted(self):
        process=self.child("r=json.loads(sys.stdin.readline())\nprint(json.dumps(dict(value={'ok':True},id=r['id'],kind='result',v=1)),flush=True)\n")
        self.assertEqual(process.call('snapshot',{}),{'ok':True})
