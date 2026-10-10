#!/bin/sh
# Packs the built Linux companion into a release archive:
#   ./linux/build.sh && ./linux/package.sh 1.2.0 [output-directory]
# The archive holds everything install.sh needs, including the self-contained shared core,
# so the target machine needs no .NET SDK.
set -eu

momentum_source=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)
momentum_version=${1:-}
momentum_output=${2:-$momentum_source/dist}
case $momentum_version in
    ''|*[!A-Za-z0-9._-]*) echo 'Usage: package.sh VERSION [OUTPUT-DIRECTORY]' >&2; exit 2 ;;
esac
/usr/bin/python3 -B - "$momentum_version" <<'PY'
import re
import sys
version = sys.argv[1]
if len(version) > 64 or re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9][A-Za-z0-9.-]*)?', version) is None:
    sys.exit('Package version must be major.minor.patch with an optional prerelease suffix (maximum 64 characters).')
PY
case $(uname -m) in
    x86_64) momentum_arch=x64 ;;
    aarch64) momentum_arch=arm64 ;;
    *) echo 'Packaging supports x86_64 and aarch64 Linux.' >&2; exit 1 ;;
esac
[ -x "$momentum_source/core/momentum-core-host" ] || {
    echo 'Shared core is not built; run ./linux/build.sh first.' >&2
    exit 1
}

momentum_name="momentum-control-linux-$momentum_version"
momentum_archive="Momentum4Control-$momentum_version-linux-$momentum_arch.tar.gz"
momentum_stage=$(mktemp -d "${TMPDIR:-/tmp}/momentum-package.XXXXXX")
trap 'rm -rf -- "$momentum_stage"' 0
trap 'exit 130' INT
trap 'exit 143' TERM

momentum_root="$momentum_stage/$momentum_name"
mkdir -p "$momentum_root/momentum_control" "$momentum_root/assets" "$momentum_root/core"
install -m 0755 "$momentum_source/install.sh" "$momentum_source/uninstall.sh" "$momentum_root/"
install -m 0644 "$momentum_source/install.py" "$momentum_source/smoke.py" "$momentum_source/README.md" "$momentum_source/LICENSE" "$momentum_root/"
install -m 0644 "$momentum_source"/momentum_control/*.py "$momentum_root/momentum_control/"
install -m 0644 "$momentum_source/assets/70-momentum-btd700.rules" "$momentum_source/assets/momentum-control.svg" "$momentum_root/assets/"
install -m 0755 "$momentum_source/core/momentum-core-host" "$momentum_root/core/"
install -m 0644 "$momentum_source/core/version.txt" "$momentum_root/core/"

/usr/bin/python3 -B - "$momentum_root" "$momentum_version" <<'PY'
from pathlib import Path
import sys
sys.path.insert(0, sys.argv[1])
import install
root = Path(sys.argv[1])
try:
    version = install.probe_core(install.read_source(root, 'core/momentum-core-host', install.MAX_CORE_BYTES))
    metadata = install.read_source(root, 'core/version.txt', 65)
    if version != sys.argv[2] or metadata != (version + '\n').encode('ascii'):
        raise ValueError('Release, helper and metadata versions do not match; rebuild with the release version.')
except (OSError, ValueError) as error:
    sys.exit(str(error))
PY

mkdir -p "$momentum_output"
tar -C "$momentum_stage" --owner=0 --group=0 --numeric-owner -czf "$momentum_output/$momentum_archive" "$momentum_name"
printf 'Packaged: %s\n' "$momentum_output/$momentum_archive"
