<#
.SYNOPSIS
    Продление внутреннего сертификата подписи и опциональный бэкап PFX.
.DESCRIPTION
    Обычная сборка сертификат создаёт сама (scripts\build-signed-installer.ps1).
    Этот скрипт только:
      -Renew     выпустить новый сертификат до истечения срока
      -BackupPfx экспортировать PFX текущего (или только что созданного) сертификата
#>

[CmdletBinding()]
param(
    [ValidateRange(1, 10)]
    [int]$ValidityYears = 5,
    [string]$OutputDirectory = (Join-Path ([Environment]::GetFolderPath("MyDocuments")) "TelegramBot-CodeSigning"),
    [switch]$Renew,
    [switch]$BackupPfx
)

$ErrorActionPreference = "Stop"

if (-not $Renew -and -not $BackupPfx) {
    throw "Укажите -Renew и/или -BackupPfx. Обычная сборка: .\scripts\build-signed-installer.ps1"
}

. (Join-Path $PSScriptRoot "CodeSigning.ps1")

if ($Renew) {
    $existing = Get-InternalSigningCertificate
    $certificate = New-InternalSigningCertificate `
        -ValidityYears $ValidityYears `
        -OutputDirectory $OutputDirectory
    Write-Host ""
    Write-Host "Новый сертификат создан." -ForegroundColor Green
    Write-Host "Thumbprint : $($certificate.Thumbprint)"
    Write-Host "Expires    : $($certificate.NotAfter)"
    if ($null -ne $existing) {
        Write-Host "Предыдущий: $($existing.Thumbprint) (не удаляйте, пока стоят сборки, подписанные им)"
    }
    Write-Warning "Сначала раздайте новый CER через GPO, затем собирайте: .\scripts\build-signed-installer.ps1"
}
else {
    $certificate = Get-InternalSigningCertificate
    if ($null -eq $certificate) {
        throw "Сертификат не найден. Сначала .\scripts\build-signed-installer.ps1 или -Renew."
    }
}

$cerPath = Save-InternalSigningCer -Certificate $certificate -OutputDirectory $OutputDirectory

if ($BackupPfx) {
    $pfxPassword = Read-Host "Password for the offline PFX backup" -AsSecureString
    $pfxPath = Export-InternalSigningPfx `
        -Certificate $certificate `
        -Password $pfxPassword `
        -OutputDirectory $OutputDirectory
    Write-Host "Backup PFX : $pfxPath"
    Write-Warning "PFX — только на ПК сборки / в сейф. Не коммитить и не раздавать."
}

Write-CodeSigningTrustHints -CerPath $cerPath
Write-Host ""
Write-Host "Сборка и подпись:"
Write-Host "  .\scripts\build-signed-installer.ps1"
