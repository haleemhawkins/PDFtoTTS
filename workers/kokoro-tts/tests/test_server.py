import os

import grpc
import numpy as np
import pytest

import common_pb2
import kokoro_pb2
from worker.backend import Synthesis, VoiceInfo
from worker.errors import TransientError
from worker.health import HealthState
from worker.server import KokoroServicer


class FakeBackend:
    provider = "CPUExecutionProvider"

    def __init__(self, fail: Exception | None = None):
        self._fail = fail

    def list_voices(self):
        return [VoiceInfo("af_heart", "af_heart", "en-us", "female")]

    def is_voice(self, voice_id):
        return voice_id == "af_heart"

    def synthesize(self, text, voice_id, speed, language):
        if self._fail:
            raise self._fail
        return Synthesis(np.zeros(2400, dtype=np.float32), 24000, [])


class Aborted(Exception):
    def __init__(self, code):
        self.code = code


class FakeContext:
    def abort(self, code, details):
        raise Aborted(code)


def make_request(**kw):
    defaults = dict(text="hello world", voice_id="af_heart", speed=1.0,
                    language="en", out_path="audio/s/00000.wav")
    defaults.update(kw)
    return kokoro_pb2.SynthesizeRequest(**defaults)


def test_synthesize_writes_audio_and_returns_metadata(tmp_path):
    servicer = KokoroServicer(FakeBackend(), data_dir=str(tmp_path))
    resp = servicer.Synthesize(make_request(), FakeContext())

    assert resp.audio_path == "audio/s/00000.wav"
    assert resp.sample_rate == 24000
    assert resp.channels == 1
    assert resp.duration_seconds == pytest.approx(0.1)  # 2400 / 24000
    assert os.path.exists(os.path.join(tmp_path, "audio/s/00000.wav"))


def test_empty_text_is_invalid_argument(tmp_path):
    servicer = KokoroServicer(FakeBackend(), data_dir=str(tmp_path))
    with pytest.raises(Aborted) as e:
        servicer.Synthesize(make_request(text="   "), FakeContext())
    assert e.value.code == grpc.StatusCode.INVALID_ARGUMENT


def test_unknown_voice_is_invalid_argument(tmp_path):
    servicer = KokoroServicer(FakeBackend(), data_dir=str(tmp_path))
    with pytest.raises(Aborted) as e:
        servicer.Synthesize(make_request(voice_id="nope"), FakeContext())
    assert e.value.code == grpc.StatusCode.INVALID_ARGUMENT


def test_transient_backend_error_maps_to_unavailable(tmp_path):
    servicer = KokoroServicer(FakeBackend(fail=TransientError("gpu hiccup")), data_dir=str(tmp_path))
    with pytest.raises(Aborted) as e:
        servicer.Synthesize(make_request(), FakeContext())
    assert e.value.code == grpc.StatusCode.UNAVAILABLE


def test_absolute_out_path_is_rejected(tmp_path):
    servicer = KokoroServicer(FakeBackend(), data_dir=str(tmp_path))
    with pytest.raises(Aborted) as e:
        servicer.Synthesize(make_request(out_path="/etc/passwd"), FakeContext())
    assert e.value.code == grpc.StatusCode.INVALID_ARGUMENT


def test_dotdot_out_path_is_rejected(tmp_path):
    servicer = KokoroServicer(FakeBackend(), data_dir=str(tmp_path))
    with pytest.raises(Aborted) as e:
        servicer.Synthesize(make_request(out_path="../outside.wav"), FakeContext())
    assert e.value.code == grpc.StatusCode.INVALID_ARGUMENT


def test_list_voices():
    servicer = KokoroServicer(FakeBackend())
    resp = servicer.ListVoices(common_pb2.ListVoicesRequest(), FakeContext())
    assert [v.id for v in resp.voices] == ["af_heart"]


def test_health_reports_serving_after_set():
    health = HealthState()
    servicer = KokoroServicer(FakeBackend(), health=health)
    assert servicer.Health(common_pb2.HealthRequest(), FakeContext()).status \
        == common_pb2.HealthResponse.STATUS_NOT_READY
    health.set_serving("ready")
    assert servicer.Health(common_pb2.HealthRequest(), FakeContext()).status \
        == common_pb2.HealthResponse.STATUS_SERVING
