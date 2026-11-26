[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $root ('artifacts\run-script-tests space-' + [Guid]::NewGuid().ToString('N'))
$originalLocalAppData = $env:LOCALAPPDATA
$originalExitCode = $global:LASTEXITCODE
$passed = 0
$state = $null

function Assert([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}
function Get-Command {
    [CmdletBinding()]
    param([Parameter(Position = 0)] [string] $Name)
    if ($Name -eq 'dotnet') {
        if ($state.MissingSdk) { return $null }
        return [pscustomobject]@{ Source = 'dotnet' }
    }
    if ($Name -like '*\vswhere.exe') {
        if ($state.MissingCpp) { return $null }
        return [pscustomobject]@{ Source = 'Get-TestVisualStudio' }
    }
    Microsoft.PowerShell.Core\Get-Command -Name $Name
}
function Get-TestVisualStudio {
    $global:LASTEXITCODE = 0
    return 'C:\Fixture\VisualStudio'
}
function Get-ItemProperty {
    [CmdletBinding()]
    param([string] $LiteralPath, [string] $Name)
    if ($LiteralPath -eq 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion') {
        return [pscustomobject]@{ InstallationType = $state.InstallationType }
    }
    if ($LiteralPath -eq 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots') {
        return [pscustomobject]@{ KitsRoot10 = $state.Kits }
    }
    throw "Unexpected registry lookup: $LiteralPath"
}
function Get-AppxPackage {
    [CmdletBinding()]
    param([string] $Name)
    Assert ($Name -eq 'Microsoft.WindowsAppRuntime.2') 'Wrong runtime framework queried.'
    if ($state.Runtime) { return $state.Runtime }
}
function powershell.exe {
    $global:LASTEXITCODE = 0
    ConvertTo-Json -InputObject @($state.Runtime | Where-Object { $null -ne $_ }) -Compress
}
function dotnet {
    $nativeArguments = @($args)
    $state.Calls.Add($nativeArguments)
    if ($nativeArguments[0] -eq '--version') {
        $global:LASTEXITCODE = $state.SdkExitCode
        return '11.0.100-rc.3.26522.148'
    }
    $global:LASTEXITCODE = $state.NativeExitCode
    if ($nativeArguments[0] -eq 'publish' -and $state.ProduceArtifact -and $state.NativeExitCode -eq 0) {
        New-Item -ItemType Directory -Path (Split-Path $state.Executable -Parent) -Force | Out-Null
        [IO.File]::WriteAllBytes($state.Executable, [byte[]]@(0))
    }
}
function Start-Process {
    [CmdletBinding()]
    param([string] $FilePath, [string] $WorkingDirectory, [switch] $PassThru)
    $state.Launches.Add([pscustomobject]@{ Path = $FilePath; WorkingDirectory = $WorkingDirectory })
    return [pscustomobject]@{ Id = 123 }
}
function Reset-State {
    if ($state -and (Test-Path -LiteralPath $state.Executable)) { Remove-Item -LiteralPath $state.Executable }
    $script:state = [pscustomobject]@{
        Calls = [Collections.Generic.List[object]]::new()
        Launches = [Collections.Generic.List[object]]::new()
        MissingSdk = $false
        InstallationType = 'Client'
        MissingCpp = $false
        SdkExitCode = 0
        NativeExitCode = 0
        ProduceArtifact = $true
        Executable = Join-Path $fixture 'artifacts\publish\CameraLightTray.App\release_win-x64\CameraLightTray.exe'
        Kits = Join-Path $fixture 'kits'
        Runtime = [pscustomobject]@{
            Name = 'Microsoft.WindowsAppRuntime.2'; Version = '2.5.1.0'; Architecture = 'X64'; IsFramework = $true
            Publisher = 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US'
        }
    }
}
function Expect-Failure([scriptblock] $Action, [string] $Pattern) {
    try { & $Action } catch {
        Assert ($_.Exception.Message -like $Pattern) "Unexpected failure: $($_.Exception.Message)"
        return
    }
    throw "Expected failure: $Pattern"
}
function Test([string] $Name, [scriptblock] $Action) {
    Reset-State
    $cwd = (Get-Location).Path
    & $Action
    Assert ((Get-Location).Path -eq $cwd) 'run.ps1 did not restore the caller directory.'
    $script:passed++
    Write-Output "PASS $Name"
}

try {
    New-Item -ItemType Directory -Path (Join-Path $fixture 'src\CameraLightTray.App') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'run.ps1'), (Join-Path $root 'global.json'), (Join-Path $root 'Directory.Build.props') -Destination $fixture
    Copy-Item -LiteralPath (Join-Path $root 'src\CameraLightTray.App\CameraLightTray.App.csproj') -Destination (Join-Path $fixture 'src\CameraLightTray.App')
    $env:LOCALAPPDATA = Join-Path $fixture 'local-data'
    foreach ($relative in @('kits\Include\10.0.26100.0\um\Windows.h', 'kits\Lib\10.0.26100.0\um\x64\kernel32.lib', 'kits\Lib\10.0.26100.0\ucrt\x64\ucrt.lib')) {
        $file = Join-Path $fixture $relative
        New-Item -ItemType Directory -Path (Split-Path $file -Parent) -Force | Out-Null
        [IO.File]::WriteAllBytes($file, [byte[]]@(0))
    }
    $script = Join-Path $fixture 'run.ps1'
    $project = Join-Path $fixture 'src\CameraLightTray.App\CameraLightTray.App.csproj'
    Test 'default mode invokes Release dotnet run from the repository root' {
        & $script
        Assert ($state.Calls.Count -eq 2) 'Expected SDK check and dotnet run only.'
        Assert (($state.Calls[1] -join '|') -eq "run|--project|$project|-c|Release") 'Incorrect build/run invocation.'
        Assert ($state.Launches.Count -eq 0) 'Development mode bypassed dotnet run.'
    }
    Test 'unsupported Windows Server fails before building or launching' {
        $state.InstallationType = 'Server'
        Expect-Failure { & $script } '*Windows Server is not supported*'
        Assert ($state.Calls.Count -eq 0 -and $state.Launches.Count -eq 0) 'Unsupported OS launched the app.'
    }
    Test '-NoBuild passes --no-build without a build, publish, or restore operation' {
        & $script -NoBuild
        Assert (($state.Calls[1] -join '|') -eq "run|--project|$project|-c|Release|--no-build") 'NoBuild did not pass through.'
        Assert ($state.Calls.Count -eq 2) 'NoBuild performed extra dotnet work.'
    }
    Test '-Published uses the SDK artifacts layout without overriding its output path' {
        & $script -Published
        $output = Split-Path $state.Executable -Parent
        Assert (($state.Calls[1] -join '|') -eq "publish|$project|-c|Release|-r|win-x64|--nologo") 'Publish must use the SDK artifacts layout without -o.'
        Assert ($state.Launches.Count -eq 1 -and $state.Launches[0].Path -eq $state.Executable -and
            $state.Launches[0].WorkingDirectory -eq $output) 'Published executable was not launched from its output folder.'
    }
    Test '-Published -NoBuild reuses the artifact without requiring the .NET SDK or native tools' {
        New-Item -ItemType Directory -Path (Split-Path $state.Executable -Parent) -Force | Out-Null
        [IO.File]::WriteAllBytes($state.Executable, [byte[]]@(0))
        $state.MissingSdk = $state.MissingCpp = $true
        & $script -Published -NoBuild
        Assert ($state.Calls.Count -eq 0 -and $state.Launches.Count -eq 1) 'Published NoBuild invoked dotnet or failed to launch.'
    }
    Test 'missing published artifact fails without building or launching' {
        Expect-Failure { & $script -Published -NoBuild } '*Published app not found*'
        Assert ($state.Calls.Count -eq 0 -and $state.Launches.Count -eq 0) 'Missing artifact caused implicit work.'
    }
    Test 'missing development build preserves dotnet run failure' {
        $state.NativeExitCode = 17
        Expect-Failure { & $script -NoBuild } '*dotnet run failed (exit code 17)*'
        Assert ($state.Calls[1] -contains '--no-build') 'Missing build retried with building enabled.'
    }
    Test 'publish failure never falls back to a stale executable' {
        New-Item -ItemType Directory -Path (Split-Path $state.Executable -Parent) -Force | Out-Null
        [IO.File]::WriteAllBytes($state.Executable, [byte[]]@(0))
        $state.NativeExitCode = 18
        Expect-Failure { & $script -Published } '*Native AOT publish failed (exit code 18)*'
        Assert ($state.Launches.Count -eq 0) 'Failed publish launched a stale build.'
    }
    Test 'missing SDK and incompatible global.json SDK fail before launching' {
        $state.MissingSdk = $true
        Expect-Failure { & $script } '*The .NET SDK is missing*'
        $state.MissingSdk = $false; $state.SdkExitCode = 19
        Expect-Failure { & $script } '*No compatible .NET SDK*'
        Assert ($state.Launches.Count -eq 0) 'Missing SDK launched the app.'
    }
    Test 'missing, old, wrong-architecture and unsigned runtime registrations are rejected' {
        foreach ($field in @('missing', 'Version', 'Architecture', 'Publisher')) {
            Reset-State
            switch ($field) {
                missing { $state.Runtime = $null }
                Version { $state.Runtime.Version = '2.5.0.0' }
                Architecture { $state.Runtime.Architecture = 'Arm64' }
                Publisher { $state.Runtime.Publisher = 'Untrusted fixture' }
            }
            Expect-Failure { & $script } '*Install the Microsoft Windows App Runtime*'
            Assert ($state.Calls.Count -eq 1 -and $state.Launches.Count -eq 0) 'Runtime prerequisite failure built or launched the app.'
        }
    }
    Test 'missing native tools fail publishing but do not affect dotnet run' {
        $state.MissingCpp = $true
        Expect-Failure { & $script -Published } '*Native AOT publishing requires Visual Studio C++*'
        & $script
        Assert ($state.Calls[-1][0] -eq 'run') 'Development mode unnecessarily required C++ tools.'
    }
    Test 'successful publish with no executable is not treated as a launch success' {
        $state.ProduceArtifact = $false
        Expect-Failure { & $script -Published } '*Publish did not produce*'
        Assert ($state.Launches.Count -eq 0) 'Missing publish output was launched.'
    }
    Test 'an existing default-storage instance is detected by its actual exclusive lock' {
        $lock = Join-Path $env:LOCALAPPDATA 'CameraLightTray\instance.lock'
        New-Item -ItemType Directory -Path (Split-Path $lock -Parent) -Force | Out-Null
        $held = [IO.File]::Open($lock, [IO.FileMode]::Create, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        try {
            & $script -Published
            Assert ($state.Calls.Count -eq 0 -and $state.Launches.Count -eq 0) 'Already-running check built or launched another instance.'
        }
        finally { $held.Dispose() }
        & $script -NoBuild
        Assert ($state.Calls.Count -eq 2) 'An unlocked stale lock file blocked launching.'
    }
    Write-Output "$passed run-script tests passed. Commands were stubbed; no real app launch, installation or light writes."
}
finally {
    $env:LOCALAPPDATA = $originalLocalAppData
    $global:LASTEXITCODE = $originalExitCode
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse }
}
