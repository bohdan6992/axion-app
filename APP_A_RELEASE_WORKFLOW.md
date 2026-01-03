# Axion APP-A (GitHub Releases) workflow

## Repos
- DATA (signals): `bohdan6992/axion-signals` (private)
- APP (binaries): `bohdan6992/axion-app` (private)  ✅ this is what Update App uses

## Release artifact contract
Each GitHub Release in `axion-app` must include an asset named:

- `TradingBridgeApi-win-x64.zip`

The zip must contain either:
- `TradingBridgeApi.exe` (self-contained publish) OR
- `TradingBridgeApi.dll` (framework-dependent publish) + required runtime files

## Build + pack (developer machine)
From the repo that contains `TradingBridgeApi`:

```powershell
# self-contained (recommended)
dotnet publish .\TradingBridgeApi\TradingBridgeApi.csproj -c Release -r win-x64 --self-contained true

$pub = ".\TradingBridgeApi\bin\Release\net8.0\win-x64\publish"
$zip = "TradingBridgeApi-win-x64.zip"

if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$pub\*" -DestinationPath $zip
```

## Create Release (manual)
1) In `bohdan6992/axion-app` create a new release:
   - Tag: `vX.Y.Z` (e.g. `v0.3.1`)
2) Upload the asset: `TradingBridgeApi-win-x64.zip`

## User machine install layout
```
C:\Axion\
  App\        (publish output)
  Launcher\   (Axion.exe)
  Cache\      (temp + state)
  Backup\     (rollback snapshots)
  Secrets\    (PAT files)
```

## Tokens
### APP repo token (for private releases)
The launcher reads:
- env: `AXION_APP_GITHUB_TOKEN`
- or file: `C:\Axion\Secrets\app_github_pat.txt`

### Signals repo token (for `axion-signals`)
The launcher injects this token into API env on start:
- env: `AXION_SIGNALS_GITHUB_TOKEN`
- or file: `C:\Axion\Secrets\signals_github_pat.txt`

The API receives it as:
- `Axion__Signals__GitHub__Token`

This avoids baking the token into `appsettings.json`.

## What Update App does (atomic)
1) Stop API
2) Download latest release asset to `C:\Axion\Cache\`
3) Validate zip contains TradingBridgeApi.exe/dll
4) Backup current `C:\Axion\App\` to `C:\Axion\Backup\App_yyyyMMdd_HHmmss\`
5) Replace `C:\Axion\App\`
6) Start API
7) On error: rollback from last backup
