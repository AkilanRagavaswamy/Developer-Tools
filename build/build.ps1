<#
.SYNOPSIS
    Build, test, package and run DevTools.

.DESCRIPTION
    One entry point for every build task, so CI and a developer's machine do the same thing.

.PARAMETER Task
    restore   Restore NuGet packages for the whole solution.
    build     Build the solution (default).
    test      Run the DevTools.Core unit tests.
    package   Produce an installable MSIX under src\DevTools.App\AppPackages.
    run       Build, register the package for development, and launch the app.
    clean     Delete every bin and obj folder.
    all       restore, build, test, package.

.PARAMETER Configuration
    Debug or Release. Defaults to Debug, except for 'package' and 'all' which default to Release.

.PARAMETER Platform
    x64 or ARM64. Defaults to the architecture of the machine you are on.

.EXAMPLE
    .\build\build.ps1 -Task all
.EXAMPLE
    .\build\build.ps1 -Task package -Configuration Release -Platform ARM64
#>
[CmdletBinding()]
param(
    [ValidateSet('restore', 'build', 'test', 'package', 'run', 'clean', 'verify', 'all')]
    [string]$Task = 'build',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration,

    [ValidateSet('x64', 'ARM64')]
    [string]$Platform
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'DevTools.sln'
$coreProject = Join-Path $root 'src\DevTools.Core\DevTools.Core.csproj'
$appProject = Join-Path $root 'src\DevTools.App\DevTools.App.csproj'
$httpProject = Join-Path $root 'src\DevTools.Http\DevTools.Http.csproj'
$testProjects = @('DevTools.Core.Tests', 'DevTools.Http.Tests')

if (-not $Configuration) {
    $Configuration = if ($Task -in @('package', 'all')) { 'Release' } else { 'Debug' }
}

if (-not $Platform) {
    $Platform = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'ARM64' } else { 'x64' }
}

$rid = if ($Platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }

function Write-Step($message) {
    Write-Host ''
    Write-Host "==> $message" -ForegroundColor Cyan
}

function Invoke-Checked($file, [string[]]$arguments) {
    Write-Host "    $file $($arguments -join ' ')" -ForegroundColor DarkGray
    & $file @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE."
    }
}

function Test-Prerequisites {
    Write-Step 'Checking prerequisites'

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        throw 'The .NET SDK was not found on PATH. Install .NET 10 or newer from https://dotnet.microsoft.com/download.'
    }

    $sdkVersion = (& dotnet --version).Trim()
    Write-Host "    .NET SDK $sdkVersion"

    $major = [int]($sdkVersion.Split('.')[0])
    if ($major -lt 10) {
        throw "DevTools needs the .NET 10 SDK or newer; found $sdkVersion."
    }

    $runtime = Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.2' -ErrorAction SilentlyContinue
    if ($runtime) {
        Write-Host "    Windows App Runtime $($runtime[0].Version)"
    }
    else {
        Write-Host '    Windows App Runtime 2.x was not found. A packaged install carries its own dependency, but running from bin needs it.' -ForegroundColor Yellow
    }
}

function Invoke-Restore {
    Write-Step "Restoring packages ($Platform)"
    Invoke-Checked 'dotnet' @('restore', $solution, "-p:Platform=$Platform")
}

function Invoke-Build {
    Write-Step "Building Core ($Configuration)"
    Invoke-Checked 'dotnet' @('build', $coreProject, '-c', $Configuration, '--nologo')

    Write-Step "Building Http ($Configuration)"
    Invoke-Checked 'dotnet' @('build', $httpProject, '-c', $Configuration, '--nologo')

    Write-Step "Building App ($Configuration | $Platform)"
    Invoke-Checked 'dotnet' @('build', $appProject, '-c', $Configuration, "-p:Platform=$Platform", '--nologo')
}

