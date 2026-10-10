import importlib.util
import os
from pathlib import Path
import shutil
import signal
import subprocess
import tempfile
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('interrupt_installer', ROOT / 'install.py')
installer = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(installer)


class InstallerInterruptTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='momentum interrupt install ')
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.source = self.root / 'source'
        shutil.copytree(ROOT, self.source, ignore=shutil.ignore_patterns('tests', '__pycache__', 'core'))
        for module in installer.MODULES:
            (self.source / 'momentum_control' / module).touch(exist_ok=True)
        self.core = self.source / 'core/momentum-core-host'
        self.core.parent.mkdir()
        self.core.write_bytes(b'#!/bin/sh\nprintf "momentum-core-host 1.2.3 protocol 1\\n"\n')
        self.core.chmod(0o755)
        self.prefix = self.root / 'prefix'
        source_patch = patch.object(installer, '__file__', str(self.source / 'install.py'))
        source_patch.start()
        self.addCleanup(source_patch.stop)
        dependencies = patch.object(installer.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0))
        dependencies.start()
        self.addCleanup(dependencies.stop)
        self.reset_prefix()

    def reset_prefix(self):
        shutil.rmtree(self.prefix, ignore_errors=True)
        unrelated = self.prefix / 'bin/other-tool'
        unrelated.parent.mkdir(parents=True)
        unrelated.write_bytes(b'unrelated')
        unrelated.chmod(0o710)

    def snapshot(self):
        return {path.relative_to(self.prefix): (path.read_bytes(), path.stat().st_mode & 0o7777)
                for path in self.prefix.rglob('*') if path.is_file()}

    def prepare_update(self):
        self.core.write_bytes(b'#!/bin/sh\nprintf "momentum-core-host 1.2.3 protocol 1\\n"\n')
        installer.install(self.prefix)
        (self.prefix / installer.CORE_HOST).chmod(0o700)
        (self.prefix / 'bin/momentum-control').chmod(0o750)
        previous = self.snapshot()
        self.core.write_bytes(b'#!/bin/sh\nprintf "momentum-core-host 1.2.4 protocol 1\\n"\n')
        return previous

    def assert_restored_and_retry(self, previous, installed):
        self.assertEqual(self.snapshot(), previous)
        self.assertEqual(list(self.prefix.rglob('*.momentum-new')), [])
        self.assertEqual(list(self.prefix.rglob('*.momentum-old')), [])
        if installed:
            self.assertIsNotNone(installer.current_manifest(self.prefix))
        else:
            self.assertFalse((self.prefix / installer.APP).exists())
        installer.install(self.prefix)
        self.assertEqual((self.prefix / installer.CORE_HOST).read_bytes(), self.core.read_bytes())
        installer.uninstall(self.prefix)
        self.assertEqual(self.snapshot(), {Path('bin/other-tool'): (b'unrelated', 0o710)})

    def test_sigint_before_manifest_commit_restores_update(self):
        previous = self.prepare_update()
        replace = os.replace
        interrupted = []
        def interrupt(source, target):
            if Path(target) == self.prefix / installer.MANIFEST and str(source).endswith('.momentum-new'):
                interrupted.append(target)
                signal.raise_signal(signal.SIGINT)
            return replace(source, target)
        with patch.object(installer.os, 'replace', side_effect=interrupt):
            with self.assertRaises(KeyboardInterrupt):
                installer.install(self.prefix)
        self.assertEqual(interrupted, [self.prefix / installer.MANIFEST])
        self.assert_restored_and_retry(previous, True)

    def test_interrupt_after_replace_restores_new_and_existing_targets(self):
        replace = os.replace
        for update in (False, True):
            for relative in (installer.CORE_HOST, installer.MANIFEST):
                with self.subTest(update=update, relative=relative):
                    self.reset_prefix()
                    previous = self.prepare_update() if update else self.snapshot()
                    interrupted = []
                    def interrupt(source, target):
                        result = replace(source, target)
                        if Path(target) == self.prefix / relative and str(source).endswith('.momentum-new'):
                            interrupted.append(target)
                            signal.raise_signal(signal.SIGINT)
                        return result
                    with patch.object(installer.os, 'replace', side_effect=interrupt):
                        with self.assertRaises(KeyboardInterrupt):
                            installer.install(self.prefix)
                    self.assertEqual(interrupted, [self.prefix / relative])
                    self.assert_restored_and_retry(previous, update)

    def test_interrupt_before_first_new_replace_cleans_up_and_allows_retry(self):
        previous = self.snapshot()
        replace = os.replace
        interrupted = []
        def interrupt(source, target):
            if str(source).endswith('.momentum-new'):
                interrupted.append(target)
                signal.raise_signal(signal.SIGINT)
            return replace(source, target)
        with patch.object(installer.os, 'replace', side_effect=interrupt):
            with self.assertRaises(KeyboardInterrupt):
                installer.install(self.prefix)
        self.assertEqual(len(interrupted), 1)
        self.assert_restored_and_retry(previous, False)

    def test_interrupt_after_unlink_restores_uninstall(self):
        unlink = Path.unlink
        for relative in (installer.CORE_HOST, installer.MANIFEST):
            with self.subTest(relative=relative):
                self.reset_prefix()
                previous = self.prepare_update()
                interrupted = []
                def interrupt(path, *args, **kwargs):
                    result = unlink(path, *args, **kwargs)
                    if path == self.prefix / relative:
                        interrupted.append(path)
                        signal.raise_signal(signal.SIGINT)
                    return result
                with patch.object(Path, 'unlink', interrupt):
                    with self.assertRaises(KeyboardInterrupt):
                        installer.uninstall(self.prefix)
                self.assertEqual(interrupted, [self.prefix / relative])
                self.assert_restored_and_retry(previous, True)

    def test_rollback_error_retains_failed_original_and_restores_others(self):
        previous = self.prepare_update()
        replace = os.replace
        failed = self.prefix / installer.CORE_HOST
        injected = []
        def interrupt(source, target):
            if Path(target) == self.prefix / installer.MANIFEST and str(source).endswith('.momentum-new'):
                injected.append('commit')
                signal.raise_signal(signal.SIGINT)
            if Path(target) == failed and str(source).endswith('.momentum-old'):
                injected.append('rollback')
                raise OSError('injected restoration failure')
            return replace(source, target)
        with patch.object(installer.os, 'replace', side_effect=interrupt):
            with self.assertRaises(BaseException) as raised:
                installer.install(self.prefix)
        self.assertIsInstance(raised.exception, OSError)
        self.assertIn('rollback failed: injected restoration failure', str(raised.exception))
        self.assertEqual(injected, ['commit', 'rollback'])
        backup = failed.with_name(failed.name + '.momentum-old')
        expected = dict(previous)
        expected[installer.CORE_HOST] = (self.core.read_bytes(), 0o755)
        expected[backup.relative_to(self.prefix)] = previous[installer.CORE_HOST]
        self.assertEqual(self.snapshot(), expected)
        with self.assertRaises(ValueError):
            installer.current_manifest(self.prefix)
        os.replace(backup, failed)
        self.assert_restored_and_retry(previous, True)

    def test_second_sigint_during_rollback_retains_unprocessed_originals(self):
        replace = os.replace
        for after_restore in (False, True):
            with self.subTest(after_restore=after_restore):
                self.reset_prefix()
                previous = self.prepare_update()
                manifest = self.prefix / installer.MANIFEST
                injected = []
                def interrupt(source, target):
                    if Path(target) == manifest and str(source).endswith('.momentum-new'):
                        injected.append('commit')
                        signal.raise_signal(signal.SIGINT)
                    if Path(target) == manifest and str(source).endswith('.momentum-old'):
                        injected.append('rollback')
                        if after_restore:
                            replace(source, target)
                        signal.raise_signal(signal.SIGINT)
                    return replace(source, target)
                with patch.object(installer.os, 'replace', side_effect=interrupt):
                    with self.assertRaises(KeyboardInterrupt):
                        installer.install(self.prefix)
                self.assertEqual(injected, ['commit', 'rollback'])
                expected_backups = {}
                for relative, original in previous.items():
                    if relative == Path('bin/other-tool') or (after_restore and relative == installer.MANIFEST):
                        continue
                    backup = (self.prefix / relative).with_name(relative.name + '.momentum-old')
                    self.assertTrue(backup.is_file(), str(backup))
                    self.assertEqual((backup.read_bytes(), backup.stat().st_mode & 0o7777), original)
                    expected_backups[backup] = self.prefix / relative
                self.assertEqual(set(self.prefix.rglob('*.momentum-old')), set(expected_backups))
                self.assertEqual(list(self.prefix.rglob('*.momentum-new')), [])
                with self.assertRaises(ValueError):
                    installer.current_manifest(self.prefix)
                for backup, target in expected_backups.items():
                    replace(backup, target)
                self.assert_restored_and_retry(previous, True)


if __name__ == '__main__':
    unittest.main()
