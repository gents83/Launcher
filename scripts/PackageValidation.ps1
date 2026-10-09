function Assert-LauncherPackageContents {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$OutputPath
    )

    if ([string]::IsNullOrWhiteSpace($OutputPath)) {
        throw 'A published package path is required.'
    }

    $packageRoot = [IO.Path]::GetFullPath($OutputPath)
    if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) {
        throw "Published app directory does not exist: $packageRoot"
    }

    foreach ($requiredRelativePath in @('Launcher.App.exe', 'wwwroot\index.html')) {
        $requiredPath = Join-Path $packageRoot $requiredRelativePath
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Required published app file is missing: $requiredRelativePath"
        }
    }

    $rootPrefix = $packageRoot.TrimEnd('\', '/')
    $forbiddenPatterns = @(
        '(^|/)projects\.json$',
        '(^|/)projects/.+\.json$',
        '(^|/)theme\.json$',
        '(^|/)(launcher(?:\.core)?|start-launcher)\.(?:ps1|cmd|bat)$',
        '\.(?:ps1|psm1|psd1|cmd|bat|cs|csproj|sln)$',
        '(^|/)tests?(?:/|$)',
        '(^|/)[^/]*\.Tests?\.(?:dll|exe|pdb)$',
        '(^|/)(?:Launcher\.App\.Tests|Launcher\.Core\.Tests|Launcher\.ProcessFixture)(?:\.|/|$)',
        '(^|/)(?:\.p4config|\.p4ignore|\.p4tickets|p4config(?:\.txt)?|p4ignore(?:\.txt)?|p4tickets|P4TICKETS|P4CONFIG|P4IGNORE)$',
        '(^|/)\.p4(?:/|$)',
        '(^|/)\.env[^/]*$',
        '(^|/)[^/]*(?:secret|credential|token|private[._-]?key)[^/]*$',
        '\.(?:pfx|p12|p7b|p7c|pem|key|snk|crt|cer|der|pvk|ppk)$'
    )

    foreach ($file in Get-ChildItem -LiteralPath $packageRoot -File -Recurse -Force -ErrorAction Stop) {
        $relativePath = $file.FullName.Substring($rootPrefix.Length).TrimStart('\', '/') -replace '\\', '/'
        foreach ($pattern in $forbiddenPatterns) {
            if ($relativePath -match $pattern) {
                throw "Forbidden file in launcher package: $relativePath"
            }
        }
    }
}
