<#
.SYNOPSIS
    Возвращает оригинальные игровые сборки из резервной копии и удаляет мод.

.EXAMPLE
    pwsh -File scripts\restore.ps1
#>
[CmdletBinding()]
param(
    [string]$GameDir
)

. "$PSScriptRoot\_common.ps1"

$resolvedGameDir = Resolve-GameDir -GameDir $GameDir
$managedDir = Get-ManagedDir -GameDir $resolvedGameDir
$backupDir = Get-BackupDir -GameDir $resolvedGameDir

Assert-GameNotRunning

if (-not (Test-Path -LiteralPath $backupDir)) {
    throw "Резервная копия не найдена: $backupDir. Восстановите файлы игры через Steam."
}

Write-Step "Восстановление оригиналов из $backupDir"
foreach ($name in @('Assembly-CSharp.dll', 'ScriptsAssDef.dll')) {
    $source = Join-Path $backupDir $name
    if (Test-Path -LiteralPath $source) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $managedDir $name) -Force
        Write-Ok "Managed\$name"
    } else {
        Write-Warn "В копии нет $name — пропущено."
    }
}

$modInManaged = Join-Path $managedDir 'LocalAIApiMod.dll'
if (Test-Path -LiteralPath $modInManaged) {
    Remove-Item -LiteralPath $modInManaged -Force
    Write-Ok 'Удалён Managed\LocalAIApiMod.dll'
}

Write-Host ''
Write-Host 'Откат завершён.' -ForegroundColor Green
