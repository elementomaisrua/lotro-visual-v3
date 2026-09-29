param([string]$ZigExe, [string]$CacheRoot = (Join-Path ([IO.Path]::GetTempPath()) 'lotro-visual-zig-cache'))
$ErrorActionPreference = 'Stop'
if ($ZigExe) { & (Join-Path $PSScriptRoot 'compilar.ps1') -ZigExe $ZigExe -CacheRoot $CacheRoot }
$manifestPath = Join-Path $PSScriptRoot 'payload-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$dll = Join-Path $PSScriptRoot $manifest.dll
if ((Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.sha256) { throw 'DLL e manifesto divergem. Recompile a DLL antes do painel.' }
$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { throw 'Compilador .NET Framework 64 bits nao encontrado.' }
$output = Join-Path $PSScriptRoot 'Painel-LOTRO.exe'
& $csc /nologo /target:winexe /platform:x64 /optimize+ /warn:4 /warnaserror+ /utf8output /codepage:65001 `
    '/reference:System.Windows.Forms.dll' '/reference:System.Drawing.dll' '/reference:System.Web.Extensions.dll' `
    "/win32manifest:$(Join-Path $PSScriptRoot 'painel.manifest')" `
    "/resource:$dll,visual-lotro-x64.dll" "/resource:$manifestPath,payload-manifest.json" `
    "/out:$output" (Join-Path $PSScriptRoot 'Painel.cs') (Join-Path $PSScriptRoot 'CarregadorNativo.cs') (Join-Path $PSScriptRoot 'MenuGame.cs')
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o painel.' }
Write-Output "Executavel criado: $output"
