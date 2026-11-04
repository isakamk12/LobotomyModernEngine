param(
    [string]$ver = "0.8.6",
    [switch]$DeployToSteam = $false
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repoRoot = $PSScriptRoot
$proj = Join-Path $repoRoot "LobotomyModernEngine.csproj"
$srcDll = Join-Path $repoRoot "bin\Release\net35\LobotomyModernEngine.dll"
$distDir = Join-Path $repoRoot "dist"
$relDir = Join-Path $repoRoot "releases\v$ver"
$templateDir = Join-Path $repoRoot "packaging\template_FullBepInEx"
$licenseFile = Join-Path $repoRoot "LICENSE"
$thirdPartyLicenseFile = Join-Path $repoRoot "THIRD_PARTY_LICENSES.txt"

New-Item -ItemType Directory -Path $relDir -Force | Out-Null
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "LME_pkg_$ver"
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
New-Item -ItemType Directory -Path $tmp | Out-Null

function Build-DistributionPackages {
    param(
        [Parameter(Mandatory=$true)][string]$editionName, # "Standard" or "DebugPlus"
        [Parameter(Mandatory=$true)][string]$dllSourcePath
    )
    
    # 1. PluginOnly パッケージ構築
    $pluginDir = Join-Path $tmp "$($editionName)_PluginOnly"
    New-Item -ItemType Directory -Path (Join-Path $pluginDir "BepInEx\plugins") -Force | Out-Null
    Copy-Item $dllSourcePath (Join-Path $pluginDir "BepInEx\plugins\LobotomyModernEngine.dll") -Force
    # ライセンス文書を同梱
    Copy-Item $licenseFile (Join-Path $pluginDir "LICENSE.txt") -Force
    
    $pluginZip = Join-Path $distDir "LobotomyModernEngine_v$($ver)_$($editionName)_PluginOnly.zip"
    if (Test-Path $pluginZip) { Remove-Item $pluginZip -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($pluginDir, $pluginZip)
    Copy-Item $pluginZip (Join-Path $relDir (Split-Path $pluginZip -Leaf)) -Force
    
    # 2. FullBepInEx パッケージ構築（Steam環境ではなく、リポジトリ内のクリーンなtemplateから構築）
    $fullDir = Join-Path $tmp "$($editionName)_FullBepInEx"
    New-Item -ItemType Directory -Path $fullDir -Force | Out-Null
    Copy-Item $templateDir\* $fullDir -Recurse -Force
    
    # 今回ビルドしたプラグインDLLを配置
    $targetPluginDir = Join-Path $fullDir "BepInEx\plugins"
    New-Item -ItemType Directory -Path $targetPluginDir -Force | Out-Null
    Copy-Item $dllSourcePath (Join-Path $targetPluginDir "LobotomyModernEngine.dll") -Force
    
    $fullZip = Join-Path $distDir "LobotomyModernEngine_v$($ver)_$($editionName)_FullBepInEx.zip"
    if (Test-Path $fullZip) { Remove-Item $fullZip -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($fullDir, $fullZip)
    Copy-Item $fullZip (Join-Path $relDir (Split-Path $fullZip -Leaf)) -Force
}

# --- A. Standard ビルド (デバッグ強化デフォルトOFF) ---
Write-Host "Building Standard Edition..."
dotnet build $proj -c Release | Out-Null
Build-DistributionPackages -editionName "Standard" -dllSourcePath $srcDll

# --- B. DebugPlus ビルド (デバッグ強化デフォルトON) ---
Write-Host "Building DebugPlus Edition..."
dotnet build $proj -c Release -p:DefineConstants=DEBUG_PLUS | Out-Null
Build-DistributionPackages -editionName "DebugPlus" -dllSourcePath $srcDll

# --- C. 任意: Steam環境への配置（-DeployToSteam 指定時のみ） ---
if ($DeployToSteam) {
    $steamPlugins = "E:\SteamLibrary\steamapps\common\LobotomyCorp\BepInEx\plugins"
    if (Test-Path $steamPlugins) {
        Copy-Item $srcDll (Join-Path $steamPlugins "LobotomyModernEngine.dll") -Force
        Write-Host "Deployed DebugPlus DLL to Steam: $steamPlugins"
    } else {
        Write-Warning "Steam plugins directory not found: $steamPlugins"
    }
} else {
    Write-Host "Steam deployment skipped (use -DeployToSteam to deploy to game)."
}

# 作業用一時ディレクトリ削除
Remove-Item $tmp -Recurse -Force

Write-Host "All 4 standalone sanitized ZIP packages for v$ver created successfully!"
Get-ChildItem $distDir -Filter "*$ver*" | Select-Object Name, Length
