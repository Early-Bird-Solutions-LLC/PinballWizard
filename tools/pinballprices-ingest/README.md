# pinballprices-ingest

Convert Ted "Doc" Finlay's PinballPrices.com workbook into the committed
`data/seeds/community_pricing_pinballprices.v1.json` seed consumed by the
Wizard's Valuation agent.

## Background

Ted maintains a hand-curated Excel workbook of US pinball machine sales
across Pinside, eBay, Liveauctioneers, Proxibid, EBTH, Captain's, and a
long tail of auction houses. The workbook contains ~13,000 sale records
across ~1,500 unique titles, updated by Ted on a near-daily basis. The
permission to use this data — with attribution to PinballPrices.com — was
granted in Gmail thread `19e0a3af22bbace3` (first request 2026-05-09,
data-sharing yes on 2026-05-14, first xlsx delivered 2026-05-15).

This is **NOT a scraper**. PinballPrices.com is hosted on Wix and the
website is a presentation layer over Ted's spreadsheet — scraping the
site would give us strictly less data than Ted's email already delivers,
plus the maintenance burden of a fragile Wix parser, plus the awkwardness
of duplicating work he's generously sharing. See the project's
"community-data-attribution" doc for the broader posture.

## Refresh workflow

```
+----------------------------+
| Ted emails xlsx attachment |
+-------------+--------------+
              |
              v
+----------------------------+
| Gmail filter applies label |   (one-time manual setup; see below)
| EarlyBirdSolutions/        |
|   PinballWizard/           |
|   PinballPricesFeed        |
+-------------+--------------+
              |
              v
+----------------------------+
| Operator downloads xlsx to |
| data/PinballPrices/        |   (gitignored)
+-------------+--------------+
              |
              v
+----------------------------+
| python ingest.py --xlsx … |
+-------------+--------------+
              |
              v
+----------------------------+
| data/seeds/                |
|   community_pricing_       |
|   pinballprices.v1.json    |   (committed; PR-reviewable diff)
+----------------------------+
```

## Usage

```bash
# Dry-run (parses + reports; does not write the seed)
python tools/pinballprices-ingest/ingest.py \
    --xlsx "data/PinballPrices/Pinball Prices 2026_Median_2.xlsx" \
    --dry-run

# Real ingest (regenerates data/seeds/community_pricing_pinballprices.v1.json)
python tools/pinballprices-ingest/ingest.py \
    --xlsx "data/PinballPrices/Pinball Prices 2026_Median_2.xlsx"

# Custom output location (rare — only for testing)
python tools/pinballprices-ingest/ingest.py \
    --xlsx "data/PinballPrices/Pinball Prices 2026_Median_2.xlsx" \
    --seed-out /tmp/seed.json
```

Dependencies: Python 3.10+ with `pandas` and `openpyxl` installed. Both
are stdlib-adjacent for any developer running the rest of the project's
Python tooling.

## What the output looks like

```json
{
  "schema_version": 1,
  "source": { "name": "PinballPrices.com", "owner_display_name": "Ted \"Doc\" Finlay", ... },
  "as_of": "2026-05-14",
  "ingest": { "source_xlsx_sha256": "...", "ingested_at_utc": "...", "ingest_warnings": [] },
  "totals": {
    "ted_reported_unique_titles": 1499,
    "ted_reported_total_sale_records": 13202,
    "ted_reported_total_recorded_sales_usd": 56217067.08,
    "observed_total_sale_records": 13202,
    "observed_unique_titles": 1499,
    "observed_total_recorded_sales_usd": 56217067.08
  },
  "era_medians_by_year_sold": [
    { "year_sold": 2026, "EM": 800.0, "EarlySS": 2100.0, ..., "Lcd": 7999.5 },
    ...
  ],
  "titles": [
    {
      "pinballprices_title": "Godzilla (Premium)",
      "pinballprices_maker": "Stern",
      "machine_year": 2021,
      "total_sale_count": 93,
      "medians_by_year_sold": [
        { "year_sold": "<2016", "median_usd": null, "sale_count": 0 },
        ...
        { "year_sold": "2026", "median_usd": 8950.0, "sale_count": 4 }
      ],
      "opdb_id": null
    },
    ...
  ]
}
```

`opdb_id` is intentionally null in v1 — the join from Ted's `(maker, title)`
display tuple to OPDB's canonical ID lives in a separate alias table
(follow-up PR, modeled on `data/seeds/pinside_slug_aliases.v1.json`).

The `observed_*` totals are computed from the raw Data sheet, and
`ted_reported_*` come from Ted's Notes sheet. They should match; any drift
shows up in `ingest.ingest_warnings`.

## One-time Gmail filter setup (Jim, manual)

The Gmail MCP available to scheduled tasks can't create labels or filters
programmatically, so this is a one-time setup in the Gmail web UI:

1. Open Gmail in a browser.
2. Settings (gear) → "See all settings" → **Filters and Blocked Addresses**.
3. Click **Create a new filter**.
4. Set:
   - **From:** `pinballprices@gmail.com`
   - **Has the words:** `has:attachment filename:xlsx`
5. Click **Create filter** at the bottom.
6. On the next screen, check **Apply the label** and create a new label:
   `EarlyBirdSolutions/PinballWizard/PinballPricesFeed`
7. Also check **Also apply filter to matching conversations** so the
   existing thread gets backfilled.
8. Click **Create filter**.

After this, every future xlsx from Ted gets the label automatically and
is trivially findable by both the operator and any future scheduled-task
automation that wants to watch for new files.

## Future automation (deferred)

A natural next iteration is a Cowork scheduled task that:

1. Searches Gmail by label for unprocessed messages.
2. Downloads the xlsx attachment.
3. Drops it under `data/PinballPrices/`.
4. Runs this ingest tool.
5. Opens a PR with the regenerated seed JSON.
6. Re-labels the email as `processed`.

That's a follow-up PR. The current state stops at "operator runs
ingest.py against a downloaded file" because the Gmail MCP attachment-
download capability is the missing link, and it's worth confirming
Ted's send cadence settles before automating around it.

## Why this lives in `tools/` and not the .NET solution

Two reasons. First, the rest of the project's data-shaping tools live
here (`tools/phase4/`, etc.) — operator-facing helpers that aren't part
of the runtime. Second, Python with pandas is the right shape for one-off
xlsx parsing; the .NET solution doesn't need an Excel dependency just to
ingest a periodic seed.

## Sanity checks

The tool emits warnings (visible in stdout and in
`seed.ingest.ingest_warnings`) when:

- The Notes sheet is missing one of the four expected summary cells.
- The Charts sheet has an unrecognized era column header.
- Ted's reported totals drift from observed totals on the Data sheet.

Drift warnings are not errors — Ted may have updated the Notes summary
manually after a partial save — but they signal "look at this before
PR-merging the seed change."
