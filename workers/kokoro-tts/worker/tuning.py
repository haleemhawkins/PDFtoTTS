"""Adapt synthesis parallelism to the host's CPU so the worker scales across
machines instead of being tuned for one box. All values can be overridden by
env (MAX_CONCURRENCY / ONNX_INTRA_OP_THREADS)."""
from __future__ import annotations

import os

# Each parallel synthesis wants roughly this many CPU threads to run well; we
# divide the available cores into that many concurrent synth slots.
CORES_PER_SYNTH = 3
MAX_PARALLEL = 8  # don't spawn absurd parallelism on very large hosts


def cpu_cores() -> int:
    """Cores actually available to this process (respects cpuset/cgroup limits on
    Linux), falling back to the logical CPU count."""
    try:
        return max(1, len(os.sched_getaffinity(0)))  # type: ignore[attr-defined]
    except (AttributeError, OSError):
        return max(1, os.cpu_count() or 1)


def auto_concurrency(cores: int) -> int:
    """How many chunks to synthesize in parallel for a given core count."""
    return max(1, min(cores // CORES_PER_SYNTH, MAX_PARALLEL))


def resolve_concurrency() -> int:
    """MAX_CONCURRENCY from env, else auto from cores."""
    env = os.environ.get("MAX_CONCURRENCY")
    if env and env.strip().isdigit() and int(env) > 0:
        return int(env)
    return auto_concurrency(cpu_cores())


def resolve_intra_threads() -> int:
    """ONNX_INTRA_OP_THREADS from env, else 0 = onnxruntime default (let each
    inference use the available cores). We intentionally do NOT cap threads per
    session: capping measured slower and also throttled the lone first chunk,
    hurting time-to-first-audio. auto_concurrency already limits how many run at
    once, so oversubscription stays bounded and the OS schedules it fine."""
    env = os.environ.get("ONNX_INTRA_OP_THREADS")
    if env and env.strip().isdigit() and int(env) > 0:
        return int(env)
    return 0
