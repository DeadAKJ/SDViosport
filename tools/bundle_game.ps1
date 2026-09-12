param (
    [string]$SteamGamePath = "C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley",
    [string]$OutputDir = ".\bundle_output",
    [switch]$Vanilla,
    [switch]$IncludeMods,
    [switch]$CreateZip
)

$ErrorActionPreference = "Stop"

Write-Host "==========================================" -ForegroundColor Cyan
if ($Vanilla) {
    Write-Host " Stardew Valley iOS Asset Bundler [PURE VANILLA]" -ForegroundColor Yellow
} else {
    Write-Host " Stardew Valley iOS Asset Bundler [SMAPI MODDED]" -ForegroundColor Cyan
}
Write-Host "==========================================" -ForegroundColor Cyan

if (-not (Test-Path $SteamGamePath)) {
    Write-Error "Stardew Valley game path not found: $SteamGamePath. Please pass -SteamGamePath <path>."
}

Write-Host "[1/4] Checking game installation..." -ForegroundColor Green
$sdvDll = Join-Path $SteamGamePath "Stardew Valley.dll"
$contentDir = Join-Path $SteamGamePath "Content"

if (-not (Test-Path $sdvDll)) {
    Write-Error "Could not find 'Stardew Valley.dll' in $SteamGamePath!"
}
if (-not (Test-Path $contentDir)) {
    Write-Error "Could not find 'Content' directory in $SteamGamePath!"
}

Write-Host "  Found Stardew Valley: $sdvDll"
$hasSMAPI = (Test-Path (Join-Path $SteamGamePath "StardewModdingAPI.dll")) -and (-not $Vanilla)
if ($hasSMAPI) {
    Write-Host "  SMAPI installation detected." -ForegroundColor Green
} else {
    Write-Host "  Vanilla mode selected (No mods, pure base game)." -ForegroundColor Yellow
}

Write-Host "[2/4] Preparing output directory: $OutputDir" -ForegroundColor Green
if (Test-Path $OutputDir) {
    Remove-Item $OutputDir -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$gameDlls = @(
    "Stardew Valley.dll",
    "StardewValley.GameData.dll",
    "xTile.dll",
    "BmFont.dll",
    "CPExtBmFont.dll",
    "Lidgren.Network.dll"
)

if ($hasSMAPI) {
    $gameDlls += "StardewModdingAPI.dll"
}

Write-Host "[3/4] Copying core assemblies and assets..." -ForegroundColor Green
foreach ($dll in $gameDlls) {
    $src = Join-Path $SteamGamePath $dll
    if (Test-Path $src) {
        Copy-Item -Path $src -Destination $OutputDir -Force
        Write-Host "  Copied $dll"
    } else {
        Write-Host "  Warning: $dll not found in game folder." -ForegroundColor Yellow
    }
}

if ($hasSMAPI) {
    $internalDir = Join-Path $SteamGamePath "smapi-internal"
    if (Test-Path $internalDir) {
        Write-Host "  Copying smapi-internal..."
        Copy-Item -Path $internalDir -Destination (Join-Path $OutputDir "smapi-internal") -Recurse -Force
    }
} else {
    # Mark as explicit vanilla mode
    Set-Content -Path (Join-Path $OutputDir "force_vanilla.txt") -Value "Vanilla Mode Active"
}

Write-Host "  Copying Content directory (this may take a few seconds)..."
Copy-Item -Path $contentDir -Destination (Join-Path $OutputDir "Content") -Recurse -Force

if ($IncludeMods -and -not $Vanilla) {
    $modsDir = Join-Path $SteamGamePath "Mods"
    if (Test-Path $modsDir) {
        Write-Host "  Copying Mods directory..." -ForegroundColor Green
        Copy-Item -Path $modsDir -Destination (Join-Path $OutputDir "Mods") -Recurse -Force
    }
}

if ($CreateZip) {
    $zipPath = "$OutputDir.zip"
    Write-Host "[4/4] Creating archive: $zipPath..." -ForegroundColor Green
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path "$OutputDir\*" -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host "Archive created successfully: $zipPath" -ForegroundColor Cyan
}

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host " Bundling Complete!" -ForegroundColor Green
Write-Host " Ready for iOS deployment via Files App (On My iPhone > Stardew Valley)." -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
