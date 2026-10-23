from __future__ import annotations

import argparse
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

from .config import CONFIG_FILENAME, load_api_key
from .exporter import build_export
from .vinmonopolet import AutoVinmonopoletClient
from .vivino import VivinoRatingClient


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="vinliste",
        description="Export Vinmonopolet red wines to JSON with optional Vivino ratings.",
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    export = subparsers.add_parser("export", help="Export red wines to JSON.")
    export.add_argument("--output", required=True, help="Destination JSON file.")
    export.add_argument(
        "--api-key-env",
        default="VINMONOPOLET_API_KEY",
        help="Environment variable containing the Vinmonopolet API key.",
    )
    export.add_argument(
        "--rating-source",
        choices=["vivino", "none"],
        default="vivino",
        help="External rating source to enrich with.",
    )
    export.add_argument(
        "--match-threshold",
        type=float,
        default=82.0,
        help="Minimum confidence for rating matches.",
    )
    export.add_argument(
        "--limit",
        type=int,
        default=None,
        help="Limit red wines exported, useful for smoke tests.",
    )
    export.add_argument(
        "--delay",
        type=float,
        default=3.0,
        help="Delay in seconds between Vivino requests.",
    )
    export.add_argument(
        "--cache",
        default=".cache/vivino_ratings.json",
        help="Path to Vivino response cache.",
    )
    export.add_argument("--verbose", action="store_true", help="Print progress.")
    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)

    if args.command != "export":
        parser.error("unknown command")

    api_key = load_api_key(args.api_key_env)
    if not api_key:
        print(
            f"Missing Vinmonopolet API key. Add it to {CONFIG_FILENAME} "
            f"or set ${args.api_key_env} before running.",
            file=sys.stderr,
        )
        return 2

    output_path = Path(args.output)
    rating_client = None
    if args.rating_source == "vivino":
        rating_client = VivinoRatingClient(
            cache_path=Path(args.cache),
            delay_seconds=args.delay,
            match_threshold=args.match_threshold,
            verbose=args.verbose,
        )

    client = AutoVinmonopoletClient(api_key=api_key, verbose=args.verbose)
    export_data = build_export(
        client=client,
        rating_client=rating_client,
        rating_source=args.rating_source,
        match_threshold=args.match_threshold,
        limit=args.limit,
        generated_at=datetime.now(timezone.utc),
        verbose=args.verbose,
    )

    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(
        json.dumps(export_data, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )

    if rating_client is not None:
        rating_client.save_cache()

    if args.verbose:
        print(f"Wrote {len(export_data['wines'])} wines to {output_path}")
    return 0
