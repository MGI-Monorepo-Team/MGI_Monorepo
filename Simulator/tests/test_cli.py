import json

from gos import SCHEMA_VERSION
from gos.cli import main as run_sim
from gos.engine import MODEL_VERSION


def test_cli_records_seed_and_versions(tmp_path):
    out = tmp_path / "out"
    assert run_sim(["--runs", "1", "--seasons", "1", "--seed", "7", "--no-json", "--out", str(out)]) == 0
    info = json.loads((out / "run_info.json").read_text())
    assert info == {"schema_version": SCHEMA_VERSION, "model_version": MODEL_VERSION, "seed": 7,
                    "mode": "results", "runs": 1, "seasons": 1}