function Invoke-Test {
    # xUnit v3 test projects are self-executing Microsoft.Testing.Platform hosts. The .NET 10 SDK
    # removed the VSTest bridge `dotnet test` used to go through, so the built host is run
    # directly — which is the supported path and avoids depending on an SDK opt-in flag.
    foreach ($name in $testProjects) {
        Write-Step "Running $name ($Configuration)"

        $project = Join-Path $root "tests\$name\$name.csproj"
        Invoke-Checked 'dotnet' @('build', $project, '-c', $Configuration, '--nologo')

        # DevTools.Http is Windows-targeted, so its tests are too; Core stays portable.
        $tfm = if ($name -eq 'DevTools.Http.Tests') { 'net10.0-windows' } else { 'net10.0' }
        $testHost = Join-Path $root "tests\$name\bin\$Configuration\$tfm\$name.exe"
        if (-not (Test-Path $testHost)) {
            throw "The test host was not produced at $testHost."
        }

        & $testHost
        if ($LASTEXITCODE -ne 0) {
            throw "$name failed with exit code $LASTEXITCODE."
        }
    }
}

function Invoke-Verify {
    # NFR-05 and NFR-06 made checkable rather than asserted: the pure engines must contain no
    # network type at all, and nothing anywhere may phone home.
    Write-Step 'Verifying the privacy boundary'

    $networkPattern = 'HttpClient|WebRequest|WebClient|TcpClient|UdpClient|\bSocket\b|Dns\.'
    $coreHits = Get-ChildItem -Path (Join-Path $root 'src\DevTools.Core') -Filter '*.cs' -Recurse |
        Select-String -Pattern $networkPattern

    if ($coreHits) {
        $coreHits | ForEach-Object { Write-Host "  $($_.Path):$($_.LineNumber)  $($_.Line.Trim())" }
        throw 'DevTools.Core must contain no network code (NFR-05).'
    }

    $telemetryHits = Get-ChildItem -Path (Join-Path $root 'src') -Filter '*.cs' -Recurse |
        Select-String -Pattern 'AppCenter|ApplicationInsights|TelemetryClient|GoogleAnalytics'

    if ($telemetryHits) {
        $telemetryHits | ForEach-Object { Write-Host "  $($_.Path):$($_.LineNumber)  $($_.Line.Trim())" }
        throw 'DevTools sends no telemetry (NFR-06).'
    }

    # The app changes nothing outside its own storage: no machine-wide proxy setting and no
    # trusted root certificates, anywhere. Checked here so the claim is enforced, not asserted.
    $intrusive = Get-ChildItem -Path (Join-Path $root 'src') -Filter '*.cs' -Recurse |
        Where-Object { $_.FullName -notlike '*\obj\*' -and $_.FullName -notlike '*\bin\*' } |
        Select-String -Pattern 'X509Store|Internet Settings|InternetSetOption'

    if ($intrusive) {
        $intrusive | ForEach-Object { Write-Host "  $($_.Path):$($_.LineNumber)  $($_.Line.Trim())" }
        throw 'ForgeKitRk must not change certificate stores or proxy settings.'
    }

    Write-Host '  No machine changes: no certificate stores, no proxy settings.' -ForegroundColor Green
    Write-Host '  Core has no network types; nothing phones home.' -ForegroundColor Green
}

function Invoke-Package {
    Write-Step "Packaging MSIX ($Configuration | $Platform)"
    Invoke-Checked 'dotnet' @(
        'build', $appProject,
        '-c', $Configuration,
        "-p:Platform=$Platform",
        "-p:RuntimeIdentifier=$rid",
        '-p:GenerateAppxPackageOnBuild=true',
        '-p:AppxPackageSigningEnabled=false',
        '-p:UapAppxPackageBuildMode=SideloadOnly',
        '--nologo'
    )

    $packages = Get-ChildItem -Path (Join-Path $root 'src\DevTools.App\AppPackages') -Filter '*.msix' -Recurse -ErrorAction SilentlyContinue
    if ($packages) {
        Write-Host ''
        Write-Host 'Package created:' -ForegroundColor Green
        $packages | ForEach-Object { Write-Host "    $($_.FullName)  ($([math]::Round($_.Length / 1MB, 2)) MB)" }
        Write-Host ''
        Write-Host 'The package is unsigned. To install it, either enable Developer Mode and use the'
        Write-Host '"run" task, or sign it with a certificate your machine trusts.'
    }
    else {
        throw 'The build reported success but produced no .msix.'
    }
}

