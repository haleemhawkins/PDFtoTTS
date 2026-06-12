import pytest

from worker.tuning import (
    MAX_PARALLEL,
    auto_concurrency,
    resolve_concurrency,
    resolve_intra_threads,
)


@pytest.mark.parametrize("cores,expected", [
    (1, 1), (2, 1), (3, 1), (4, 1), (6, 2), (9, 3), (12, 4), (24, 8), (64, MAX_PARALLEL),
])
def test_concurrency_scales_with_cores_and_is_clamped(cores, expected):
    assert auto_concurrency(cores) == expected


def test_env_overrides_auto(monkeypatch):
    monkeypatch.setenv("MAX_CONCURRENCY", "2")
    monkeypatch.setenv("ONNX_INTRA_OP_THREADS", "5")
    assert resolve_concurrency() == 2
    assert resolve_intra_threads() == 5


def test_intra_threads_default_is_zero_when_unset(monkeypatch):
    monkeypatch.delenv("ONNX_INTRA_OP_THREADS", raising=False)
    assert resolve_intra_threads() == 0  # 0 = onnxruntime default (no cap)


def test_concurrency_falls_back_to_auto_when_env_absent(monkeypatch):
    monkeypatch.delenv("MAX_CONCURRENCY", raising=False)
    assert resolve_concurrency() >= 1
