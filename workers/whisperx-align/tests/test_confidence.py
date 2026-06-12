from worker.confidence import flag, is_low_confidence


def test_is_low_confidence():
    assert is_low_confidence(0.1, 0.30)
    assert not is_low_confidence(0.9, 0.30)
    assert not is_low_confidence(0.30, 0.30)  # boundary is not "below"


def test_flag_list():
    assert flag([0.9, 0.2, 0.5, 0.05], 0.30) == [False, True, False, True]
