from pathlib import Path
import re


def _installed_version():
    path = Path(__file__).resolve().parent.parent / 'core/version.txt'
    try:
        if path.is_symlink() or not path.is_file():
            return 'development'
        with path.open('rb') as source:
            data = source.read(66)
        version = data.decode('ascii').removesuffix('\n')
        if len(data) <= 65 and len(version) <= 64 and re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9][A-Za-z0-9.-]*)?', version):
            return version
    except (OSError, UnicodeError):
        pass
    return 'development'


__version__ = _installed_version()
