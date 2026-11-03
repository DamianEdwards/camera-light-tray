[CmdletBinding()]
param([switch] $NativeTests)
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    dotnet build CameraLightTray.slnx -c Release --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    dotnet run --project tests\CameraLightTray.Tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic tests failed.' }
    foreach ($script in Get-ChildItem tools -Filter '*.ps1') {
        $errors = $null
        [Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$null, [ref]$errors) | Out-Null
        if ($errors.Count) { throw "$($script.Name): $errors" }
    }
    if ($NativeTests) {
        dotnet publish tests\CameraLightTray.Tests -c Release -r win-x64 -p:PublishAot=true -o artifacts\native-tests --nologo -v:q
        if ($LASTEXITCODE -ne 0) { throw 'Native test publish failed.' }
        & .\artifacts\native-tests\CameraLightTray.Tests.exe
        if ($LASTEXITCODE -ne 0) { throw 'Native tests failed.' }
    }
}
finally { Pop-Location }
