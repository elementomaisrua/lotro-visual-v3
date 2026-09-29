param([Parameter(Mandatory = $true)][string]$ZigExe,
      [string]$CacheRoot = (Join-Path ([IO.Path]::GetTempPath()) 'lotro-visual-zig-cache'))
$ErrorActionPreference = 'Stop'
$compiler = (Resolve-Path -LiteralPath $ZigExe).Path
$zigRoot = Split-Path -Parent $compiler
$oldLocal = $env:ZIG_LOCAL_CACHE_DIR
$oldGlobal = $env:ZIG_GLOBAL_CACHE_DIR
try {
    $env:ZIG_LOCAL_CACHE_DIR = Join-Path $CacheRoot 'local'
    $env:ZIG_GLOBAL_CACHE_DIR = Join-Path $CacheRoot 'global'
    $dll = Join-Path $PSScriptRoot 'visual-lotro-x64.dll'
    & $compiler cc -x c++ -target x86_64-windows-gnu -std=c++17 -O2 -fno-exceptions -fno-rtti -fno-builtin -fno-math-errno -Wall -Wextra -Werror -nostdlib -isystem (Join-Path $zigRoot 'lib\libc\include\any-windows-any') '-Wl,--entry,DllMain' -shared (Join-Path $PSScriptRoot 'visual_lotro.cpp') -lkernel32 -o $dll
    if ($LASTEXITCODE -ne 0) { throw 'Falha na compilacao.' }
    [ordered]@{dll='visual-lotro-x64.dll'; sha256=(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant(); profile='native-x64-kernel32-no-tls-no-crt';} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'payload-manifest.json') -Encoding utf8
    Write-Output 'DLL visual compilada; manifesto atualizado.'
} finally {
    $env:ZIG_LOCAL_CACHE_DIR = $oldLocal
    $env:ZIG_GLOBAL_CACHE_DIR = $oldGlobal
}