function Invoke-Run {
    # A running copy holds DevTools.exe open and the build fails on the copy, not on anything
    # wrong with the code. Closing it first is what makes `run` repeatable.
    $running = Get-Process -Name 'DevTools' -ErrorAction SilentlyContinue

    if ($running) {
        Write-Step 'Closing the running app'
        $running | Stop-Process -Force
        Start-Sleep -Milliseconds 700
    }

    Invoke-Build

    Write-Step 'Registering the package for development'
    $outputDir = Join-Path $root "src\DevTools.App\bin\$Platform\$Configuration\net10.0-windows10.0.26100.0"
    $manifest = Join-Path $outputDir 'AppxManifest.xml'

    if (-not (Test-Path $manifest)) {
        throw "No AppxManifest.xml at $manifest. Build first."
    }

    $identity = ([xml](Get-Content $manifest)).Package.Identity.Name

    # A packaging run leaves a staged copy of the whole app in an `AppX` subfolder, and a
    # registration can end up pointing at that copy instead of the layout that was just built.
    # Nothing updates it afterwards, so the app silently runs the build from whenever that folder
    # was made — once for two weeks, while every rebuild reported success. It is a packaging
    # artefact that `run` does not need, so it goes.
    $staged = Join-Path $outputDir 'AppX'
    if (Test-Path $staged) {
        Write-Host "    Removing the stale staged layout at $staged"
        Remove-Item $staged -Recurse -Force -ErrorAction SilentlyContinue
    }

    # An existing registration pointing anywhere else is removed rather than updated.
    # Re-registering at the same version does not reliably move it, and registering over one
    # whose folder has gone fails outright with a path-not-found deployment error.
    $existing = Get-AppxPackage -Name $identity

    if ($existing -and $existing.InstallLocation -ne $outputDir) {
        Write-Host "    Registration pointed at $($existing.InstallLocation); removing it"
        Remove-AppxPackage -Package $existing.PackageFullName -ErrorAction SilentlyContinue
    }

    # Registering a loose layout needs Developer Mode; it avoids having to sign the package.
    Add-AppxPackage -Register $manifest -ForceUpdateFromAnyVersion

    $package = Get-AppxPackage -Name $identity

    if (-not $package) {
        throw "The package '$identity' is not registered."
    }

    # Checked rather than assumed: running the wrong binaries looks exactly like a change that
    # did not work, which is the most expensive kind of wrong.
    if ($package.InstallLocation -ne $outputDir) {
        throw "The package is registered at $($package.InstallLocation), not $outputDir."
    }

    $aumid = "$($package.PackageFamilyName)!App"

    Write-Step "Launching $aumid"
    Start-Process "shell:AppsFolder\$aumid"
}

function Invoke-Clean {
    Write-Step 'Cleaning'
    foreach ($directory in @('bin', 'obj')) {
        Get-ChildItem -Path $root -Directory -Recurse -Filter $directory -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\node_modules\\' } |
            ForEach-Object {
                Write-Host "    removing $($_.FullName)" -ForegroundColor DarkGray
                Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
            }
    }

    $appPackages = Join-Path $root 'src\DevTools.App\AppPackages'
    if (Test-Path $appPackages) {
        Remove-Item $appPackages -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$started = Get-Date

switch ($Task) {
    'restore' { Test-Prerequisites; Invoke-Restore }
    'build' { Test-Prerequisites; Invoke-Restore; Invoke-Build }
    'test' { Test-Prerequisites; Invoke-Restore; Invoke-Test }
    'package' { Test-Prerequisites; Invoke-Restore; Invoke-Build; Invoke-Package }
    'run' { Test-Prerequisites; Invoke-Restore; Invoke-Run }
    'clean' { Invoke-Clean }
    'verify' { Invoke-Verify }
    'all' { Test-Prerequisites; Invoke-Restore; Invoke-Build; Invoke-Test; Invoke-Verify; Invoke-Package }
}

Write-Host ''
Write-Host "Done in $([math]::Round(((Get-Date) - $started).TotalSeconds, 1))s." -ForegroundColor Green
