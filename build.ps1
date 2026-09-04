param(
    [switch]$Release,
    [switch]$Publish,
    [switch]$Help
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

foreach ($tool in @("dotnet", "cargo")) {
    if ($null -eq (Get-Command $tool -ErrorAction SilentlyContinue)) {
        Write-Host "Hata: $tool PATH'te bulunmali." -ForegroundColor Red
        exit 1
    }
}

if ($Help) {
    Write-Host "Kullanim: .\build.ps1 [-Release] [-Publish] [-Help]"
    Write-Host "  (bayraksiz)  Debug dev-loop"
    Write-Host "  -Release     Release dev-loop"
    Write-Host "  -Publish     Release paket (dist\migurdex-win-x64.zip)"
    exit 0
}

$Root = $PSScriptRoot
$RustDir = Join-Path $Root "Migurdex.Native"
$ApiDir = Join-Path $Root "Migurdex.Api"
$Configuration = if ($Publish -or $Release) { "Release" } else { "Debug" }
$RustProfile = if ($Publish -or $Release) { "release" } else { "debug" }
$OutDir = Join-Path $ApiDir "bin\$Configuration\net10.0"
$OutPlugins = Join-Path $OutDir "Plugins"
$SlnPath = Join-Path $Root "Migurdex.slnx"
$TargetFramework = "net10.0"

$PluginProjects = @(
    Get-ChildItem -Directory (Join-Path $Root "Plugins\Migurdex.Plugins.*") -ErrorAction SilentlyContinue |
        Where-Object { Test-Path (Join-Path $_.FullName ($_.Name + ".csproj")) } |
        Select-Object -ExpandProperty Name
)

function Copy-IfChanged {
    param(
        [Parameter(Mandatory = $true)] [string] $Source,
        [Parameter(Mandatory = $true)] [string] $Destination
    )

    $destinationItem = Get-Item $Destination -ErrorAction SilentlyContinue
    $sourceItem = Get-Item $Source

    if ($null -eq $destinationItem -or
        $sourceItem.Length -ne $destinationItem.Length -or
        $sourceItem.LastWriteTimeUtc -gt $destinationItem.LastWriteTimeUtc) {
        Copy-Item $Source -Destination $Destination -Force
        return $true
    }

    return $false
}

function Test-DotnetRestoreNeeded {
    $projects = Get-ChildItem -Path $Root -Recurse -Filter "*.csproj" | Where-Object {
        $_.FullName -notlike "*\bin\*" -and $_.FullName -notlike "*\obj\*"
    }

    foreach ($project in $projects) {
        $assetsFile = Join-Path $project.DirectoryName "obj\project.assets.json"
        if (!(Test-Path $assetsFile)) {
            return $true
        }
    }

    return $false
}

if ($Publish) {
    $Runtime = "win-x64"
    $DistDir = Join-Path $Root "dist"
    $ApiDistDir = Join-Path $DistDir "api"

    Write-Host "--- Migurdex Paketleme Başlatıldı (Release) ---" -ForegroundColor Cyan

    Write-Host "[1/6] Temizlik yapılıyor..." -ForegroundColor Yellow
    if (Test-Path $DistDir) { Remove-Item -Recurse -Force $DistDir }
    New-Item -ItemType Directory -Force -Path $ApiDistDir | Out-Null

    Write-Host "[2/6] Rust Core derleniyor..." -ForegroundColor Yellow
    Push-Location (Join-Path $Root "Migurdex.Native")
    cargo build --release
    Pop-Location

    Write-Host "[3/6] API derleniyor..." -ForegroundColor Yellow
    dotnet publish (Join-Path $Root "Migurdex.Api\Migurdex.Api.csproj") `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:PublishTrimmed=false `
        -o $ApiDistDir

    Copy-Item (Join-Path $Root "Migurdex.Native\target\release\migurdex_native.dll") "$ApiDistDir\"

    Write-Host "[4/6] CLI derleniyor..." -ForegroundColor Yellow
    $TempCli = Join-Path $DistDir "temp_cli"
    dotnet publish (Join-Path $Root "Migurdex.Cli\Migurdex.Cli.csproj") `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:PublishTrimmed=true `
        -o $TempCli

    Move-Item (Join-Path $TempCli "migurdex.exe") $DistDir
    Remove-Item -Recurse -Force $TempCli

    Write-Host "[5/6] Pluginler derleniyor..." -ForegroundColor Yellow
    $PluginsOut = Join-Path $ApiDistDir "Plugins"
    if (!(Test-Path $PluginsOut)) { New-Item -ItemType Directory -Path $PluginsOut -Force | Out-Null }

    foreach ($pluginProject in $PluginProjects) {
        Write-Host "Derleniyor: $pluginProject" -ForegroundColor Gray
        dotnet publish (Join-Path $Root "Plugins\$pluginProject\$pluginProject.csproj") `
            -c $Configuration `
            -o $PluginsOut `
            --no-self-contained
    }

    Write-Host "[6/6] Paket hazırlanıyor..." -ForegroundColor Yellow
    $ZipPath = Join-Path $DistDir "migurdex-$Runtime.zip"
    if (Test-Path $ZipPath) { Remove-Item -Force $ZipPath }
    Compress-Archive -Path (Join-Path $DistDir "migurdex.exe"), (Join-Path $DistDir "api") -DestinationPath $ZipPath
    Write-Host "Arşiv oluşturuldu: $ZipPath" -ForegroundColor Green
    Write-Host "Çalıştırmak için: .\dist\migurdex.exe" -ForegroundColor White
    exit 0
}

Write-Host "--- 1. Rust Core Kontrol Ediliyor... ---" -ForegroundColor Cyan
Push-Location $RustDir

$lastBuild = Get-Item "target\$RustProfile\migurdex_native.dll" -ErrorAction SilentlyContinue
$lastSrcChange = Get-ChildItem -Recurse "src" | Sort-Object LastWriteTime -Descending | Select-Object -First 1

if ($null -eq $lastBuild -or $lastSrcChange.LastWriteTime -gt $lastBuild.LastWriteTime) {
    Write-Host "[RUST] Değişiklik algılandı, derleniyor..." -ForegroundColor Yellow
    if ($RustProfile -eq "release") {
        cargo build --release
    } else {
        cargo build
    }
} else {
    Write-Host "[RUST] Güncel, derleme atlanıyor." -ForegroundColor Green
}
Pop-Location

Write-Host "--- 2. .NET Çözümü Derleniyor... ---" -ForegroundColor Cyan

if (Test-DotnetRestoreNeeded) {
    Write-Host "[DOTNET] Restore gerekli, paketler geri yükleniyor..." -ForegroundColor Yellow
    dotnet restore $SlnPath
}
else {
    Write-Host "[DOTNET] Restore atlanıyor." -ForegroundColor Green
}

dotnet build $SlnPath -c $Configuration -m --no-restore # -m: parallel build

Write-Host "--- 3. Pluginler Hazırlanıyor... ---" -ForegroundColor Cyan
if (!(Test-Path $OutPlugins)) { New-Item -ItemType Directory -Path $OutPlugins -Force }

$expectedNames = @{}
foreach ($pluginProject in $PluginProjects) {
    $pluginBin = Join-Path $Root "Plugins\$pluginProject\bin\$Configuration\$TargetFramework"
    if (Test-Path $pluginBin) {
        Get-ChildItem -Path $pluginBin -File | ForEach-Object { $expectedNames[$_.Name] = $true }
    }
}
$prunedPluginFiles = 0
if ($expectedNames.Count -gt 0) {
    Get-ChildItem -Path $OutPlugins -File -ErrorAction SilentlyContinue | ForEach-Object {
        if (-not $expectedNames.ContainsKey($_.Name)) {
            Remove-Item $_.FullName -Force
            $prunedPluginFiles++
        }
    }
} else {
    Write-Host "[UYARI] Plugin bulunamadi, budama atlandi." -ForegroundColor Yellow
}
if ($prunedPluginFiles -gt 0) {
    Write-Host "[PLUGIN] $prunedPluginFiles eski dosya silindi." -ForegroundColor Yellow
}

$copiedPluginFiles = 0
foreach ($pluginProject in $PluginProjects) {
    $pluginBin = Join-Path $Root "Plugins\$pluginProject\bin\$Configuration\$TargetFramework"

    if (Test-Path $pluginBin) {
        Get-ChildItem -Path $pluginBin -File | ForEach-Object {
            $destination = Join-Path $OutPlugins $_.Name
            if (Copy-IfChanged $_.FullName $destination) {
                $copiedPluginFiles++
            }
        }
    }
}

Write-Host "[PLUGIN] $copiedPluginFiles dosya güncellendi." -ForegroundColor Green

Write-Host "--- 4. DLL'ler Kopyalanıyor... ---" -ForegroundColor Cyan
$RustDll = Join-Path $RustDir "target\$RustProfile\migurdex_native.dll"
if (Test-Path $RustDll) {
    $destinationRustDll = Join-Path $OutDir "migurdex_native.dll"
    if (Copy-IfChanged $RustDll $destinationRustDll) {
        Write-Host "[RUST] migurdex_native.dll kopyalandı." -ForegroundColor Green
    }
    else {
        Write-Host "[RUST] migurdex_native.dll güncel, kopyalama atlandı." -ForegroundColor Green
    }
}

Write-Host "`n[BAŞARILI] Tamamlandı!" -ForegroundColor Green

