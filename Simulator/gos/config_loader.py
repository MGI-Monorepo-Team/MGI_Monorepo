"""Read the management-game configuration used as simulator input. 
The simulator reads the repository's StreamingAssets files instead of keeping
copied values, so simulation inputs stay aligned with Unity."""
from __future__ import annotations

import json
from dataclasses import dataclass
from functools import lru_cache
from pathlib import Path
from typing import Any

REPO_ROOT = Path(__file__).resolve().parents[2]  # gos -> Simulator -> repo


class ConfigurationLoadError(ValueError):
    """Raised when a required simulator input is missing or malformed."""


@dataclass(frozen=True)
class SimulatorConfiguration:
    """Raw, source-owned configuration consumed by the outcome simulator."""

    cards_catalog: dict[str, Any]
    teams: list[dict[str, Any]]
    coaches: list[dict[str, Any]]
    coach_bonus_rules: dict[str, Any]
    facilities: dict[str, Any]
    progression: dict[str, Any]


class StreamingAssetsConfigLoader:
    """Loads the configuration inputs required by the simulator."""

    _RELATIVE_PATHS = {
        "cards_catalog": Path("CCAS/cards_catalog.json"),
        "teams": Path("Coaches/Database/team.json"),
        "coaches": Path("Coaches/coaches_schema_data.json"),
        "coach_bonus_rules": Path("Coaches/coaches_bonus_config.json"),
        "facilities": Path("Facilities/facilities_config.json"),
        "progression": Path("Progression/progression.json"),
    }

    def __init__(self, streaming_assets_path: str | Path) -> None:
        self._streaming_assets_path = Path(streaming_assets_path)

    @classmethod
    def from_repository_root(cls, repository_root: str | Path = REPO_ROOT) -> "StreamingAssetsConfigLoader":
        return cls(Path(repository_root) / "Assets" / "StreamingAssets")

    def load(self) -> SimulatorConfiguration:
        loaded = {name: self._read_json(name, path) for name, path in self._RELATIVE_PATHS.items()}

        self._require_object("cards_catalog", loaded["cards_catalog"])
        self._require_list("teams", loaded["teams"])
        self._require_object("coaches", loaded["coaches"])
        self._require_list("coaches.coach", loaded["coaches"].get("coach"))
        self._require_object("coach_bonus_rules", loaded["coach_bonus_rules"])
        self._require_object("facilities", loaded["facilities"])
        self._require_object("progression", loaded["progression"])

        return SimulatorConfiguration(
            cards_catalog=loaded["cards_catalog"],
            teams=loaded["teams"],
            coaches=loaded["coaches"]["coach"],
            coach_bonus_rules=loaded["coach_bonus_rules"],
            facilities=loaded["facilities"],
            progression=loaded["progression"],
        )

    def _read_json(self, name: str, relative_path: Path) -> Any:
        path = self._streaming_assets_path / relative_path
        if not path.is_file():
            raise ConfigurationLoadError(f"Missing {name} configuration: {path}")
        try:
            with path.open(encoding="utf-8") as source:
                return json.load(source)
        except json.JSONDecodeError as error:
            raise ConfigurationLoadError(f"Invalid JSON in {name} configuration: {path}") from error

    @staticmethod
    def _require_object(name: str, value: Any) -> None:
        if not isinstance(value, dict):
            raise ConfigurationLoadError(f"Expected {name} configuration to be a JSON object")

    @staticmethod
    def _require_list(name: str, value: Any) -> None:
        if not isinstance(value, list):
            raise ConfigurationLoadError(f"Expected {name} configuration to be a JSON array")


@lru_cache(maxsize=None)
def load_repository_config(repository_root: str | None = None) -> SimulatorConfiguration:
    """Load once per process. Pass a root to read a different checkout."""
    return StreamingAssetsConfigLoader.from_repository_root(repository_root or REPO_ROOT).load()
