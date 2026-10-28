# Builds Clueless and packs everything it needs to run into dist\Clueless-<version>-win-x64.zip:
# the app, the WebView2 libraries, the UI, the Chrome extension and the offline speech engine
# with both English models. Usage: powershell -File scripts\package.ps1 [-Version 1.0.0]
param([string]$Version = "")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $Version) {
  $line = Select-String -Path Clueless.cs -Pattern 'AssemblyInformationalVersion\("([^"]+)"\)' | Select-Object -First 1
  $Version = $line.Matches[0].Groups[1].Value
}

cmd /c ".\build.cmd"
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$name = "Clueless-$Version-win-x64"
$stage = Join-Path $root "dist\$name"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

Copy-Item Clueless.exe, Microsoft.Web.WebView2.Core.dll, Microsoft.Web.WebView2.WinForms.dll, WebView2Loader.dll, README.md, LICENSE $stage
Copy-Item ui, extension -Destination $stage -Recurse
New-Item -ItemType Directory -Force "$stage\whisper\models" | Out-Null
Copy-Item whisper\whisper-server.exe, whisper\whisper.dll, whisper\ggml*.dll "$stage\whisper"
Copy-Item whisper\models\*.bin "$stage\whisper\models"

$zip = Join-Path $root "dist\$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
"$hash  $name.zip" | Set-Content -Encoding ascii "$zip.sha256"
Write-Host "Packed $zip ($([math]::Round((Get-Item $zip).Length / 1MB)) MB)"
Write-Host "SHA-256 $hash"
