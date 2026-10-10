#!/usr/bin/python3
import argparse
import hashlib
import json
import os
import re
import selectors
import shutil
import stat
from pathlib import Path
import subprocess
import sys
import tempfile
import time


APP = Path('share/momentum4-control-linux')
MANIFEST = APP / 'manifest.json'
CORE_HOST = APP / 'core/momentum-core-host'
CORE_VERSION = APP / 'core/version.txt'
CORE_PROBE_TIMEOUT = 10.0
CORE_PROBE_CLEANUP_TIMEOUT = 0.5
MAX_PROBE_STDOUT = 256
MAX_PROBE_STDERR = 4096
MAX_CORE_BYTES = 128 * 1024 * 1024
MAX_FILE_BYTES = 2 * 1024 * 1024
MODULES = ('__init__.py', '__main__.py', 'backend.py', 'cli.py', 'gui.py', 'protocol.py', 'dongle.py', 'firmware.py', 'firmware_update.py',
           'preferences.py', 'desktop.py', 'i18n.py', 'updates.py', 'sound_zones.py', 'sound_check.py',
           'core_proxy.py', 'bluez.py', 'hidraw.py', 'models.py', 'errors.py')
DEPENDENCIES = '''  Debian 13 / Ubuntu 25.04+: sudo apt install python3-pyside6.qtcore python3-pyside6.qtgui python3-pyside6.qtwidgets python3-dbus python3-gi
  Ubuntu 24.04:  sudo apt install python3-pip python3-dbus python3-gi libegl1 libgl1 libxkbcommon0 libfontconfig1
                 /usr/bin/python3 -m pip install --user --break-system-packages PySide6-Essentials
  Fedora:        sudo dnf install python3-pyside6 python3-dbus python3-gobject
  Arch:          sudo pacman -S pyside6 python-dbus python-gobject'''
ALLOWED = {
    *(str(APP / 'momentum_control' / module) for module in MODULES),
    str(APP / 'install.py'), str(APP / 'README.md'), str(APP / 'LICENSE'),
    str(APP / 'assets/70-momentum-btd700.rules'),
    str(CORE_HOST), str(CORE_VERSION),
    'bin/momentum-control', 'bin/momentumctl',
    'share/applications/momentum-control.desktop',
    'share/icons/hicolor/scalable/apps/momentum-control.svg',
}


def safe_path(prefix, relative):
    path = prefix / relative
    for ancestor in (path, *path.parents):
        if ancestor.is_symlink():
            raise ValueError(f'Refusing symlink path: {ancestor}')
    return path


def digest(data):
    return hashlib.sha256(data).hexdigest()


def managed_digest(path, relative):
    limit = MAX_CORE_BYTES if relative == str(CORE_HOST) else MAX_FILE_BYTES
    if path.stat().st_size > limit:
        raise ValueError(f'Managed file exceeds its size limit: {path}')
    result = hashlib.sha256()
    size = 0
    with path.open('rb') as source:
        while chunk := source.read(min(1024 * 1024, limit + 1 - size)):
            size += len(chunk)
            if size > limit:
                raise ValueError(f'Managed file exceeds its size limit: {path}')
            result.update(chunk)
    return result.hexdigest()


def current_manifest(prefix):
    path = safe_path(prefix, MANIFEST)
    if not path.exists():
        if safe_path(prefix, APP).exists():
            raise ValueError('Refusing existing unmanaged application directory')
        return None
    if not path.is_file() or path.stat().st_size > 65536:
        raise ValueError('Invalid installation manifest')
    data = json.loads(path.read_text())
    if data.get('application') != 'momentum4-control-linux' or data.get('version') != 1:
        raise ValueError('Unrecognized installation manifest')
    files = data.get('files')
    if not isinstance(files, dict) or not files or not files.keys() <= ALLOWED:
        raise ValueError('Manifest contains unrecognized paths')
    for relative, expected in files.items():
        path = safe_path(prefix, relative)
        if not path.is_file() or not isinstance(expected, str) or managed_digest(path, relative) != expected:
            raise ValueError(f'Refusing modified or missing managed file: {path}')
    for path in safe_path(prefix, APP).rglob('*'):
        safe_path(prefix, path.relative_to(prefix))
        if path.is_file() and str(path.relative_to(prefix)) not in files and path != prefix / MANIFEST:
            raise ValueError(f'Refusing unknown application file: {path}')
        if not path.is_file() and not path.is_dir():
            raise ValueError(f'Refusing nonregular application path: {path}')
    return files


