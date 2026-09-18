# Runs the off-game test harnesses. Usage, from the repo root:  .\tools\run-tests.ps1
#
# These do NOT launch Valheim. They compile the mod's pure source (today: Config/ConfigLedger.cs,
# the config migration's decisions) on plain .NET and run it, so the logic that fails silently -
# a default that should have moved and did not, an admin value that should have been kept - is
# checkable in about a second. Same shape as Ragnarok's Wrath's tools\run-tests.ps1.

$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot '..'

$harnesses = @(
    'tests\ConfigLedgerTests\ConfigLedgerTests.csproj'
)

$anyFailed = $false
foreach ($h in $harnesses) {
    $path = Join-Path $root $h
    Write-Host "== $h" -ForegroundColor Cyan
    dotnet run --project $path --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { $anyFailed = $true; Write-Host "   FAILED (exit $LASTEXITCODE)" -ForegroundColor Red }
}

if ($anyFailed) { exit 1 }
Write-Host "All harnesses passed." -ForegroundColor Green
