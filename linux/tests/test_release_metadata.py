import os
from pathlib import Path
import runpy
import shutil
import subprocess
import tarfile
import tempfile
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]


class ReleaseMetadataTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='momentum release metadata ')
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.source = self.root / 'linux'
        shutil.copytree(ROOT, self.source, ignore=shutil.ignore_patterns('tests', 'core', 'dist', '__pycache__'))
        (self.source / 'core').mkdir()
        self.version = self.source / 'core/version.txt'

    def imported_version(self):
        return runpy.run_path(str(self.source / 'momentum_control/__init__.py'))['__version__']

    def test_reports_bounded_release_metadata_without_starting_helper(self):
        self.version.write_text('7.8.9-rc.1\n')
        self.assertEqual(self.imported_version(), '7.8.9-rc.1')
        helper = self.source / 'core/momentum-core-host'
        helper.write_text('#!/bin/sh\nexit 99\n')
        helper.chmod(0o755)
        self.assertEqual(self.imported_version(), '7.8.9-rc.1')

    def test_absent_or_invalid_metadata_is_development(self):
        self.assertEqual(self.imported_version(), 'development')
        for value in (b'', b'1.2\n', b'1.2.3\nextra', b'1.2.3\x00', b'1.2.3 ', b'1.2.3-' + b'a' * 65, b'\xff'):
            with self.subTest(value=value):
                self.version.write_bytes(value)
                self.assertEqual(self.imported_version(), 'development')

    def test_nonregular_metadata_is_never_opened(self):
        os.mkfifo(self.version)
        with patch.object(Path, 'open', side_effect=AssertionError('Nonregular metadata was opened')):
            self.assertEqual(self.imported_version(), 'development')

    def package(self, version):
        return subprocess.run([str(self.source / 'package.sh'), version, str(self.root / 'out')],
                              capture_output=True, text=True, timeout=15)

    def helper(self, version):
        path = self.source / 'core/momentum-core-host'
        path.write_text('#!/bin/sh\nprintf "momentum-core-host ' + version + ' protocol 1\\n"\n')
        path.chmod(0o755)

    def test_archive_version_matches_metadata_and_executable(self):
        self.helper('7.8.9-rc.1')
        self.version.write_text('7.8.9-rc.1\n')
        result = self.package('7.8.9-rc.1')
        self.assertEqual(result.returncode, 0, result.stderr)
        archive, = (self.root / 'out').glob('*.tar.gz')
        with tarfile.open(archive) as bundle:
            member = bundle.extractfile('momentum-control-linux-7.8.9-rc.1/core/version.txt')
            self.assertEqual(member.read(), b'7.8.9-rc.1\n')
            self.assertIn('momentum-control-linux-7.8.9-rc.1/smoke.py', bundle.getnames())

    def test_mismatched_or_missing_metadata_cannot_be_packaged(self):
        self.helper('7.8.9')
        for metadata in (None, '7.8.8\n', '7.8.9\n'):
            with self.subTest(metadata=metadata):
                self.version.unlink(missing_ok=True)
                if metadata is not None:
                    self.version.write_text(metadata)
                result = self.package('7.8.8')
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(list((self.root / 'out').glob('*.tar.gz')))

    def test_invalid_release_version_is_rejected(self):
        self.helper('7.8.9')
        self.version.write_text('7.8.9\n')
        for version in ('ci', '1.2', '../7.8.9', '7.8.9\nextra', '7.8.9-' + 'a' * 65):
            with self.subTest(version=version):
                self.assertNotEqual(self.package(version).returncode, 0)
                self.assertFalse(list((self.root / 'out').glob('*.tar.gz')))


if __name__ == '__main__':
    unittest.main()
