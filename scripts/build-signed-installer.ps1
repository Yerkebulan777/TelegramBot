<#
.SYNOPSIS
    Одним запуском: publish Server/Worker/GrantLogonRight → подпись всех EXE → подписанный Setup.
.DESCRIPTION
    Требует заранее созданный сертификат (scripts\setup-internal-code-signing.ps1)
    и установленные .NET SDK, Inno Setup 7, Windows SDK (signtool.exe).

    Результат: Installer\Output\TelegramBotSetup.exe
#>

[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$installerDir = Join-Path $repoRoot "Installer"
$publishRoot = Join-Path $installerDir "publish"
$outputPath = Join-Path $installerDir "Output\TelegramBotSetup.exe"
$issPath = Join-Path $installerDir "TelegramBot.iss"
$subjectPattern = "CN=TelegramBot Internal*"
$codeSigningOid = "1.3.6.1.5.5.7.3.3"

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Find-SignTool {
    $fromPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($null -ne $fromPath) {
        return $fromPath.Source
    }

    $candidates = @(
        Join-Path ${env:ProgramFiles(x86)} "Microsoft SDKs\ClickOnce\SignTool\signtool.exe"
    )

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (Test-Path $kitsRoot) {
        $candidates += Get-ChildItem $kitsRoot -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
            Sort-Object FullName -Descending |
            Select-Object -ExpandProperty FullName
    }

    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path $candidate)) {
            return $candidate
        }
    }

    throw "signtool.exe не найден. Установите Windows SDK или ClickOnce Signing Tools."
}

function Find-Iscc {
    $candidates = @(
        "C:\Program Files\Inno Setup 7\ISCC.exe",
        "C:\Program Files (x86)\Inno Setup 7\ISCC.exe",
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe"
    )
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    throw "ISCC.exe не найден. Установите Inno Setup 7: https://jrsoftware.org/isdl.php"
}

function Get-SigningCertificate {
    $certificate = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object {
            $_.Subject -like $subjectPattern -and
            $_.HasPrivateKey -and
            $_.NotAfter -gt (Get-Date) -and
            $_.EnhancedKeyUsageList.ObjectId -contains $codeSigningOid
        } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1

    if ($null -eq $certificate) {
        throw @"
Сертификат '$subjectPattern' не найден в Cert:\CurrentUser\My.
Сначала один раз выполните:
  .\scripts\setup-internal-code-signing.ps1
"@
    }

    return $certificate
}

function Invoke-DotNetPublish {
    param(
        [string]$ProjectPath,
        [string]$OutputDir
    )

    if (Test-Path $OutputDir) {
        Remove-Item -Recurse -Force $OutputDir
    }
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

    Write-Host "  publish $([IO.Path]::GetFileName($ProjectPath)) → $OutputDir"
    & dotnet publish $ProjectPath -c $Configuration -o $OutputDir --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed: $ProjectPath"
    }
}

function Invoke-SignFile {
    param(
        [string]$SignToolExe,
        [string]$Thumbprint,
        [string]$FilePath
    )

    Write-Host "  sign $FilePath"
    & $SignToolExe sign /sha1 $Thumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $FilePath
    if ($LASTEXITCODE -ne 0) {
        throw "Подпись не удалась: $FilePath"
    }
}

function Assert-Signed {
    param(
        [string]$FilePath,
        [string]$ExpectedThumbprint
    )

    $signature = Get-AuthenticodeSignature -FilePath $FilePath -ErrorAction Stop
    $actual = $signature.SignerCertificate.Thumbprint

    if ($null -eq $actual) {
        throw "Нет подписи: $FilePath (Status=$($signature.Status))"
    }

    if ($actual -ne $ExpectedThumbprint) {
        throw "Thumbprint не совпал для $FilePath. Ожидали $ExpectedThumbprint, получили $actual"
    }

    # Self-signed: Status часто UnknownError, но сертификат уже наш.
    if ($signature.Status -notin @("Valid", "UnknownError")) {
        throw "Некорректная подпись: $FilePath (Status=$($signature.Status))"
    }

    Write-Host "  OK  $FilePath [$($signature.Status)]" -ForegroundColor Green
}

# --- main ---

Set-Location $repoRoot

Write-Step "Проверка окружения"
$certificate = Get-SigningCertificate
$signToolExe = Find-SignTool
$isccExe = Find-Iscc
Write-Host "  cert      : $($certificate.Thumbprint) ($($certificate.Subject))"
Write-Host "  expires   : $($certificate.NotAfter.ToString('yyyy-MM-dd'))"
Write-Host "  signtool  : $signToolExe"
Write-Host "  iscc      : $isccExe"

Write-Step "Publish всех проектов"
$projects = @(
    @{ Project = Join-Path $repoRoot "TelegramBot.Server\TelegramBot.Server.csproj"; Out = Join-Path $publishRoot "Server" },
    @{ Project = Join-Path $repoRoot "TelegramBot.Worker\TelegramBot.Worker.csproj"; Out = Join-Path $publishRoot "Worker" },
    @{ Project = Join-Path $installerDir "GrantLogonRight\GrantLogonRight.csproj"; Out = Join-Path $publishRoot "GrantLogonRight" }
)
foreach ($item in $projects) {
    Invoke-DotNetPublish -ProjectPath $item.Project -OutputDir $item.Out
}

Write-Step "Подпись всех EXE в Installer\publish"
$publishedExes = Get-ChildItem -Path $publishRoot -Filter *.exe -Recurse -File |
    Sort-Object FullName
if ($publishedExes.Count -eq 0) {
    throw "В $publishRoot нет ни одного .exe после publish."
}
foreach ($exe in $publishedExes) {
    Invoke-SignFile -SignToolExe $signToolExe -Thumbprint $certificate.Thumbprint -FilePath $exe.FullName
}

Write-Step "Сборка Setup (Inno Setup подпишет Setup и Uninstall)"
$signCommand = "`$q$signToolExe`$q sign /sha1 $($certificate.Thumbprint) /fd SHA256 /tr $TimestampUrl /td SHA256 `$f"
& $isccExe "/DEnableCodeSigning" "/STelegramBotInternalSign=$signCommand" $issPath
if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed"
}
if (-not (Test-Path $outputPath)) {
    throw "Setup не создан: $outputPath"
}

Write-Step "Проверка подписей"
foreach ($exe in $publishedExes) {
    Assert-Signed -FilePath $exe.FullName -ExpectedThumbprint $certificate.Thumbprint
}
Assert-Signed -FilePath $outputPath -ExpectedThumbprint $certificate.Thumbprint

Write-Host ""
Write-Host "Готово. Подписанный инсталлятор:" -ForegroundColor Green
Write-Host "  $outputPath"
Write-Host ""
Write-Host "Скопируйте файл на внутренний UNC и запустите на целевом ПК от администратора."
