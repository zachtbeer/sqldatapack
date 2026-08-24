#!/usr/bin/env bash
# SessionStart hook: prepares a Claude Code on the web container for this repo.
#
# The container image has no .NET SDK, so we install the SDKs the solution
# targets (net8.0 and net10.0), warm the NuGet cache from the lock files, and
# restore the local docfx tool. Container state is cached after the hook
# completes, so subsequent sessions re-run this quickly.
set -euo pipefail

# Local machines are expected to already have a working .NET toolchain.
if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

PROJECT_DIR="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
DOTNET_INSTALL_DIR="$HOME/.dotnet"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export NUGET_XMLDOC_MODE=skip
export PATH="$DOTNET_INSTALL_DIR:$DOTNET_INSTALL_DIR/tools:$HOME/.dotnet/tools:$PATH"

install_sdk() {
  local channel="$1"
  if [ -d "$DOTNET_INSTALL_DIR/sdk" ] && \
     find "$DOTNET_INSTALL_DIR/sdk" -maxdepth 1 -name "${channel%.*}.*" -print -quit | grep -q .; then
    echo "[session-start] .NET SDK $channel already installed"
    return 0
  fi
  echo "[session-start] installing .NET SDK $channel"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$TMPDIR_HOOK/dotnet-install.sh"
  bash "$TMPDIR_HOOK/dotnet-install.sh" --channel "$channel" --install-dir "$DOTNET_INSTALL_DIR" --no-path
}

TMPDIR_HOOK="$(mktemp -d)"
trap 'rm -rf "$TMPDIR_HOOK"' EXIT

# The container image hardcodes user.name=Claude in ~/.gitconfig, and the
# clone is recreated every session, so pin the maintainer identity locally.
git -C "$PROJECT_DIR" config user.name "zachtbeer"
git -C "$PROJECT_DIR" config user.email "233656951+zachtbeer@users.noreply.github.com"

install_sdk 8.0
install_sdk 10.0

cd "$PROJECT_DIR"

# --locked-mode matches CI: it fails loudly if packages.lock.json is stale
# rather than silently resolving different versions.
echo "[session-start] restoring solution packages"
dotnet restore SqlDataPack.slnx --locked-mode

echo "[session-start] restoring local dotnet tools (docfx)"
dotnet tool restore

# Persist the toolchain for the rest of the session.
if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  {
    echo "export DOTNET_ROOT=\"$DOTNET_INSTALL_DIR\""
    echo "export PATH=\"$DOTNET_INSTALL_DIR:\$HOME/.dotnet/tools:\$PATH\""
    echo 'export DOTNET_CLI_TELEMETRY_OPTOUT=1'
    echo 'export DOTNET_NOLOGO=1'
    echo 'export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1'
    echo 'export NUGET_XMLDOC_MODE=skip'
    # Default image for the integration suite; those tests still need a Docker
    # daemon, which the web container does not provide.
    echo 'export SQLDATAPACK_SQLSERVER_IMAGE=mcr.microsoft.com/mssql/server:2025-latest'
  } >> "$CLAUDE_ENV_FILE"
fi

echo "[session-start] ready: $(dotnet --version) (SDKs: $(dotnet --list-sdks | tr '\n' ' '))"
