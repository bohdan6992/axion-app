# Axion Launcher (APP-A: GitHub Releases)

This WPF app is the **launcher + updater** for Axion.

## Canon (Variant 2)

- **Update App** updates only `C:\Axion\App\` (TradingBridgeApi publish output).
- Signals are **never** cached locally and are **never** updated by the launcher.
- TradingBridgeApi reads `signals/**` directly from the separate private DATA repo (`bohdan6992/axion-signals`) via PAT.

## Expected install layout (Windows)

```
C:\Axion\
  App\        TradingBridgeApi.exe (+ publish files)
  Launcher\   Axion.exe
  Cache\      temp zips, staging, state files
  Backup\     previous app snapshots for rollback
  Secrets\    token files (optional)
```

The launcher detects `C:\Axion\` automatically based on its own location
(it expects to run from `C:\Axion\Launcher\`).

## Starting the API

Launcher starts TradingBridgeApi on the old port **5127** and forces **Development** to always show Swagger:

- `ASPNETCORE_URLS=http://127.0.0.1:5127`
- `ASPNETCORE_ENVIRONMENT=Development`

Swagger: `http://127.0.0.1:5127/swagger`

## Update App (APP-A)

Launcher downloads the latest release from the **APP repo** (private), finds the asset:

- `TradingBridgeApi-win-x64.zip`

Then performs atomic update:

1. Stop API
2. Download zip to `C:\Axion\Cache\`
3. Validate zip contains `TradingBridgeApi.exe` or `TradingBridgeApi.dll`
4. Backup current `C:\Axion\App\` to `C:\Axion\Backup\App_yyyyMMdd_HHmmss\`
5. Replace `C:\Axion\App\`
6. Start API

If something fails, rollback uses the last backup pointer stored in `C:\Axion\Cache\last_backup.txt`.

## GitHub token for APP repo

Private releases require PAT.

Supported options:

1) env var: `AXION_APP_GITHUB_TOKEN`

2) file: `C:\Axion\Secrets\app_github_pat.txt`

In production, prefer **Windows Credential Manager**.
