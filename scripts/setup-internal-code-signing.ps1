<#
.SYNOPSIS
    Creates the internal Authenticode certificate used to sign TelegramBot.
.DESCRIPTION
    Run once on the dedicated build computer. The private key stays in the
    current user's certificate store. The exported PFX is an offline backup;
    only the CER file may be distributed to company computers.
#>

[CmdletBinding()]
param(
    [string]$Subject = "CN=TelegramBot Internal Code Signing",
    [ValidateRange(1, 10)]
    [int]$ValidityYears = 5,
    [string]$OutputDirectory = (Join-Path ([Environment]::GetFolderPath("MyDocuments")) "TelegramBot-CodeSigning"),
    [switch]$Renew
)

$ErrorActionPreference = "Stop"
$codeSigningOid = "1.3.6.1.5.5.7.3.3"

$existingCertificate = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object {
        $_.Subject -eq $Subject -and
        $_.HasPrivateKey -and
        $_.NotAfter -gt (Get-Date) -and
        $_.EnhancedKeyUsageList.ObjectId -contains $codeSigningOid
    } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if ($null -ne $existingCertificate -and -not $Renew) {
    throw "A valid certificate already exists: $($existingCertificate.Thumbprint), expires $($existingCertificate.NotAfter)."
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$certificate = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $Subject `
    -KeyAlgorithm RSA `
    -KeyLength 3072 `
    -HashAlgorithm SHA256 `
    -KeyExportPolicy Exportable `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -NotAfter (Get-Date).AddYears($ValidityYears)

$cerPath = Join-Path $OutputDirectory "TelegramBot-Internal-Code-Signing-$($certificate.Thumbprint).cer"
$pfxPath = Join-Path $OutputDirectory "TelegramBot-Internal-Code-Signing-$($certificate.Thumbprint).pfx"
$pfxPassword = Read-Host "Password for the offline PFX backup" -AsSecureString

Export-Certificate -Cert $certificate -FilePath $cerPath -Force | Out-Null
Export-PfxCertificate `
    -Cert $certificate `
    -FilePath $pfxPath `
    -Password $pfxPassword `
    -ChainOption EndEntityCertOnly `
    -Force | Out-Null

# Trust the certificate for verification on the build computer.
Import-Certificate -FilePath $cerPath -CertStoreLocation Cert:\CurrentUser\Root | Out-Null
Import-Certificate -FilePath $cerPath -CertStoreLocation Cert:\CurrentUser\TrustedPublisher | Out-Null

Write-Host ""
Write-Host "Internal code-signing certificate created." -ForegroundColor Green
Write-Host "Thumbprint : $($certificate.Thumbprint)"
Write-Host "Expires    : $($certificate.NotAfter)"
Write-Host "Public CER: $cerPath"
Write-Host "Backup PFX: $pfxPath"
Write-Host ""
Write-Warning "Move the PFX to protected offline storage. Never distribute or commit it. Deploy only the CER file."
