param(
  [string]$Base = "http://127.0.0.1:5127",
  [string]$GhOwner = "bohdan6992",
  [string]$GhRepo  = "axion-signals",
  [string]$GhBranch = "main"
)

$ErrorActionPreference = "Stop"

function OK($m){ Write-Host ("[OK  ] " + $m) -ForegroundColor Green }
function WARN($m){ Write-Host ("[WARN] " + $m) -ForegroundColor Yellow }
function ERR($m){ Write-Host ("[ERR ] " + $m) -ForegroundColor Red }

function Invoke-Json($Method, $Url, $Headers=$null, $Body=$null) {
  $p = @{
    Method = $Method
    Uri = $Url
    UseBasicParsing = $true
    TimeoutSec = 180
    MaximumRedirection = 0
  }
  if ($Headers) { $p.Headers = $Headers }
  if ($Body -ne $null) {
    $p.ContentType = "application/json"
    $p.Body = ($Body | ConvertTo-Json)
  }

  $r = Invoke-WebRequest @p
  if ([string]::IsNullOrWhiteSpace($r.Content)) { return $null }
  return ($r.Content | ConvertFrom-Json)
}

function Invoke-Text($Url, $Headers=$null) {
  $p = @{
    Method = "GET"
    Uri = $Url
    UseBasicParsing = $true
    TimeoutSec = 180
    MaximumRedirection = 5
  }
  if ($Headers) { $p.Headers = $Headers }
  $r = Invoke-WebRequest @p
  return $r.Content
}

function Get-GhRawUrl($relPath){
  $rel = $relPath.TrimStart("/")
  return ("https://raw.githubusercontent.com/{0}/{1}/{2}/{3}" -f $GhOwner,$GhRepo,$GhBranch,$rel)
}

function Get-FirstTickerFromJsonlRaw($relPath){
  $url = Get-GhRawUrl $relPath
  # streaming read first few KB
  $req = [System.Net.HttpWebRequest]::Create($url)
  $req.Method = "GET"
  $req.Timeout = 180000
  $req.ReadWriteTimeout = 180000

  $resp = $req.GetResponse()
  try {
    $stream = $resp.GetResponseStream()
    $sr = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)
    $max = 5000
    $i = 0
    while (-not $sr.EndOfStream -and $i -lt $max) {
      $line = $sr.ReadLine()
      $i++
      if ([string]::IsNullOrWhiteSpace($line)) { continue }
      $trim = $line.Trim()
      if ($trim.StartsWith("#")) { continue }
      try {
        $obj = $trim | ConvertFrom-Json
        if ($obj -and $obj.ticker) {
          $t = ("" + $obj.ticker).Trim().ToUpper()
          if ($t.Length -ge 1) { return $t }
        }
      } catch {
        continue
      }
    }
  } finally {
    $resp.Close()
  }
  return $null
}

function Get-SampleTicker($strategy){
  # prefer best_params.jsonl (usually smaller), fallback to onefile.jsonl
  $t = Get-FirstTickerFromJsonlRaw ($strategy + "/best_params.jsonl")
  if ($t) { return $t }
  return (Get-FirstTickerFromJsonlRaw ($strategy + "/onefile.jsonl"))
}

Write-Host ("BASE=" + $Base)

# routes
$routes = $null
try {
  $routes = Invoke-Json "GET" ($Base + "/__routes")
  OK "__routes"
} catch {
  ERR "__routes"
  ERR $_
  exit 1
}

# login
$email = Read-Host "Email"
$passSecure = Read-Host "Password" -AsSecureString
$passPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
  [Runtime.InteropServices.Marshal]::SecureStringToBSTR($passSecure)
)

$token = $null
try {
  $login = Invoke-Json "POST" ($Base + "/api/auth/login") $null @{ email=$email; password=$passPlain }
  $token = $login.token
  if (-not $token) { $token = $login.accessToken }
  if (-not $token) { $token = $login.jwt }
  if (-not $token) { throw "token missing" }
  OK "AUTH login"
} catch {
  ERR "AUTH login"
  ERR $_
  exit 1
}

