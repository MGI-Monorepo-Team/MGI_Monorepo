import json

import pytest

from gos.export import ValidationError, export_batch, read_csv, write_csv
from gos.schema import columns


def test_export_writes_all_files(tmp_path, batch, journey):
    games, seasons = batch
    files = export_batch(tmp_path, games, seasons, journey, weeks_per_season=3)
    assert sorted(p.name for p in files) == [
        "game_outcomes.csv", "game_outcomes.json", "player_journey.csv", "season_summary.csv"]


def test_csv_round_trip_keeps_types(tmp_path, batch):
    games, _ = batch
    path = write_csv(games, tmp_path / "g.csv", "game_outcomes")
    assert read_csv(path, "game_outcomes") == games


def test_csv_header_is_in_contract_order(tmp_path, batch):
    games, _ = batch
    path = write_csv(games, tmp_path / "g.csv", "game_outcomes")
    assert path.read_text().splitlines()[0].split(",") == columns("game_outcomes")


def test_optional_fields_read_back_as_none(tmp_path, journey):
    path = write_csv(journey, tmp_path / "j.csv", "player_journey")
    row = read_csv(path, "player_journey")[0]
    assert row["placement"] is None and row["season_bonus_coins"] is None


def test_json_has_version_games_and_seasons(tmp_path, batch):
    games, seasons = batch
    export_batch(tmp_path, games, seasons, weeks_per_season=3)
    doc = json.loads((tmp_path / "game_outcomes.json").read_text())
    assert doc["schema_version"] == "0.2"
    assert len(doc["games"]) == len(games) and len(doc["seasons"]) == len(seasons)


def test_invalid_batch_writes_nothing(tmp_path, batch):
    games, seasons = batch
    games[0]["result"] = "T"
    with pytest.raises(ValidationError) as err:
        export_batch(tmp_path / "out", games, seasons, weeks_per_season=3)
    assert "'result' must be one of" in err.value.errors[0]
    assert not (tmp_path / "out").exists()


def test_bad_header_is_rejected(tmp_path):
    p = tmp_path / "bad.csv"
    p.write_text("team,result\nt0,W\n")
    with pytest.raises(ValueError, match="header does not match"):
        read_csv(p, "game_outcomes")


def test_bad_value_names_the_line(tmp_path, batch):
    games, _ = batch
    path = write_csv(games, tmp_path / "g.csv", "game_outcomes")
    lines = path.read_text().splitlines()
    cells = lines[3].split(",")
    cells[columns("game_outcomes").index("week")] = "three"
    lines[3] = ",".join(cells)
    path.write_text("\n".join(lines) + "\n")
    with pytest.raises(ValueError, match="line 4: 'week' should be int"):
        read_csv(path, "game_outcomes")


def test_json_can_be_skipped(tmp_path, batch):
    games, seasons = batch
    files = export_batch(tmp_path, games, seasons, weeks_per_season=3, write_json=False)
    assert "game_outcomes.json" not in [p.name for p in files]
