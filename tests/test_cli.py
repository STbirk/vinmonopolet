from __future__ import annotations

import json

from vinliste import cli


class DummyClient:
    def __init__(self, *, api_key, verbose=False):
        self.api_key = api_key
        self.verbose = verbose


def test_cli_export_writes_json_without_live_network(monkeypatch, tmp_path) -> None:
    output = tmp_path / "wines.json"

    monkeypatch.setenv("VINMONOPOLET_API_KEY", "test-key")
    monkeypatch.setattr(cli, "AutoVinmonopoletClient", DummyClient)
    monkeypatch.setattr(
        cli,
        "build_export",
        lambda **kwargs: {
            "metadata": {"count": 0},
            "wines": [],
        },
    )

    exit_code = cli.main(["export", "--output", str(output), "--rating-source", "none"])

    assert exit_code == 0
    assert json.loads(output.read_text(encoding="utf-8")) == {
        "metadata": {"count": 0},
        "wines": [],
    }


def test_cli_missing_api_key_returns_error(monkeypatch, tmp_path) -> None:
    monkeypatch.delenv("VINMONOPOLET_API_KEY", raising=False)

    exit_code = cli.main(["export", "--output", str(tmp_path / "wines.json")])

    assert exit_code == 2