$AuthH = @{ Authorization = ("Bearer " + $token) }

# pick tickers from GitHub
$tChrono = $null
$tOpen = $null
$tArb = $null

try { $tChrono = Get-SampleTicker "chrono"; if ($tChrono) { OK ("Pick chrono ticker " + $tChrono) } else { WARN "No chrono ticker picked" } }
catch { WARN "Pick chrono ticker failed" }

try { $tOpen = Get-SampleTicker "opendoor"; if ($tOpen) { OK ("Pick opendoor ticker " + $tOpen) } else { WARN "No opendoor ticker picked" } }
catch { WARN "Pick opendoor ticker failed" }

try { $tArb = Get-SampleTicker "arbitrage"; if ($tArb) { OK ("Pick arbitrage ticker " + $tArb) } else { WARN "No arbitrage ticker picked" } }
catch { WARN "Pick arbitrage ticker failed" }

# CHRONO endpoints
try {
  Invoke-Json "GET" ($Base + "/api/strategy/chrono/summary") | Out-Null
  OK "CHRONO summary"
  if ($tChrono) {
    Invoke-Json "GET" ($Base + "/api/strategy/chrono/ticker/" + $tChrono) | Out-Null
    OK ("CHRONO ticker " + $tChrono)
    Invoke-Json "GET" ($Base + "/api/strategy/chrono/best-params/" + $tChrono) | Out-Null
    OK ("CHRONO best-params " + $tChrono)
  }
} catch {
  ERR "CHRONO failed"
  ERR $_
}

# OPENDOOR endpoints
try {
  Invoke-Json "GET" ($Base + "/api/strategy/opendoor/summary") | Out-Null
  OK "OPENDOOR summary"
  if ($tOpen) {
    Invoke-Json "GET" ($Base + "/api/strategy/opendoor/ticker/" + $tOpen) | Out-Null
    OK ("OPENDOOR ticker " + $tOpen)
    Invoke-Json "GET" ($Base + "/api/strategy/opendoor/best-params/" + $tOpen) | Out-Null
    OK ("OPENDOOR best-params " + $tOpen)
  }
  # signals (optional)
  $hasOpenSignals = $false
  foreach ($r in $routes) { if ($r.pattern -eq "api/strategy/opendoor/signals") { $hasOpenSignals = $true } }
  if ($hasOpenSignals) {
    Invoke-Json "GET" ($Base + "/api/strategy/opendoor/signals?limit=5&offset=0") | Out-Null
    OK "OPENDOOR signals"
  }
} catch {
  ERR "OPENDOOR failed"
  ERR $_
}

# ARBITRAGE endpoints (auth)
try {
  Invoke-Json "GET" ($Base + "/api/arbitrage/summary?q=") $AuthH | Out-Null
  OK "ARBITRAGE summary"
  if ($tArb) {
    Invoke-Json "GET" ($Base + "/api/arbitrage/ticker/" + $tArb) $AuthH | Out-Null
    OK ("ARBITRAGE ticker " + $tArb)
    Invoke-Json "GET" ($Base + "/api/arbitrage/best-params/" + $tArb) $AuthH | Out-Null
    OK ("ARBITRAGE best-params " + $tArb)
    Invoke-Json "GET" ($Base + "/api/arbitrage/signals/global/any/all?tickers=" + $tArb + "&limit=5&offset=0&minRate=0.3&minTotal=1") $AuthH | Out-Null
    OK "ARBITRAGE signals"
  }
} catch {
  ERR "ARBITRAGE failed"
  ERR $_
}

# LIVE (optional)
try { Invoke-Json "GET" ($Base + "/api/live/snapshot") | Out-Null; OK "LIVE snapshot" }
catch { WARN "LIVE snapshot failed (ok)" }

try { Invoke-Json "GET" ($Base + "/api/live/full-quotes") | Out-Null; OK "LIVE full-quotes" }
catch { WARN "LIVE full-quotes failed (ok)" }

Write-Host "DONE."
