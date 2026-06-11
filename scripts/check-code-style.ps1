param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$solutionPath = Join-Path $repoRoot "TelegramBot.slnx"

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$Command
    )

    & $Command
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

Push-Location $repoRoot
try {
    Invoke-CheckedCommand { dotnet restore $solutionPath }
    Invoke-CheckedCommand { dotnet format $solutionPath --verify-no-changes --no-restore --verbosity diagnostic }
    Invoke-CheckedCommand { dotnet build $solutionPath --no-restore --configuration $Configuration }
}
finally {
    Pop-Location
}
