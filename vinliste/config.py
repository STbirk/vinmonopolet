from __future__ import annotations

import json
import os
from pathlib import Path
from typing import Any


CONFIG_FILENAME = "vinliste.local.json"
DEFAULT_API_KEY_ENV = "VINMONOPOLET_API_KEY"


def config_paths() -> list[Path]:
    """Where the local config is looked for: the working directory, then the project root."""
    paths = [Path.cwd() / CONFIG_FILENAME, Path(__file__).resolve().parents[1] / CONFIG_FILENAME]
    deduped: list[Path] = []
    for path in paths:
        if path not in deduped:
            deduped.append(path)
    return deduped


def load_config(paths: list[Path] | None = None) -> dict[str, Any]:
    for path in paths if paths is not None else config_paths():
        if not path.exists():
            continue
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            continue
        if isinstance(data, dict):
            return data
    return {}


def load_api_key(
    env_name: str = DEFAULT_API_KEY_ENV,
    *,
    paths: list[Path] | None = None,
) -> str | None:
    """Return the Vinmonopolet key: the environment variable wins, then the primary
    key from the local config, then its secondary key."""
    env_value = os.environ.get(env_name, "").strip()
    if env_value:
        return env_value

    section = load_config(paths).get("vinmonopolet")
    if not isinstance(section, dict):
        return None
    for field in ("primary_key", "secondary_key"):
        value = str(section.get(field) or "").strip()
        if value:
            return value
    return None
