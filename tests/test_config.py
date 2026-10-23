from __future__ import annotations

import json
from pathlib import Path

from vinliste.config import load_api_key


def _write_config(path: Path, primary: str, secondary: str) -> Path:
    path.write_text(
        json.dumps({"vinmonopolet": {"primary_key": primary, "secondary_key": secondary}}),
        encoding="utf-8",
    )
    return path


def test_load_api_key_prefers_environment(monkeypatch, tmp_path) -> None:
    monkeypatch.setenv("VINMONOPOLET_API_KEY", "env-key")
    config = _write_config(tmp_path / "vinliste.local.json", "primary", "secondary")

    assert load_api_key(paths=[config]) == "env-key"


def test_load_api_key_uses_primary_then_secondary(monkeypatch, tmp_path) -> None:
    monkeypatch.delenv("VINMONOPOLET_API_KEY", raising=False)

    primary = _write_config(tmp_path / "a.json", "primary", "secondary")
    secondary_only = _write_config(tmp_path / "b.json", "", "secondary")

    assert load_api_key(paths=[primary]) == "primary"
    assert load_api_key(paths=[secondary_only]) == "secondary"


def test_load_api_key_missing_or_empty_config(monkeypatch, tmp_path) -> None:
    monkeypatch.delenv("VINMONOPOLET_API_KEY", raising=False)
    empty = _write_config(tmp_path / "empty.json", "", "")

    assert load_api_key(paths=[tmp_path / "missing.json"]) is None
    assert load_api_key(paths=[empty]) is None
