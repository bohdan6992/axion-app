# TradingBridgeNet / TradingBridgeApi – Variant A Scaffold (Per-Strategy Controllers + FilesServices)

Variant A principles:
- **LIVE** data is returned ONLY by `api/live/snapshot` and is the ONLY source of filterable fields in UI.
- **STATIC** strategy data is read ONLY from `signals/{strategy}/` (3 files: summary.csv, onefile.jsonl, best_params.jsonl).
- Each strategy has its own Controller + FilesService to avoid confusion.

## Strategy data placement
Put the data files here (relative to the API output directory):
`TradingBridgeApi/signals/{strategy}/`
- `summary.csv`
- `onefile.jsonl`
- `best_params.jsonl`

## Add a new strategy
1) Create `signals/{new}/` with the 3 files
2) Copy controller + files service from MeanRev template and rename
3) Register the new FilesService in Program.cs
