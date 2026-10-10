import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch
from momentum_control import updates


class ReleaseProcessTests(unittest.TestCase):
    result = {'tag': 'v1.2', 'url': 'https://github.com/Ariazonaa/momentum4-control/releases/tag/v1.2'}

    def child(self, script):
        real_popen = subprocess.Popen
        self.processes = []
        def start(command, **options):
            self.assertEqual(command, [sys.executable, '-I', '-S', str(Path(updates.__file__).resolve())])
            self.assertEqual(options, dict(stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL, close_fds=True, env={}, cwd='/'))
            process = real_popen([sys.executable, '-I', '-S', '-c', script], **options)
            self.processes.append(process)
            return process
        return patch.object(updates.subprocess, 'Popen', side_effect=start)

    def assert_reaped(self):
        self.assertEqual(len(self.processes), 1)
        process = self.processes[0]
        self.assertIsNotNone(process.returncode)
        self.assertTrue(process.stdout.closed)
        with self.assertRaises(ChildProcessError):
            os.waitpid(process.pid, os.WNOHANG)

    def test_success_is_isolated_and_does_not_inherit_open_device_descriptor(self):
        with tempfile.TemporaryFile() as descriptor:
            os.set_inheritable(descriptor.fileno(), True)
            script = f'''import json, os, sys
assert sys.flags.isolated and sys.flags.no_site
assert sys.flags.ignore_environment
assert set(os.environ) <= {{'LC_CTYPE'}}
try:
    os.fstat({descriptor.fileno()})
except OSError:
    pass
else:
    raise AssertionError('inherited descriptor')
print({json.dumps(self.result)!r})
'''
            with self.child(script):
                result = updates.check_project_release()
        self.assertEqual(result, updates.ProjectRelease(**self.result))
        self.assertEqual(self.processes[0].returncode, 0)
        self.assert_reaped()

    def test_fixed_helper_entrypoint_returns_validated_release_without_network(self):
        script = f'''import io, runpy, urllib.request
class Reply(io.BytesIO):
    status = 200
class Opener:
    def open(self, request, timeout):
        assert request.full_url == 'https://api.github.com/repos/Ariazonaa/momentum4-control/releases/latest'
        assert timeout <= 3
        return Reply(b'{{"tag_name":"v1.2"}}')
urllib.request.build_opener = lambda *args: Opener()
runpy.run_path({str(Path(updates.__file__).resolve())!r}, run_name='__main__')
'''
        with self.child(script):
            result = updates.check_project_release()
        self.assertEqual(result, updates.ProjectRelease(**self.result))
        self.assert_reaped()

    def test_cancelled_before_start_never_creates_child(self):
        with patch.object(updates.subprocess, 'Popen', side_effect=AssertionError('child started')):
            with self.assertRaisesRegex(updates.ReleaseError, 'cancelled'):
                updates.check_project_release(lambda: True)

    def test_blocked_real_resolver_child_cancels_and_is_reaped(self):
        with tempfile.TemporaryDirectory() as directory:
            entered = Path(directory) / 'resolver-entered'
            script = f'''import runpy, socket, time
from pathlib import Path
def resolver(*args, **kwargs):
    Path({str(entered)!r}).touch()
    time.sleep(2)
    raise socket.gaierror('offline stall')
socket.getaddrinfo = resolver
socket.socket.connect = lambda *args, **kwargs: (_ for _ in ()).throw(AssertionError('network forbidden'))
runpy.run_path({str(Path(updates.__file__).resolve())!r}, run_name='__main__')
'''
            started = time.monotonic()
            with self.child(script), self.assertRaisesRegex(updates.ReleaseError, 'cancelled'):
                updates.check_project_release(lambda: entered.exists() and time.monotonic() - started > .2)
            self.assertTrue(entered.exists())
            self.assertLess(time.monotonic() - started, 1.5)
            self.assert_reaped()

    def test_ignored_terminate_escalates_to_kill_and_reaps(self):
        script = "import signal, time; signal.signal(signal.SIGTERM, signal.SIG_IGN); time.sleep(2)"
        started = time.monotonic()
        with self.child(script), self.assertRaisesRegex(updates.ReleaseError, 'cancelled'):
            updates.check_project_release(lambda: time.monotonic() - started > .3)
        self.assertLess(time.monotonic() - started, 1.5)
        self.assertEqual(self.processes[0].returncode, -signal.SIGKILL)
        self.assert_reaped()

    def test_real_stalled_child_has_absolute_ten_second_deadline(self):
        started = time.monotonic()
        with self.child('import time; time.sleep(11)'), self.assertRaisesRegex(updates.ReleaseError, 'timed out'):
            updates.check_project_release()
        elapsed = time.monotonic() - started
        self.assertGreaterEqual(elapsed, 9.9)
        self.assertLess(elapsed, 11.5)
        self.assert_reaped()

    def test_result_overflow_stops_and_reaps_still_running_child(self):
        with self.child("import sys, time; sys.stdout.write('x' * 1025); sys.stdout.flush(); time.sleep(2)"), self.assertRaisesRegex(updates.ReleaseError, 'size limit'):
            updates.check_project_release()
        self.assert_reaped()

    def test_malformed_wrong_url_tag_and_exit_status_are_rejected(self):
        cases = [('', 0), ('[]', 0), ('{', 0), ('{"tag":"v1","tag":"v2","url":"x"}', 0),
            (json.dumps(dict(self.result, url='https://evil.invalid')), 0),
            (json.dumps(dict(self.result, tag='../secret', url=updates.TAG_ROOT + '../secret')), 0),
            (json.dumps(dict(self.result, tag=True)), 0),
            (json.dumps(dict(self.result, extra=1)), 0),
            (json.dumps(self.result), 1)]
        for data, status in cases:
            with self.subTest(data=data, status=status):
                with self.child(f'import sys; print({data!r}); sys.exit({status})'), self.assertRaises(updates.ReleaseError):
                    updates.check_project_release()
                self.assert_reaped()

    def test_start_failure_has_generic_error(self):
        with patch.object(updates.subprocess, 'Popen', side_effect=OSError('private path')):
            with self.assertRaisesRegex(updates.ReleaseError, '^Project release check failed$'):
                updates.check_project_release()