def launcher(app_path, cli):
    entry = 'from momentum_control.cli import main' if cli else 'from momentum_control.__main__ import main'
    return f'''#!/usr/bin/python3
import sys
sys.dont_write_bytecode = True
sys.path.insert(0, {str(app_path)!r})
try:
    {entry}
    raise SystemExit(main())
except ImportError as error:
    print("Missing dependency: " + str(error) + "\\n" + {DEPENDENCIES!r}, file=sys.stderr)
    raise SystemExit(1)
'''.encode()


def desktop_escape(value):
    quoted = ''.join('\\' + character if character in '"`$\\' else character for character in value)
    return quoted.replace('\\', '\\\\').replace('%', '%%')


def read_source(source, relative, limit=MAX_FILE_BYTES):
    path = safe_path(source, relative)
    if not path.is_file() or path.stat().st_size > limit:
        raise ValueError(f'Missing, nonregular or oversized application source: {path}')
    with path.open('rb') as stream:
        data = stream.read(limit + 1)
    if len(data) > limit:
        raise ValueError(f'Application source grew beyond its size limit: {path}')
    return data


def stop_core_probe(process):
    if process.poll() is not None:
        return
    try:
        process.terminate()
        process.wait(timeout=CORE_PROBE_CLEANUP_TIMEOUT)
    except subprocess.TimeoutExpired:
        pass
    finally:
        if process.poll() is None:
            process.kill()
            try:
                try:
                    process.wait(timeout=CORE_PROBE_CLEANUP_TIMEOUT)
                except KeyboardInterrupt:
                    process.wait(timeout=CORE_PROBE_CLEANUP_TIMEOUT)
                    raise
            except subprocess.TimeoutExpired:
                raise ValueError('Shared core probe could not stop; close it before retrying installation') from None


def probe_core(core_data):
    # Compatibility checking, not a sandbox: only install packages you trust.
    try:
        with tempfile.TemporaryDirectory(prefix='momentum-core-probe-') as directory:
            snapshot = Path(directory) / 'momentum-core-host'
            snapshot.write_bytes(core_data)
            snapshot.chmod(0o700)
            deadline = time.monotonic() + CORE_PROBE_TIMEOUT
            process = subprocess.Popen([str(snapshot), '--version'], cwd=directory, env={},
                                       stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                       stderr=subprocess.PIPE, close_fds=True)
            try:
                stdout = bytearray()
                sizes = {'stdout': 0, 'stderr': 0}
                with selectors.DefaultSelector() as selector:
                    selector.register(process.stdout, selectors.EVENT_READ, ('stdout', MAX_PROBE_STDOUT))
                    selector.register(process.stderr, selectors.EVENT_READ, ('stderr', MAX_PROBE_STDERR))
                    while selector.get_map():
                        remaining = deadline - time.monotonic()
                        if remaining <= 0:
                            raise ValueError('Shared core startup timed out; use a compatible Linux release or rebuild ./linux/build.sh')
                        for key, _ in selector.select(remaining):
                            name, limit = key.data
                            chunk = os.read(key.fd, min(4096, limit + 1 - sizes[name]))
                            if not chunk:
                                selector.unregister(key.fileobj)
                                continue
                            sizes[name] += len(chunk)
                            if sizes[name] > limit:
                                raise ValueError('Shared core startup output exceeded its limit; use a compatible release or rebuild ./linux/build.sh')
                            if name == 'stdout':
                                stdout.extend(chunk)
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise subprocess.TimeoutExpired(process.args, CORE_PROBE_TIMEOUT)
                if process.wait(timeout=remaining) != 0:
                    raise ValueError('Shared core startup failed; use a release matching your Linux architecture and runtime, or rebuild ./linux/build.sh')
            finally:
                try:
                    stop_core_probe(process)
                finally:
                    process.stdout.close()
                    process.stderr.close()
    except subprocess.TimeoutExpired:
        raise ValueError('Shared core startup timed out; use a compatible Linux release or rebuild ./linux/build.sh') from None
    except OSError:
        raise ValueError('Shared core could not start; use a release matching your Linux architecture and runtime, or rebuild ./linux/build.sh') from None
    match = re.fullmatch(rb'momentum-core-host ([0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9][A-Za-z0-9.-]*)?) protocol 1(?:\r?\n)?', stdout)
    if match is None or len(match[1]) > 64:
        raise ValueError('Shared core returned an invalid version or unsupported protocol; use a matching release or rebuild ./linux/build.sh')
    return match[1].decode('ascii')


