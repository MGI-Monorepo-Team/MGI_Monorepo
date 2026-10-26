import pytest

pytest.importorskip("matplotlib")

from gos.cli import main as run_sim  # noqa: E402
from gos.report import main as report  # noqa: E402


def test_report_builds_for_one_folder(tmp_path):
    out = tmp_path / "out"
    assert run_sim(["--runs", "2", "--seasons", "2", "--no-json", "--out", str(out)]) == 0
    assert report([str(out)]) == 0
    assert (out / "report.png").stat().st_size > 10_000


def test_report_compares_two_folders(tmp_path):
    a, b = tmp_path / "a", tmp_path / "b"
    run_sim(["--runs", "2", "--seasons", "2", "--no-json", "--out", str(a)])
    run_sim(["--runs", "2", "--seasons", "2", "--mode", "lump", "--no-json", "--out", str(b)])
    assert report([str(a), "--compare", str(b)]) == 0


def test_report_fails_on_a_broken_batch(tmp_path):
    out = tmp_path / "out"
    run_sim(["--runs", "1", "--seasons", "1", "--no-json", "--out", str(out)])
    p = out / "game_outcomes.csv"
    lines = p.read_text().splitlines()
    lines.pop()  # drop one row so a match has 1 row
    p.write_text("\n".join(lines) + "\n")
    assert report([str(out)]) == 1
