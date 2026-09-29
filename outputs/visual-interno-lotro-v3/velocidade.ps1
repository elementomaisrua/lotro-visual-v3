param([ValidateRange(1.0,2.0)][single]$Multiplicador = 1.0,
      [ValidateRange(5,120)][int]$DuracaoSegundos = 60,
      [int]$PidAlvo)
$ErrorActionPreference = 'Stop'
if (-not $PidAlvo) {
    $games = @(Get-Process lotroclient64 -ErrorAction Stop)
    if ($games.Count -ne 1) { throw 'Informe -PidAlvo: existe mais de um LOTRO.' }
    $PidAlvo = $games[0].Id
}
$game = Get-Process -Id $PidAlvo -ErrorAction Stop
if ($game.ProcessName -ne 'lotroclient64') { throw 'O alvo deve ser LOTRO x64.' }
$dir = Join-Path $env:TEMP ('LotroVisualInterno\pid-' + $PidAlvo)
$state = Get-Content -LiteralPath (Join-Path $dir 'estado.json') -Raw | ConvertFrom-Json
$creation = [uint64]$game.StartTime.ToUniversalTime().ToFileTimeUtc()
if ([uint64]$state.inicio_processo -ne $creation -or $state.revisao_visual -lt 4) { throw 'Atualize a DLL da v3 nesta sessao.' }
if ($Multiplicador -gt 1 -and -not $state.movimento.disponivel) { throw 'Propriedade privada de movimento nao encontrada; nada foi alterado.' }
$expires = [uint64][DateTime]::UtcNow.AddSeconds($DuracaoSegundos).ToFileTimeUtc()
$serial = [uint32](Get-Random -Minimum 1 -Maximum 2147483647)
$stream = New-Object IO.MemoryStream
$writer = New-Object IO.BinaryWriter($stream)
try {
    $writer.Write([uint32]0x53564c43); $writer.Write([uint32]1); $writer.Write($creation); $writer.Write($expires)
    $writer.Write($serial); $writer.Write([single]$Multiplicador); $writer.Flush()
    $tmp = Join-Path $dir ('velocidade-' + [guid]::NewGuid().ToString('N') + '.tmp')
    $target = Join-Path $dir 'velocidade.bin'
    [IO.File]::WriteAllBytes($tmp,$stream.ToArray())
    if ([IO.File]::Exists($target)) { [IO.File]::Replace($tmp,$target,[NullString]::Value) } else { [IO.File]::Move($tmp,$target) }
} finally { $writer.Dispose(); $stream.Dispose() }
[ordered]@{pid=$PidAlvo;solicitacao=$serial;multiplicador=$Multiplicador;duracao_segundos=$DuracaoSegundos;observacao='Pedido enviado a DLL. Confira movimento.resultado no estado.json; nao comprova velocidade aceita pelo servidor.'} | ConvertTo-Json
