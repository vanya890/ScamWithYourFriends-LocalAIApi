<#
.SYNOPSIS
    Собирает мод (LocalAIApiMod.dll) и IL-патчер (LocalAIApiPatcher).

.EXAMPLE
    pwsh -File scripts\build.ps1
    pwsh -File scripts\build.ps1 -GameDir "S:\Games\Scam With Your Friends"
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [string]$Configuration = 'Release'
)

. "$PSScriptRoot\_common.ps1"

$repoRoot = Get-RepoRoot
$resolvedGameDir = Resolve-GameDir -GameDir $GameDir
$buildDir = Get-BuildDir
$patcherDir = Get-PatcherDir

Write-Step "Каталог игры: $resolvedGameDir"

Write-Step "Сборка LocalAIApiMod (net40)"
dotnet build (Join-Path $repoRoot 'src\LocalAIApiMod\LocalAIApiMod.csproj') `
    -c $Configuration `
    -p:GameDir="$resolvedGameDir" `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "Сборка LocalAIApiMod завершилась с кодом $LASTEXITCODE" }
Write-Ok (Join-Path $buildDir 'LocalAIApiMod.dll')

Write-Step "Сборка LocalAIApiPatcher (net9.0)"
dotnet publish (Join-Path $repoRoot 'src\LocalAIApiPatcher\LocalAIApiPatcher.csproj') `
    -c $Configuration `
    -o $patcherDir `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "Сборка LocalAIApiPatcher завершилась с кодом $LASTEXITCODE" }
Write-Ok (Join-Path $patcherDir 'LocalAIApiPatcher.dll')

Write-Host ''
Write-Host 'Сборка завершена.' -ForegroundColor Green
