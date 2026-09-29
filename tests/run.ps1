param([Parameter(Mandatory=$true)][string]$ZigExe,
      [string]$GameDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$zig = (Resolve-Path -LiteralPath $ZigExe).Path
$panel = Join-Path $root 'outputs\visual-interno-lotro-v3\Painel-LOTRO.exe'
if (-not (Test-Path -LiteralPath $panel)) { throw 'Compile o painel antes de executar os testes.' }
$output = Join-Path $root '.cache\validation'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$oldLocal = $env:ZIG_LOCAL_CACHE_DIR
$oldGlobal = $env:ZIG_GLOBAL_CACHE_DIR
try {
    $env:ZIG_LOCAL_CACHE_DIR = Join-Path $output 'zig-local'
    $env:ZIG_GLOBAL_CACHE_DIR = Join-Path $output 'zig-global'
    $movement = Join-Path $output 'test-speed-control.exe'
    & $zig cc -x c++ -target x86_64-windows-gnu -std=c++17 -O2 -Wall -Wextra -Werror (Join-Path $PSScriptRoot 'test_speed_control.cpp') -o $movement
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o teste de movimento.' }
    & $movement
    if ($LASTEXITCODE -ne 0) { throw 'Teste de movimento falhou.' }
    foreach ($command in @('--autoteste','--teste-menu')) {
        $report = Join-Path $output ($command.Substring(2) + '.json')
        $run = Start-Process -FilePath $panel -ArgumentList @($command,('"' + $report + '"')) -WindowStyle Hidden -Wait -PassThru
        Get-Content -LiteralPath $report -Raw
        if ($run.ExitCode -ne 0) { throw ('Teste falhou: ' + $command) }
    }
    if ($GameDirectory) {
        $game = (Resolve-Path -LiteralPath $GameDirectory).Path
        $names = Join-Path $output 'test-auto-metadata.exe'
        & $zig cc -x c++ -target x86_64-windows-gnu -std=c++17 -O2 -Wall -Wextra -Werror -municode (Join-Path $PSScriptRoot 'test_auto_metadata.cpp') -o $names
        if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o teste de nomes.' }
        & $names $game
        if ($LASTEXITCODE -ne 0) { throw 'Teste de nomes falhou.' }
    }
} finally {
    $env:ZIG_LOCAL_CACHE_DIR = $oldLocal
    $env:ZIG_GLOBAL_CACHE_DIR = $oldGlobal
}
