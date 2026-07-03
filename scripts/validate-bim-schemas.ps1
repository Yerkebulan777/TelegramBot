<#
.SYNOPSIS
    Validates BIM TaskFile/ResultFile XML samples against their XSD schemas.
.DESCRIPTION
    This script validates sample XML files against the BIM contract schemas
    from the canonical RevitBIMFusion/Docs directory to detect drift
    between C# models and XSD schemas.
#>

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$SchemaDir = if ($env:BIM_CONTRACT_DIRECTORY) {
    $env:BIM_CONTRACT_DIRECTORY
} else {
    Join-Path (Split-Path -Parent $RepoRoot) "RevitBIMFusion" "Docs"
}
$SamplesDir = Join-Path $RepoRoot "scripts" "bim-schema-test-samples"

function Test-XmlSchema {
    param(
        [string]$Description,
        [string]$Schema,
        [string]$DataFile,
        [switch]$ShouldFail
    )

    Write-Host "  $Description ... " -NoNewline

    $errors = [System.Collections.Generic.List[string]]::new()
    $settings = [System.Xml.XmlReaderSettings]::new()
    $settings.ValidationType = [System.Xml.ValidationType]::Schema
    $settings.Schemas.Add($null, $Schema) | Out-Null
    $settings.add_ValidationEventHandler({
        param($sender, $eventArgs)
        $errors.Add($eventArgs.Message)
    })

    try {
        $reader = [System.Xml.XmlReader]::Create($DataFile, $settings)
        try {
            while ($reader.Read()) { }
        }
        finally {
            $reader.Dispose()
        }
    }
    catch {
        $errors.Add($_.Exception.Message)
    }

    $passed = $errors.Count -eq 0
    if (-not $ShouldFail -and $passed) {
        Write-Host "PASS" -ForegroundColor Green
        return $true
    }

    if ($ShouldFail -and -not $passed) {
        Write-Host "CORRECTLY FAILED" -ForegroundColor Green
        return $true
    }

    if ($passed) {
        Write-Host "SHOULD HAVE FAILED (but passed)" -ForegroundColor Red
    }
    else {
        Write-Host "FAIL" -ForegroundColor Red
        foreach ($errorText in $errors) {
            Write-Host "     $errorText" -ForegroundColor Red
        }
    }

    return $false
}

function Test-SchemaCompiles {
    param(
        [string]$Description,
        [string]$Schema
    )

    Write-Host "  $Description ... " -NoNewline
    try {
        $schemas = [System.Xml.Schema.XmlSchemaSet]::new()
        $schemas.Add($null, $Schema) | Out-Null
        $schemas.Compile()
        Write-Host "PASS" -ForegroundColor Green
        return $true
    }
    catch {
        Write-Host "FAIL" -ForegroundColor Red
        Write-Host "     $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }
}

function Validate-Samples {
    param(
        [string]$SchemaName,
        [string]$SchemaFile,
        [string[]]$ValidSamples,
        [string[]]$InvalidSamples
    )

    Write-Host ""
    Write-Host "======================================================" -ForegroundColor Cyan
    Write-Host "  Schema: $SchemaName ($SchemaFile)" -ForegroundColor Cyan
    Write-Host "======================================================" -ForegroundColor Cyan

    $schemaPath = Join-Path $SchemaDir $SchemaFile
    $allOk = $true

    if (-not (Test-SchemaCompiles -Description "Schema compiles" -Schema $schemaPath)) {
        $allOk = $false
    }

    Write-Host ""
    Write-Host "  Positive tests (should PASS)" -ForegroundColor Yellow
    foreach ($sample in $ValidSamples) {
        $dataFile = Join-Path $SamplesDir $sample
        if (-not (Test-Path $dataFile)) {
            Write-Host "  MISSING: $sample" -ForegroundColor Red
            $allOk = $false
            continue
        }

        if (-not (Test-XmlSchema -Description $sample -Schema $schemaPath -DataFile $dataFile)) {
            $allOk = $false
        }
    }

    Write-Host ""
    Write-Host "  Negative tests (should FAIL)" -ForegroundColor Yellow
    foreach ($sample in $InvalidSamples) {
        $dataFile = Join-Path $SamplesDir $sample
        if (-not (Test-Path $dataFile)) {
            Write-Host "  MISSING: $sample" -ForegroundColor Red
            $allOk = $false
            continue
        }

        if (-not (Test-XmlSchema -Description $sample -Schema $schemaPath -DataFile $dataFile -ShouldFail)) {
            $allOk = $false
        }
    }

    if (-not $allOk) {
        throw "Schema validation FAILED for $SchemaName. See errors above."
    }

    Write-Host ""
    Write-Host "  All checks passed for $SchemaName" -ForegroundColor Green
}

Write-Host "BIM XML Schema Validation"
Write-Host "Validates XML samples against XSD schemas"
Write-Host ""

try {
    Validate-Samples `
        -SchemaName "TaskFile" `
        -SchemaFile "TaskFile.schema.xsd" `
        -ValidSamples @(
            "taskfile-valid-1.xml",
            "taskfile-valid-2.xml",
            "taskfile-valid-3.xml"
        ) `
        -InvalidSamples @(
            "taskfile-invalid-missing-fields.xml",
            "taskfile-invalid-wrong-type-commandid.xml",
            "taskfile-invalid-unknown-option.xml"
        )

    Validate-Samples `
        -SchemaName "ResultFile" `
        -SchemaFile "ResultFile.schema.xsd" `
        -ValidSamples @(
            "resultfile-valid-1.xml",
            "resultfile-valid-2.xml",
            "resultfile-valid-3.xml",
            "resultfile-valid-4.xml"
        ) `
        -InvalidSamples @(
            "resultfile-invalid-missing-status.xml",
            "resultfile-invalid-bad-enum.xml"
        )

    Write-Host ""
    Write-Host "ALL BIM SCHEMA VALIDATIONS PASSED" -ForegroundColor Green
    exit 0
}
catch {
    Write-Host ""
    Write-Host "BIM SCHEMA VALIDATION FAILED" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
