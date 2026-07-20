import os

import grpc
import pytest

import alignment_pb2
import common_pb2
from worker.backend import AlignedWord, AlignResult
from worker.errors import TransientError
from worker.server import AlignmentServicer


class FakeBackend:
    provider = "cpu"

    def __init__(self, result=None, fail=None):
        self._result = result
        self._fail = fail

    def align(self, audio_path, transcript, language):
        if self._fail:
            raise self._fail
        return self._result


class Aborted(Exception):
    def __init__(self, code):
        self.code = code


class FakeContext:
    def abort(self, code, details):
        raise Aborted(code)


def _audio(tmp_path):
    path = os.path.join(tmp_path, "audio", "00000.wav")
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(b"RIFF....")  # existence is all the servicer checks
    return "audio/00000.wav"


def test_align_returns_words_and_flags_low_confidence(tmp_path):
    rel = _audio(tmp_path)
    result = AlignResult(
        words=[AlignedWord("hello", 0, 500, 0.9), AlignedWord("world", 500, 1000, 0.1)],
        audio_duration_ms=1000)
    servicer = AlignmentServicer(FakeBackend(result=result), data_dir=str(tmp_path), threshold=0.30)

    resp = servicer.Align(
        alignment_pb2.AlignRequest(audio_path=rel, transcript="hello world", language="en"),
        FakeContext())

    assert resp.audio_duration_ms == 1000
    assert [w.text for w in resp.words] == ["hello", "world"]
    assert resp.words[0].low_confidence is False
    assert resp.words[1].low_confidence is True  # 0.1 < 0.30


def test_empty_transcript_is_invalid_argument(tmp_path):
    rel = _audio(tmp_path)
    servicer = AlignmentServicer(FakeBackend(), data_dir=str(tmp_path))
    with pytest.raises(Aborted) as e:
        servicer.Align(alignment_pb2.AlignRequest(audio_path=rel, transcript="  "), FakeContext())
    assert e.value.code == grpc.StatusCode.INVALID_ARGUMENT


def test_missing_audio_is_invalid_argument(tmp_path):
    servicer = AlignmentServicer(FakeBackend(), data_dir=str(tmp_path))
    with pytest.raises(Aborted) as e:
        servicer.Align(
            alignment_pb2.AlignRequest(audio_path="audio/nope.wav", transcript="hi"), FakeContext())
    assert e.value.code == grpc.StatusCode.INVALID_ARGUMENT


def test_transient_backend_error_maps_to_unavailable(tmp_path):
    rel = _audio(tmp_path)
    servicer = AlignmentServicer(FakeBackend(fail=TransientError("oom-ish")), data_dir=str(tmp_path))
    with pytest.raises(Aborted) as e:
        servicer.Align(
            alignment_pb2.AlignRequest(audio_path=rel, transcript="hi", language="en"), FakeContext())
    assert e.value.code == grpc.StatusCode.UNAVAILABLE
