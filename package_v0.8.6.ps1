param(
    [string]$ver = "0.8.6"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$proj = "E:\3DS\lobotomy\LobotomyModernEngine\LobotomyModernEngine.csproj"
$srcDll = "E:\3DS\lobotomy\LobotomyModernEngine\bin\Release\net35\LobotomyModernEngine.dll"
$distDir = "C:\Users\rpgmi\Documents\GitHub\LobotomyModernEngine\dist"
$relDir = "C:\Users\rpgmi\Documents\GitHub\LobotomyModernEngine\releases\v$ver"

New-Item -ItemType Directory -Path $relDir -Force | Out-Null
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "LME_pkg_$ver"
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
New-Item -ItemType Directory -Path $tmp | Out-Null

function Prepare-CleanFullBepInEx {
    param(
        [Parameter(Mandatory=$true)][string]$targetDir,
        [Parameter(Mandatory=$true)][string]$dllPath
    )
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
    Copy-Item "E:\SteamLibrary\steamapps\common\LobotomyCorp\doorstop_config.ini" $targetDir -Force
    Copy-Item "E:\SteamLibrary\steamapps\common\LobotomyCorp\winhttp.dll" $targetDir -Force
    
    # BepInExディレクトリをコピー
    Copy-Item "E:\SteamLibrary\steamapps\common\LobotomyCorp\BepInEx" $targetDir -Recurse -Force
    
    $bepDir = Join-Path $targetDir "BepInEx"
    
    # 1. 個人情報・実行環境ログ（LogOutput.log）を完全削除
    $logFiles = Get-ChildItem -Path $bepDir -Filter "*LogOutput*.log" -Recurse -Force
    foreach ($f in $logFiles) {
        Remove-Item $f.FullName -Force
        Write-Host "Removed dirty log: $($f.FullName)"
    }
    
    # 2. キャッシュフォルダ（絶対パス含む .dat）を完全削除
    $cacheDir = Join-Path $bepDir "cache"
    if (Test-Path $cacheDir) {
        Remove-Item $cacheDir -Recurse -Force
        Write-Host "Removed dirty cache dir: $cacheDir"
    }
    
    # 3. plugins配下のクリーンアップ（PDBや旧ファイルを除去し、今回ビルドのDLLのみ配置）
    $pluginsDir = Join-Path $bepDir "plugins"
    if (Test-Path $pluginsDir) {
        Remove-Item $pluginsDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $pluginsDir -Force | Out-Null
    Copy-Item $dllPath (Join-Path $pluginsDir "LobotomyModernEngine.dll") -Force
}

# --- A. Standard ビルド (デバッグ強化デフォルトOFF) ---
Write-Host "Building Standard Edition..."
dotnet build $proj -c Release | Out-Null

$stdPluginDir = Join-Path $tmp "StdPluginOnly"
New-Item -ItemType Directory -Path (Join-Path $stdPluginDir "BepInEx\plugins") -Force | Out-Null
Copy-Item $srcDll (Join-Path $stdPluginDir "BepInEx\plugins\LobotomyModernEngine.dll") -Force

$stdPluginZip = Join-Path $distDir "LobotomyModernEngine_v$($ver)_Standard_PluginOnly.zip"
if (Test-Path $stdPluginZip) { Remove-Item $stdPluginZip -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($stdPluginDir, $stdPluginZip)
Copy-Item $stdPluginZip (Join-Path $relDir (Split-Path $stdPluginZip -Leaf)) -Force

$stdFullDir = Join-Path $tmp "StdFullBepInEx"
Prepare-CleanFullBepInEx -targetDir $stdFullDir -dllPath $srcDll

$stdFullZip = Join-Path $distDir "LobotomyModernEngine_v$($ver)_Standard_FullBepInEx.zip"
if (Test-Path $stdFullZip) { Remove-Item $stdFullZip -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($stdFullDir, $stdFullZip)
Copy-Item $stdFullZip (Join-Path $relDir (Split-Path $stdFullZip -Leaf)) -Force

# --- B. DebugPlus ビルド (デバッグ強化デフォルトON & Steam配備) ---
Write-Host "Building DebugPlus Edition..."
dotnet build $proj -c Release -p:DefineConstants=DEBUG_PLUS | Out-Null

$dbgPluginDir = Join-Path $tmp "DbgPluginOnly"
New-Item -ItemType Directory -Path (Join-Path $dbgPluginDir "BepInEx\plugins") -Force | Out-Null
Copy-Item $srcDll (Join-Path $dbgPluginDir "BepInEx\plugins\LobotomyModernEngine.dll") -Force

$dbgPluginZip = Join-Path $distDir "LobotomyModernEngine_v$($ver)_DebugPlus_PluginOnly.zip"
if (Test-Path $dbgPluginZip) { Remove-Item $dbgPluginZip -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($dbgPluginDir, $dbgPluginZip)
Copy-Item $dbgPluginZip (Join-Path $relDir (Split-Path $dbgPluginZip -Leaf)) -Force

$dbgFullDir = Join-Path $tmp "DbgFullBepInEx"
Prepare-CleanFullBepInEx -targetDir $dbgFullDir -dllPath $srcDll

$dbgFullZip = Join-Path $distDir "LobotomyModernEngine_v$($ver)_DebugPlus_FullBepInEx.zip"
if (Test-Path $dbgFullZip) { Remove-Item $dbgFullZip -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($dbgFullDir, $dbgFullZip)
Copy-Item $dbgFullZip (Join-Path $relDir (Split-Path $dbgFullZip -Leaf)) -Force

# Steam側へ DebugPlus DLL を配備
Copy-Item $srcDll "E:\SteamLibrary\steamapps\common\LobotomyCorp\BepInEx\plugins\LobotomyModernEngine.dll" -Force

# E:\3DS側にもミラーコピー
Copy-Item $distDir "E:\3DS\lobotomy\LobotomyModernEngine\dist" -Recurse -Force
Copy-Item $relDir "E:\3DS\lobotomy\LobotomyModernEngine\releases\v$ver" -Recurse -Force

Remove-Item $tmp -Recurse -Force
Write-Host "All 4 sanitized ZIP packages for v$ver created successfully!"
Get-ChildItem $distDir -Filter "*$ver*" | Select-Object Name, Length
