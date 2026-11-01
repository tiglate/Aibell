#!/usr/bin/env bash

set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
solution="$repo_root/Aibell.slnx"
desktop_project="$repo_root/src/Aibell.Desktop/Aibell.Desktop.csproj"
configuration="${CONFIGURATION:-Debug}"
action="${1:-}"

usage() {
    echo "Usage: ./make.sh {build|run|test|coverage|secrets|radio-check|format|clean|publish|deb|appimage|flatpak|snap|msi} [arguments]"
    echo "Set CONFIGURATION=Release to override the default Debug configuration."
    echo "Set RID (for example linux-x64) to override publish runtime detection."
    echo "deb, appimage, flatpak and snap build Linux x64 packages into artifacts/packages (extra arguments go to the packaging tool command)."
    echo "msi builds a Windows x64 installer (WiX) into artifacts/packages and runs only on Windows x64."
}

detect_rid() {
    local os architecture
    os="$(uname -s)"
    architecture="$(uname -m)"

    case "$architecture" in
        x86_64|amd64) architecture="x64" ;;
        aarch64|arm64) architecture="arm64" ;;
        *) echo "Unsupported architecture: $architecture" >&2; exit 1 ;;
    esac

    case "$os" in
        Linux) echo "linux-$architecture" ;;
        MINGW*|MSYS*|CYGWIN*) echo "win-$architecture" ;;
        *) echo "Unsupported operating system: $os" >&2; exit 1 ;;
    esac
}

case "$action" in
    build)
        shift
        dotnet build "$solution" --configuration "$configuration" --maxcpucount:1 "$@"
        ;;
    run)
        shift
        dotnet build "$desktop_project" --configuration "$configuration" --maxcpucount:1
        dotnet run --project "$desktop_project" --configuration "$configuration" --no-build -- "$@"
        ;;
    test)
        shift
        dotnet test "$solution" --configuration "$configuration" --maxcpucount:1 "$@"
        ;;
    coverage)
        shift
        if [[ "$(detect_rid)" != "linux-x64" || "$configuration" != "Debug" ]]; then
            echo "The coverage baseline uses Linux x64 and CONFIGURATION=Debug." >&2
            exit 2
        fi
        cd "$repo_root"
        dotnet tool restore
        dotnet build "$solution" --configuration Debug --maxcpucount:1 "$@"
        env DOTNET_COVERAGE_TELEMETRY_OPTOUT=1 AIBELL_NATIVE_PLAYBACK_TESTS=1 \
            AIBELL_METADATA_WRITE_TESTS=1 AIBELL_LINUX_INTEGRATION_TESTS=1 \
            dotnet tool run dotnet-coverage -- collect --settings tests/coverage.settings.xml \
            --output-format cobertura --output artifacts/coverage/modern.cobertura.xml --nologo \
            "$repo_root/make.sh" test --no-build --no-restore
        dotnet run --project tools/AibellTools -- coverage-gate artifacts/coverage/modern.cobertura.xml
        ;;
    secrets)
        shift
        cd "$repo_root"
        dotnet run --project tools/AibellTools -- secrets --download "$@"
        ;;
    radio-check)
        shift
        cd "$repo_root"
        dotnet run --project tools/AibellTools -- radio-check "$@"
        ;;
    format)
        shift
        dotnet format whitespace "$solution" "$@"
        ;;
    clean)
        shift
        dotnet clean "$solution" --configuration "$configuration" --maxcpucount:1 "$@"
        artifacts_dir="$repo_root/artifacts"
        case "$artifacts_dir" in
            "$repo_root/artifacts") rm -rf -- "$artifacts_dir" ;;
            *) echo "Refusing to clean unexpected path: $artifacts_dir" >&2; exit 1 ;;
        esac
        ;;
    publish)
        shift
        runtime_id="${RID:-$(detect_rid)}"
        dotnet publish "$desktop_project" \
            --configuration Release \
            --runtime "$runtime_id" \
            --self-contained true \
            -p:NuGetLockFilePath="packages.$runtime_id.lock.json" \
            --maxcpucount:1 \
            --output "$repo_root/artifacts/$runtime_id" \
            "$@"
        ;;
    deb|appimage|flatpak|snap)
        shift
        if [[ "$(detect_rid)" != "linux-x64" ]]; then
            echo "Linux packages are built for Linux x64 on a Linux x64 machine." >&2
            exit 2
        fi
        publish_dir="$repo_root/artifacts/deb-publish"
        rm -rf -- "$publish_dir"
        dotnet publish "$desktop_project" \
            --configuration Release \
            --runtime linux-x64 \
            --self-contained true \
            -p:PublishReadyToRun=true \
            -p:DebugType=none \
            -p:DebugSymbols=false \
            -p:NuGetLockFilePath="packages.linux-x64.lock.json" \
            --maxcpucount:1 \
            --output "$publish_dir"
        cd "$repo_root"
        dotnet run --project tools/AibellTools -- "$action" --publish-dir "$publish_dir" "$@"
        ;;
    msi)
        shift
        if [[ "$(detect_rid)" != "win-x64" ]]; then
            echo "The MSI installer is built for Windows x64 on a Windows x64 machine." >&2
            exit 2
        fi
        publish_dir="$repo_root/artifacts/msi-publish"
        rm -rf -- "$publish_dir"
        dotnet publish "$desktop_project" \
            --configuration Release \
            --runtime win-x64 \
            --self-contained true \
            -p:PublishReadyToRun=true \
            -p:DebugType=none \
            -p:DebugSymbols=false \
            -p:NuGetLockFilePath="packages.win-x64.lock.json" \
            --maxcpucount:1 \
            --output "$publish_dir"
        cd "$repo_root"
        dotnet tool restore
        dotnet run --project tools/AibellTools -- msi --publish-dir "$publish_dir" "$@"
        ;;
    *)
        usage
        exit 2
        ;;
esac
