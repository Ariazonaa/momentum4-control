import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]


class InstalledGuiSmokeTests(unittest.TestCase):
    def test_real_window_event_loop_and_worker_shutdown(self):
        result = subprocess.run([sys.executable, '-B', str(ROOT / 'smoke.py'), str(ROOT)],
                                capture_output=True, text=True, timeout=15,
                                env={**os.environ, 'QT_QPA_PLATFORM': 'offscreen'})
        self.assertEqual(result.returncode, 0, result.stderr)
        observed = json.loads(result.stdout)
        self.assertEqual({key: observed[key] for key in ('event_loop', 'visible', 'discovered', 'worker_stopped')},
                         dict(event_loop=True, visible=True, discovered=True, worker_stopped=True))
        self.assertEqual(observed['external_calls'], 0)
        self.assertFalse(observed['timeout'])

    def test_mismatched_installed_version_fails(self):
        result = subprocess.run([sys.executable, '-B', str(ROOT / 'smoke.py'), str(ROOT), '--expected-version', 'not-a-release'],
                                capture_output=True, text=True, timeout=15,
                                env={**os.environ, 'QT_QPA_PLATFORM': 'offscreen'})
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Installed version does not match the archive', result.stderr)

    def test_missing_installed_package_cannot_fall_back_to_archive(self):
        with tempfile.TemporaryDirectory() as temporary:
            archive = Path(temporary) / 'archive'
            shutil.copytree(ROOT / 'momentum_control', archive / 'momentum_control')
            shutil.copy2(ROOT / 'smoke.py', archive / 'smoke.py')
            (archive / 'core').mkdir()
            (archive / 'core/version.txt').write_text('2.4.6\n')
            application = Path(temporary) / 'installed application'
            application.mkdir()
            for package_exists in (False, True):
                with self.subTest(package_exists=package_exists):
                    if package_exists:
                        (application / 'momentum_control').mkdir()
                    result = subprocess.run([sys.executable, '-B', str(archive / 'smoke.py'), str(application),
                                             '--expected-version', '2.4.6'],
                                            capture_output=True, text=True, timeout=15,
                                            env={**os.environ, 'QT_QPA_PLATFORM': 'offscreen'})
                    self.assertNotEqual(result.returncode, 0, result.stdout)
                    self.assertIn('Installed module is missing or outside the application', result.stderr)

    def test_installed_modules_cannot_symlink_to_archive(self):
        with tempfile.TemporaryDirectory() as temporary:
            for name in ('package', '__init__.py', 'gui.py'):
                with self.subTest(name=name):
                    application = Path(temporary) / f'installed application {name}'
                    application.mkdir()
                    package = application / 'momentum_control'
                    if name == 'package':
                        package.symlink_to(ROOT / 'momentum_control', target_is_directory=True)
                    else:
                        shutil.copytree(ROOT / 'momentum_control', package)
                        (package / name).unlink()
                        (package / name).symlink_to(ROOT / 'momentum_control' / name)
                    result = subprocess.run([sys.executable, '-B', str(ROOT / 'smoke.py'), str(application)],
                                            capture_output=True, text=True, timeout=15,
                                            env={**os.environ, 'QT_QPA_PLATFORM': 'offscreen'})
                    self.assertNotEqual(result.returncode, 0, result.stdout)
                    self.assertIn('Installed module is missing or outside the application', result.stderr)

    def test_preloaded_archive_modules_have_their_origins_checked(self):
        with tempfile.TemporaryDirectory() as temporary:
            application = Path(temporary) / 'installed application'
            shutil.copytree(ROOT / 'momentum_control', application / 'momentum_control')
            for module in ('momentum_control', 'momentum_control.gui'):
                with self.subTest(module=module):
                    script = f"""
import runpy
import sys
sys.path.insert(0, {str(ROOT)!r})
import momentum_control.gui
if {module!r} == 'momentum_control':
    momentum_control.__path__ = [{str(application / 'momentum_control')!r}]
    del sys.modules['momentum_control.gui']
    del momentum_control.gui
else:
    del sys.modules['momentum_control']
sys.argv = [{str(ROOT / 'smoke.py')!r}, {str(application)!r}]
runpy.run_path(sys.argv[0], run_name='__main__')
"""
                    result = subprocess.run([sys.executable, '-B', '-c', script],
                                            capture_output=True, text=True, timeout=15,
                                            env={**os.environ, 'QT_QPA_PLATFORM': 'offscreen'})
                    self.assertNotEqual(result.returncode, 0, result.stdout)
                    self.assertIn(f'Imported module is outside the application: {module}', result.stderr)

    def test_installed_application_path_with_spaces(self):
        with tempfile.TemporaryDirectory() as temporary:
            application = Path(temporary) / 'installed application'
            shutil.copytree(ROOT / 'momentum_control', application / 'momentum_control')
            result = subprocess.run([sys.executable, '-B', str(ROOT / 'smoke.py'), 'installed application'],
                                    cwd=temporary, capture_output=True, text=True, timeout=15,
                                    env={**os.environ, 'QT_QPA_PLATFORM': 'offscreen'})
            self.assertEqual(result.returncode, 0, result.stderr)
            observed = json.loads(result.stdout)
            self.assertTrue(observed['visible'])
            self.assertTrue(observed['worker_stopped'])
