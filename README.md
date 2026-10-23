# Vinliste

Export active Vinmonopolet red wines to JSON and enrich them with best-effort public ratings.

## Setup

```powershell
python -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install -e ".[dev]"
Copy-Item vinliste.example.json vinliste.local.json
```

Put your Vinmonopolet subscription keys in `vinliste.local.json` (`primary_key`, and optionally `secondary_key` as a fallback). That file is git-ignored, so the keys stay on your machine. The `VINMONOPOLET_API_KEY` environment variable, if set, takes precedence over the file.

The API key is never written to source or output files.
The CLI probes the official Product API first. If the subscription only returns sparse records, it falls back to Vinmonopolet's public product-search JSON.

## Usage

```powershell
python -m vinliste export --output red_wines.json --rating-source vivino --match-threshold 82 --delay 3 --verbose
```

Launch the GUI with:

```powershell
python -m vinliste.gui
```

or, after installation:

```powershell
vinliste-gui
```

In the GUI, click any table header to sort that column. Use `Load JSON` to reopen saved exports, `Save JSON` to write the current dataset, and the price fields to filter by NOK range. Select one or more rows with Shift/Ctrl and use `Rate selected` to update only those rows. `Get missing ratings` skips sources that already have a matched score.

The table supports Ctrl+C for selected rows and right-click copy for a single cell. The Vinmonopolet column opens the product page in your browser. The ratings button checks Vivino, CellarTracker, Falstaff, and Wine Enthusiast with best-effort public lookups. Scores are shown on a shared 0-100 scale, and `Best rating` is the average of matched source scores. Sources run in parallel for each wine so the window keeps reporting progress instead of looking frozen. The status line updates for each wine/source, and `Stop` keeps partial results if a run is taking too long.

Useful options:

- `--output`: JSON file path.
- `--api-key-env`: environment variable containing the Vinmonopolet API key. Defaults to `VINMONOPOLET_API_KEY`.
- `--rating-source`: `vivino` or `none`.
- `--match-threshold`: minimum fuzzy match confidence for Vivino matches.
- `--limit`: limit exported red wines for smoke tests.
- `--delay`: delay between Vivino requests.
- `--cache`: Vivino cache path. Defaults to `.cache/vivino_ratings.json`.
- `--verbose`: print progress.

## Notes

Rating lookup is best-effort. If a source blocks, requires login, or shows a captcha, the exporter records the rating status and still keeps the Vinmonopolet product and price data.
