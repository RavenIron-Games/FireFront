# Graceful stop for the FireFront test server, with the save actually VERIFIED.
#
# The history is the justification, so keep it: the original CTRL_BREAK helper
# lived in a session scratchpad that got cleaned between sessions. After that,
# every "graceful stop" was launching PowerShell against a file that no longer
# existed, failing SILENTLY, timing out, and falling through to a force-kill —
# which skips Valheim's shutdown save entirely. It only ever failed harmlessly
# because nobody happened to be connected.
#
# Hence two rules here:
#   * fail LOUDLY — every failure mode has a distinct message
#   * verify the save from the LOG, never from a file mtime. mtime races the
#     write and already produced one false "the world didn't save" alarm.
#
# It also will not force-kill unless you explicitly ask. A stop that cannot
# save should leave the server running, not quietly discard world state.

param(
    [string]$ServerDir      = "C:\Users\donfr\FireFrontTestServer",
    [string]$LogPath        = "",
    [string]$World          = "Dedicated",
    [string]$SaveDir        = "",
    [int]   $TimeoutSeconds = 180,
    [switch]$AllowForceKill
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrEmpty($LogPath)) { $LogPath = Join-Path $ServerDir "ff-test.log" }

$proc = Get-Process valheim_server -ErrorAction SilentlyContinue
if (-not $proc) { Write-Host "No valheim_server running."; exit 0 }
if (@($proc).Count -gt 1) { Write-Host "WARNING: $(@($proc).Count) server processes; stopping all." -ForegroundColor Yellow }

$helper = Join-Path $PSScriptRoot "_ctrlbreak-helper.ps1"
if (-not (Test-Path $helper)) {
    Write-Host "FATAL: helper missing at $helper" -ForegroundColor Red
    Write-Host "This is the exact failure that caused silent force-kills. Do not proceed." -ForegroundColor Red
    exit 1
}

# Where the log ends now, so only a save from here on counts.
$logLinesBefore = 0
if (Test-Path $LogPath) {
    $logLinesBefore = (Get-Content $LogPath -ErrorAction SilentlyContinue | Measure-Object -Line).Lines
}

# And when the stop began, so only a WRITE from here on counts. The log line is
# no longer sufficient on its own - see the note above the save check below.
$stopBegan = Get-Date

# Resolve where this server's world actually lives. A server launched with
# -savedir keeps it under that directory; otherwise Valheim uses LocalLow.
if ([string]::IsNullOrEmpty($SaveDir)) {
    $candidateRoots = @(
        (Join-Path $ServerDir "saves\worlds_local"),
        (Join-Path $env:USERPROFILE "AppData\LocalLow\IronGate\Valheim\worlds_local")
    )
} else {
    $candidateRoots = @((Join-Path $SaveDir "worlds_local"))
}

$worldPaths = @()
foreach ($root in $candidateRoots) {
    if (-not (Test-Path $root)) { continue }
    # 1.0.12 writes the world as a DIRECTORY; older builds wrote <World>.db.
    foreach ($leaf in @($World, "$World.db")) {
        $candidate = Join-Path $root $leaf
        if (Test-Path $candidate) { $worldPaths += $candidate }
    }
}

foreach ($p in @($proc)) {
    Write-Host "Sending CTRL_BREAK to $($p.Id)..."
    $h = Start-Process powershell -ArgumentList "-NoProfile","-ExecutionPolicy","Bypass","-File",$helper,"-TargetPid",$p.Id `
                       -WindowStyle Hidden -Wait -PassThru
    switch ($h.ExitCode) {
        0 { Write-Host "  signal delivered" }
        # 0xC000013A STATUS_CONTROL_C_EXIT: the helper attached to the target's
        # console, so the break it raised killed the helper too. That is the
        # NORMAL outcome and is itself proof of delivery — not a failure,
        # despite looking like one.
        -1073741510 { Write-Host "  signal delivered (helper consumed by its own event - expected)" }
        2 { Write-Host "  ATTACH FAILED - target has no console. It was probably launched with -RedirectStandardOutput; use -logfile alone (see start-test-server.ps1)." -ForegroundColor Yellow }
        3 { Write-Host "  GenerateConsoleCtrlEvent failed" -ForegroundColor Yellow }
        4 { Write-Host "  process already gone" }
        default { Write-Host "  helper exit $($h.ExitCode)" -ForegroundColor Yellow }
    }

    if ($p.WaitForExit($TimeoutSeconds * 1000)) {
        Write-Host "  exited"
    } else {
        Write-Host "  DID NOT EXIT in ${TimeoutSeconds}s" -ForegroundColor Red
        if ($AllowForceKill) {
            Write-Host "  force-killing - THE WORLD MAY NOT HAVE SAVED" -ForegroundColor Red
            Stop-Process -Id $p.Id -Force
        } else {
            Write-Host "  leaving it running. Re-run with -AllowForceKill only if you accept losing unsaved world state." -ForegroundColor Red
            exit 1
        }
    }
}

Start-Sleep -Seconds 3

# TWO ways to confirm a save, because neither alone is reliable any more.
#
# The log line came first and is now the weaker of the two: Valheim 1.0.12
# stopped emitting "World saved ( ...ms )" and a clean shutdown instead shows
# "Saving" followed by Unload lines. Relying on it alone made this script report
# lost world state after every single clean stop on 2026-09-12 - four in a row,
# all false, each disproved by looking at the world on disk. A save warning that
# is always wrong is worse than none, because it trains you to ignore the real one.
#
# So the file is the authority: if the world was written at or after the moment
# the stop began, it saved, whatever the log does or does not say. 1.0.12 writes
# the world as a DIRECTORY, which is also why any loose <World>.db sitting beside
# it is a stale pre-1.0 backup whose timestamp means nothing.
$saved = $false
$how = ""

foreach ($wp in $worldPaths) {
    $written = (Get-Item $wp -ErrorAction SilentlyContinue).LastWriteTime
    if ($written -and $written -ge $stopBegan.AddSeconds(-2)) {
        $saved = $true
        $how = "world written $($written.ToString('HH:mm:ss')) at $wp"
        break
    }
}

if (-not $saved -and (Test-Path $LogPath)) {
    $new = Get-Content $LogPath -ErrorAction SilentlyContinue | Select-Object -Skip $logLinesBefore
    $saveLine = $new | Where-Object { $_ -match "World saved" } | Select-Object -Last 1
    if ($saveLine) { $saved = $true; $how = "log line: $saveLine" }
}

if ($saved) {
    Write-Host "SAVE CONFIRMED - $how" -ForegroundColor Green
    exit 0
}

Write-Host "NO save detected after the stop began." -ForegroundColor Red
if ($worldPaths.Count -eq 0) {
    Write-Host "  (could not find world '$World' under any known save root, so only the log was checked -" -ForegroundColor Yellow
    Write-Host "   pass -World or -SaveDir to point this at the right one)" -ForegroundColor Yellow
} else {
    Write-Host "  checked: $($worldPaths -join ', ')" -ForegroundColor Yellow
}
Write-Host "If players were connected, world state since the last autosave may be lost." -ForegroundColor Red
exit 1
