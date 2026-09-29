param([int]$PidAlvo)
$ErrorActionPreference = 'Stop'
if (-not $PidAlvo) {
    $targets = @(Get-Process lotroclient64 -ErrorAction SilentlyContinue)
    if ($targets.Count -ne 1) { throw 'Informe -PidAlvo: nao ha exatamente um LOTRO aberto.' }
    $PidAlvo = $targets[0].Id
}
$exe = Join-Path $PSScriptRoot 'Painel-LOTRO.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Painel-LOTRO.exe nao encontrado ao lado deste script.' }
$report = Join-Path ([IO.Path]::GetTempPath()) ('lotro-parar-' + [Guid]::NewGuid().ToString('N') + '.json')
$controller = Start-Process -FilePath $exe -ArgumentList @('--parar', ('"' + $report + '"'), $PidAlvo) -WindowStyle Hidden -PassThru -Wait
if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report -Raw }
if ($controller.ExitCode -ne 0) { throw "Falha ao encerrar o visual. Causa detalhada: $report" }
