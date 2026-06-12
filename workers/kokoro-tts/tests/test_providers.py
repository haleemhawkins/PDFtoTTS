from worker.providers import create_session, select_providers


def test_rocm_preferred_when_available():
    avail = ["CPUExecutionProvider", "ROCMExecutionProvider"]
    assert select_providers(avail)[0] == "ROCMExecutionProvider"


def test_cpu_fallback_always_present():
    ordered = select_providers(["CPUExecutionProvider"])
    assert ordered == ["CPUExecutionProvider"]


def test_unavailable_providers_are_skipped():
    ordered = select_providers(["CUDAExecutionProvider", "CPUExecutionProvider"])
    assert ordered == ["CUDAExecutionProvider", "CPUExecutionProvider"]


def test_create_session_falls_back_when_higher_priority_fails():
    attempted = []

    def factory(providers):
        attempted.append(providers[0])
        if providers[0] != "CPUExecutionProvider":
            raise RuntimeError("no GPU")
        return object()

    session, provider = create_session(
        ["ROCMExecutionProvider", "CPUExecutionProvider"], factory)

    assert provider == "CPUExecutionProvider"
    assert attempted == ["ROCMExecutionProvider", "CPUExecutionProvider"]


def test_create_session_raises_when_none_work():
    def factory(providers):
        raise RuntimeError("nope")

    try:
        create_session(["CPUExecutionProvider"], factory)
        assert False, "expected RuntimeError"
    except RuntimeError:
        pass
