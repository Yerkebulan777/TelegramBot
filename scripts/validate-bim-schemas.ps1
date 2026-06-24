<#
.SYNOPSIS
    Validates BIM TaskFile/ResultFile JSON samples against their JSON Schemas.
    Uses ajv-cli (Node.js) to validate Draft 2020-12 schemas.
.DESCRIPTION
    This script validates sample JSON files against the BIM contract schemas
    (Docs/TaskFile.schema.json, Docs/ResultFile.schema.json) to detect drift
    between C# models and JSON schemas.
    
    Steps:
    1. Validates the schemas themselves are valid JSON Schema
    2. Validates expected-valid samples MUST pass
    3. Validates expected-invalid samples MUST fail (negative testing)
.NOTES
    Requires:
      - Node.js (pre-installed on GitHub Actions windows-latest runners)
      - ajv-cli installed globally (npm install -g ajv-cli)
    Schemas use Draft 2020-12 — ajv v8+ with --spec=draft2020 flag required.
#>

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$SchemaDir = Join-Path $RepoRoot "Docs"
$SamplesDir = Join-Path $RepoRoot "scripts" "bim-schema-test-samples"

# ── Helper: run ajv for one schema ──
function Test-Ajv {
    param(
        [string]$Description,
        [string]$Schema,
        [string]$DataFile,
        [switch]$ShouldFail
    )

    Write-Host "  🔍 $Description ... " -NoNewline
    $result = & ajv validate --spec=draft2020 -s $Schema -d $DataFile 2>&1
    $exitCode = $LASTEXITCODE

    if (-not $ShouldFail) {
        # Expected to PASS
        if ($exitCode -eq 0) {
            Write-Host "✅ PASS" -ForegroundColor Green
            return $true
        } else {
            Write-Host "❌ FAIL" -ForegroundColor Red
            Write-Host "     $result" -ForegroundColor Red
            return $false
        }
    } else {
        # Expected to FAIL
        if ($exitCode -ne 0) {
            Write-Host "✅ CORRECTLY FAILED" -ForegroundColor Green
            return $true
        } else {
            Write-Host "❌ SHOULD HAVE FAILED (but passed)" -ForegroundColor Red
            return $false
        }
    }
}

# ── Helper: validate samples for one schema type ──
function Validate-Samples {
    param(
        [string]$SchemaName,
        [string]$SchemaFile,
        [string[]]$ValidSamples,
        [string[]]$InvalidSamples
    )

    Write-Host "`n══════════════════════════════════════════════════════" -ForegroundColor Cyan
    Write-Host "  Schema: $SchemaName ($SchemaFile)" -ForegroundColor Cyan
    Write-Host "══════════════════════════════════════════════════════`n" -ForegroundColor Cyan

    $schemaPath = Join-Path $SchemaDir $SchemaFile
    $allOk = $true

    # ── Step 1: Validate the schema itself ──
    Write-Host "  ── Schema self-validation ──" -ForegroundColor Yellow
    if (-not (Test-Ajv -Description "Schema is valid JSON Schema" -Schema $schemaPath -DataFile $schemaPath -ShouldFail)) {
        $allOk = $false
    }
    Write-Host ""

    # ── Step 2: Validate expected-to-pass samples ──
    Write-Host "  ── Positive tests (should PASS) ──" -ForegroundColor Yellow
    foreach ($sample in $ValidSamples) {
        $dataFile = Join-Path $SamplesDir $sample
        if (-not (Test-Path $dataFile)) {
            Write-Host "  ❌ MISSING: $sample" -ForegroundColor Red
            $allOk = $false
            continue
        }
        if (-not (Test-Ajv -Description "$sample" -Schema $schemaPath -DataFile $dataFile)) {
            $allOk = $false
        }
    }

    # ── Step 3: Validate expected-to-fail samples ──
    Write-Host "" 
    Write-Host "  ── Negative tests (should FAIL) ──" -ForegroundColor Yellow
    foreach ($sample in $InvalidSamples) {
        $dataFile = Join-Path $SamplesDir $sample
        if (-not (Test-Path $dataFile)) {
            Write-Host "  ❌ MISSING: $sample" -ForegroundColor Red
            $allOk = $false
            continue
        }
        if (-not (Test-Ajv -Description "$sample" -Schema $schemaPath -DataFile $dataFile -ShouldFail)) {
            $allOk = $false
        }
    }

    if (-not $allOk) {
        throw "Schema validation FAILED for $SchemaName. See errors above."
    }

    Write-Host "`n  ✅ All checks passed for $SchemaName" -ForegroundColor Green
}

# ════════════════════════════════════════
# MAIN
# ════════════════════════════════════════
Write-Host "╔══════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║      BIM JSON Schema Validation                          ║" -ForegroundColor Cyan
Write-Host "║      Validates C# model samples against JSON schemas     ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""

try {
    # ── TaskFile ──
    Validate-Samples `
        -SchemaName "TaskFile" `
        -SchemaFile "TaskFile.schema.json" `
        -ValidSamples @(
            "taskfile-valid-1.json",
            "taskfile-valid-2.json",
            "taskfile-valid-3.json"
        ) `
        -InvalidSamples @(
            "taskfile-invalid-missing-fields.json",
            "taskfile-invalid-wrong-type-commandid.json",
            "taskfile-invalid-unknown-option.json"
        )

    # ── ResultFile ──
    Validate-Samples `
        -SchemaName "ResultFile" `
        -SchemaFile "ResultFile.schema.json" `
        -ValidSamples @(
            "resultfile-valid-1.json",
            "resultfile-valid-2.json",
            "resultfile-valid-3.json",
            "resultfile-valid-4.json"
        ) `
        -InvalidSamples @(
            "resultfile-invalid-missing-status.json",
            "resultfile-invalid-bad-enum.json"
        )

    Write-Host "`n╔══════════════════════════════════════════════════════╗" -ForegroundColor Green
    Write-Host "║  ✅ ALL BIM SCHEMA VALIDATIONS PASSED                ║" -ForegroundColor Green
    Write-Host "╚══════════════════════════════════════════════════════╝" -ForegroundColor Green
    exit 0
}
catch {
    Write-Host "`n╔══════════════════════════════════════════════════════╗" -ForegroundColor Red
    Write-Host "║  ❌ BIM SCHEMA VALIDATION FAILED                     ║" -ForegroundColor Red
    Write-Host "╚══════════════════════════════════════════════════════╝" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
