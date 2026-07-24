<#
.SYNOPSIS
    Publishes TelegramBot and builds a signed Inno Setup installer.
.DESCRIPTION
    Finds the internal certificate in Cert:\CurrentUser\My, signs the three
    application executables, and asks Inno Setup to sign Setup and Uninstall.
#>

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$buildProject = Join-Path $repoRoot "Installer\Installer.build.proj"
$outputPath = Join-Path $repoRoot "Installer\Output\TelegramBotSetup.exe"
$subject = "CN=TelegramBot Internal Code Signing"
$codeSigningOid = "1.3.6.1.5.5.7.3.3"

$certificate = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object {
        $_.Subject -eq $subject -and
        $_.HasPrivateKey -and
        $_.NotAfter -gt (Get-Date) -and
        $_.EnhancedKeyUsageList.ObjectId -contains $codeSigningOid
    } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if ($null -eq $certificate) {
    throw "No valid '$subject' certificate with a private key was found. Run scripts\setup-internal-code-signing.ps1 first."
}

$signToolCommand = Get-Command signtool.exe -ErrorAction SilentlyContinue
if ($null -ne $signToolCommand) {
    $signToolExe = $signToolCommand.Source
}
else {
    $windowsKitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    $signToolExe = Get-ChildItem $windowsKitsRoot -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
        Sort-Object FullName -Descending |
        Select-Object -ExpandProperty FullName -First 1
}

if ([string]::IsNullOrWhiteSpace($signToolExe)) {
    throw "signtool.exe was not found. Install Windows SDK."
}

Write-Host "Building with certificate $($certificate.Thumbprint)"
& dotnet build $buildProject -t:Installer `
    "-p:SignThumbprint=$($certificate.Thumbprint)" `
    "-p:SignToolExe=$signToolExe"
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& $signToolExe verify /pa /all /v $outputPath
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "Signed installer: $outputPath" -ForegroundColor Green
