"""Resolve client-supplied paths under the shared data root (defense-in-depth)."""
from __future__ import annotations

import os


class UnsafePath(ValueError):
    """Raised when a relative path would escape the data directory."""


def resolve_under_data(data_dir: str, relative: str) -> str:
    """
    Join ``relative`` under ``data_dir`` and require the result stay inside it.

    Rejects empty paths, absolute paths (``os.path.join`` would otherwise drop
    ``data_dir``), and any ``..`` that escapes the root after realpath resolution.
    """
    if not relative or not str(relative).strip():
        raise UnsafePath("path is empty")
    rel = str(relative).strip()
    # Absolute client paths win over data_dir with bare join — reject them.
    if os.path.isabs(rel):
        raise UnsafePath(f"absolute paths are not allowed: {rel!r}")
    # Reject .. components before join so we fail closed even if realpath races.
    parts = rel.replace("\\", "/").split("/")
    if any(p == ".." for p in parts):
        raise UnsafePath(f"path escapes data dir: {rel!r}")

    root = os.path.realpath(data_dir)
    full = os.path.realpath(os.path.join(root, rel))
    if full != root and not full.startswith(root + os.sep):
        raise UnsafePath(f"path escapes data dir: {rel!r}")
    return full
