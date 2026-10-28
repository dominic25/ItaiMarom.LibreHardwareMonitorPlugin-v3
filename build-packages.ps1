<#
.SYNOPSIS
    Builds the installable Macro Deck plugin packages.

.DESCRIPTION
    Produces .macroDeckPlugin artifacts in dist/ by default:

      * the store package, with the manifest exactly as it ships; and
      * a test package whose id and name carry a suffix, so it can be installed
        alongside the store package instead of replacing it.

    Both packages contain the same source and binary. Only the manifest differs:
    the test package uses a distinct plugin id (and a "(Test)" display name), which
    is what lets Macro Deck keep the two installed at the same time. The compiled
    localization scope is the exception: it is derived from manifest.json at compile
    time, so the test build overrides it to the test id as well - otherwise the host
    rejects the test plugin's string catalog and its UI shows raw keys.

.PARAMETER Variant
    Which package(s) to build: All (default), Store or Test.

.PARAMETER Output
    Directory the artifacts are written to. Defaults to dist/ at the repository root.

.PARAMETER TestIdSuffix
    Appended to the plugin id for the test package. Defaults to .test.

.PARAMETER SkipTests
    Skips the dotnet test run that normally happens before packaging.

.PARAMETER OpenOutput
    Opens the output directory in Explorer when the build finishes.

.PARAMETER DotnetRoot
    Directory containing the .NET 10 SDK, when it is not the dotnet on PATH.

.EXAMPLE
    .\build-packages.ps1
    Runs the test suite, then builds the store and test packages.

.EXAMPLE
    .\build-packages.ps1 -Variant Test -SkipTests
    Builds only the test package, without running the test suite.
#>
[CmdletBinding()]
param(
    [ValidateSet('All', 'Store', 'Test')]
    [string]$Variant = 'All',

    [string]$Output,

    [string]$TestIdSuffix = '.test',

    [switch]$SkipTests,

    [switch]$OpenOutput,

    [string]$DotnetRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$projectDir = [System.IO.Path]::Combine($repoRoot, 'src', 'ItaiMarom.LibreHardwareMonitorPlugin')
$manifestPath = [System.IO.Path]::Combine($projectDir, 'manifest.json')
$buildConfigPath = [System.IO.Path]::Combine($projectDir, 'macrodeck-build.json')

function Resolve-Dotnet {
    param([string]$Root)

    $candidates = @()
    if ($Root) { $candidates += $Root }
    if ($env:DOTNET_ROOT) { $candidates += $env:DOTNET_ROOT }
    $candidates += [System.IO.Path]::Combine($env:LOCALAPPDATA, 'dotnet10')
    $candidates += 'C:\Program Files\dotnet'

    foreach ($candidate in $candidates) {
        $exe = [System.IO.Path]::Combine($candidate, 'dotnet.exe')
        if (-not (Test-Path -LiteralPath $exe)) { continue }

        $version = & $exe --version 2>$null
        if ($LASTEXITCODE -eq 0 -and $version -match '^10\.') {
            return $exe
        }
    }

    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) {
        $version = & $onPath.Source --version 2>$null
        if ($LASTEXITCODE -eq 0 -and $version -match '^10\.') {
            return $onPath.Source
        }
    }

    throw 'The .NET 10 SDK was not found. Install it, or pass -DotnetRoot <directory>.'
}

function Resolve-Cli {
    $onPath = Get-Command macrodeck-plugin -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $candidate = [System.IO.Path]::Combine($env:USERPROFILE, '.dotnet', 'tools', 'macrodeck-plugin.exe')
    if (Test-Path -LiteralPath $candidate) { return $candidate }

    throw 'macrodeck-plugin was not found. Install it with: dotnet tool install --global MacroDeck.Plugin.Cli --prerelease'
}

