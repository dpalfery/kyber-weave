from fixture import add


def test_add_sums_two_numbers() -> None:
    assert add(2, 3) == 5
