#!/bin/sh
# Install the GPU-worker resume hook so suspend/resume can't leave synthesis stuck
# at 0%. Needs root to write under /usr/lib/systemd. Run: sudo ops/install-resume-hook.sh
set -e

SRC="$(cd "$(dirname "$0")" && pwd)/pdftotts-resume-hook.sh"
DEST="/usr/lib/systemd/system-sleep/pdftotts-resume"

if [ "$(id -u)" -ne 0 ]; then
  echo "Run as root: sudo $0" >&2
  exit 1
fi

install -m 0755 "$SRC" "$DEST"
echo "Installed $DEST"
echo "Test without sleeping:  sudo $DEST post suspend"
