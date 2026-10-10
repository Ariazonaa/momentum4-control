import importlib.util
import json
import os
from pathlib import Path
import subprocess
import shutil
import tempfile
import time
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("momentum_installer", ROOT / "install.py")
installer_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer_module)


class InstallTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='momentum install ')
        self.prefix = Path(self.temp.name) / 'local prefix'
        self.app = self.prefix / 'share/momentum4-control-linux'
        self.installer = ROOT / 'install.py'

    def tearDown(self):
        self.temp.cleanup()

    def run_installer(self, *args):
        return subprocess.run(['/usr/bin/python3', str(self.installer), '--prefix', str(self.prefix), *args], text=True, capture_output=True)

    def files(self):
        return {str(path.relative_to(self.prefix)): (path.read_bytes(), path.stat().st_mode & 0o7777)
                for path in self.prefix.rglob('*') if path.is_file()}

    def test_failed_install_restores_files_modes_and_allows_retry(self):
        original_replace = os.replace
        original_write = Path.open
        for update in (False, True):
            for operation in ('write', 'replace'):
                for fail_at in (2, len(installer_module.payloads(self.prefix)) + 1):
                    with self.subTest(update=update, operation=operation, fail_at=fail_at):
                        shutil.rmtree(self.prefix, ignore_errors=True)
                        if update:
                            installer_module.install(self.prefix)
                            launcher = self.prefix / 'bin/momentum-control'
                            launcher.chmod(0o700)
                        unrelated = self.prefix / 'bin/other-tool'
                        unrelated.parent.mkdir(parents=True, exist_ok=True)
                        unrelated.write_text('keep me')
                        previous = self.files()
                        calls = 0
                        def replace(source, target):
                            nonlocal calls
                            calls += 1
                            if calls == fail_at:
                                raise OSError('injected rename failure')
                            return original_replace(source, target)
                        def open_file(path, mode='r', *args, **kwargs):
                            nonlocal calls
                            if mode == 'xb' and path.name.endswith('.momentum-new'):
                                calls += 1
                                if calls == fail_at:
                                    output = original_write(path, mode, *args, **kwargs)
                                    class FailingOutput:
                                        def __enter__(self):
                                            return self
                                        def __exit__(self, *exception):
                                            return output.__exit__(*exception)
                                        def write(self, data):
                                            output.write(data[:1])
                                            raise OSError('injected write failure')
                                    return FailingOutput()
                            return original_write(path, mode, *args, **kwargs)
                        payload = installer_module.payloads(self.prefix)
                        payload['bin/momentum-control'] += b'\n'
                        payload['share/momentum4-control-linux/momentum_control/__init__.py'] += b'\n'
                        injection = patch.object(installer_module.os, 'replace', replace) if operation == 'replace' else patch.object(Path, 'open', open_file)
                        with patch.object(installer_module, 'payloads', return_value=payload), injection:
                            with self.assertRaisesRegex(OSError, 'injected'):
                                installer_module.install(self.prefix)
                        self.assertEqual(self.files(), previous)
                        if not update:
                            self.assertFalse(self.app.exists())
                        installer_module.install(self.prefix)
                        self.assertEqual(unrelated.read_text(), 'keep me')
                        installer_module.uninstall(self.prefix)

    def test_failed_uninstall_restores_files_and_allows_retry(self):
        original_unlink = Path.unlink
        for fail_at in (2, len(installer_module.payloads(self.prefix)) + 1):
            with self.subTest(fail_at=fail_at):
                installer_module.install(self.prefix)
                previous = self.files()
                calls = 0
                def unlink(path, *args, **kwargs):
                    nonlocal calls
                    if str(path.relative_to(self.prefix)) in previous:
                        calls += 1
                        if calls == fail_at:
                            raise OSError('injected removal failure')
                    return original_unlink(path, *args, **kwargs)
                with patch.object(Path, 'unlink', unlink):
                    with self.assertRaisesRegex(OSError, 'injected removal'):
                        installer_module.uninstall(self.prefix)
                self.assertEqual(self.files(), previous)
                installer_module.uninstall(self.prefix)
                self.assertFalse(self.app.exists())

    def test_install_copies_sources_launchers_run_from_other_cwd_and_uninstall(self):
        result = self.run_installer()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse(self.app.is_symlink())
        self.assertTrue((self.app / 'momentum_control/gui.py').is_file())
        self.assertTrue((self.app / 'LICENSE').is_file())
        self.assertEqual((self.app / 'LICENSE').read_bytes(), (ROOT.parent / 'LICENSE').read_bytes())
        launcher = subprocess.run([str(self.prefix / 'bin/momentum-control'), '--help'], cwd='/', text=True, capture_output=True)
        self.assertEqual(launcher.returncode, 0, launcher.stderr)
        self.assertIn('Momentum Control', launcher.stdout)
        cli = subprocess.run([str(self.prefix / 'bin/momentumctl'), '--help'], cwd='/', text=True, capture_output=True)
        self.assertEqual(cli.returncode, 0, cli.stderr)
        self.assertIn('usage:', cli.stdout)
        result = self.run_installer('--uninstall')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse(self.app.exists())
        self.assertFalse((self.app / 'LICENSE').exists())
        self.assertFalse((self.prefix / 'bin/momentum-control').exists())
        self.assertFalse((self.prefix / 'share/applications/momentum-control.desktop').exists())

    def test_parity_payload_launches_installed_gui_with_all_modules_and_uninstalls(self):
        result = self.run_installer()
        self.assertEqual(result.returncode, 0, result.stderr)
        launcher = self.prefix / 'bin/momentum-control'
        script = f'''import importlib, pathlib, runpy, socket, sys
sys.dont_write_bytecode = True
sys.path.insert(0, {str(self.app)!r})
def forbidden(*args, **kwargs):
    raise AssertionError('external access forbidden')
socket.socket.connect = forbidden
socket.socket.sendto = forbidden
import dbus
dbus.SystemBus = forbidden
dbus.SessionBus = forbidden
from momentum_control import gui
for name in ('preferences', 'desktop', 'i18n', 'updates', 'sound_zones', 'sound_check'):
    module = importlib.import_module('momentum_control.' + name)
    assert pathlib.Path(module.__file__).parent == pathlib.Path({str(self.app / 'momentum_control')!r})
def launch(argv, minimized=False):
    assert argv == [{str(launcher)!r}] and minimized is True
    print('installed GUI entry reached')
    return 0
gui.main = launch
sys.argv = [{str(launcher)!r}, '--minimized']
runpy.run_path({str(launcher)!r}, run_name='__main__')
'''
        result = subprocess.run(['/usr/bin/python3', '-I', '-c', script], cwd='/', capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout, 'installed GUI entry reached\n')
        manifest = json.loads((self.app / 'manifest.json').read_text())['files']
        self.assertEqual(len(manifest), 30)
        for name in ('preferences.py', 'desktop.py', 'i18n.py', 'updates.py', 'sound_zones.py', 'sound_check.py'):
            path = self.app / 'momentum_control' / name
            self.assertEqual(path.read_bytes(), (ROOT / 'momentum_control' / name).read_bytes())
            self.assertEqual(manifest[str(path.relative_to(self.prefix))], installer_module.digest(path.read_bytes()))
        result = self.run_installer('--uninstall')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse(self.app.exists())
        self.assertFalse(launcher.exists())

    def test_dongle_payload_is_owned_and_uninstalled_without_system_changes(self):
        result = self.run_installer()
        self.assertEqual(result.returncode, 0, result.stderr)
        manifest = json.loads((self.app / 'manifest.json').read_text())['files']
        for name in ('dongle.py', 'firmware.py', 'firmware_update.py'):
            path = self.app / 'momentum_control' / name
            self.assertTrue(path.is_file(), f'Missing dongle module: {name}')
            self.assertIn(str(path.relative_to(self.prefix)), manifest)
        rule = self.app / 'assets/70-momentum-btd700.rules'
        self.assertTrue(rule.is_file())
        self.assertIn(str(rule.relative_to(self.prefix)), manifest)
        self.assertFalse((self.prefix / 'etc').exists())
        self.assertEqual(self.run_installer('--uninstall').returncode, 0)
        self.assertFalse(rule.exists())

    def test_incomplete_sources_refused_before_installing_files(self):
        source = Path(self.temp.name) / 'source'
        shutil.copytree(ROOT, source, ignore=shutil.ignore_patterns('__pycache__', 'tests'))
        (source / 'momentum_control/cli.py').unlink()
        self.installer = source / 'install.py'
        result = self.run_installer()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('source', result.stderr)
        self.assertFalse(self.app.exists())

    def test_managed_update_and_uninstall_preserve_unrelated_files(self):
        self.assertEqual(self.run_installer().returncode, 0)
        self.assertEqual(self.run_installer().returncode, 0)
        other = self.prefix / 'bin/other-tool'
        other.write_text('keep me')
        self.assertEqual(self.run_installer('--uninstall').returncode, 0)
        self.assertEqual(other.read_text(), 'keep me')

    def test_refuses_unmanaged_launcher_before_installing_any_files(self):
        target = self.prefix / 'bin/momentum-control'
        target.parent.mkdir(parents=True)
        target.write_text('unrelated app')
        result = self.run_installer()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('unmanaged', result.stderr)
        self.assertEqual(target.read_text(), 'unrelated app')
        self.assertFalse(self.app.exists())

    def test_refuses_modified_managed_launcher_and_unknown_app_files(self):
        self.assertEqual(self.run_installer().returncode, 0)
        target = self.prefix / 'bin/momentum-control'
        target.write_text('modified')
        result = self.run_installer('--uninstall')
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(target.read_text(), 'modified')
        self.assertTrue(self.app.exists())

    def test_symlink_ancestor_does_not_modify_outside_prefix(self):
        outside = Path(self.temp.name) / 'outside'
        outside.mkdir()
        self.prefix.mkdir()
        (self.prefix / 'share').symlink_to(outside, target_is_directory=True)
        result = self.run_installer()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('symlink', result.stderr)
        self.assertEqual(list(outside.iterdir()), [])

    def test_desktop_entry_launches_paths_with_reserved_characters(self):
        import gi
        try:
            gi.require_version('GioUnix', '2.0')
            from gi.repository import GioUnix as Desktop
        except (ImportError, ValueError):
            from gi.repository import Gio as Desktop
        self.prefix = Path(self.temp.name) / 'prefix $ " \\ % ='
        self.app = self.prefix / 'share/momentum4-control-linux'
        self.assertEqual(self.run_installer().returncode, 0)
        marker = Path(self.temp.name) / 'launched'
        launcher = self.prefix / 'bin/momentum-control'
        launcher.write_text('from pathlib import Path\nPath(' + repr(str(marker)) + ').write_text("launched")\n')
        try:
            info = Desktop.DesktopAppInfo.new_from_filename(str(self.prefix / 'share/applications/momentum-control.desktop'))
        except TypeError:
            info = None
        self.assertIsNotNone(info)
        self.assertTrue(info.launch([], None))
        deadline = time.monotonic() + 2
        while not marker.exists() and time.monotonic() < deadline:
            time.sleep(.01)
        self.assertTrue(marker.exists(), 'Desktop launcher did not preserve its path')

    def test_unknown_app_file_prevents_uninstall_before_deleting_managed_files(self):
        self.assertEqual(self.run_installer().returncode, 0)
        unknown = self.app / 'personal.txt'
        unknown.write_text('keep')
        result = self.run_installer('--uninstall')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('unknown', result.stderr)
        self.assertTrue((self.prefix / 'bin/momentum-control').exists())
        self.assertEqual(unknown.read_text(), 'keep')

    def test_uninstall_rejects_manifest_paths_outside_installation(self):
        self.assertEqual(self.run_installer().returncode, 0)
        manifest = self.app / 'manifest.json'
        data = json.loads(manifest.read_text())
        data['files']['../victim'] = 'irrelevant'
        manifest.write_text(json.dumps(data))
        result = self.run_installer('--uninstall')
        self.assertNotEqual(result.returncode, 0)
        self.assertTrue((self.prefix / 'bin/momentum-control').exists())


if __name__ == '__main__':
    unittest.main()
