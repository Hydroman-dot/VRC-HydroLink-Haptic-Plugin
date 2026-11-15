$ErrorActionPreference='Stop'
dotnet run --project "$PSScriptRoot/tests/HydroHaptic.Tests.csproj" -c Release
if($LASTEXITCODE -ne 0){throw 'Tests failed'}
dotnet build "$PSScriptRoot/plugins/HydroHaptic/HydroHaptic.csproj" -c Release
if($LASTEXITCODE -ne 0){throw 'Build failed'}
$m=Get-Content "$PSScriptRoot/plugins/HydroHaptic/plugin.json" -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Force "$PSScriptRoot/artifacts" | Out-Null
$zip="$PSScriptRoot/artifacts/$($m.id)-$($m.version).zip"
Compress-Archive -Path "$PSScriptRoot/plugins/HydroHaptic/bin/Release/net8.0-windows/*" -DestinationPath $zip -Force
$hash=(Get-FileHash $zip).Hash.ToLowerInvariant()
Set-Content "$zip.sha256" "$hash  $([IO.Path]::GetFileName($zip))" -Encoding ascii
