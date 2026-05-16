#!/usr/bin/env python3
"""
PinballPrices xlsx -> community pricing seed.

Ted "Doc" Finlay (PinballPrices.com) periodically emails an updated Excel
workbook containing his hand-curated pinball sale records. This tool
ingests that workbook and emits a normalized seed JSON consumed by the
Wizard's Valuation agent.

Data flow
---------
    Gmail (from:pinballprices@gmail.com, has:attachment, *.xlsx)
        |
        v  (operator downloads the attachment; see README.md)
    data/PinballPrices/<workbook>.xlsx        (gitignored - Ted's raw data)
        |
        v  (this tool)
    data/seeds/community_pricing_pinballprices.v1.json   (committed seed)
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import pandas as pd

TOOL_VERSION = "1.0.0"
SCHEMA_VERSION = 1

YEAR_BUCKETS: list[str] = [
    "<2016", "2016", "2017", "2018", "2019", "2020",
    "2021", "2022", "2023", "2024", "2025", "2026",
]

ERAS: list[str] = [
    "EM", "EarlySS", "Alphanumeric", "GoldenAgeDmd", "ModernDmd", "Lcd",
]

ERA_HEADER_NORMALIZATION: dict[str, str] = {
    "em": "EM",
    "early ss": "EarlySS",
    "alphanumeric": "Alphanumeric",
    "golden age dmd": "GoldenAgeDmd",
    "modern dmd": "ModernDmd",
    "lcd": "Lcd",
}


@dataclass
class IngestResult:
    seed: dict[str, Any]
    report: list[str]


def _file_sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def _normalize_str(value: Any) -> str | None:
    if value is None:
        return None
    if isinstance(value, float) and pd.isna(value):
        return None
    s = str(value).strip()
    if not s:
        return None
    return re.sub(r"\s+", " ", s)


def _safe_int(value: Any) -> int | None:
    """Coerce to int, rounding first to absorb spreadsheet formula FP noise.

    Cells like Total Titles come through pandas as 1498.99999999995 because
    Ted's Excel SUM/COUNTUNIQUE formulas round-trip through double precision.
    Plain int() truncates and would emit 1498 for a cell that displays as
    1499 in Excel.
    """
    if value is None:
        return None
    if isinstance(value, float) and pd.isna(value):
        return None
    try:
        return int(round(float(value)))
    except (TypeError, ValueError):
        return None


def _safe_float(value: Any) -> float | None:
    if value is None:
        return None
    if isinstance(value, float) and pd.isna(value):
        return None
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def _round_money(value: float | None) -> float | None:
    if value is None:
        return None
    return round(value, 2)


def _looks_like_number(value: str) -> bool:
    try:
        float(value)
        return True
    except ValueError:
        return False


def parse_notes_sheet(xlsx_path: Path, warnings: list[str]) -> dict[str, Any]:
    notes = pd.read_excel(xlsx_path, sheet_name="Notes", header=None)

    as_of: str | None = None
    total_titles: int | None = None
    total_sales: int | None = None
    total_recorded_usd: float | None = None

    for _, row in notes.iterrows():
        label = _normalize_str(row.iloc[0]) if len(row) > 0 else None
        if not label:
            continue
        col_b = row.iloc[1] if len(row) > 1 else None
        col_c = row.iloc[2] if len(row) > 2 else None
        normalized_label = label.lower().rstrip(":").strip()

        if normalized_label.startswith("last date of data entered"):
            for candidate in (col_c, col_b):
                if isinstance(candidate, (pd.Timestamp, datetime)):
                    as_of = candidate.date().isoformat()
                    break
        elif normalized_label == "total titles":
            total_titles = _safe_int(col_b) or _safe_int(col_c)
        elif normalized_label == "total # sales":
            total_sales = _safe_int(col_b) or _safe_int(col_c)
        elif normalized_label == "total $":
            total_recorded_usd = _round_money(
                _safe_float(col_b) or _safe_float(col_c)
            )

    if as_of is None:
        warnings.append("Notes sheet: could not find 'Last Date of data entered:'")
    if total_titles is None:
        warnings.append("Notes sheet: could not find 'Total Titles'")
    if total_sales is None:
        warnings.append("Notes sheet: could not find 'Total # Sales'")
    if total_recorded_usd is None:
        warnings.append("Notes sheet: could not find 'Total $'")

    return {
        "as_of": as_of,
        "ted_reported_unique_titles": total_titles,
        "ted_reported_total_sale_records": total_sales,
        "ted_reported_total_recorded_sales_usd": total_recorded_usd,
    }


def parse_charts_sheet(xlsx_path: Path, warnings: list[str]) -> list[dict[str, Any]]:
    charts = pd.read_excel(xlsx_path, sheet_name="Charts", header=None)

    header_row_idx: int | None = None
    for idx in range(min(10, len(charts))):
        row = charts.iloc[idx]
        for col_idx in range(len(row)):
            cell = _normalize_str(row.iloc[col_idx])
            if cell and cell.lower() == "year sold":
                header_row_idx = idx
                break
        if header_row_idx is not None:
            break

    if header_row_idx is None:
        warnings.append("Charts sheet: could not locate 'Year Sold' header row")
        return []

    header_row = charts.iloc[header_row_idx]
    year_sold_col: int | None = None
    era_columns: list[tuple[int, str]] = []
    for col_idx in range(len(header_row)):
        cell = _normalize_str(header_row.iloc[col_idx])
        if cell is None:
            continue
        if cell.lower() == "year sold":
            year_sold_col = col_idx
            continue
        normalized = ERA_HEADER_NORMALIZATION.get(cell.lower())
        if normalized is not None:
            era_columns.append((col_idx, normalized))
        else:
            warnings.append(
                "Charts sheet: unrecognized era header "
                + repr(cell) + " at column " + str(col_idx) + "; skipping"
            )

    if year_sold_col is None or not era_columns:
        warnings.append("Charts sheet: failed to bind year-sold or era columns")
        return []

    rows: list[dict[str, Any]] = []
    for idx in range(header_row_idx + 1, len(charts)):
        row = charts.iloc[idx]
        year_sold = _safe_int(row.iloc[year_sold_col])
        if year_sold is None:
            continue
        bucket: dict[str, Any] = {"year_sold": year_sold}
        for col_idx, era_name in era_columns:
            bucket[era_name] = _round_money(_safe_float(row.iloc[col_idx]))
        rows.append(bucket)

    return rows


def parse_medians_sheet(xlsx_path: Path, warnings: list[str]) -> list[dict[str, Any]]:
    medians = pd.read_excel(xlsx_path, sheet_name="Medians")

    required_cols = {"Pinball", "Maker", "Year", "Total Sales"}
    missing = required_cols - set(medians.columns)
    if missing:
        warnings.append(
            "Medians sheet: missing expected columns " + str(sorted(missing))
            + "; present: " + str(list(medians.columns))
        )

    titles: list[dict[str, Any]] = []
    for _, row in medians.iterrows():
        title = _normalize_str(row.get("Pinball"))
        maker = _normalize_str(row.get("Maker"))
        if not title or not maker:
            continue
        if _looks_like_number(title) and _looks_like_number(maker):
            continue
        machine_year = _safe_int(row.get("Year"))

        medians_by_year: list[dict[str, Any]] = []
        for bucket in YEAR_BUCKETS:
            median_col = bucket + " Median"
            sales_col = bucket + " Sales"
            if median_col not in row.index or sales_col not in row.index:
                continue
            median = _round_money(_safe_float(row[median_col]))
            sale_count = _safe_int(row[sales_col]) or 0
            medians_by_year.append({
                "year_sold": bucket,
                "median_usd": median,
                "sale_count": sale_count,
            })

        total_sale_count = _safe_int(row.get("Total Sales")) or 0
        titles.append({
            "pinballprices_title": title,
            "pinballprices_maker": maker,
            "machine_year": machine_year,
            "total_sale_count": total_sale_count,
            "medians_by_year_sold": medians_by_year,
            "opdb_id": None,
        })

    titles.sort(key=lambda t: (
        t["pinballprices_maker"] or "",
        t["pinballprices_title"] or "",
        t["machine_year"] or 0,
    ))
    return titles


def parse_data_sheet_stats(xlsx_path: Path) -> dict[str, Any]:
    data = pd.read_excel(xlsx_path, sheet_name="Data")
    sales = data[data["Date Sold"].notna()].copy()
    price_col = next(
        (c for c in sales.columns if isinstance(c, str) and c.strip() == "Price"),
        None,
    )
    total = sales[price_col].sum() if price_col else None
    return {
        "observed_total_sale_records": int(len(sales)),
        "observed_unique_titles": int(
            sales["Pinball"].dropna().astype(str).str.strip().nunique()
        ),
        "observed_total_recorded_sales_usd": _round_money(
            float(total) if total is not None else None
        ),
    }


def build_seed(xlsx_path: Path) -> IngestResult:
    warnings: list[str] = []
    notes = parse_notes_sheet(xlsx_path, warnings)
    era_medians = parse_charts_sheet(xlsx_path, warnings)
    titles = parse_medians_sheet(xlsx_path, warnings)
    observed = parse_data_sheet_stats(xlsx_path)

    if (notes["ted_reported_total_sale_records"] is not None
            and observed["observed_total_sale_records"] != notes["ted_reported_total_sale_records"]):
        warnings.append(
            "Data/Notes drift: Ted reports "
            + str(notes["ted_reported_total_sale_records"]) + " sale records, "
            + "Data sheet contains " + str(observed["observed_total_sale_records"])
        )
    if (notes["ted_reported_unique_titles"] is not None
            and observed["observed_unique_titles"] != notes["ted_reported_unique_titles"]):
        warnings.append(
            "Data/Notes drift: Ted reports "
            + str(notes["ted_reported_unique_titles"]) + " unique titles, "
            + "Data sheet contains " + str(observed["observed_unique_titles"])
        )

    seed: dict[str, Any] = {
        "schema_version": SCHEMA_VERSION,
        "source": {
            "name": "PinballPrices.com",
            "owner_display_name": "Ted \"Doc\" Finlay",
            "owner_contact": "pinballprices@gmail.com",
            "homepage": "https://pinballprices.com",
            "attribution_requirement": (
                "Every value surfaced from this seed MUST carry a visible "
                "'source: PinballPrices.com' label with a click-through link "
                "to https://pinballprices.com per the permission grant in "
                "Gmail thread 19e0a3af22bbace3 (2026-05-14)."
            ),
            "ingestion_method": "manual_xlsx_email_hand_off",
            "refresh_cadence_note": (
                "Ted emails an updated workbook on his own cadence; operators "
                "download the attachment and re-run tools/pinballprices-ingest "
                "to regenerate this seed."
            ),
        },
        "as_of": notes["as_of"],
        "ingest": {
            "source_xlsx_filename": xlsx_path.name,
            "source_xlsx_sha256": _file_sha256(xlsx_path),
            "ingested_at_utc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
            "tool_version": TOOL_VERSION,
            "ingest_warnings": warnings,
        },
        "totals": {
            "ted_reported_unique_titles": notes["ted_reported_unique_titles"],
            "ted_reported_total_sale_records": notes["ted_reported_total_sale_records"],
            "ted_reported_total_recorded_sales_usd": notes["ted_reported_total_recorded_sales_usd"],
            **observed,
        },
        "era_medians_by_year_sold": era_medians,
        "titles": titles,
    }

    report = _build_report(seed, warnings)
    return IngestResult(seed=seed, report=report)


def _build_report(seed: dict[str, Any], warnings: list[str]) -> list[str]:
    totals = seed["totals"]
    lines: list[str] = []
    lines.append("PinballPrices ingest - " + seed["ingest"]["source_xlsx_filename"])
    lines.append("=" * 72)
    lines.append("  as-of:                       " + str(seed["as_of"]))
    lines.append(
        "  unique titles:               "
        + str(totals["observed_unique_titles"])
        + " (Ted reports " + str(totals["ted_reported_unique_titles"]) + ")"
    )
    lines.append(
        "  total sale records:          "
        + str(totals["observed_total_sale_records"])
        + " (Ted reports " + str(totals["ted_reported_total_sale_records"]) + ")"
    )
    obs_total = totals["observed_total_recorded_sales_usd"]
    obs_total_str = "${:,.2f}".format(obs_total) if obs_total is not None else "n/a"
    lines.append("  total recorded sales (USD):  " + obs_total_str)
    lines.append(
        "  era buckets:                 "
        + str(len(seed["era_medians_by_year_sold"])) + " years x "
        + str(len(ERAS)) + " eras"
    )
    lines.append("  titles emitted:              " + str(len(seed["titles"])))
    if warnings:
        lines.append("")
        lines.append("  Warnings (" + str(len(warnings)) + "):")
        for w in warnings:
            lines.append("    - " + w)
    return lines


def diff_against_previous(new_seed: dict[str, Any], previous_path: Path) -> list[str]:
    if not previous_path.exists():
        return ["  (no previous seed at " + str(previous_path) + "; first ingest)"]

    try:
        previous = json.loads(previous_path.read_text())
    except json.JSONDecodeError as exc:
        return ["  (previous seed at " + str(previous_path) + " unparseable: " + str(exc) + ")"]

    prev_titles = {
        (t.get("pinballprices_maker"), t.get("pinballprices_title")): t
        for t in previous.get("titles", [])
    }
    new_titles = {
        (t.get("pinballprices_maker"), t.get("pinballprices_title")): t
        for t in new_seed.get("titles", [])
    }

    added = sorted(set(new_titles) - set(prev_titles))
    removed = sorted(set(prev_titles) - set(new_titles))

    lines: list[str] = []
    lines.append("  titles added:   " + str(len(added)).rjust(4))
    lines.append("  titles removed: " + str(len(removed)).rjust(4))

    gainers: list[tuple[str, str, int]] = []
    for key in set(new_titles) & set(prev_titles):
        delta = (new_titles[key].get("total_sale_count") or 0) - (
            prev_titles[key].get("total_sale_count") or 0
        )
        if delta > 0:
            maker, title = key
            gainers.append((maker or "?", title or "?", delta))
    gainers.sort(key=lambda x: -x[2])
    if gainers:
        lines.append("  top sale-count gainers vs. previous seed:")
        for maker, title, delta in gainers[:10]:
            lines.append("    +" + str(delta).rjust(4) + "   " + maker + " / " + title)

    return lines


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Convert a PinballPrices.com workbook into the committed community pricing seed.",
    )
    parser.add_argument("--xlsx", required=True, type=Path,
                        help="Path to Ted's xlsx workbook.")
    parser.add_argument("--seed-out", type=Path,
                        default=Path("data/seeds/community_pricing_pinballprices.v1.json"),
                        help="Where to write the seed JSON.")
    parser.add_argument("--dry-run", action="store_true",
                        help="Parse and report, but do not write the seed JSON.")
    args = parser.parse_args(argv)

    if not args.xlsx.exists():
        print("error: xlsx not found: " + str(args.xlsx), file=sys.stderr)
        return 2

    result = build_seed(args.xlsx)

    for line in result.report:
        print(line)

    print()
    print("Diff vs. previously-committed seed:")
    for line in diff_against_previous(result.seed, args.seed_out):
        print(line)

    if args.dry_run:
        print()
        print("--dry-run: not writing seed.")
        return 0

    args.seed_out.parent.mkdir(parents=True, exist_ok=True)
    args.seed_out.write_text(
        json.dumps(result.seed, indent=2, sort_keys=False, ensure_ascii=False) + "\n"
    )
    print()
    print("Wrote " + str(args.seed_out))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