function New-TestManifest {
    param(
        [Parameter(Mandatory)] [string]$Source,
        [Parameter(Mandatory)] [string]$Destination,
        [Parameter(Mandatory)] [string]$IdSuffix
    )

    if ([string]::IsNullOrWhiteSpace($IdSuffix)) {
        throw 'The test package needs its own plugin id; -TestIdSuffix must not be empty.'
    }

    $text = Get-Content -LiteralPath $Source -Raw
    $manifest = $text | ConvertFrom-Json

    $testId = "$($manifest.id)$IdSuffix"
    $testName = "$($manifest.name) (Test)"

    $idLine = '"id": "' + $manifest.id + '"'
    $nameLine = '"name": "' + $manifest.name + '"'

    if (-not $text.Contains($idLine) -or -not $text.Contains($nameLine)) {
        throw 'manifest.json is not in the expected shape; cannot derive the test manifest.'
    }

    $text = $text.Replace($idLine, '"id": "' + $testId + '"')
    $text = $text.Replace($nameLine, '"name": "' + $testName + '"')

    $patched = $text | ConvertFrom-Json
    if ($patched.id -ne $testId -or $patched.name -ne $testName) {
        throw 'The test manifest did not take the test id and name.'
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
    [System.IO.File]::WriteAllText($Destination, $text, [System.Text.UTF8Encoding]::new($false))

    return $patched
}

function Invoke-PackageBuild {
    param(
        [Parameter(Mandatory)] [string]$Label,
        [Parameter(Mandatory)] [string]$OutputDirectory,
        [string]$Manifest,
        [string]$LocalizationScope,
        [switch]$TestBuild
    )

    $arguments = @(
        'build',
        '--source', $projectDir,
        '--build-config', $buildConfigPath,
        '--output', $OutputDirectory,
        '--force',
        '--no-color'
    )

    if ($Manifest) {
        $arguments += @('--manifest', $Manifest)
    }

    Write-Host "Building the $Label package..." -ForegroundColor Cyan

    # The localization analyzer takes its scope from the MSBuild property of this name, which defaults
    # to the id in the project's own manifest.json. That file is the store one even when --manifest
    # points the package at the test manifest, so the test build has to be told its scope explicitly.
    # PluginTestBuild turns on the variable-name namespace the test package needs to coexist with the
    # store one. Both are MSBuild properties, and an environment variable is how this script reaches
    # the compiler without the CLI having to know about either.
    $previousScope = $env:MacroDeckLocalizationScope
    $previousTestBuild = $env:PluginTestBuild

    if ($LocalizationScope) {
        $env:MacroDeckLocalizationScope = $LocalizationScope
    }
    else {
        Remove-Item -LiteralPath 'Env:MacroDeckLocalizationScope' -ErrorAction SilentlyContinue
    }

    if ($TestBuild) {
        $env:PluginTestBuild = 'true'
    }
    else {
        Remove-Item -LiteralPath 'Env:PluginTestBuild' -ErrorAction SilentlyContinue
    }

    Push-Location $projectDir
    try {
        & $cli @arguments
        if ($LASTEXITCODE -ne 0) {
            throw "macrodeck-plugin build failed for the $Label package (exit code $LASTEXITCODE)."
        }
    }
    finally {
        Pop-Location
        if ($null -eq $previousScope) {
            Remove-Item -LiteralPath 'Env:MacroDeckLocalizationScope' -ErrorAction SilentlyContinue
        }
        else {
            $env:MacroDeckLocalizationScope = $previousScope
        }

        if ($null -eq $previousTestBuild) {
            Remove-Item -LiteralPath 'Env:PluginTestBuild' -ErrorAction SilentlyContinue
        }
        else {
            $env:PluginTestBuild = $previousTestBuild
        }
    }
}

function Read-PackageEntryBytes {
    param(
        [Parameter(Mandatory)] [string]$PackagePath,
        [Parameter(Mandatory)] [string]$Suffix
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entry = $archive.Entries |
            Where-Object { $_.FullName.EndsWith($Suffix, [System.StringComparison]::Ordinal) } |
            Select-Object -First 1

        if (-not $entry) {
            throw "The package at $PackagePath has no entry ending in $Suffix."
        }

        $stream = $entry.Open()
        try {
            $buffer = [System.IO.MemoryStream]::new()
            $stream.CopyTo($buffer)
            return $buffer.ToArray()
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-PackageLocalizationScope {
    param(
        [Parameter(Mandatory)] [string]$PackagePath,
        [Parameter(Mandatory)] [string]$Scope,
        [string]$ForbiddenScope
    )

    # String literals live in the assembly's metadata as UTF-16, so the scope has to be looked for
    # there rather than in the UTF-8 resource blob.
    $bytes = Read-PackageEntryBytes -PackagePath $PackagePath -Suffix 'ItaiMarom.LibreHardwareMonitorPlugin.dll'
    $text = [System.Text.Encoding]::Unicode.GetString($bytes)

    if (-not $text.Contains($Scope)) {
        throw "The package at $PackagePath does not carry the localization scope $Scope. Run the build again after deleting src/ItaiMarom.LibreHardwareMonitorPlugin/obj."
    }

    if ($ForbiddenScope -and $text.Contains($ForbiddenScope)) {
        throw "The package at $PackagePath carries the localization scope $ForbiddenScope as well as $Scope."
    }
}

function Read-PackageManifest {
    param([Parameter(Mandatory)] [string]$Path)

    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $archive.GetEntry('manifest.json')
        if (-not $entry) {
            throw "The package at $Path has no manifest.json."
        }

        $reader = [System.IO.StreamReader]::new($entry.Open())
        try {
            return ($reader.ReadToEnd() | ConvertFrom-Json)
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

try {
    $dotnetExe = Resolve-Dotnet -Root $DotnetRoot
    $dotnetDirectory = Split-Path -Parent $dotnetExe
    $env:PATH = "$dotnetDirectory;$env:PATH"
    $env:DOTNET_ROOT = $dotnetDirectory

    $cli = Resolve-Cli

    if (-not $Output) {
        $Output = [System.IO.Path]::Combine($repoRoot, 'dist')
    }
    $Output = [System.IO.Path]::GetFullPath($Output)
    New-Item -ItemType Directory -Force -Path $Output | Out-Null

    if (-not $SkipTests) {
        Write-Host 'Running the test suite...' -ForegroundColor Cyan
        Push-Location $repoRoot
        try {
            & $dotnetExe test --nologo
            if ($LASTEXITCODE -ne 0) {
                throw "dotnet test failed (exit code $LASTEXITCODE)."
            }
        }
        finally {
            Pop-Location
        }
    }

    $storeManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $packages = [System.Collections.Generic.List[object]]::new()

    if ($Variant -in 'All', 'Store') {
        Invoke-PackageBuild -Label 'store' -OutputDirectory $Output

        $storeArtifact = [System.IO.Path]::Combine(
            $Output,
            "$($storeManifest.id)-$($storeManifest.version).macroDeckPlugin")

        if (-not (Test-Path -LiteralPath $storeArtifact)) {
            throw "The store package was not written to $storeArtifact."
        }

        $packaged = Read-PackageManifest -Path $storeArtifact
        if ($packaged.id -ne $storeManifest.id -or $packaged.version -ne $storeManifest.version) {
            throw 'The store package does not carry the source manifest id and version.'
        }

        Assert-PackageLocalizationScope `
            -PackagePath $storeArtifact `
            -Scope "plugin:$($storeManifest.id)" `
            -ForbiddenScope "plugin:$($storeManifest.id)$TestIdSuffix"

        $packages.Add([pscustomobject]@{
            Variant  = 'Store'
            Id       = $packaged.id
            Version  = $packaged.version
            Artifact = $storeArtifact
        })
    }

    if ($Variant -in 'All', 'Test') {
        $tempDirectory = [System.IO.Path]::Combine(
            [System.IO.Path]::GetTempPath(),
            'lhm-test-manifest-' + [guid]::NewGuid().ToString('N'))
        $testManifestPath = [System.IO.Path]::Combine($tempDirectory, 'manifest.json')

        try {
            $testManifest = New-TestManifest `
                -Source $manifestPath `
                -Destination $testManifestPath `
                -IdSuffix $TestIdSuffix

            Invoke-PackageBuild `
                -Label 'test' `
                -Manifest $testManifestPath `
                -LocalizationScope "plugin:$($testManifest.id)" `
                -TestBuild `
                -OutputDirectory $Output

            $testArtifact = [System.IO.Path]::Combine(
                $Output,
                "$($testManifest.id)-$($testManifest.version).macroDeckPlugin")

            if (-not (Test-Path -LiteralPath $testArtifact)) {
                throw "The test package was not written to $testArtifact."
            }

            $packaged = Read-PackageManifest -Path $testArtifact
            if ($packaged.id -ne $testManifest.id -or $packaged.name -ne $testManifest.name) {
                throw 'The test package does not carry the test id and name.'
            }

            Assert-PackageLocalizationScope `
                -PackagePath $testArtifact `
                -Scope "plugin:$($testManifest.id)"

            $packages.Add([pscustomobject]@{
                Variant  = 'Test'
                Id       = $packaged.id
                Version  = $packaged.version
                Artifact = $testArtifact
            })
        }
        finally {
            Remove-Item -LiteralPath $tempDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Write-Host ''
    Write-Host 'Packages:' -ForegroundColor Green
    foreach ($package in $packages) {
        Write-Host ("  {0,-5} {1} {2}" -f $package.Variant, $package.Id, $package.Version)
        Write-Host ("        {0}" -f $package.Artifact)
    }

    if ($packages.Count -eq 2) {
        Write-Host 'The store and test packages use different plugin ids, so both can be installed at the same time.' -ForegroundColor DarkGray
    }

    Write-Host 'Install an artifact by opening it with Macro Deck (double-click), or from the plugin manager.' -ForegroundColor DarkGray

    if ($OpenOutput) {
        Start-Process -FilePath $Output
    }
}
catch {
    Write-Host "error: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
