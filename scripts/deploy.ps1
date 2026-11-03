<#
.SYNOPSIS
    Патчит игровые сборки и разворачивает мод в каталог Managed.

    Схема развёртывания (BepInEx в этой игре выключен, мод грузится через IL-патч):
        Assembly-CSharp.patched.dll -> Managed\Assembly-CSharp.dll
        ScriptsAssDef.patched.dll   -> Managed\ScriptsAssDef.dll
        LocalAIApiMod.dll           -> Managed\LocalAIApiMod.dll
        LocalAIApi.cfg              -> BepInEx\plugins\LocalAIApi\LocalAIApi.cfg

    Чистые оригиналы складываются в <GameDir>\_LocalAIApiMod_backup и повторно
    используются как вход патчера, поэтому повторный деплой идемпотентен.

.EXAMPLE
    pwsh -File scripts\deploy.ps1
    pwsh -File scripts\deploy.ps1 -ApiKey "magpie" -Model "group/never-stuck"
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [string]$ApiKey = 'magpie',
    [string]$Endpoint = 'http://127.0.0.1:3425/v1/chat/completions',
    [string]$Model = 'group/never-stuck',
    [switch]$SkipBuild
)

. "$PSScriptRoot\_common.ps1"

$repoRoot = Get-RepoRoot
$resolvedGameDir = Resolve-GameDir -GameDir $GameDir
$managedDir = Get-ManagedDir -GameDir $resolvedGameDir
$buildDir = Get-BuildDir
$patcherDir = Get-PatcherDir
$backupDir = Get-BackupDir -GameDir $resolvedGameDir

Assert-GameNotRunning

if (-not $SkipBuild) {
    Write-Step "Сборка мода и патчера"
    & (Join-Path $PSScriptRoot 'build.ps1') -GameDir $resolvedGameDir -Configuration Release
    if ($LASTEXITCODE -ne 0) { throw "build.ps1 завершился с кодом $LASTEXITCODE" }
}

$patcherDll = Join-Path $patcherDir 'LocalAIApiPatcher.dll'
$modDll = Join-Path $buildDir 'LocalAIApiMod.dll'
if (-not (Test-Path -LiteralPath $patcherDll)) { throw "Патчер не найден: $patcherDll. Запустите scripts\build.ps1." }
if (-not (Test-Path -LiteralPath $modDll)) { throw "Мод не найден: $modDll. Запустите scripts\build.ps1." }

Write-Step "Резервные копии оригиналов -> $backupDir"
New-Item -ItemType Directory -Path $backupDir -Force | Out-Null

$pristineAssembly = Join-Path $backupDir 'Assembly-CSharp.dll'
$pristineScripts = Join-Path $backupDir 'ScriptsAssDef.dll'

if (-not (Test-Path -LiteralPath $pristineAssembly)) {
    # Приоритет: явный .bak в Managed (в этой игре он есть и совпадает с чистым оригиналом),
    # затем текущий файл Managed.
    $candidates = @(
        (Join-Path $managedDir 'Assembly-CSharp.dll.bak'),
        (Join-Path $managedDir 'Assembly-CSharp.dll.bak2'),
        (Join-Path $managedDir 'Assembly-CSharp.dll')
    ) | Where-Object { Test-Path -LiteralPath $_ }

    if (-not $candidates) { throw "Не найдено ни одного файла Assembly-CSharp*.dll в $managedDir" }

    Copy-Item -LiteralPath $candidates[0] -Destination $pristineAssembly -Force
    Write-Ok "Backup: $pristineAssembly <- $($candidates[0])"
}

if (-not (Test-Path -LiteralPath $pristineScripts)) {
    $currentScripts = Join-Path $managedDir 'ScriptsAssDef.dll'
    if (Test-Path -LiteralPath $currentScripts) {
        Copy-Item -LiteralPath $currentScripts -Destination $pristineScripts -Force
        Write-Ok "Backup: $pristineScripts"
    } else {
        Write-Warn "ScriptsAssDef.dll не найден — патч HostAiSettings будет пропущен."
    }
}

Write-Step "IL-патч"
dotnet $patcherDll `
    --managed $managedDir `
    --output $buildDir `
    --mod $modDll `
    --api-key $ApiKey `
    --assembly-csharp $pristineAssembly `
    --scripts-assdef $pristineScripts
if ($LASTEXITCODE -ne 0) { throw "Патчер завершился с кодом $LASTEXITCODE" }

Write-Step "Установка пропатченных сборок в Managed"
Copy-Item -LiteralPath (Join-Path $buildDir 'Assembly-CSharp.patched.dll') -Destination (Join-Path $managedDir 'Assembly-CSharp.dll') -Force
Write-Ok 'Managed\Assembly-CSharp.dll'

$patchedScripts = Join-Path $buildDir 'ScriptsAssDef.patched.dll'
if (Test-Path -LiteralPath $patchedScripts) {
    Copy-Item -LiteralPath $patchedScripts -Destination (Join-Path $managedDir 'ScriptsAssDef.dll') -Force
    Write-Ok 'Managed\ScriptsAssDef.dll'
}

Copy-Item -LiteralPath $modDll -Destination (Join-Path $managedDir 'LocalAIApiMod.dll') -Force
Write-Ok 'Managed\LocalAIApiMod.dll'

Write-Step 'Конфигурация'
$cfgDir = Join-Path $resolvedGameDir 'BepInEx\plugins\LocalAIApi'
$cfgPath = Join-Path $cfgDir 'LocalAIApi.cfg'
if (-not (Test-Path -LiteralPath $cfgPath)) {
    New-Item -ItemType Directory -Path $cfgDir -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'config\LocalAIApi.cfg.example') -Destination $cfgPath -Force
    Write-Ok "Создан $cfgPath"
} else {
    Write-Warn "Существующий конфиг сохранён: $cfgPath"
}

Write-Host ''
Write-Host 'Развёртывание завершено. Запустите игру.' -ForegroundColor Green
