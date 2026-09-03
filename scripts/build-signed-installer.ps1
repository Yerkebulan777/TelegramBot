<#
.SYNOPSIS
    Publishes TelegramBot and builds a signed Inno Setup installer.
.DESCRIPTION
    Finds the internal certificate in Cert:\CurrentUser\My, signs the three
    application executables, and asks Inno Setup to sign Setup and Uninstall.
#>

$ErrorActionPreference = "Stop"
Import-Module Microsoft.PowerShell.Security -ErrorAction SilentlyContinue

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$buildProject = Join-Path $repoRoot "Installer\Installer.build.proj"
$outputPath = Join-Path $repoRoot "Installer\Output\TelegramBotSetup.exe"
$subjectPattern = "CN=TelegramBot Internal*"
$codeSigningOid = "1.3.6.1.5.5.7.3.3"

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
    throw "No valid certificate matching '$subjectPattern' with a private key was found in Cert:\CurrentUser\My. Run scripts\setup-internal-code-signing.ps1 first."
}

$signToolCommand = Get-Command signtool.exe -ErrorAction SilentlyContinue
if ($null -ne $signToolCommand) {
    $signToolExe = $signToolCommand.Source
} else {
    $clickOnceRoot = Join-Path ${env:ProgramFiles(x86)} "Microsoft SDKs\ClickOnce\SignTool"
    if (Test-Path $clickOnceRoot) {
        $signToolExe = Get-ChildItem $clickOnceRoot -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty FullName -First 1
    }
    if ([string]::IsNullOrWhiteSpace($signToolExe)) {
        $windowsKitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
        if (Test-Path $windowsKitsRoot) {
            $signToolExe = Get-ChildItem $windowsKitsRoot -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
                Sort-Object FullName -Descending |
                Select-Object -ExpandProperty FullName -First 1
        }
    }
}

if ([string]::IsNullOrWhiteSpace($signToolExe)) {
    throw "signtool.exe was not found. Install Windows SDK or ClickOnce SDK."
}

Write-Host "Building with certificate $($certificate.Thumbprint)"
& dotnet build $buildProject -t:Installer `
    "-p:SignThumbprint=$($certificate.Thumbprint)" `
    "-p:SignToolExe=$signToolExe"
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "Verifying signature with Get-AuthenticodeSignature..."
$signature = Get-AuthenticodeSignature -FilePath $outputPath -ErrorAction Stop

if ($signature.Status -eq "Valid") {
    Write-Host "File is signed with valid signature" -ForegroundColor Green
    if ($signature.SignerCertificate.Thumbprint -eq $certificate.Thumbprint) {
        Write-Host "Signer certificate thumbprint matches: $($signature.SignerCertificate.Thumbprint)" -ForegroundColor Green
    } else {
        throw "Signer certificate thumbprint mismatch. Expected: $($certificate.Thumbprint), Got: $($signature.SignerCertificate.Thumbprint)"
    }
} elseif ($signature.Status -eq "UnknownError") {
    if ($null -ne $signature.SignerCertificate -and $signature.SignerCertificate.Thumbprint -eq $certificate.Thumbprint) {
        Write-Host "Signature found with matching thumbprint (self-signed certificate): $($signature.SignerCertificate.Thumbprint)" -ForegroundColor Yellow
    } else {
        throw "File signature is invalid or thumbprint does not match. Status: $($signature.Status)"
    }
} else {
    throw "File is not properly signed. Status: $($signature.Status)"
}

Write-Host ""
Write-Host "Signed installer successfully created: $outputPath" -ForegroundColor Green