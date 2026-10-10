import importlib.util
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import tempfile
import time
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('preflight_installer', ROOT / 'install.py')
installer = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(installer)
VALID = b'momentum-core-host 2.3.4-rc.1 protocol 1\n'


def helper(body):
    return ('#!/usr/bin/python3\n' + body + '\n').encode()


def printing(stdout=VALID, stderr=b'', status=0):
    return helper(f'import os\nos.write(1, {stdout!r})\nos.write(2, {stderr!r})\nraise SystemExit({status})')


class PreflightTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='momentum preflight ')
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.source = self.root / 'source'
        shutil.copytree(ROOT, self.source, ignore=shutil.ignore_patterns('tests', '__pycache__', 'core'))
        self.core = self.source / 'core/momentum-core-host'
        self.core.parent.mkdir()
        self.write_core(printing())
        self.prefix = self.root / 'prefix'
        source_patch = patch.object(installer, '__file__', str(self.source / 'install.py'))
        source_patch.start()
        self.addCleanup(source_patch.stop)
        dependencies = patch.object(installer.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0))
        dependencies.start()
        self.addCleanup(dependencies.stop)

    def write_core(self, data):
        self.core.write_bytes(data)
        self.core.chmod(0o755)

    def snapshot(self):
        return {str(path.relative_to(self.prefix)): (path.read_bytes() if path.is_file() else None,
                                                    path.stat().st_mode & 0o7777)
                for path in self.prefix.rglob('*')}

    def test_bad_helpers_leave_fresh_and_upgrade_prefix_unchanged(self):
        cases = [b'invalid executable', printing(status=3), printing(b''),
                 printing(b'momentum-core-host 1.2.3 protocol 2\n'),
                 printing(b'momentum-core-host 1.2.3 protocol 1\n\n'),
                 printing(b'momentum-core-host 1.2.3 protocol 1\x1b[31m'),
                 printing(b'momentum-core-host 1.2.3 protocol 1\n' + b'x' * 256),
                 printing(stderr=b'private diagnostic\x1b[31m' + b'x' * 4097)]
        for installed in (False, True):
            if installed:
                self.write_core(printing())
                installer.install(self.prefix)
            previous = self.snapshot()
            for data in cases:
                with self.subTest(installed=installed, data=data[:80]):
                    self.write_core(data)
                    with self.assertRaisesRegex(ValueError, 'Shared core') as raised:
                        installer.install(self.prefix)
                    self.assertNotIn('private diagnostic', str(raised.exception))
                    self.assertNotIn('\x1b', str(raised.exception))
                    self.assertEqual(self.snapshot(), previous)
                    if not installed:
                        self.assertFalse(self.prefix.exists())

    def test_version_grammar_and_canonical_metadata(self):
        for suffix in (b'', b'\n', b'\r\n'):
            with self.subTest(suffix=suffix):
                self.write_core(printing(VALID.rstrip(b'\n') + suffix))
                installer.install(self.prefix)
                self.assertEqual((self.prefix / installer.APP / 'core/version.txt').read_bytes(), b'2.3.4-rc.1\n')
                self.assertEqual(len(installer.current_manifest(self.prefix)), 30)
        for version in (b'1.2', b'1.2.3+build', b'1.2.3-', b'1.2.3-.bad', b'1.2.3-/bad',
                        b'1.2.3-hello world', b'1.2.3-\xff', b'1.2.3-' + b'a' * 59):
            with self.subTest(version=version):
                previous = self.snapshot()
                self.write_core(printing(b'momentum-core-host ' + version + b' protocol 1\n'))
                with self.assertRaises(ValueError):
                    installer.install(self.prefix)
                self.assertEqual(self.snapshot(), previous)
        self.write_core(printing(b'momentum-core-host ' + b'1.2.3-' + b'a' * 58 + b' protocol 1\n'))
        installer.install(self.prefix)
        self.assertEqual((self.prefix / installer.CORE_VERSION).read_bytes(), b'1.2.3-' + b'a' * 58 + b'\n')

    def test_legacy_manifest_upgrade_and_uninstall_do_not_probe_broken_helper(self):
        installer.install(self.prefix)
        metadata = self.prefix / installer.APP / 'core/version.txt'
        metadata.unlink()
        manifest_path = self.prefix / installer.MANIFEST
        manifest = json.loads(manifest_path.read_text())
        del manifest['files'][str(metadata.relative_to(self.prefix))]
        manifest_path.write_text(json.dumps(manifest))
        self.assertEqual(len(installer.current_manifest(self.prefix)), 29)
        (self.source / 'core/version.txt').write_bytes(b'not trusted\n')
        installer.install(self.prefix)
        self.assertEqual(metadata.read_bytes(), b'2.3.4-rc.1\n')
        self.core.unlink()
        installed_core = self.prefix / installer.CORE_HOST
        installed_core.chmod(0o644)
        installer.uninstall(self.prefix)
        self.assertFalse((self.prefix / installer.APP).exists())

    def test_probe_and_installed_payload_use_same_frozen_bytes(self):
        original = self.core.read_bytes()
        real_popen = installer.subprocess.Popen
        def mutate_source(*args, **kwargs):
            self.write_core(printing(b'momentum-core-host 9.9.9 protocol 2\n'))
            return real_popen(*args, **kwargs)
        with patch.object(installer.subprocess, 'Popen', side_effect=mutate_source):
            installer.install(self.prefix)
        self.assertEqual((self.prefix / installer.CORE_HOST).read_bytes(), original)
        self.assertNotEqual(self.core.read_bytes(), original)
        self.assertEqual((self.prefix / installer.CORE_VERSION).read_bytes(), b'2.3.4-rc.1\n')

    def test_probe_closes_inherited_fds_stdin_and_environment_and_uses_private_directory(self):
        marker = self.root / 'inherited-file'
        with marker.open('wb') as inherited:
            os.set_inheritable(inherited.fileno(), True)
            self.write_core(helper(f'''import os, pathlib, sys
assert sys.argv[1:] == ['--version']
assert os.read(0, 1) == b''
assert 'PREFLIGHT_SENTINEL' not in os.environ
assert pathlib.Path.cwd() == pathlib.Path(sys.argv[0]).parent
assert pathlib.Path.cwd().stat().st_mode & 0o777 == 0o700
try:
    os.write({inherited.fileno()}, b'inherited')
except OSError:
    pass
else:
    raise SystemExit(8)
os.write(1, {VALID!r})'''))
            with patch.dict(os.environ, {'PREFLIGHT_SENTINEL': 'must not inherit'}):
                installer.install(self.prefix)
        self.assertEqual(marker.read_bytes(), b'')
        self.assertEqual((self.prefix / installer.CORE_VERSION).read_bytes(), b'2.3.4-rc.1\n')

    def test_output_limits_accept_boundary_and_reject_one_byte_over(self):
        for size in (4096, 4097):
            with self.subTest(stderr=size):
                self.write_core(printing(stderr=b'x' * size))
                if size == 4096:
                    installer.install(self.prefix)
                else:
                    previous = self.snapshot()
                    with self.assertRaisesRegex(ValueError, 'output'):
                        installer.install(self.prefix)
                    self.assertEqual(self.snapshot(), previous)
        self.write_core(printing(b'x' * 257))
        with self.assertRaisesRegex(ValueError, 'output'):
            installer.install(self.prefix)

    def test_hanging_and_flooding_processes_are_reaped_and_prefix_unchanged(self):
        for installed in (False, True):
            if installed:
                self.write_core(printing())
                installer.install(self.prefix)
            for behavior in ('time.sleep(30)', 'os.close(1); os.close(2); time.sleep(30)',
                             'while True: os.write(1, b"x" * 1024)',
                             'while True: os.write(2, b"x" * 8192)'):
                with self.subTest(installed=installed, behavior=behavior):
                    marker = self.root / 'child-state'
                    self.write_core(helper(f'''import os, pathlib, signal, time
signal.signal(signal.SIGTERM, signal.SIG_IGN)
pathlib.Path({str(marker)!r}).write_text(str(os.getpid()) + '\\n' + os.getcwd())
{behavior}'''))
                    previous = self.snapshot()
                    started = time.monotonic()
                    with patch.object(installer, 'CORE_PROBE_TIMEOUT', 0.15):
                        with self.assertRaises(ValueError):
                            installer.install(self.prefix)
                    self.assertLess(time.monotonic() - started, 2)
                    child, directory = marker.read_text().splitlines()
                    with self.assertRaises(ProcessLookupError):
                        os.kill(int(child), 0)
                    with self.assertRaises(ChildProcessError):
                        os.waitpid(int(child), os.WNOHANG)
                    self.assertFalse(Path(directory).exists())
                    self.assertEqual(self.snapshot(), previous)

    def test_sigint_during_cleanup_reaps_process_before_propagating(self):
        for installed in (False, True):
            if installed:
                self.write_core(printing())
                installer.install(self.prefix)
            for behavior in ('time.sleep(30)', 'os.write(1, b"x" * 257); time.sleep(30)'):
                with self.subTest(installed=installed, behavior=behavior):
                    marker = self.root / 'cleanup-interrupted-child'
                    self.write_core(helper(f'''import os, pathlib, signal, time

def interrupt_parent(signum, frame):
    time.sleep(0.03)
    os.kill(os.getppid(), signal.SIGINT)

signal.signal(signal.SIGTERM, interrupt_parent)
pathlib.Path({str(marker)!r}).write_text(str(os.getpid()) + '\\n' + os.getcwd())
{behavior}'''))
                    previous = self.snapshot()
                    interruption = KeyboardInterrupt('cleanup interrupted')
                    def interrupt(signum, frame):
                        raise interruption
                    previous_handler = signal.signal(signal.SIGINT, interrupt)
                    started = time.monotonic()
                    try:
                        with patch.object(installer, 'CORE_PROBE_TIMEOUT', 0.15):
                            with self.assertRaises(KeyboardInterrupt) as raised:
                                installer.install(self.prefix)
                        self.assertIs(raised.exception, interruption)
                        self.assertLess(time.monotonic() - started, 2)
                        child, directory = marker.read_text().splitlines()
                        with self.assertRaises(ProcessLookupError):
                            os.kill(int(child), 0)
                        with self.assertRaises(ChildProcessError):
                            os.waitpid(int(child), os.WNOHANG)
                        self.assertFalse(Path(directory).exists())
                        self.assertEqual(self.snapshot(), previous)
                        if not installed:
                            self.assertFalse(self.prefix.exists())
                    finally:
                        signal.signal(signal.SIGINT, previous_handler)
                        if marker.exists():
                            child = int(marker.read_text().splitlines()[0])
                            try:
                                os.kill(child, signal.SIGKILL)
                            except ProcessLookupError:
                                pass
                            try:
                                os.waitpid(child, 0)
                            except ChildProcessError:
                                pass

    def test_sigint_during_kill_wait_reaps_process_before_propagating(self):
        marker = self.root / 'kill-wait-child'
        self.write_core(helper(f'''import os, pathlib, signal, time
signal.signal(signal.SIGTERM, signal.SIG_IGN)
pathlib.Path({str(marker)!r}).write_text(str(os.getpid()))
time.sleep(30)'''))
        process = subprocess.Popen([str(self.core)], stdin=subprocess.DEVNULL,
                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        interruption = KeyboardInterrupt('kill wait interrupted')
        def interrupt(signum, frame):
            raise interruption
        previous_handler = signal.signal(signal.SIGINT, interrupt)
        real_wait = process.wait
        waits = 0
        def interrupted_wait(timeout):
            nonlocal waits
            waits += 1
            if waits == 2:
                os.kill(os.getpid(), signal.SIGINT)
            return real_wait(timeout=timeout)
        try:
            deadline = time.monotonic() + 2
            while not marker.exists() and time.monotonic() < deadline:
                time.sleep(0.01)
            self.assertTrue(marker.exists())
            with patch.object(process, 'wait', side_effect=interrupted_wait):
                with self.assertRaises(KeyboardInterrupt) as raised:
                    installer.stop_core_probe(process)
            self.assertIs(raised.exception, interruption)
            self.assertEqual(process.returncode, -signal.SIGKILL)
            with self.assertRaises(ProcessLookupError):
                os.kill(process.pid, 0)
            with self.assertRaises(ChildProcessError):
                os.waitpid(process.pid, os.WNOHANG)
        finally:
            signal.signal(signal.SIGINT, previous_handler)
            process.kill()
            real_wait(timeout=2)

    def test_sigint_during_probe_reaps_process_before_propagating(self):
        for installed in (False, True):
            with self.subTest(installed=installed):
                if installed:
                    self.write_core(printing())
                    installer.install(self.prefix)
                previous = self.snapshot()
                marker = self.root / 'interrupted-child'
                self.write_core(helper(f'''import os, pathlib, signal, time
pathlib.Path({str(marker)!r}).write_text(str(os.getpid()) + '\\n' + os.getcwd())
os.kill({os.getpid()}, signal.SIGINT)
time.sleep(30)'''))
                with self.assertRaises(KeyboardInterrupt):
                    installer.install(self.prefix)
                child, directory = marker.read_text().splitlines()
                with self.assertRaises(ProcessLookupError):
                    os.kill(int(child), 0)
                with self.assertRaises(ChildProcessError):
                    os.waitpid(int(child), os.WNOHANG)
                self.assertFalse(Path(directory).exists())
                self.assertEqual(self.snapshot(), previous)
                if not installed:
                    self.assertFalse(self.prefix.exists())


if __name__ == '__main__':
    unittest.main()
