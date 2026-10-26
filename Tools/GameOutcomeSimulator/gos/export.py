"""Export a validated batch for LoopLab (CSV) and DM systems (JSON), and read CSV back."""
import csv
import json
from pathlib import Path

from gos import SCHEMA_VERSION
from gos.schema import TABLES, columns
from gos.validate import validate_batch


class ValidationError(Exception):
    def __init__(self, errors):
        self.errors = errors
        super().__init__(f"{len(errors)} validation problem(s), first: {errors[0]}")


def write_csv(rows, path, table):
    """Write rows in contract column order. Missing optional fields become empty cells."""
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=columns(table))
        writer.writeheader()
        for row in rows:
            writer.writerow({k: row.get(k) for k in columns(table)})
    return path


def _coerce(value, typ, name, line):
    if value == "":
        return None
    try:
        if typ is bool:
            if value in ("True", "true", "1"):
                return True
            if value in ("False", "false", "0"):
                return False
            raise ValueError
        return typ(value)
    except ValueError:
        raise ValueError(f"line {line}: '{name}' should be {typ.__name__}, got {value!r}") from None


def read_csv(path, table):
    """Read a CSV back into typed rows. Header must match the contract exactly."""
    fields = {f.name: f.type for f in TABLES[table]}
    with Path(path).open(newline="") as f:
        reader = csv.DictReader(f)
        if reader.fieldnames != columns(table):
            raise ValueError(f"{path}: header does not match the {table} contract")
        return [{k: _coerce(v, fields[k], k, i) for k, v in row.items()} for i, row in enumerate(reader, start=2)]


def export_batch(out_dir, games, seasons=(), journey=(), weeks_per_season=10, points_per_win=2, write_json=True):
    """Validate, then write the batch. Writes nothing if validation fails."""
    errors = validate_batch(games, seasons, journey, weeks_per_season, points_per_win)
    if errors:
        raise ValidationError(errors)
    out = Path(out_dir)
    written = [write_csv(games, out / "game_outcomes.csv", "game_outcomes")]
    if seasons:
        written.append(write_csv(seasons, out / "season_summary.csv", "season_summary"))
    if journey:
        written.append(write_csv(journey, out / "player_journey.csv", "player_journey"))
    if not write_json:  # LoopLab only needs the CSVs, JSON is for DM systems testing
        return written
    doc = {"schema_version": SCHEMA_VERSION, "games": list(games), "seasons": list(seasons)}
    json_path = out / "game_outcomes.json"
    json_path.write_text(json.dumps(doc, separators=(",", ":")))  # compact, big batches get large
    written.append(json_path)
    return written
