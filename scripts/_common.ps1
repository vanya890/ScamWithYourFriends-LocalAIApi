<#
    Общие функции для скриптов сборки и развёртывания LocalAIApiMod.
    Подключается через ". "$PSScriptRoot\_common.ps1".

    Разрешение каталога игры полностью повторяет логику Directory.Build.props:
      1. Параметр -GameDir
      2. Переменная окружения SCAM_GAME_DIR
      3. Файл game.dir в корне репозитория
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-RepoRoot {
    return (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
}

function Write-Step {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Ok {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host "    $Message" -ForegroundColor Green
}

function Write-Warn {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host "    $Message" -ForegroundColor Yellow
}

function Resolve-GameDir {
    param([string]$GameDir)

    if ($GameDir) {
        return (Resolve-Path -LiteralPath $GameDir).Path
    }

    if ($env:SCAM_GAME_DIR) {
        return (Resolve-Path -LiteralPath $env:SCAM_GAME_DIR).Path
    }

    $gameDirFile = Join-Path (Get-RepoRoot) 'game.dir'
    if (Test-Path -LiteralPath $gameDirFile) {
        $firstLine = Get-Content -LiteralPath $gameDirFile |
            Where-Object { $_ -and -not $_.TrimStart().StartsWith('#') } |
            Select-Object -First 1
        if ($firstLine) {
            return (Resolve-Path -LiteralPath $firstLine.Trim()).Path
        }
    }

    throw "Каталог игры не задан. Укажите -GameDir, переменную SCAM_GAME_DIR или создайте game.dir (см. game.dir.example)."
}

function Get-ManagedDir {
    param([Parameter(Mandatory)][string]$GameDir)

    $gameName = Split-Path -Leaf $GameDir
    $managed = Join-Path $GameDir "${gameName}_Data\Managed"

    if (-not (Test-Path -LiteralPath $managed)) {
        throw "Каталог Managed не найден: $managed"
    }

    return (Resolve-Path -LiteralPath $managed).Path
}

function Get-BackupDir {
    param([Parameter(Mandatory)][string]$GameDir)
    return (Join-Path $GameDir '_LocalAIApiMod_backup')
}

function Get-BuildDir {
    return (Join-Path (Get-RepoRoot) 'build')
}

function Get-PatcherDir {
    return (Join-Path (Get-RepoRoot) 'build\patcher')
}

function Assert-GameNotRunning {
    $running = Get-Process -Name 'Scam With Your Friends' -ErrorAction SilentlyContinue
    if ($running) {
        throw "Игра запущена (PID: $($running.Id -join ', ')). Закройте её перед изменением Managed-сборок."
    }
}
