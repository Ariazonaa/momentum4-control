#!/bin/sh
set -eu

momentum_source=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)
momentum_dotnet=${MOMENTUM_DOTNET:-dotnet}
case $(uname -m) in
    x86_64) momentum_runtime=linux-x64 ;;
    aarch64) momentum_runtime=linux-arm64 ;;
    *) echo 'Shared core build supports x86_64 and aarch64 Linux.' >&2; exit 1 ;;
esac
command -v "$momentum_dotnet" >/dev/null 2>&1 || {
    echo 'Install the .NET SDK pinned by global.json, or set MOMENTUM_DOTNET to its executable.' >&2
    exit 1
}
momentum_project="$momentum_source/../src/Momentum4.CoreHost/Momentum4.CoreHost.csproj"
cd "$momentum_source/.."
momentum_build_dir=$(mktemp -d "${TMPDIR:-/tmp}/momentum-core-publish.XXXXXX")
trap 'rm -rf -- "$momentum_build_dir"' 0
trap 'exit 130' INT
trap 'exit 143' TERM

"$momentum_dotnet" restore "$momentum_project" --runtime "$momentum_runtime" \
    --configfile "$momentum_source/NuGet.Config" -p:PublishSingleFile=true
"$momentum_dotnet" publish "$momentum_project" --configuration Release \
    --runtime "$momentum_runtime" --self-contained true --no-restore \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=None --output "$momentum_build_dir"
"$momentum_build_dir/momentum-core-host" --version
[ ! -L "$momentum_source/core" ] && [ ! -L "$momentum_source/core/momentum-core-host.new" ] || {
    echo 'Refusing symlinked shared core build destination.' >&2
    exit 1
}
mkdir -p "$momentum_source/core"
install -m 0755 "$momentum_build_dir/momentum-core-host" "$momentum_source/core/momentum-core-host.new"
mv -f "$momentum_source/core/momentum-core-host.new" "$momentum_source/core/momentum-core-host"
printf 'Built shared core: %s\n' "$momentum_source/core/momentum-core-host"
