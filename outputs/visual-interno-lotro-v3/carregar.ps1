param([int]$PidAlvo)
$ErrorActionPreference = 'Stop'
if (-not $PidAlvo) {
    $targets = @(Get-Process lotroclient64 -ErrorAction SilentlyContinue)
    if ($targets.Count -ne 1) { throw 'Informe -PidAlvo: nao ha exatamente um LOTRO aberto.' }
    $PidAlvo = $targets[0].Id
}
$target = Get-Process -Id $PidAlvo
if ($target.ProcessName -ne 'lotroclient64') { throw 'Este visual e especifico do LOTRO x64.' }
$exe = Join-Path $PSScriptRoot 'Painel-LOTRO.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Painel-LOTRO.exe nao encontrado ao lado deste script. Execute compilar-painel.ps1.' }
$report = Join-Path ([IO.Path]::GetTempPath()) ('lotro-carregar-' + [Guid]::NewGuid().ToString('N') + '.json')
$loader = Start-Process -FilePath $exe -ArgumentList @('--carregar', ('"' + $report + '"'), $PidAlvo) -WindowStyle Hidden -PassThru -Wait
if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report -Raw }
if ($loader.ExitCode -ne 0) { throw "Falha no carregamento. Causa detalhada no relatorio: $report" }