def payloads(prefix):
    source = Path(__file__).resolve().parent
    payload = {}
    core_path = safe_path(source, 'core/momentum-core-host')
    if not core_path.is_file():
        raise ValueError('Shared core is not built; run ./linux/build.sh before installing')
    core_status = core_path.stat()
    if not core_status.st_mode & 0o111 or not 1 <= core_status.st_size <= MAX_CORE_BYTES:
        raise ValueError('Shared core must be an executable file of at most 128 MiB')
    with core_path.open('rb') as core_file:
        core_data = core_file.read(MAX_CORE_BYTES + 1)
    if not 1 <= len(core_data) <= MAX_CORE_BYTES:
        raise ValueError('Shared core file changed or exceeds 128 MiB')
    version = probe_core(core_data)
    payload[str(CORE_HOST)] = core_data
    payload[str(CORE_VERSION)] = (version + '\n').encode('ascii')
    for module in MODULES:
        path = source / 'momentum_control' / module
        if path.is_symlink():
            raise ValueError(f'Refusing symlink source: {path}')
        if not path.is_file():
            raise ValueError(f'Missing application source: {path}')
        payload[str(APP / 'momentum_control' / module)] = read_source(source, Path('momentum_control') / module)
    for name in ('install.py', 'README.md', 'LICENSE'):
        path = source / name
        if path.is_symlink() or not path.is_file():
            raise ValueError(f'Missing or symlinked application source: {path}')
        payload[str(APP / name)] = read_source(source, name)
    rule = source / 'assets/70-momentum-btd700.rules'
    if rule.is_symlink() or not rule.is_file():
        raise ValueError(f'Missing or symlinked application source: {rule}')
    payload[str(APP / 'assets/70-momentum-btd700.rules')] = read_source(source, 'assets/70-momentum-btd700.rules')
    for name, cli in (('momentum-control', False), ('momentumctl', True)):
        payload[f'bin/{name}'] = launcher(prefix / APP, cli)
    command = desktop_escape(str(prefix / 'bin/momentum-control'))
    payload['share/applications/momentum-control.desktop'] = f'''[Desktop Entry]
Type=Application
Name=Momentum Control
Comment=Bluetooth headset settings for Sennheiser MOMENTUM
Exec=/usr/bin/python3 "{command}"
Icon=momentum-control
Terminal=false
Categories=AudioVideo;Audio;
StartupNotify=true
'''.encode()
    payload['share/icons/hicolor/scalable/apps/momentum-control.svg'] = read_source(source, 'assets/momentum-control.svg')
    return payload


