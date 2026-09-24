# Starts the FireFront test server. Pairs with stop-test-server.ps1.
#
# The test server is a SEPARATE INSTALL, not the Steam one — see docs/HANDOFF.md.
# The Steam install is RavenIron's live Ravenrest modpack: two servers there
# share one FireFront.dll (so a test build could not differ from Ravenrest's)
# and its mandatory-mod list rejects a plain client with "incompatible version".
#
# Two deliberate choices, both learned the hard way:
#
#  1. NO -RedirectStandardOutput. Unity's own -logfile captures everything, and
#     redirecting stdout can leave the process without an attachable console —
#     exactly what stop-test-server.ps1 needs to deliver CTRL_BREAK. A server
#     you cannot stop gracefully is a server that loses world state.
#  2. It REFUSES to start a second instance. Double-starting on one port
#     happened twice in a single session; the second process fails to bind and
#     lingers, and then it is unclear which one is real.

param(
    [string]$ServerDir  = "$env:USERPROFILE\FireFrontTestServer",
    [int]   $Port       = 2458,
    [string]$World      = "Dedicated",
    [string]$ServerName = "FireFront Test",
    [string]$Password   = "firetest"
)

$existing = Get-Process valheim_server -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "REFUSING: a valheim_server is already running (PID $($existing.Id -join ', ')). Stop it first." -ForegroundColor Red
    exit 1
}

$exe = Join-Path $ServerDir "valheim_server.exe"
if (-not (Test-Path $exe)) {
    Write-Host "No valheim_server.exe under $ServerDir" -ForegroundColor Red
    exit 1
}

$env:SteamAppId = "892970"
$log = Join-Path $ServerDir "ff-test.log"

Start-Process -FilePath $exe -WorkingDirectory $ServerDir -WindowStyle Hidden -ArgumentList `
    '-logfile', "`"$log`"", '-nographics', '-batchmode',
    '-name', "`"$ServerName`"", '-port', $Port, '-world', "`"$World`"",
    '-password', $Password, '-crossplay'

Start-Sleep -Seconds 5
$p = Get-Process valheim_server -ErrorAction SilentlyContinue
if (-not $p) { Write-Host "FAILED to start - check $log" -ForegroundColor Red; exit 1 }
Write-Host "started PID $($p.Id), port $Port, world '$World', password '$Password'"
Write-Host "waiting for the join code..."

for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Seconds 5
    $code = Select-String -Path $log -Pattern "registered with join code (\d+)" -ErrorAction SilentlyContinue |
            Select-Object -Last 1
    if ($code) { Write-Host ("JOIN CODE: " + $code.Matches[0].Groups[1].Value) -ForegroundColor Green; exit 0 }
}
Write-Host "no join code after 5 minutes - check $log" -ForegroundColor Yellow
exit 1
