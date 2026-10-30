param(
    [switch]$Uninstall,
    [switch]$Purge,
    [switch]$Help
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Repo = "roxyrekt/Migurdex"
$InstallDir = Join-Path $env:LOCALAPPDATA "Migurdex"
$ConfigDir = Join-Path $env:USERPROFILE ".config\migurdex"

if ($Help) {
    Write-Host "Migurdex Windows Kurulum Scripti" -ForegroundColor Cyan
    Write-Host "Kullanım: .\install.ps1 [-Uninstall] [-Purge] [-Help]"
    Write-Host "  -Uninstall   Migurdex'i sistemden kaldırır (ayarları korur)"
    Write-Host "  -Purge       Migurdex'i ve tüm yapılandırma/geçmiş dosyalarını tamamen siler"
    Write-Host "  -Help        Bu yardım mesajını gösterir"
    return
}

function Stop-MigurdexProcesses {
    $processes = Get-Process -Name "migurdex", "Migurdex.Api" -ErrorAction SilentlyContinue
    if ($processes) {
        Write-Host "Açık Migurdex süreçleri sonlandırılıyor..." -ForegroundColor Yellow
        $processes | Stop-Process -Force
        Start-Sleep -Milliseconds 500
    }
}

if ($Uninstall -or $Purge) {
    Write-Host "Migurdex kaldırılıyor..." -ForegroundColor Yellow
    Stop-MigurdexProcesses

    if (Test-Path $InstallDir) {
        Remove-Item -Recurse -Force $InstallDir
    }

    try {
        $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
        if ($userPath -like "*$InstallDir*") {
            $pathItems = $userPath -split ";" | Where-Object { $_ -and ($_ -ne $InstallDir) }
            $newUserPath = $pathItems -join ";"
            [Environment]::SetEnvironmentVariable("Path", $newUserPath, "User")
            Write-Host "PATH ortam değişkeni güncellendi." -ForegroundColor Gray
        }
    }
    catch {
        Write-Host "PATH temizlenirken bir uyarı oluştu: $_" -ForegroundColor Yellow
    }

    if ($Purge) {
        if (Test-Path $ConfigDir) {
            Remove-Item -Recurse -Force $ConfigDir
        }
        Write-Host "✓ Migurdex ve tüm yapılandırma/geçmiş verileri tamamen temizlendi." -ForegroundColor Green
    }
    else {
        Write-Host "✓ Migurdex başarıyla kaldırıldı." -ForegroundColor Green
        if (Test-Path $ConfigDir) {
            Write-Host "Not: Yapılandırma ve geçmiş dosyalarınız saklandı ($ConfigDir)." -ForegroundColor Cyan
            Write-Host "Tüm kalıntıları da silmek isterseniz: irm https://raw.githubusercontent.com/$Repo/main/install.ps1 | % { & ([scriptblock]::Create(`$_)) -Purge }" -ForegroundColor Gray
        }
    }
    return
}

Write-Host @"
    __  ____                        __
   /  |/  (_)___ ___  ___________  / /__  _  __
  / /|_/ / / __ `/ / / / ___/ __ \/ _ \ |/ /
 / /  / / / /_/ / /_/ / /  / /_/ /  __/>  <
/_/  /_/_/\__, /\__,_/_/  /_____/\___/_/|_|
         /____/
"@ -ForegroundColor Cyan

Write-Host "`n--- Migurdex Windows Kurulumu Başlatılıyor ---`n" -ForegroundColor Cyan

if (-not [Environment]::Is64BitOperatingSystem) {
    Write-Host "Hata: Migurdex yalnızca 64-bit Windows sistemlerde çalışabilir." -ForegroundColor Red
    return
}

Write-Host "[1/4] Son sürüm kontrol ediliyor..." -ForegroundColor Yellow

$TarExe = Join-Path $env:SystemRoot "System32\tar.exe"
if (-not (Test-Path $TarExe)) {
    $TarExe = (Get-Command tar.exe -ErrorAction SilentlyContinue).Source
}
if (-not $TarExe) {
    Write-Host "Hata: tar.exe bulunamadi. Windows 10 1803 veya uzeri gerekli." -ForegroundColor Red
    return
}

$DownloadUrl = $null
$LatestTag = $null

if (-not $env:PROCESSOR_ARCHITECTURE -or $env:PROCESSOR_ARCHITECTURE -eq "AMD64") {
    $AssetRuntime = "win-x64"
} else {
    $AssetRuntime = "win-x64"   # su an sadece x64
}

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13
    $releaseInfo = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers @{ "Accept" = "application/vnd.github.v3+json" } -TimeoutSec 10
    if ($releaseInfo -and $releaseInfo.tag_name) {
        $LatestTag = $releaseInfo.tag_name
        $DownloadUrl = "https://github.com/$Repo/releases/download/$LatestTag/migurdex-$AssetRuntime.tar.gz"
        Write-Host "Bulunan son sürüm: $LatestTag" -ForegroundColor Green
    }
}
catch {
    Write-Host "GitHub API sorgulanamadı, doğrudan 'latest' bağlantısı kullanılacak." -ForegroundColor Yellow
}

if (-not $DownloadUrl) {
    $DownloadUrl = "https://github.com/$Repo/releases/latest/download/migurdex-$AssetRuntime.tar.gz"
}

$TempArchive = Join-Path $env:TEMP "migurdex-$AssetRuntime.tar.gz"


try {
    Write-Host "[2/4] Migurdex paketi indiriliyor..." -ForegroundColor Yellow
    if (Test-Path $TempArchive) { Remove-Item -Force $TempArchive }

    Invoke-WebRequest -Uri $DownloadUrl -OutFile $TempArchive -UseBasicParsing

    Write-Host "[3/4] Dosyalar kuruluyor ($InstallDir)..." -ForegroundColor Yellow
    Stop-MigurdexProcesses

    $BackupDir = "${InstallDir}.old"
    if (Test-Path $BackupDir) { Remove-Item -Recurse -Force $BackupDir -ErrorAction SilentlyContinue }
    if (Test-Path $InstallDir) { Move-Item -Path $InstallDir -Destination $BackupDir -Force }
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

    try {
        & $TarExe -xf $TempArchive -C $InstallDir
        if ($LASTEXITCODE -ne 0) {
            throw "Arşiv açılamadı (tar exit $LASTEXITCODE)"
        }
        if (-not (Test-Path (Join-Path $InstallDir "migurdex.exe"))) {
            throw "Arşivde migurdex.exe bulunamadı"
        }
    }
    catch {
        Remove-Item -Recurse -Force $InstallDir -ErrorAction SilentlyContinue
        if (Test-Path $BackupDir) { Move-Item -Path $BackupDir -Destination $InstallDir -Force }
        throw
    }

    if (Test-Path $BackupDir) { Remove-Item -Recurse -Force $BackupDir -ErrorAction SilentlyContinue }
}
finally {
    if (Test-Path $TempArchive) { Remove-Item -Force $TempArchive -ErrorAction SilentlyContinue }
}

Write-Host "[4/4] Ortam değişkenleri ve sistem kontrolleri yapılıyor..." -ForegroundColor Yellow
try {
    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    if ($null -eq $userPath) { $userPath = "" }

    $pathParts = $userPath -split ";" | Where-Object { $_ }
    if ($pathParts -notcontains $InstallDir) {
        $newUserPath = if ([string]::IsNullOrWhiteSpace($userPath)) { $InstallDir } else { "$userPath;$InstallDir" }
        [Environment]::SetEnvironmentVariable("Path", $newUserPath, "User")
        Write-Host "✓ Migurdex kullanıcı PATH değişkenine kalıcı olarak eklendi." -ForegroundColor Green
    }
    else {
        Write-Host "✓ Migurdex zaten PATH değişkeninde kayıtlı." -ForegroundColor Green
    }

    $currentSessionPathParts = $env:Path -split ";" | Where-Object { $_ }
    if ($currentSessionPathParts -notcontains $InstallDir) {
        $env:Path = "$env:Path;$InstallDir"
    }
}
catch {
    Write-Host "PATH değişkeni ayarlanırken uyarı: $_" -ForegroundColor Yellow
}

$mpvInstalled = $null -ne (Get-Command "mpv" -ErrorAction SilentlyContinue)
if (-not $mpvInstalled) {
    Write-Host "`n⚠ UYARI: Sisteminizde 'mpv' video oynatıcısı bulunamadı!" -ForegroundColor Yellow
    Write-Host "Migurdex videoları oynatmak için MPV oynatıcısına ihtiyaç duyar." -ForegroundColor Yellow

    $wingetAvailable = $null -ne (Get-Command "winget" -ErrorAction SilentlyContinue)
    if ($wingetAvailable) {
        Write-Host "MPV'yi otomatik yüklemek için komut satırına şunu yazabilirsiniz:" -ForegroundColor Cyan
        Write-Host "  winget install shinchiro.mpv" -ForegroundColor White
    }
    else {
        Write-Host "MPV'yi https://mpv.io adresinden indirip PATH'e ekleyebilirsiniz." -ForegroundColor Cyan
    }
}
else {
    Write-Host "✓ MPV oynatıcısı algılandı." -ForegroundColor Green
}

Write-Host "`n🎉 Migurdex kurulumu başarıyla tamamlandı!`n" -ForegroundColor Green
Write-Host "Çalıştırmak için: " -NoNewline
Write-Host "migurdex" -ForegroundColor Cyan
Write-Host "Kaldırmak için:   irm https://raw.githubusercontent.com/$Repo/main/install.ps1 | % { & ([scriptblock]::Create(`$_)) -Uninstall }" -ForegroundColor Gray
Write-Host "Tam temizlik:     irm https://raw.githubusercontent.com/$Repo/main/install.ps1 | % { & ([scriptblock]::Create(`$_)) -Purge }" -ForegroundColor Gray