def replace_files(prefix, payload, removed=()):
    targets = {relative: safe_path(prefix, relative) for relative in (*payload, *removed)}
    for path in targets.values():
        for suffix in ('.momentum-new', '.momentum-old'):
            temporary = safe_path(prefix, path.with_name(path.name + suffix).relative_to(prefix))
            if temporary.exists():
                raise ValueError(f'Refusing existing staging file: {temporary}')
        if any(ancestor.exists() and not ancestor.is_dir() for ancestor in path.parents):
            raise ValueError(f'Refusing non-directory parent for {path}')
    staging = []
    backups = {}
    directories = []
    changed = []
    try:
        for relative, path in targets.items():
            missing = []
            directory = path.parent
            while not directory.exists():
                missing.append(directory)
                directory = directory.parent
            for directory in reversed(missing):
                directory.mkdir()
                directories.append(directory)
            if path.exists():
                backup = path.with_name(path.name + '.momentum-old')
                with backup.open('xb') as output:
                    staging.append(backup)
                    with path.open('rb') as source:
                        shutil.copyfileobj(source, output)
                backup.chmod(stat.S_IMODE(path.stat().st_mode))
                backups[path] = backup
            if relative in payload:
                temporary = path.with_name(path.name + '.momentum-new')
                with temporary.open('xb') as output:
                    staging.append(temporary)
                    output.write(payload[relative])
                temporary.chmod(0o755 if relative.startswith('bin/') or relative == str(CORE_HOST) else 0o644)
        for relative, path in targets.items():
            changed.append(path)
            if relative in payload:
                os.replace(path.with_name(path.name + '.momentum-new'), path)
            else:
                path.unlink()
        changed.clear()
    except BaseException as error:
        failures = []
        for path in reversed(changed):
            try:
                if path in backups:
                    os.replace(backups[path], path)
                else:
                    path.unlink(missing_ok=True)
            except OSError as rollback_error:
                failures.append(str(rollback_error))
        if failures:
            raise OSError(f'{error}; rollback failed: {"; ".join(failures)}') from error
        raise
    finally:
        retained_backups = {backups[path] for path in changed if path in backups}
        for path in staging:
            if path not in retained_backups:
                path.unlink(missing_ok=True)
        for directory in reversed(directories):
            if directory.exists() and not any(directory.iterdir()):
                directory.rmdir()


def install(prefix):
    check = subprocess.run(['/usr/bin/python3', '-c', 'import PySide6, dbus, gi'], capture_output=True, text=True)
    if check.returncode:
        raise ValueError('Missing Python dependencies (PySide6, dbus-python, PyGObject) for /usr/bin/python3.\n' + DEPENDENCIES)
    old = current_manifest(prefix) or {}
    payload = payloads(prefix)
    for relative in payload:
        path = safe_path(prefix, relative)
        if path.exists() and relative not in old:
            raise ValueError(f'Refusing existing unmanaged file: {path}')
    manifest = {'application': 'momentum4-control-linux', 'version': 1, 'files': {name: digest(data) for name, data in payload.items()}}
    payload[str(MANIFEST)] = (json.dumps(manifest, indent=2) + '\n').encode()
    replace_files(prefix, payload, old.keys() - payload.keys())
    print(f'Installed Momentum Control in {prefix / APP}')
    print(f'Launch: {prefix / "bin/momentum-control"}')


def uninstall(prefix):
    files = current_manifest(prefix)
    if files is None:
        raise ValueError('No managed Momentum Control installation found')
    replace_files(prefix, {}, (*files, str(MANIFEST)))
    app_path = safe_path(prefix, APP)
    for directory in sorted((path for path in app_path.rglob('*') if path.is_dir()), key=lambda path: len(path.parts), reverse=True):
        directory.rmdir()
    app_path.rmdir()
    print(f'Uninstalled Momentum Control from {prefix}')


def main():
    parser = argparse.ArgumentParser(description='Per-user Momentum Control installation')
    location = parser.add_mutually_exclusive_group()
    location.add_argument('--prefix', type=Path, help='Installation prefix (default: ~/.local)')
    location.add_argument('--home', type=Path, help='Install under this home directory’s .local')
    parser.add_argument('--uninstall', action='store_true')
    args = parser.parse_args()
    prefix = (args.prefix or (args.home or Path.home()) / '.local').absolute()
    if any(character in str(prefix) for character in ('\n', '\r', '\x00')):
        parser.error('Installation path contains a control character')
    try:
        safe_path(prefix, APP)
        (uninstall if args.uninstall else install)(prefix)
    except (OSError, ValueError, TypeError, json.JSONDecodeError) as error:
        print(f'Installation refused: {error}', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
