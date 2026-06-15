from __future__ import annotations

import json
import os
import queue
import threading
import tkinter as tk
import webbrowser
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime, timezone
from pathlib import Path
from tkinter import filedialog, messagebox, ttk
from typing import Any, Callable

from .rating_sources import (
    SOURCE_LABELS,
    best_rating,
    create_rating_clients,
    default_ratings,
    normalized_rating_score,
)
from .vinmonopolet import AutoVinmonopoletClient, is_red_wine, product_to_wine


DEFAULT_CACHE_DIR = Path(".cache")


class VinlisteApp(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.title("Vinliste")
        self.geometry("1180x720")
        self.minsize(980, 580)

        self.wines: list[dict[str, Any]] = []
        self.worker: threading.Thread | None = None
        self.events: queue.Queue[tuple[str, Any]] = queue.Queue()
        self.stop_requested = threading.Event()

        self.api_key = tk.StringVar(value=os.environ.get("VINMONOPOLET_API_KEY", ""))
        self.limit = tk.StringVar(value="")
        self.delay = tk.StringVar(value="3")
        self.match_threshold = tk.StringVar(value="82")
        self.price_min = tk.StringVar(value="")
        self.price_max = tk.StringVar(value="")
        self.status = tk.StringVar(value="Ready")
        self.sort_column: str | None = None
        self.sort_descending = False
        self.price_min_value: float | None = None
        self.price_max_value: float | None = None
        self._table_wines: dict[str, dict[str, Any]] = {}
        self._context_cell: tuple[str, str] | None = None

        self._build_ui()
        self.after(100, self._poll_events)

    def _build_ui(self) -> None:
        self.columnconfigure(0, weight=1)
        self.rowconfigure(1, weight=1)

        controls = ttk.Frame(self, padding=(12, 10))
        controls.grid(row=0, column=0, sticky="ew")
        controls.columnconfigure(1, weight=1)

        ttk.Label(controls, text="Vinmonopolet API key").grid(row=0, column=0, sticky="w")
        ttk.Entry(controls, textvariable=self.api_key, show="*", width=42).grid(
            row=0, column=1, sticky="ew", padx=(8, 14)
        )

        ttk.Label(controls, text="Limit").grid(row=0, column=2, sticky="w")
        ttk.Entry(controls, textvariable=self.limit, width=8).grid(
            row=0, column=3, sticky="w", padx=(8, 14)
        )

        ttk.Label(controls, text="Rating delay").grid(row=0, column=4, sticky="w")
        ttk.Entry(controls, textvariable=self.delay, width=8).grid(
            row=0, column=5, sticky="w", padx=(8, 14)
        )

        ttk.Label(controls, text="Match threshold").grid(row=0, column=6, sticky="w")
        ttk.Entry(controls, textvariable=self.match_threshold, width=8).grid(
            row=0, column=7, sticky="w", padx=(8, 0)
        )

        ttk.Label(controls, text="Price min").grid(row=1, column=0, sticky="w", pady=(8, 0))
        ttk.Entry(controls, textvariable=self.price_min, width=10).grid(
            row=1, column=1, sticky="w", padx=(8, 14), pady=(8, 0)
        )

        ttk.Label(controls, text="Price max").grid(row=1, column=2, sticky="w", pady=(8, 0))
        ttk.Entry(controls, textvariable=self.price_max, width=10).grid(
            row=1, column=3, sticky="w", padx=(8, 14), pady=(8, 0)
        )

        ttk.Button(controls, text="Apply filter", command=self.apply_price_filter).grid(
            row=1, column=4, sticky="w", padx=(0, 8), pady=(8, 0)
        )
        ttk.Button(controls, text="Clear filter", command=self.clear_price_filter).grid(
            row=1, column=5, sticky="w", pady=(8, 0)
        )

        buttons = ttk.Frame(self, padding=(12, 0, 12, 8))
        buttons.grid(row=2, column=0, sticky="ew")
        buttons.columnconfigure(8, weight=1)

        self.scan_button = ttk.Button(
            buttons,
            text="Scan Vinmonopolet",
            command=self.scan_vinmonopolet,
        )
        self.scan_button.grid(row=0, column=0, padx=(0, 8))

        self.rate_button = ttk.Button(
            buttons,
            text="Get ratings",
            command=self.get_vivino_ratings,
        )
        self.rate_button.grid(row=0, column=1, padx=(0, 8))

        self.missing_button = ttk.Button(
            buttons,
            text="Get missing ratings",
            command=self.get_missing_ratings,
        )
        self.missing_button.grid(row=0, column=2, padx=(0, 8))

        self.selected_button = ttk.Button(
            buttons,
            text="Rate selected",
            command=self.get_selected_ratings,
        )
        self.selected_button.grid(row=0, column=3, padx=(0, 8))

        self.load_button = ttk.Button(buttons, text="Load JSON", command=self.load_json)
        self.load_button.grid(row=0, column=4, padx=(0, 8))

        self.save_button = ttk.Button(buttons, text="Save JSON", command=self.save_json)
        self.save_button.grid(row=0, column=5, padx=(0, 8))

        self.clear_button = ttk.Button(buttons, text="Clear", command=self.clear_results)
        self.clear_button.grid(row=0, column=6, padx=(0, 16))

        self.stop_button = ttk.Button(buttons, text="Stop", command=self.stop_current_task)
        self.stop_button.grid(row=0, column=7, padx=(0, 16))
        self.stop_button.configure(state="disabled")

        ttk.Label(buttons, textvariable=self.status).grid(row=0, column=8, sticky="w")

        table_frame = ttk.Frame(self, padding=(12, 0, 12, 0))
        table_frame.grid(row=1, column=0, sticky="nsew")
        table_frame.columnconfigure(0, weight=1)
        table_frame.rowconfigure(0, weight=1)

        rating_columns = tuple(SOURCE_LABELS)
        columns = (
            "productId",
            "name",
            "vintage",
            "country",
            "region",
            "price",
            "inStock",
            *rating_columns,
            "best",
            "status",
            "link",
        )
        self.table = ttk.Treeview(
            table_frame,
            columns=columns,
            show="headings",
            selectmode="extended",
        )
        headings = {
            "productId": "Product ID",
            "name": "Name",
            "vintage": "Vintage",
            "country": "Country",
            "region": "Region",
            "price": "Price NOK",
            "inStock": "In stock",
            "best": "Best rating",
            "status": "Rating status",
            "link": "Vinmonopolet",
        }
        headings.update(SOURCE_LABELS)
        widths = {
            "productId": 90,
            "name": 320,
            "vintage": 70,
            "country": 120,
            "region": 150,
            "price": 95,
            "inStock": 80,
            "vivino": 80,
            "cellartracker": 115,
            "falstaff": 85,
            "wine_enthusiast": 125,
            "wine_review_online": 145,
            "best": 90,
            "status": 150,
            "link": 115,
        }
        for column in columns:
            self.table.heading(
                column,
                text=headings[column],
                command=lambda selected=column: self.sort_by_column(selected),
            )
            self.table.column(column, width=widths[column], minwidth=50, anchor="w")

        y_scroll = ttk.Scrollbar(table_frame, orient="vertical", command=self.table.yview)
        x_scroll = ttk.Scrollbar(table_frame, orient="horizontal", command=self.table.xview)
        self.table.configure(yscrollcommand=y_scroll.set, xscrollcommand=x_scroll.set)

        self.table.grid(row=0, column=0, sticky="nsew")
        y_scroll.grid(row=0, column=1, sticky="ns")
        x_scroll.grid(row=1, column=0, sticky="ew")

        self.table.bind("<Control-c>", self.copy_selected_rows)
        self.table.bind("<ButtonRelease-1>", self.open_link_from_event)
        self.table.bind("<Double-1>", self.open_link_from_event)
        self.table.bind("<Button-3>", self.show_table_menu)
        self.table_menu = tk.Menu(self, tearoff=False)
        self.table_menu.add_command(label="Copy selected rows", command=self.copy_selected_rows)
        self.table_menu.add_command(label="Copy cell", command=self.copy_context_cell)

    def scan_vinmonopolet(self) -> None:
        api_key = self.api_key.get().strip()
        if not api_key:
            messagebox.showerror(
                "Missing API key",
                "Enter a Vinmonopolet API key or set VINMONOPOLET_API_KEY.",
            )
            return

        limit = self._parse_optional_int(self.limit.get(), "Limit")
        if limit is None and self.limit.get().strip():
            return

        self._start_worker(
            "Scanning Vinmonopolet...",
            lambda: self._scan_worker(api_key=api_key, limit=limit),
        )

    def get_vivino_ratings(self) -> None:
        if not self.wines:
            messagebox.showinfo("No wines", "Scan Vinmonopolet before getting ratings.")
            return

        delay = self._parse_float(self.delay.get(), "Rating delay")
        threshold = self._parse_float(self.match_threshold.get(), "Match threshold")
        if delay is None or threshold is None:
            return

        self._start_worker(
            "Getting ratings...",
            lambda: self._ratings_worker(
                delay=delay,
                threshold=threshold,
                target_wines=self.wines,
                missing_only=False,
            ),
        )

    def get_missing_ratings(self) -> None:
        if not self.wines:
            messagebox.showinfo("No wines", "Scan Vinmonopolet or load a JSON file first.")
            return

        delay = self._parse_float(self.delay.get(), "Rating delay")
        threshold = self._parse_float(self.match_threshold.get(), "Match threshold")
        if delay is None or threshold is None:
            return

        self._start_worker(
            "Getting missing ratings...",
            lambda: self._ratings_worker(
                delay=delay,
                threshold=threshold,
                target_wines=self.wines,
                missing_only=True,
            ),
        )

    def get_selected_ratings(self) -> None:
        selected_wines = self._selected_wines()
        if not selected_wines:
            messagebox.showinfo("No rows selected", "Select one or more rows to rate.")
            return

        delay = self._parse_float(self.delay.get(), "Rating delay")
        threshold = self._parse_float(self.match_threshold.get(), "Match threshold")
        if delay is None or threshold is None:
            return

        self._start_worker(
            f"Getting ratings for {len(selected_wines)} selected wines...",
            lambda: self._ratings_worker(
                delay=delay,
                threshold=threshold,
                target_wines=selected_wines,
                missing_only=False,
            ),
        )

    def load_json(self) -> None:
        path = filedialog.askopenfilename(
            title="Load saved results",
            filetypes=[("JSON files", "*.json"), ("All files", "*.*")],
        )
        if not path:
            return

        try:
            payload = json.loads(Path(path).read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as exc:
            messagebox.showerror("Load failed", str(exc))
            return

        wines = payload.get("wines") if isinstance(payload, dict) else payload
        if not isinstance(wines, list):
            messagebox.showerror("Load failed", "JSON must contain a wines array.")
            return

        self.wines = [_normalize_loaded_wine(wine) for wine in wines if isinstance(wine, dict)]
        self._refresh_table()
        self.status.set(f"Loaded {len(self.wines)} wines from {path}")

    def save_json(self) -> None:
        if not self.wines:
            messagebox.showinfo("No wines", "There are no results to save.")
            return

        path = filedialog.asksaveasfilename(
            title="Save results",
            defaultextension=".json",
            filetypes=[("JSON files", "*.json"), ("All files", "*.*")],
            initialfile="red_wines.json",
        )
        if not path:
            return

        payload = {
            "metadata": {
                "generated_at": datetime.now(timezone.utc).isoformat(),
                "source": "Vinmonopolet GUI",
                "rating_sources": list(SOURCE_LABELS),
                "count": len(self.wines),
            },
            "wines": [_refresh_computed_rating_fields(wine) for wine in self.wines],
        }
        Path(path).write_text(
            json.dumps(payload, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
        self.status.set(f"Saved {len(self.wines)} wines to {path}")

    def clear_results(self) -> None:
        if self.worker and self.worker.is_alive():
            return
        self.wines = []
        self._table_wines = {}
        self._refresh_table()
        self.status.set("Ready")

    def apply_price_filter(self) -> None:
        min_value = self._parse_optional_float(self.price_min.get(), "Price min")
        max_value = self._parse_optional_float(self.price_max.get(), "Price max")
        if min_value is None and self.price_min.get().strip():
            return
        if max_value is None and self.price_max.get().strip():
            return
        if min_value is not None and max_value is not None and min_value > max_value:
            messagebox.showerror("Invalid price filter", "Price min cannot be greater than price max.")
            return
        self.price_min_value = min_value
        self.price_max_value = max_value
        self._refresh_table()
        self.status.set(f"Showing {len(self._table_wines)} of {len(self.wines)} wines.")

    def clear_price_filter(self) -> None:
        self.price_min.set("")
        self.price_max.set("")
        self.price_min_value = None
        self.price_max_value = None
        self._refresh_table()
        self.status.set(f"Showing all {len(self.wines)} wines.")

    def stop_current_task(self) -> None:
        if self.worker and self.worker.is_alive():
            self.stop_requested.set()
            self.status.set("Stopping after the current request finishes...")

    def _scan_worker(self, *, api_key: str, limit: int | None) -> None:
        wines: list[dict[str, Any]] = []
        client = AutoVinmonopoletClient(api_key=api_key, verbose=False)

        for product in client.iter_products():
            if self.stop_requested.is_set():
                break
            if not is_red_wine(product):
                continue
            wine = product_to_wine(product)
            wine["ratings"] = default_ratings()
            wine["rating"] = wine["ratings"]["vivino"]
            wines.append(wine)
            if len(wines) % 25 == 0:
                self.events.put(("progress", f"Scanned {len(wines)} red wines..."))
                self.events.put(("rows", wines.copy()))
            if limit is not None and len(wines) >= limit:
                break

        self.events.put(("rows", wines))
        if self.stop_requested.is_set():
            self.events.put(("done", f"Stopped after scanning {len(wines)} red wines."))
        else:
            self.events.put(("done", f"Scanned {len(wines)} red wines."))

    def _ratings_worker(
        self,
        *,
        delay: float,
        threshold: float,
        target_wines: list[dict[str, Any]],
        missing_only: bool,
    ) -> None:
        clients = create_rating_clients(
            cache_dir=DEFAULT_CACHE_DIR,
            delay_seconds=delay,
            match_threshold=threshold,
            verbose=False,
        )
        total_steps = sum(
            1
            for wine in target_wines
            for client in clients
            if not missing_only or _needs_rating_update(wine, client.source)
        )
        completed_steps = 0

        for index, wine in enumerate(target_wines, start=1):
            if self.stop_requested.is_set():
                break
            ratings = wine.setdefault("ratings", default_ratings())
            clients_to_run = [
                client
                for client in clients
                if not missing_only or _needs_rating_update(wine, client.source)
            ]
            if not clients_to_run:
                continue
            name = wine.get("productLongName") or wine.get("productShortName") or "wine"
            self.events.put(
                (
                    "progress",
                    f"Rating {index}/{len(target_wines)}: {name}",
                )
            )
            with ThreadPoolExecutor(max_workers=len(clients_to_run)) as executor:
                futures = {
                    executor.submit(client.rate_wine, wine): client
                    for client in clients_to_run
                }
                for future in as_completed(futures):
                    client = futures[future]
                    if self.stop_requested.is_set():
                        break
                    ratings[client.source] = future.result()
                    if client.source == "vivino":
                        wine["rating"] = ratings[client.source]
                    _refresh_computed_rating_fields(wine)
                    completed_steps += 1
                    label = SOURCE_LABELS.get(client.source, client.source)
                    self.events.put(
                        (
                            "progress",
                            f"Completed {completed_steps}/{total_steps}: {label}",
                        )
                    )
                    self.events.put(("rows", self.wines.copy()))

        for client in clients:
            client.save_cache()
        if self.stop_requested.is_set():
            self.events.put(("done", "Stopped rating run. Partial results were kept."))
        else:
            self.events.put(("done", f"Finished {completed_steps} rating checks."))

    def _start_worker(self, message: str, target: Callable[[], None]) -> None:
        if self.worker and self.worker.is_alive():
            messagebox.showinfo("Busy", "A task is already running.")
            return

        self.status.set(message)
        self.stop_requested.clear()
        self._set_buttons_enabled(False)

        def run() -> None:
            try:
                target()
            except Exception as exc:  # pragma: no cover - UI safety net.
                self.events.put(("error", str(exc)))

        self.worker = threading.Thread(target=run, daemon=True)
        self.worker.start()

    def _poll_events(self) -> None:
        while True:
            try:
                event, payload = self.events.get_nowait()
            except queue.Empty:
                break

            if event == "progress":
                self.status.set(payload)
            elif event == "rows":
                self.wines = payload
                self._refresh_table()
            elif event == "done":
                self.status.set(payload)
                self._set_buttons_enabled(True)
                self.stop_requested.clear()
            elif event == "error":
                self.status.set("Error")
                self._set_buttons_enabled(True)
                self.stop_requested.clear()
                messagebox.showerror("Task failed", payload)

        self.after(100, self._poll_events)

    def _refresh_table(self) -> None:
        if self.sort_column:
            self.wines.sort(
                key=lambda wine: _sort_value(wine, self.sort_column or ""),
                reverse=self.sort_descending,
            )
        self.table.delete(*self.table.get_children())
        self._table_wines = {}
        for index, wine in enumerate(wine for wine in self.wines if self._passes_filters(wine)):
            item_id = f"wine-{index}-{wine.get('productId') or ''}"
            self._table_wines[item_id] = wine
            self.table.insert("", "end", iid=item_id, values=_row_values(wine))

    def sort_by_column(self, column: str) -> None:
        if self.sort_column == column:
            self.sort_descending = not self.sort_descending
        else:
            self.sort_column = column
            self.sort_descending = False
        self._refresh_table()

    def _set_buttons_enabled(self, enabled: bool) -> None:
        state = "normal" if enabled else "disabled"
        for button in (
            self.scan_button,
            self.rate_button,
            self.missing_button,
            self.selected_button,
            self.load_button,
            self.save_button,
            self.clear_button,
        ):
            button.configure(state=state)
        self.stop_button.configure(state="disabled" if enabled else "normal")

    def _parse_optional_int(self, value: str, label: str) -> int | None:
        value = value.strip()
        if not value:
            return None
        try:
            parsed = int(value)
        except ValueError:
            messagebox.showerror("Invalid value", f"{label} must be a whole number.")
            return None
        if parsed <= 0:
            messagebox.showerror("Invalid value", f"{label} must be greater than zero.")
            return None
        return parsed

    def _parse_float(self, value: str, label: str) -> float | None:
        try:
            parsed = float(value.strip().replace(",", "."))
        except ValueError:
            messagebox.showerror("Invalid value", f"{label} must be a number.")
            return None
        if parsed < 0:
            messagebox.showerror("Invalid value", f"{label} cannot be negative.")
            return None
        return parsed

    def _parse_optional_float(self, value: str, label: str) -> float | None:
        value = value.strip()
        if not value:
            return None
        return self._parse_float(value, label)

    def _passes_filters(self, wine: dict[str, Any]) -> bool:
        price = _price_value(wine)
        if self.price_min_value is not None and (
            price is None or price < self.price_min_value
        ):
            return False
        if self.price_max_value is not None and (
            price is None or price > self.price_max_value
        ):
            return False
        return True

    def _selected_wines(self) -> list[dict[str, Any]]:
        return [
            self._table_wines[item_id]
            for item_id in self.table.selection()
            if item_id in self._table_wines
        ]

    def copy_selected_rows(self, _event: tk.Event | None = None) -> str:
        selected = self.table.selection()
        if not selected:
            return "break"
        lines = []
        for item_id in selected:
            wine = self._table_wines.get(item_id)
            if not wine:
                continue
            lines.append("\t".join(str(value or "") for value in _row_values(wine)))
        if lines:
            self.clipboard_clear()
            self.clipboard_append("\n".join(lines))
            self.status.set(f"Copied {len(lines)} row(s).")
        return "break"

    def show_table_menu(self, event: tk.Event) -> None:
        item_id = self.table.identify_row(event.y)
        column_id = self.table.identify_column(event.x)
        if item_id and item_id not in self.table.selection():
            self.table.selection_set(item_id)
        self._context_cell = (item_id, column_id) if item_id and column_id else None
        self.table_menu.tk_popup(event.x_root, event.y_root)

    def copy_context_cell(self) -> None:
        if not self._context_cell:
            return
        item_id, column_id = self._context_cell
        wine = self._table_wines.get(item_id)
        column = _column_name_from_id(self.table["columns"], column_id)
        if not wine or not column:
            return
        value = _copy_value(wine, column)
        self.clipboard_clear()
        self.clipboard_append(str(value or ""))
        self.status.set("Copied cell.")

    def open_link_from_event(self, event: tk.Event) -> str | None:
        item_id = self.table.identify_row(event.y)
        column = _column_name_from_id(self.table["columns"], self.table.identify_column(event.x))
        if column != "link":
            return None
        wine = self._table_wines.get(item_id)
        url = _wine_url(wine or {})
        if url:
            webbrowser.open(url)
            self.status.set(f"Opened {url}")
        return "break"


def _row_values(wine: dict[str, Any]) -> tuple[Any, ...]:
    price = wine.get("price") or {}
    ratings = _ratings_for_wine(wine)
    best = best_rating(wine)
    return (
        wine.get("productId"),
        wine.get("productLongName") or wine.get("productShortName"),
        wine.get("vintage"),
        wine.get("country"),
        wine.get("region"),
        price.get("salesPrice"),
        _in_stock_display(wine),
        *(_rating_display(ratings.get(source)) for source in SOURCE_LABELS),
        best.get("score"),
        _status_display(ratings),
        "Open" if _wine_url(wine) else "",
    )


def _empty_rating() -> dict[str, Any]:
    return default_ratings()["vivino"]


def _ratings_for_wine(wine: dict[str, Any]) -> dict[str, dict[str, Any]]:
    ratings = wine.get("ratings")
    merged = default_ratings()
    if isinstance(ratings, dict):
        merged.update(ratings)
        return merged
    merged["vivino"] = wine.get("rating") or _empty_rating()
    return merged


def _rating_display(rating: dict[str, Any] | None) -> str:
    if not rating:
        return ""
    score = normalized_rating_score(rating)
    count = rating.get("rating_count")
    if score is not None and count is not None:
        return f"{score} ({count})"
    if score is not None:
        return str(score)
    return str(rating.get("status") or "")


def _status_display(ratings: dict[str, dict[str, Any]]) -> str:
    counts: dict[str, int] = {}
    for source in SOURCE_LABELS:
        rating = ratings.get(source) or {}
        status = str(rating.get("status") or "unknown")
        counts[status] = counts.get(status, 0) + 1
    return ", ".join(f"{status}: {count}" for status, count in sorted(counts.items()))


def _sort_value(wine: dict[str, Any], column: str) -> tuple[int, Any]:
    ratings = _ratings_for_wine(wine)
    best = best_rating(wine)
    values = {
        "productId": wine.get("productId"),
        "name": wine.get("productLongName") or wine.get("productShortName"),
        "vintage": wine.get("vintage"),
        "country": wine.get("country"),
        "region": wine.get("region"),
        "price": _price_value(wine),
        "inStock": _in_stock_value(wine),
        "best": best.get("score"),
        "status": _status_display(ratings),
        "link": _wine_url(wine),
    }
    for source in SOURCE_LABELS:
        values[source] = normalized_rating_score(ratings.get(source))
    value = values.get(column)
    if value is None:
        return (1, "")
    if isinstance(value, (int, float)):
        return (0, value)
    return (0, str(value).casefold())


def _normalize_loaded_wine(wine: dict[str, Any]) -> dict[str, Any]:
    normalized = dict(wine)
    normalized["ratings"] = _ratings_for_wine(normalized)
    normalized["rating"] = normalized["ratings"].get("vivino") or normalized.get("rating")
    if not normalized.get("url"):
        normalized["url"] = _wine_url(normalized)
    if "availability" not in normalized:
        normalized["availability"] = {
            "inStock": _legacy_in_stock(normalized),
            "summary": ((normalized.get("saleStatus") or {}).get("name")),
        }
    return _refresh_computed_rating_fields(normalized)


def _refresh_computed_rating_fields(wine: dict[str, Any]) -> dict[str, Any]:
    ratings = wine.get("ratings")
    if isinstance(ratings, dict):
        for rating in ratings.values():
            if isinstance(rating, dict):
                rating["score_100"] = normalized_rating_score(rating)
    wine["bestRating"] = best_rating(wine)
    return wine


def _needs_rating_update(wine: dict[str, Any], source: str) -> bool:
    rating = (_ratings_for_wine(wine).get(source) or {})
    return rating.get("status") != "matched" or normalized_rating_score(rating) is None


def _price_value(wine: dict[str, Any]) -> float | None:
    price = (wine.get("price") or {}).get("salesPrice")
    try:
        return float(price) if price is not None else None
    except (TypeError, ValueError):
        return None


def _in_stock_display(wine: dict[str, Any]) -> str:
    value = _in_stock_value(wine)
    if value is True:
        return "Yes"
    if value is False:
        return "No"
    return ""


def _in_stock_value(wine: dict[str, Any]) -> bool | None:
    availability = wine.get("availability") or {}
    if "inStock" in availability:
        value = availability.get("inStock")
        if isinstance(value, bool):
            return value
    return _legacy_in_stock(wine)


def _legacy_in_stock(wine: dict[str, Any]) -> bool | None:
    status = str((wine.get("saleStatus") or {}).get("name") or "").casefold()
    if not status:
        return None
    if status in {"aktiv", "active"}:
        return True
    if status in {"utsolgt", "inactive", "expired"}:
        return False
    return None


def _wine_url(wine: dict[str, Any]) -> str | None:
    url = wine.get("url")
    if url:
        return str(url)
    product_id = wine.get("productId")
    if product_id:
        return f"https://www.vinmonopolet.no/p/{product_id}"
    return None


def _column_name_from_id(columns: Any, column_id: str) -> str | None:
    if not column_id.startswith("#"):
        return None
    try:
        index = int(column_id[1:]) - 1
    except ValueError:
        return None
    if 0 <= index < len(columns):
        return str(columns[index])
    return None


def _copy_value(wine: dict[str, Any], column: str) -> Any:
    if column == "link":
        return _wine_url(wine)
    values = dict(zip(_table_columns(), _row_values(wine)))
    return values.get(column)


def _table_columns() -> tuple[str, ...]:
    return (
        "productId",
        "name",
        "vintage",
        "country",
        "region",
        "price",
        "inStock",
        *SOURCE_LABELS,
        "best",
        "status",
        "link",
    )


def main() -> int:
    app = VinlisteApp()
    app.mainloop()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
