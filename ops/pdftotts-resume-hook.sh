#!/bin/sh
# systemd system-sleep hook: restart the PDFtoTTS GPU workers after the host
# resumes from suspend/hibernate.
#
# Why: the RX 7800 XT's /dev/dri device nodes disappear across a suspend cycle.
# Docker can't re-attach the ROCm passthrough, so `kokoro-tts` and `whisperx-align`
# exit 255 ("error gathering device information while adding custom device
# /dev/dri: no such file or directory") and `restart: unless-stopped` can't recover
# them. The API stays up and keeps sending synth requests to dead workers, so the
# reader silently sits at 0%. This hook brings them back once the GPU nodes return.
#
# Install with: ops/install-resume-hook.sh  (copies this to the path below)
#   /usr/lib/systemd/system-sleep/pdftotts-resume
# systemd runs system-sleep scripts as root with two args: pre|post  suspend|...

COMPOSE_FILE="/home/acehawk/RiderProjects/PDFtoTTS/docker-compose.yml"

case "$1" in
  post)
    # Wait (up to 30s) for the amdgpu device nodes to come back before restarting,
    # otherwise the workers would just exit 255 again on the missing /dev/dri.
    i=0
    while [ "$i" -lt 30 ]; do
      [ -e /dev/dri/renderD128 ] && [ -e /dev/kfd ] && break
      i=$((i + 1))
      sleep 1
    done
    /usr/bin/docker compose -f "$COMPOSE_FILE" up -d kokoro-tts whisperx-align
    ;;
esac
