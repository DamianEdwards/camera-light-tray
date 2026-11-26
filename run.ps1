<#
.SYNOPSIS
Build and run Camera Light Tray, or reuse an existing build.
.EXAMPLE
.\run.ps1
.EXAMPLE
.\run.ps1 -NoBuild
.EXAMPLE
.\run.ps1 -Published
.EXAMPLE
.\run.ps1 -Published -NoBuild
#>
[CmdletBinding()]
param([switch] $Published, [switch] $NoBuild)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Push-Location $PSScriptRoot
try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or
        [Environment]::OSVersion.Version.Build -lt 22621 -or
        [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') {
        throw 'Camera Light Tray requires Windows 11 x64 (build 22621 or later).'
    }
    $windows = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -Name InstallationType
    if ($windows.InstallationType -ne 'Client') { throw 'Windows Server is not supported by the camera sensor API. Use Windows 11 x64.' }

    $lockPath = Join-Path $env:LOCALAPPDATA 'CameraLightTray\instance.lock'
    if (Test-Path -LiteralPath $lockPath) {
        $instance = $null
        try {
            $instance = [IO.File]::Open($lockPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        }
        catch [IO.IOException] {
            if (($_.Exception.HResult -band 0xffff) -in @(32, 33)) {
                Write-Host 'Camera Light Tray is already running. Use its system tray icon.'
                return
            }
            throw
        }
        finally { if ($instance) { $instance.Dispose() } }
    }

    $projectPath = Join-Path $PSScriptRoot 'src\CameraLightTray.App\CameraLightTray.App.csproj'
    $publishedPath = Join-Path $PSScriptRoot 'artifacts\publish\CameraLightTray.App\release_win-x64'
    $executablePath = Join-Path $publishedPath 'CameraLightTray.exe'
    if ($Published -and $NoBuild -and -not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
        throw "Published app not found: $executablePath. Run .\run.ps1 -Published first."
    }

    if (-not ($Published -and $NoBuild)) {
        if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
            throw 'The .NET SDK is missing. Install the SDK specified in global.json.'
        }
        $requiredSdk = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
        $sdk = & dotnet --version
        if ($LASTEXITCODE -ne 0) { throw "No compatible .NET SDK was found. Install SDK $requiredSdk as specified in global.json." }
        Write-Host "Using .NET SDK $sdk."
    }

    [xml] $project = Get-Content -LiteralPath $projectPath -Raw
    $runtimeVersion = @($project.Project.ItemGroup.PackageReference |
        Where-Object Include -eq 'Microsoft.WindowsAppSDK.Runtime')[0].Version
    $runtimeName = "Microsoft.WindowsAppRuntime.$(([version]$runtimeVersion).Major)"
    if ($PSEdition -eq 'Desktop') {
        $runtimes = @(Get-AppxPackage -Name $runtimeName -ErrorAction Stop)
    }
    else {
        # The Appx module requires Windows PowerShell, even when this script runs in pwsh.
        $query = @"
`$ErrorActionPreference = 'Stop'
try {
    ConvertTo-Json -InputObject @(Get-AppxPackage -Name '$runtimeName' | Select-Object Name, Version, @{Name='Architecture';Expression={`$_.Architecture.ToString()}}, Publisher, IsFramework) -Compress
} catch { [Console]::Error.WriteLine(`$_.Exception.Message); exit 1 }
"@
        $json = & powershell.exe -NoProfile -NonInteractive -Command $query
        if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the shared Windows App Runtime registration for this user.' }
        $runtimes = @($json | ConvertFrom-Json)
    }
    $compatible = @($runtimes | Where-Object {
        $_.Name -eq $runtimeName -and "$($_.Architecture)" -eq 'X64' -and $_.IsFramework -and
        $_.Publisher -eq 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US' -and
        [version]$_.Version -ge [version]$runtimeVersion
    })
    if (-not $compatible.Count) {
        throw "Install the Microsoft Windows App Runtime $runtimeVersion or later (compatible $(([version]$runtimeVersion).Major).x, x64) for this user: https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads. No camera-monitor service is required."
    }

    if ($Published) {
        if (-not $NoBuild) {
            $vswhere = Get-Command (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe') -ErrorAction SilentlyContinue
            if (-not $vswhere) {
                throw 'Native AOT publishing requires Visual Studio C++ x64 build tools. Install the Desktop development with C++ workload.'
            }
            $visualStudio = & $vswhere.Source -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
            if ($LASTEXITCODE -ne 0 -or -not $visualStudio) {
                throw 'Native AOT publishing requires the Visual Studio Desktop development with C++ workload (x64 tools).'
            }
            $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
            $sdkRoot = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots' -ErrorAction SilentlyContinue
            if ($sdkRoot.KitsRoot10) { $kits = $sdkRoot.KitsRoot10 }
            $windowsSdk = @(Get-ChildItem -LiteralPath (Join-Path $kits 'Include') -Directory -ErrorAction SilentlyContinue |
                Where-Object {
                    $_.Name -match '^\d+\.\d+\.\d+\.\d+$' -and [version]$_.Name -ge [version]'10.0.26100.0' -and
                    (Test-Path -LiteralPath (Join-Path $_.FullName 'um\Windows.h')) -and
                    (Test-Path -LiteralPath (Join-Path $kits "Lib\$($_.Name)\um\x64\kernel32.lib")) -and
                    (Test-Path -LiteralPath (Join-Path $kits "Lib\$($_.Name)\ucrt\x64\ucrt.lib"))
                })
            if (-not $windowsSdk.Count) { throw 'Native AOT publishing requires Windows SDK 10.0.26100.0 or later with x64 libraries.' }
            & dotnet publish $projectPath -c Release -r win-x64 --nologo
            if ($LASTEXITCODE -ne 0) { throw "Native AOT publish failed (exit code $LASTEXITCODE). The app was not started." }
        }
        if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
            throw "Publish did not produce $executablePath. The app was not started."
        }
        $process = Start-Process -FilePath $executablePath -WorkingDirectory $publishedPath -PassThru
        Write-Host "Started published Camera Light Tray (PID $($process.Id))."
    }
    else {
        $arguments = @('run', '--project', $projectPath, '-c', 'Release')
        if ($NoBuild) { $arguments += '--no-build' }
        & dotnet @arguments
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet run failed (exit code $LASTEXITCODE). For a missing build, run .\run.ps1 without -NoBuild first."
        }
    }
}
finally { Pop-Location }
