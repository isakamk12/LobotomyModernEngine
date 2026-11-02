param(
    [string]$ver = "0.7.8"
)

Add-Type -AssemblyName System.IO.Compression.FileSystem

$srcDll = "E:\3DS\lobotomy\LobotomyModernEngine\bin\Release\net35\LobotomyModernEngine.dll"
$distDir = "E:\3DS\lobotomy\LobotomyModernEngine\dist"
$relDir = "E:\3DS\lobotomy\LobotomyModernEngine\releases\v$ver"

New-Item -ItemType Directory -Path $relDir -Force | Out-Null
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "LME_pkg_$ver"
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
New-Item -ItemType Directory -Path $tmp | Out-Null

# 1. PluginOnly
$pluginOnlyDir = Join-Path $tmp "PluginOnly"
New-Item -ItemType Directory -Path (Join-Path $pluginOnlyDir "BepInEx\plugins") -Force | Out-Null
Copy-Item $srcDll (Join-Path $pluginOnlyDir "BepInEx\plugins\LobotomyModernEngine.dll") -Force

$stdPluginZip = Join-Path $distDir "LobotomyModernEngine_v$($ver)_Standard_PluginOnly.zip"
$dbgPluginZip = Join-Path $distDir "LobotomyModernEngine_v$($ver)_DebugPlus_PluginOnly.zip"
if (Test-Path $stdPluginZip) { Remove-Item $stdPluginZip -Force }
if (Test-Path $dbgPluginZip) { Remove-Item $dbgPluginZip -Force }

[System.IO.Compression.ZipFile]::CreateFromDirectory($pluginOnlyDir, $stdPluginZip)
Copy-Item $stdPluginZip $dbgPluginZip -Force
Copy-Item $stdPluginZip (Join-Path $relDir (Split-Path $stdPluginZip -Leaf)) -Force
Copy-Item $dbgPluginZip (Join-Path $relDir (Split-Path $dbgPluginZip -Leaf)) -Force

# 2. FullBepInEx
$fullDir = Join-Path $tmp "FullBepInEx"
New-Item -ItemType Directory -Path $fullDir | Out-Null
Copy-Item "E:\SteamLibrary\steamapps\common\LobotomyCorp\doorstop_config.ini" $fullDir -Force
Copy-Item "E:\SteamLibrary\steamapps\common\LobotomyCorp\winhttp.dll" $fullDir -Force
Copy-Item "E:\SteamLibrary\steamapps\common\LobotomyCorp\BepInEx" $fullDir -Recurse -Force
Copy-Item $srcDll (Join-Path $fullDir "BepInEx\plugins\LobotomyModernEngine.dll") -Force

$stdFullZip = Join-Path $distDir "LobotomyModernEngine_v$($ver)_Standard_FullBepInEx.zip"
$dbgFullZip = Join-Path $distDir "LobotomyModernEngine_v$($ver)_DebugPlus_FullBepInEx.zip"
if (Test-Path $stdFullZip) { Remove-Item $stdFullZip -Force }
if (Test-Path $dbgFullZip) { Remove-Item $dbgFullZip -Force }

[System.IO.Compression.ZipFile]::CreateFromDirectory($fullDir, $stdFullZip)
Copy-Item $stdFullZip $dbgFullZip -Force
Copy-Item $stdFullZip (Join-Path $relDir (Split-Path $stdFullZip -Leaf)) -Force
Copy-Item $dbgFullZip (Join-Path $relDir (Split-Path $dbgFullZip -Leaf)) -Force

# Steam側への最新DLL配備
Copy-Item $srcDll "E:\SteamLibrary\steamapps\common\LobotomyCorp\BepInEx\plugins\LobotomyModernEngine.dll" -Force

Remove-Item $tmp -Recurse -Force
Write-Host "All 4 ZIP packages for v$ver created successfully and deployed to Steam!"
Get-ChildItem $distDir -Filter "*$ver*" | Select-Object Name, Length
