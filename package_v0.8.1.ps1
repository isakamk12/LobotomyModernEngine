param(
    [string]$ver = "0.8.1"
)

Add-Type -AssemblyName System.IO.Compression.FileSystem

$proj = "E:\3DS\lobotomy\LobotomyModernEngine\LobotomyModernEngine.csproj"
$srcDll = "E:\3DS\lobotomy\LobotomyModernEngine\bin\Release\net35\LobotomyModernEngine.dll"
$distDir = "E:\3DS\lobotomy\LobotomyModernEngine\dist"
$relDir = "E:\3DS\lobotomy\LobotomyModernEngine\releases\v$ver"

New-Item -ItemType Directory -Path $relDir -Force | Out-Null
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "LME_pkg_$ver"
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
New-Item -ItemType Directory -Path $tmp | Out-Null

# --- A. Standard ビルド (デバッグ強化デフォルトOFF) ---
dotnet build $proj -c Release | Out-Null

$stdPluginDir = Join-Path $tmp "StdPluginOnly"
New-Item -ItemType Directory -Path (Join-Path $stdPluginDir "BepInEx\plugins") -Force | Out-Null
Copy-Item $srcDll (Join-Path $stdPluginDir "BepInEx\plugins\LobotomyModernEngine.dll") -Force

$stdPluginZip = Join-Path $distDir "LobotomyModernEngine_v$($ver)_Standard_PluginOnly.zip"
if (Test-Path $stdPluginZip) { Remove-Item $stdPluginZip -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($stdPluginDir, $stdPluginZip)
Copy-Item $stdPluginZip (Join-Path $relDir (Split-Path $stdPluginZip -Leaf)) -Force

$stdFullDir = Join-Path $tmp "StdFullBepInEx"
New-Item -ItemType Directory -Path $stdFullDir | Out-Null
Copy-Item "E:\SteamLibrary\steamapps\common\LobotomyCorp\doorstop_config.ini" $stdFullDir -Force
Copy-Item "E:\SteamLibrary\steamapps\common\LobotomyCorp\winhttp.dll" $stdFullDir -Force
Copy-Item "E:\SteamLibrary\steamapps\common\LobotomyCorp\BepInEx" $stdFullDir -Recurse -Force
Copy-Item $srcDll (Join-Path $stdFullDir "BepInEx\plugins\LobotomyModernEngine.dll") -Force

$stdFullZip = Join-Path $distDir "LobotomyModernEngine_v$($ver)_Standard_FullBepInEx.zip"
if (Test-Path $stdFullZip) { Remove-Item $stdFullZip -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($stdFullDir, $stdFullZip)
Copy-Item $stdFullZip (Join-Path $relDir (Split-Path $stdFullZip -Leaf)) -Force

# --- B. DebugPlus ビルド (デバッグ強化デフォルトON & Steam配備) ---
dotnet build $proj -c Release -p:DefineConstants=DEBUG_PLUS | Out-Null

$dbgPluginDir = Join-Path $tmp "DbgPluginOnly"
New-Item -ItemType Directory -Path (Join-Path $dbgPluginDir "BepInEx\plugins") -Force | Out-Null
Copy-Item $srcDll (Join-Path $dbgPluginDir "BepInEx\plugins\LobotomyModernEngine.dll") -Force

$dbgPluginZip = Join-Path $distDir "LobotomyModernEngine_v$($ver)_DebugPlus_PluginOnly.zip"
if (Test-Path $dbgPluginZip) { Remove-Item $dbgPluginZip -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($dbgPluginDir, $dbgPluginZip)
Copy-Item $dbgPluginZip (Join-Path $relDir (Split-Path $dbgPluginZip -Leaf)) -Force

$dbgFullDir = Join-Path $tmp "DbgFullBepInEx"
New-Item -ItemType Directory -Path $dbgFullDir | Out-Null
Copy-Item "E:\SteamLibrary\steamapps\common\LobotomyCorp\doorstop_config.ini" $dbgFullDir -Force
Copy-Item "E:\SteamLibrary\steamapps\common\LobotomyCorp\winhttp.dll" $dbgFullDir -Force
Copy-Item "E:\SteamLibrary\steamapps\common\LobotomyCorp\BepInEx" $dbgFullDir -Recurse -Force
Copy-Item $srcDll (Join-Path $dbgFullDir "BepInEx\plugins\LobotomyModernEngine.dll") -Force

$dbgFullZip = Join-Path $distDir "LobotomyModernEngine_v$($ver)_DebugPlus_FullBepInEx.zip"
if (Test-Path $dbgFullZip) { Remove-Item $dbgFullZip -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($dbgFullDir, $dbgFullZip)
Copy-Item $dbgFullZip (Join-Path $relDir (Split-Path $dbgFullZip -Leaf)) -Force

# Steam側へ DebugPlus DLL を配備
Copy-Item $srcDll "E:\SteamLibrary\steamapps\common\LobotomyCorp\BepInEx\plugins\LobotomyModernEngine.dll" -Force

Remove-Item $tmp -Recurse -Force
Write-Host "All 4 ZIP packages for v$ver created and DebugPlus deployed to Steam!"
Get-FileHash $srcDll, "E:\SteamLibrary\steamapps\common\LobotomyCorp\BepInEx\plugins\LobotomyModernEngine.dll" -Algorithm SHA256
Get-ChildItem $distDir -Filter "*$ver*" | Select-Object Name, Length
