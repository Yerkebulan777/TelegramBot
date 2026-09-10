# Shared helpers for internal Authenticode signing. Dot-source only — no param()
# block (it would steal arguments from the caller).

$script:CodeSigningSubject = "CN=TelegramBot Internal Code Signing"
$script:CodeSigningSubjectPattern = "CN=TelegramBot Internal*"
$script:CodeSigningOid = "1.3.6.1.5.5.7.3.3"
$script:TimestampUrls = @(
    "http://timestamp.digicert.com",
    "http://timestamp.sectigo.com",
    "http://timestamp.globalsign.com/tsa/r6advanced1"
)

function Get-CodeSigningOutputDirectory {
    param([string]$OutputDirectory)
    if ($OutputDirectory) {
        return $OutputDirectory
    }
    Join-Path ([Environment]::GetFolderPath("MyDocuments")) "TelegramBot-CodeSigning"
}

function Get-InternalSigningCerPath {
    param(
        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [string]$OutputDirectory
    )
    Join-Path (Get-CodeSigningOutputDirectory $OutputDirectory) `
        "TelegramBot-Internal-Code-Signing-$($Certificate.Thumbprint).cer"
}

function Get-InternalSigningCertificate {
    Get-ChildItem Cert:\CurrentUser\My |
        Where-Object {
            $_.Subject -like $script:CodeSigningSubjectPattern -and
            $_.HasPrivateKey -and
            $_.NotAfter -gt (Get-Date) -and
            $null -ne $_.EnhancedKeyUsageList -and
            $_.EnhancedKeyUsageList.ObjectId -contains $script:CodeSigningOid
        } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
}

function Save-InternalSigningCer {
    param(
        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [string]$OutputDirectory
    )

    $directory = Get-CodeSigningOutputDirectory $OutputDirectory
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $cerPath = Get-InternalSigningCerPath -Certificate $Certificate -OutputDirectory $directory
    Export-Certificate -Cert $Certificate -FilePath $cerPath -Force | Out-Null

    # CurrentUser\Root is skipped: Windows often shows a modal confirmation there.
    $trusted = Get-ChildItem Cert:\CurrentUser\TrustedPublisher -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint -eq $Certificate.Thumbprint } |
        Select-Object -First 1
    if ($null -eq $trusted) {
        Import-Certificate -FilePath $cerPath -CertStoreLocation Cert:\CurrentUser\TrustedPublisher | Out-Null
    }

    return $cerPath
}

function New-InternalSigningCertificate {
    param(
        [int]$ValidityYears = 5,
        [string]$OutputDirectory
    )

    $certificate = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $script:CodeSigningSubject `
        -KeyAlgorithm RSA `
        -KeyLength 3072 `
        -HashAlgorithm SHA256 `
        -KeyExportPolicy Exportable `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -NotAfter (Get-Date).AddYears($ValidityYears)

    Save-InternalSigningCer -Certificate $certificate -OutputDirectory $OutputDirectory | Out-Null
    return $certificate
}

function Initialize-InternalSigningCertificate {
    param(
        [int]$ValidityYears = 5,
        [string]$OutputDirectory
    )

    $certificate = Get-InternalSigningCertificate
    if ($null -eq $certificate) {
        Write-Host "Сертификат не найден — создаю новый в Cert:\CurrentUser\My."
        $certificate = New-InternalSigningCertificate `
            -ValidityYears $ValidityYears `
            -OutputDirectory $OutputDirectory
        Write-Host "  thumbprint : $($certificate.Thumbprint)"
        Write-Host "  expires    : $($certificate.NotAfter.ToString('yyyy-MM-dd'))"
        return $certificate
    }

    $remainingDays = [math]::Ceiling(($certificate.NotAfter - (Get-Date)).TotalDays)
    if ($remainingDays -le 90) {
        Write-Warning "Сертификат истекает $($certificate.NotAfter.ToString('yyyy-MM-dd')) (осталось $remainingDays дн.). Продление: .\scripts\setup-internal-code-signing.ps1 -Renew"
    }

    Save-InternalSigningCer -Certificate $certificate -OutputDirectory $OutputDirectory | Out-Null
    return $certificate
}

