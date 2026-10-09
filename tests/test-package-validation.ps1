$ErrorActionPreference = 'Stop'
$validatorPath = Join-Path $PSScriptRoot '..\scripts\PackageValidation.ps1'
if (Test-Path -LiteralPath $validatorPath -PathType Leaf) {
    . $validatorPath
}

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ("ProjectLauncher-PackageValidation-" + [guid]::NewGuid().ToString('N'))
try {
    $packageRoot = Join-Path $fixtureRoot 'package'
    $wwwRoot = Join-Path $packageRoot 'wwwroot'
    $null = New-Item -ItemType Directory -Path $wwwRoot -Force
    [IO.File]::WriteAllBytes((Join-Path $packageRoot 'Launcher.App.exe'), [byte[]]@())
    [IO.File]::WriteAllText((Join-Path $wwwRoot 'index.html'), '<!doctype html>', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllBytes((Join-Path $packageRoot 'System.Security.Cryptography.X509Certificates.dll'), [byte[]]@())

    Assert-LauncherPackageContents -OutputPath $packageRoot
    Write-Output 'Clean package accepted.'

    $forbiddenFiles = @(
        'projects.json',
        'projects/demo.json',
        'theme.json',
        'launcher.ps1',
        '.env',
        '.env.local',
        'client_secret.json',
        'release-private-key.json',
        'credentials.json',
        'release.pfx',
        'Launcher.App.Tests.dll',
        '.p4config',
        '.p4tickets',
        'p4tickets',
        'P4TICKETS'
    )
    foreach ($relativePath in $forbiddenFiles) {
        $filePath = Join-Path $packageRoot ($relativePath -replace '/', [IO.Path]::DirectorySeparatorChar)
        $parentPath = Split-Path -Parent $filePath
        $null = New-Item -ItemType Directory -Path $parentPath -Force
        [IO.File]::WriteAllText($filePath, 'forbidden fixture', [Text.UTF8Encoding]::new($false))

        $validationError = $null
        try {
            Assert-LauncherPackageContents -OutputPath $packageRoot
        }
        catch {
            $validationError = $_.Exception
        }
        if ($null -eq $validationError) {
            throw "Package validator accepted forbidden file '$relativePath'."
        }
        if ($validationError.Message -notlike "*$relativePath*") {
            throw "Package validator rejected '$relativePath' for an unexpected reason: $($validationError.Message)"
        }

        Remove-Item -LiteralPath $filePath -Force
        Write-Output "Rejected $relativePath."
    }

    Write-Output 'Package validation tests passed.'
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
}
