"""Defense-in-depth path resolution under DATA_DIR (no gRPC / GPU deps)."""
import os

import pytest

from shared.paths import UnsafePath, resolve_under_data


def test_relative_path_stays_under_data(tmp_path):
    full = resolve_under_data(str(tmp_path), "audio/s/00000.wav")
    assert full.startswith(os.path.realpath(str(tmp_path)) + os.sep)
    assert full.endswith(os.path.join("audio", "s", "00000.wav"))


@pytest.mark.parametrize("bad", [
    "/etc/passwd",
    "../outside.wav",
    "a/../../etc/passwd",
    "",
    "   ",
])
def test_rejects_escapes(tmp_path, bad):
    with pytest.raises(UnsafePath):
        resolve_under_data(str(tmp_path), bad)