function Export-InternalSigningPfx {
    param(
        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [Parameter(Mandatory)]
        [securestring]$Password,
        [string]$OutputDirectory
    )

    $directory = Get-CodeSigningOutputDirectory $OutputDirectory
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $pfxPath = Join-Path $directory "TelegramBot-Internal-Code-Signing-$($Certificate.Thumbprint).pfx"
    Export-PfxCertificate `
        -Cert $Certificate `
        -FilePath $pfxPath `
        -Password $Password `
        -ChainOption EndEntityCertOnly `
        -Force | Out-Null
    return $pfxPath
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
        if ($candidate -and (Test-Path $candidate)) {
            return $candidate
        }
    }

    throw "signtool.exe не найден. Установите Windows SDK или ClickOnce Signing Tools."
}

function Find-Iscc {
    $userPrograms = Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "Programs"
    foreach ($candidate in @(
            "C:\Program Files\Inno Setup 7\ISCC.exe",
            "C:\Program Files (x86)\Inno Setup 7\ISCC.exe",
            "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
            "C:\Program Files\Inno Setup 6\ISCC.exe",
            (Join-Path $userPrograms "Inno Setup 7\ISCC.exe"),
            (Join-Path $userPrograms "Inno Setup 6\ISCC.exe")
        )) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    throw "ISCC.exe не найден. Установите Inno Setup 7: https://jrsoftware.org/isdl.php"
}

function Invoke-AuthenticodeSign {
    param(
        [Parameter(Mandatory)]
        [string]$SignToolExe,
        [Parameter(Mandatory)]
        [string]$Thumbprint,
        [Parameter(Mandatory)]
        [string]$FilePath
    )

    # pwsh 7.4+ can treat a failed signtool as a terminating error and skip TSA fallback.
    $PSNativeCommandUseErrorActionPreference = $false

    Write-Host "  sign $FilePath"
    foreach ($url in $script:TimestampUrls) {
        & $SignToolExe sign /sha1 $Thumbprint /fd SHA256 /tr $url /td SHA256 $FilePath
        if ($LASTEXITCODE -eq 0) {
            return
        }
        Write-Warning "Timestamp $url недоступен для $FilePath — следующий сервер."
    }

    Write-Warning "Все timestamp-серверы недоступны; подписываю без timestamp: $FilePath"
    & $SignToolExe sign /sha1 $Thumbprint /fd SHA256 $FilePath
    if ($LASTEXITCODE -ne 0) {
        throw "Подпись не удалась: $FilePath"
    }
}

function Assert-AuthenticodeSigned {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,
        [Parameter(Mandatory)]
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
    # Self-signed: Status часто UnknownError, пока CER не в LocalMachine\Root.
    if ($signature.Status -notin @("Valid", "UnknownError")) {
        throw "Некорректная подпись: $FilePath (Status=$($signature.Status))"
    }

    Write-Host "  OK  $FilePath [$($signature.Status)]" -ForegroundColor Green
}

function Write-CodeSigningTrustHints {
    param(
        [Parameter(Mandatory)]
        [string]$CerPath
    )

    Write-Host ""
    Write-Host "Публичный CER (раздать на рабочие ПК, не коммитить):"
    Write-Host "  $CerPath"
    Write-Host ""
    Write-Host "Один раз — GPO: Computer Configuration → Policies → Windows Settings →"
    Write-Host "  Security Settings → Public Key Policies"
    Write-Host "  → Trusted Root Certification Authorities"
    Write-Host "  → Trusted Publishers"
    Write-Host ""
    Write-Host "Или один тестовый ПК от администратора:"
    Write-Host "  Import-Certificate -FilePath '$CerPath' -CertStoreLocation Cert:\LocalMachine\Root"
    Write-Host "  Import-Certificate -FilePath '$CerPath' -CertStoreLocation Cert:\LocalMachine\TrustedPublisher"
}
