<#
.SYNOPSIS
    Проверяет локальный эндпоинт, состояние развёртывания и свежесть лога мода.

.EXAMPLE
    pwsh -File scripts\smoke-test.ps1
    pwsh -File scripts\smoke-test.ps1 -Endpoint "http://127.0.0.1:11434/v1/chat/completions"
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [string]$Endpoint,
    [string]$ApiKey = 'magpie',
    [string]$Model = 'group/never-stuck'
)

. "$PSScriptRoot\_common.ps1"

$script:failures = 0

function Test-Result {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][bool]$Passed,
        [string]$Detail = ''
    )

    if ($Passed) {
        Write-Host ("  [OK]   {0} {1}" -f $Name, $Detail) -ForegroundColor Green
    } else {
        Write-Host ("  [FAIL] {0} {1}" -f $Name, $Detail) -ForegroundColor Red
        $script:failures++
    }
}

$repoRoot = Get-RepoRoot
$resolvedGameDir = Resolve-GameDir -GameDir $GameDir
$managedDir = Get-ManagedDir -GameDir $resolvedGameDir
$buildDir = Get-BuildDir

if (-not $Endpoint) {
    $cfgPath = Join-Path $resolvedGameDir 'BepInEx\plugins\LocalAIApi\LocalAIApi.cfg'
    if (-not (Test-Path -LiteralPath $cfgPath)) {
        $cfgPath = Join-Path $resolvedGameDir 'LocalAIApi.cfg'
    }

    if (Test-Path -LiteralPath $cfgPath) {
        $line = Get-Content -LiteralPath $cfgPath |
            Where-Object { $_ -match '^\s*ApiEndpoint\s*=' } |
            Select-Object -First 1
        if ($line) {
            $Endpoint = ($line -split '=', 2)[1].Trim()
        }
    }
}

Write-Step 'Развёртывание'

$deployedMod = Join-Path $managedDir 'LocalAIApiMod.dll'
Test-Result 'Managed\LocalAIApiMod.dll присутствует' (Test-Path -LiteralPath $deployedMod)

$builtMod = Join-Path $buildDir 'LocalAIApiMod.dll'
if ((Test-Path -LiteralPath $deployedMod) -and (Test-Path -LiteralPath $builtMod)) {
    $deployedHash = (Get-FileHash -LiteralPath $deployedMod).Hash
    $builtHash = (Get-FileHash -LiteralPath $builtMod).Hash
    Test-Result 'Развёрнутый мод совпадает со сборкой' ($deployedHash -eq $builtHash) "($($deployedHash.Substring(0, 12))...)"
}

$deployedAssembly = Join-Path $managedDir 'Assembly-CSharp.dll'
$builtAssembly = Join-Path $buildDir 'Assembly-CSharp.patched.dll'
if ((Test-Path -LiteralPath $deployedAssembly) -and (Test-Path -LiteralPath $builtAssembly)) {
    $deployedHash = (Get-FileHash -LiteralPath $deployedAssembly).Hash
    $builtHash = (Get-FileHash -LiteralPath $builtAssembly).Hash
    Test-Result 'Managed\Assembly-CSharp.dll пропатчен' ($deployedHash -eq $builtHash) "($($deployedHash.Substring(0, 12))...)"
} else {
    Test-Result 'Managed\Assembly-CSharp.dll пропатчен' $false '(нет файла сборки - запустите deploy.ps1)'
}

# IL-проверка: убеждаемся, что тела методов реально заменены, а не просто
# что в файле встречается нужная строка.
$patcherDll = Join-Path (Get-PatcherDir) 'LocalAIApiPatcher.dll'
if (Test-Path -LiteralPath $patcherDll) {
    $verifyOutput = & dotnet $patcherDll --verify --managed $managedDir 2>&1
    $verifyOk = $LASTEXITCODE -eq 0
    Test-Result 'IL-проверка пропатченных сборок' $verifyOk
    $verifyOutput | Where-Object { $_ -match '^(FAIL|SKIP)' } | ForEach-Object { Write-Host "         $_" -ForegroundColor DarkGray }
} else {
    Test-Result 'IL-проверка пропатченных сборок' $false "(патчер не собран: $patcherDll)"
}

Write-Step 'Локальный эндпоинт'

if (-not $Endpoint) {
    Test-Result 'ApiEndpoint определён' $false '(не найден LocalAIApi.cfg)'
} else {
    Write-Host "    endpoint: $Endpoint"

    $message = @{
        role    = 'user'
        content = 'ping'
    }

    $payload = @{
        model      = $Model
        messages   = @($message)
        max_tokens = 16
        stream     = $false
    } | ConvertTo-Json -Depth 5 -Compress

    $headers = @{
        Authorization = "Bearer $ApiKey"
    }

    try {
        $response = Invoke-RestMethod -Uri $Endpoint -Method Post -TimeoutSec 60 `
            -ContentType 'application/json' `
            -Headers $headers `
            -Body $payload

        $content = $response.choices[0].message.content
        if (-not $content) {
            $content = $response.choices[0].message.reasoning_content
        }
        $length = (($content | Out-String).Trim()).Length
        Test-Result 'POST chat/completions отвечает' ([bool]$content) "(len=$length)"
    } catch {
        Test-Result 'POST chat/completions отвечает' $false $_.Exception.Message
    }
}

Write-Step 'Лог рантайма'

$logPath = Join-Path $resolvedGameDir 'LocalAIApi_runtime.log'
if (Test-Path -LiteralPath $logPath) {
    $info = Get-Item -LiteralPath $logPath
    $ageMinutes = [int]((Get-Date) - $info.LastWriteTime).TotalMinutes
    Test-Result 'LocalAIApi_runtime.log обновлялся за последний час' ($ageMinutes -le 60) "($ageMinutes мин назад)"

    $tail = Get-Content -LiteralPath $logPath -Tail 40
    Test-Result 'В логе есть успешный HTTP-ответ' ([bool]($tail | Where-Object { $_ -match 'HTTP response in \d+ms' }))
    Test-Result 'В логе есть непустой ответ модели' ([bool]($tail | Where-Object { $_ -match 'choice message: \S' -and $_ -notmatch 'choice message: $' }))
} else {
    Test-Result 'LocalAIApi_runtime.log существует' $false "$logPath"
}

Write-Host ''
if ($script:failures -eq 0) {
    Write-Host 'Все проверки пройдены.' -ForegroundColor Green
    exit 0
}

Write-Host "Провалено проверок: $script:failures" -ForegroundColor Red
exit 1
