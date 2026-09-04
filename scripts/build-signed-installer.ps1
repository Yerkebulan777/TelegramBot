<#
.SYNOPSIS
    Один запуск: сертификат (создать если нет) → publish → подпись → Setup.
.DESCRIPTION
    Идемпотентно. Повторный запуск переиспользует уже существующий сертификат.
    Продление только явно: scripts\setup-internal-code-signing.ps1 -Renew

    Результат: Installer\Output\TelegramBotSetup.exe
#>

[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

. (Join-Path $PSScriptRoot "CodeSigning.ps1")

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$installerDir = Join-Path $repoRoot "Installer"
$publishRoot = Join-Path $installerDir "publish"
$outputPath = Join-Path $installerDir "Output\TelegramBotSetup.exe"
$issPath = Join-Path $installerDir "TelegramBot.iss"

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
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

Set-Location $repoRoot

Write-Step "Сертификат"
$certificate = Initialize-InternalSigningCertificate
$cerPath = Get-InternalSigningCerPath -Certificate $certificate

Write-Step "Проверка окружения"
$signToolExe = Find-SignTool
$isccExe = Find-Iscc
Write-Host "  cert      : $($certificate.Thumbprint) ($($certificate.Subject))"
Write-Host "  expires   : $($certificate.NotAfter.ToString('yyyy-MM-dd'))"
Write-Host "  cer       : $cerPath"
Write-Host "  signtool  : $signToolExe"
Write-Host "  iscc      : $isccExe"

Write-Step "Publish всех проектов"
$projects = @(
    @{ Project = Join-Path $repoRoot "TelegramBot.Server\TelegramBot.Server.csproj"; Out = Join-Path $publishRoot "Server" },
    @{ Project = Join-Path $repoRoot "TelegramBot.Worker\TelegramBot.Worker.csproj"; Out = Join-Path $publishRoot "Worker" },
    @{ Project = Join-Path $repoRoot "TelegramBot.RootPathSetup\TelegramBot.RootPathSetup.csproj"; Out = Join-Path $publishRoot "RootPathSetup" },
    @{ Project = Join-Path $installerDir "GrantLogonRight\GrantLogonRight.csproj"; Out = Join-Path $publishRoot "GrantLogonRight" }
)
foreach ($item in $projects) {
    Invoke-DotNetPublish -ProjectPath $item.Project -OutputDir $item.Out
}

Write-Step "Подпись всех EXE в Installer\publish"
$publishedExes = @(Get-ChildItem -Path $publishRoot -Filter *.exe -Recurse -File | Sort-Object FullName)
if ($publishedExes.Count -eq 0) {
    throw "В $publishRoot нет ни одного .exe после publish."
}
foreach ($exe in $publishedExes) {
    Invoke-AuthenticodeSign -SignToolExe $signToolExe -Thumbprint $certificate.Thumbprint -FilePath $exe.FullName
}

Write-Step "Сборка Setup (Inno Setup подпишет Setup и Uninstall)"
# Inno SignTool is CreateProcess — must be a real .exe, not a .cmd.
$powershellExe = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
$signOne = Join-Path $PSScriptRoot "sign-one.ps1"
# $f is already quoted by Inno when the path contains spaces. Wrapping it in
# $q again produced -FilePath ""C:\path\file"" and breaks on spaces.
$signCommand = "`$q$powershellExe`$q -NoProfile -ExecutionPolicy Bypass -File `$q$signOne`$q -SignToolExe `$q$signToolExe`$q -Thumbprint $($certificate.Thumbprint) -FilePath `$f"
& $isccExe "/DEnableCodeSigning" "/STelegramBotInternalSign=$signCommand" $issPath
if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed"
}
if (-not (Test-Path $outputPath)) {
    throw "Setup не создан: $outputPath"
}

Write-Step "Проверка подписей"
foreach ($exe in $publishedExes) {
    Assert-AuthenticodeSigned -FilePath $exe.FullName -ExpectedThumbprint $certificate.Thumbprint
}
Assert-AuthenticodeSigned -FilePath $outputPath -ExpectedThumbprint $certificate.Thumbprint

Write-Host ""
Write-Host "Готово. Подписанный инсталлятор:" -ForegroundColor Green
Write-Host "  $outputPath"
Write-CodeSigningTrustHints -CerPath $cerPath
Write-Host ""
Write-Host "Скопируйте Setup на внутренний UNC и запустите на целевом ПК от администратора."
Write-Host "Офлайн-бэкап PFX (по желанию): .\scripts\setup-internal-code-signing.ps1 -BackupPfx"
