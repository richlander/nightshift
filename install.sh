#!/usr/bin/env bash
#
# install.sh — build the Nightshift tools as NativeAOT and deploy them onto PATH.
#
# Run this at the START OF EVERY ROUND. Nightshift is self-hosted: as orders land
# they change the very tools the shift runs on (nightshift, turnstile, octoshift,
# nightsky). If you keep running yesterday's binaries, landed fixes are not in
# effect and the shift is coordinating itself with stale code. Rebuilding +
# redeploying is how the running shift adopts what it just merged.
#
# What it does:
#   * publishes each product as a self-contained NativeAOT native binary, and
#   * installs that binary into PREFIX (default ~/.local/bin) so `nightshift`,
#     `turnstile`, `octoshift`, and `nightsky` on PATH are the fresh builds.
#
# What it does NOT do: restart running services. Deploying a binary does not
# change an already-running process. After install, restart anything that must
# pick up new code — always the `nightshift plan` controller, and the
# `turnstile serve` daemon ONLY when the Turnstile server itself changed
# (restarting the daemon drops every watcher, so don't do it gratuitously).
#
# Usage:
#   ./install.sh                 # build + deploy all tools
#   PREFIX=/usr/local/bin ./install.sh
#   CONFIG=Release ./install.sh  # override the build configuration
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$REPO_ROOT"

CONFIG="${CONFIG:-Release}"
PREFIX="${PREFIX:-$HOME/.local/bin}"

# product-csproj : deployed-command-name (AssemblyName)
TOOLS=(
  "src/nightshift/nightshift.csproj:nightshift"
  "src/Turnstile/Turnstile.csproj:turnstile"
  "src/Octoshift/Octoshift.csproj:octoshift"
  "src/Nightsky/Nightsky.csproj:nightsky"
)

echo "install.sh: NativeAOT build + deploy"
echo "  config : $CONFIG"
echo "  prefix : $PREFIX"
echo

mkdir -p "$PREFIX"

for entry in "${TOOLS[@]}"; do
  proj="${entry%%:*}"
  name="${entry##*:}"
  out="artifacts/publish/$name"

  echo ">> publishing $name  ($proj)"
  dotnet publish "$proj" \
    -c "$CONFIG" \
    -p:PublishAot=true \
    -o "$out" \
    >/dev/null

  bin="$out/$name"
  if [[ ! -x "$bin" ]]; then
    echo "install.sh: expected native binary '$bin' was not produced." >&2
    exit 1
  fi

  # `install` unlinks then copies, so this is safe even if the old binary is a
  # currently-running process (it keeps its old inode until it exits).
  install -m 0755 "$bin" "$PREFIX/$name"
  echo "   deployed -> $PREFIX/$name  ($(du -h "$PREFIX/$name" | cut -f1))"
done

echo
echo "install.sh: done. Deployed to $PREFIX:"
for entry in "${TOOLS[@]}"; do
  name="${entry##*:}"
  printf '  %-10s %s\n' "$name" "$PREFIX/$name"
done

case ":$PATH:" in
  *":$PREFIX:"*) : ;;
  *) echo; echo "NOTE: $PREFIX is not on your PATH — add it so the tools resolve." ;;
esac

echo
echo "Reminder: restart the 'nightshift plan' controller to run the new code;"
echo "restart 'turnstile serve' only if the Turnstile server changed this round."
