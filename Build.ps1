param(
    [string]$ValheimDir = 'D:\SteamLibrary\steamapps\common\Valheim',
    [string]$BepInExDir = 'D:\ValheimModTools\BepInExPack-5.4.2351\BepInExPack_Valheim\BepInEx'
)
$ErrorActionPreference = 'Stop'
& dotnet build "$PSScriptRoot\ConnectedMining.csproj" -c Release "-p:ValheimDir=$ValheimDir" "-p:BepInExDir=$BepInExDir"
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
$dll = "$PSScriptRoot\bin\Release\net48\ConnectedMining.dll"
& dotnet run --project "$PSScriptRoot\tests\Checks.csproj" -c Release -- $ValheimDir $dll
if ($LASTEXITCODE -ne 0) { throw 'Verification failed.' }
$stage = "$PSScriptRoot\dist\package"
$plugins = "$stage\BepInEx\plugins\ConnectedMining"
New-Item -ItemType Directory -Force $plugins | Out-Null
Copy-Item -LiteralPath $dll -Destination "$plugins\ConnectedMining.dll" -Force
Copy-Item -LiteralPath "$PSScriptRoot\README.md" -Destination "$stage\README.md" -Force
$version = ([xml](Get-Content -LiteralPath "$PSScriptRoot\ConnectedMining.csproj" -Raw)).Project.PropertyGroup.Version
Compress-Archive -Path "$stage\*" -DestinationPath "$PSScriptRoot\dist\ConnectedMining-$version.zip" -Force
Write-Host "Package: $PSScriptRoot\dist\ConnectedMining-$version.zip"
