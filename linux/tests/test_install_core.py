import importlib.util
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('core_installer', ROOT / 'install.py')
installer = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(installer)
ADAPTER_MODULES = ('core_proxy.py', 'bluez.py', 'hidraw.py', 'models.py', 'errors.py')


class CoreInstallerTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='momentum core install ')
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.source = self.root / 'source'
        shutil.copytree(ROOT, self.source, ignore=shutil.ignore_patterns('tests', '__pycache__', 'core'))
        for name in ADAPTER_MODULES:
            (self.source / 'momentum_control' / name).touch(exist_ok=True)
        self.core = self.source / 'core' / 'momentum-core-host'
        self.core.parent.mkdir()
        self.core.write_bytes(b'#!/bin/sh\n[ "$1" = "--version" ] || exit 2\nprintf "packaging-fixture\\n"\n')
        self.core.chmod(0o755)
        self.prefix = self.root / 'prefix'
        self.core_relative = 'share/momentum4-control-linux/core/momentum-core-host'
        self.source_patch = patch.object(installer, '__file__', str(self.source / 'install.py'))
        self.source_patch.start()
        self.addCleanup(self.source_patch.stop)

    def test_owned_core_and_adapters_install_executable_and_uninstall(self):
        installer.install(self.prefix)
        manifest = installer.current_manifest(self.prefix)
        self.assertIn(self.core_relative, manifest)
        installed = self.prefix / self.core_relative
        self.assertEqual(installed.read_bytes(), self.core.read_bytes())
        self.assertEqual(installed.stat().st_mode & 0o777, 0o755)
        for name in ADAPTER_MODULES:
            self.assertIn(str(installer.APP / 'momentum_control' / name), manifest)
        result = subprocess.run([str(installed), '--version'], cwd='/', env={},
                                capture_output=True, text=True, timeout=2)
        self.assertEqual((result.returncode, result.stdout), (0, 'packaging-fixture\n'))
        installer.uninstall(self.prefix)
        self.assertFalse((self.prefix / installer.APP).exists())

    def test_unbuilt_nonexecutable_empty_and_oversized_core_refused_before_install(self):
        for state in ('missing', 'nonexecutable', 'empty', 'oversized', 'fifo'):
            with self.subTest(state=state):
                self.core.unlink(missing_ok=True)
                if state == 'fifo':
                    os.mkfifo(self.core)
                elif state != 'missing':
                    self.core.write_bytes(b'fixture')
                    self.core.chmod(0o644 if state == 'nonexecutable' else 0o755)
                    if state in ('empty', 'oversized'):
                        with self.core.open('r+b') as output:
                            output.truncate(0 if state == 'empty' else 128 * 1024 * 1024 + 1)
                with self.assertRaises(ValueError):
                    installer.install(self.prefix)
                self.assertFalse(self.prefix.exists())

    def test_core_leaf_and_parent_symlinks_refused(self):
        target = self.root / 'external'
        target.mkdir()
        external = target / 'momentum-core-host'
        shutil.copy2(self.core, external)
        self.core.unlink()
        self.core.symlink_to(external)
        with self.assertRaises(ValueError):
            installer.payloads(self.prefix)
        self.core.unlink()
        self.core.parent.rmdir()
        self.core.parent.symlink_to(target, target_is_directory=True)
        with self.assertRaises(ValueError):
            installer.payloads(self.prefix)
        self.assertEqual(external.read_bytes(), b'#!/bin/sh\n[ "$1" = "--version" ] || exit 2\nprintf "packaging-fixture\\n"\n')

    def test_growing_core_is_bounded_even_when_stat_reported_small(self):
        self.core.write_bytes(b'x' * 33)
        original_stat = Path.stat
        def reported_size(path, *args, **kwargs):
            result = original_stat(path, *args, **kwargs)
            if path == self.core:
                fields = list(result)
                fields[6] = 1
                return os.stat_result(fields)
            return result
        with patch.object(installer, 'MAX_CORE_BYTES', 32), patch.object(Path, 'stat', reported_size):
            with self.assertRaises(ValueError):
                installer.payloads(self.prefix)
        self.assertFalse(self.prefix.exists())

    def test_update_failure_restores_core_bytes_mode_and_manifest(self):
        installer.install(self.prefix)
        installed = self.prefix / self.core_relative
        self.assertTrue(installed.is_file())
        original = installed.read_bytes()
        installed.chmod(0o700)
        manifest_path = self.prefix / installer.MANIFEST
        original_manifest = manifest_path.read_bytes()
        self.core.write_bytes(original + b'\n')
        replace = os.replace
        def fail_manifest(source, target):
            if Path(target) == manifest_path and str(source).endswith('.momentum-new'):
                raise OSError('injected final manifest failure')
            return replace(source, target)
        with patch.object(installer.os, 'replace', side_effect=fail_manifest):
            with self.assertRaisesRegex(OSError, 'injected final manifest failure'):
                installer.install(self.prefix)
        self.assertEqual(installed.read_bytes(), original)
        self.assertEqual(installed.stat().st_mode & 0o777, 0o700)
        self.assertEqual(manifest_path.read_bytes(), original_manifest)
        self.assertIsNotNone(installer.current_manifest(self.prefix))
        self.assertEqual(list(self.prefix.rglob('*.momentum-new')), [])
        self.assertEqual(list(self.prefix.rglob('*.momentum-old')), [])

    def test_modified_installed_core_is_preserved_and_refused(self):
        installer.install(self.prefix)
        installed = self.prefix / self.core_relative
        self.assertTrue(installed.is_file())
        installed.write_bytes(b'changed by user')
        for action in (installer.install, installer.uninstall):
            with self.subTest(action=action.__name__), self.assertRaises(ValueError):
                action(self.prefix)
            self.assertEqual(installed.read_bytes(), b'changed by user')

    def test_oversized_installed_core_is_rejected_before_reading(self):
        installer.install(self.prefix)
        installed = self.prefix / self.core_relative
        self.assertIn(self.core_relative, installer.current_manifest(self.prefix))
        with installed.open('r+b') as output:
            output.truncate(128 * 1024 * 1024 + 1)
        original_open = Path.open
        def guarded_open(path, *args, **kwargs):
            self.assertNotEqual(path, installed, 'Oversized managed binary was read before its size was checked')
            return original_open(path, *args, **kwargs)
        with patch.object(Path, 'open', guarded_open):
            with self.assertRaises(ValueError):
                installer.current_manifest(self.prefix)

    def test_oversized_source_module_never_creates_unmanageable_installation(self):
        module = self.source / 'momentum_control' / '__init__.py'
        module.write_bytes(b'x' * (2 * 1024 * 1024 + 1))
        with self.assertRaises(ValueError):
            installer.install(self.prefix)
        self.assertFalse(self.prefix.exists())


if __name__ == '__main__':
    unittest.main()
