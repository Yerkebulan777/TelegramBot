<#
.SYNOPSIS
    Sign a single file with TSA fallback. Inno Setup SignTool adapter — not for hand use.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$FilePath,
    [Parameter(Mandatory)]
    [string]$Thumbprint,
    [Parameter(Mandatory)]
    [string]$SignToolExe
)

$ErrorActionPreference = "Stop"
# Inno's $f may arrive with quotes; extra $q on the caller used to double them.
$FilePath = $FilePath.Trim().Trim('"')

. (Join-Path $PSScriptRoot "CodeSigning.ps1")
Invoke-AuthenticodeSign -SignToolExe $SignToolExe -Thumbprint $Thumbprint -FilePath $FilePath
