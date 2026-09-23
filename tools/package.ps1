# Builds the store release zip: RavenIron-FireFront-<version>.zip in dist\.
# Same shape as Ragnarok's Wrath's script, same guard: the THREE places the version
# lives (Plugin.cs const, csproj, manifest.json) must agree or this refuses to
# package — a release can never claim a version its own log denies. (This repo
# shipped with csproj at 0.17.2 while Plugin.cs said 0.17.3; this guard is why
# that can't happen again.)
#
# The zip goes to Hexium (hexium.gg) - the only store we publish to (owner, 2026-09-03).
# Its LAYOUT is Thunderstore's package format, because that is the format Hexium consumes;
# format and channel are different things, and these headers conflated them for a week.

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

# --- the three versions must agree -------------------------------------------------
$pluginVer   = (Select-String -Path "$root\Plugin.cs" -Pattern 'VERSION\s*=\s*"([^"]+)"').Matches[0].Groups[1].Value
$csprojVer   = (Select-String -Path "$root\FireFront.csproj" -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value
$manifestVer = (Get-Content "$root\manifest.json" -Raw | ConvertFrom-Json).version_number

if (($pluginVer -ne $csprojVer) -or ($pluginVer -ne $manifestVer)) {
    Write-Host "VERSION MISMATCH - refusing to package:" -ForegroundColor Red
    Write-Host "  Plugin const : $pluginVer"
    Write-Host "  csproj       : $csprojVer"
    Write-Host "  manifest.json: $manifestVer"
    exit 1
}

# --- clean Release build -----------------------------------------------------------
dotnet build "$root\FireFront.csproj" -c Release -v q --nologo
if ($LASTEXITCODE -ne 0) { Write-Host "Build failed." -ForegroundColor Red; exit 1 }

$dll = "$root\bin\Release\net472\FireFront.dll"
if (-not (Test-Path $dll)) { $dll = "$root\bin\Release\FireFront.dll" }
if (-not (Test-Path $dll)) { Write-Host "No Release DLL under $root\bin\Release" -ForegroundColor Red; exit 1 }

# --- assemble the flat zip both stores expect --------------------------------------
$dist = "$root\dist"
$stage = "$dist\stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# Store files at the zip root; the DLL under plugins\ — the BepInEx layout mod
# managers map onto BepInEx/plugins (and Hexium requires; a root-level DLL was
# refused 2026-08-27).
Copy-Item "$root\manifest.json", "$root\README.md", "$root\CHANGELOG.md", "$root\LICENSE", "$root\icon.png" -Destination $stage
New-Item -ItemType Directory -Force -Path "$stage\plugins" | Out-Null
Copy-Item $dll -Destination "$stage\plugins"

$zip = "$dist\RavenIron-FireFront-$pluginVer.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
# Entries are written by hand for two reasons learned on upload day (2026-08-27):
# PS 5.1's Compress-Archive produces structurally quirky zips Hexium rejects outright,
# and .NET Framework's CreateFromDirectory names nested entries with BACKSLASHES
# (spec says forward slashes), which a strict parser reads as a weird root filename
# instead of a plugins/ folder. Explicit CreateEntry with '/' sidesteps both.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem $stage -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive, $_.FullName, $rel,
            [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }
Remove-Item $stage -Recurse -Force

Write-Host "Packaged: $zip" -ForegroundColor Green
Get-Item $zip | Select-Object Name, Length
