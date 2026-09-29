# LOTRO Visual v3

Painel para Windows x64 com lista de entidades próximas, nomes, categorias e marcadores sobrepostos ao LOTRO. **F6** mostra ou oculta o menu; **Esc** também oculta.

A DLL lê as entidades dentro do cliente. O painel recebe os relatórios dessa DLL. Nomes localizados são resolvidos pelos identificadores presentes nas entidades e pelos arquivos da instalação inglesa do jogo. As categorias usam propriedades do cliente: monstros, NPCs, RealNPCs, objetos e jogadores.

## Requisitos

- Windows x64 com .NET Framework 4.8 e seu compilador C#.
- Zig 0.13.0 para compilar a DLL C++.
- Instalação inglesa do LOTRO, incluindo `client_local_English.dat`.
- Cliente compatível: PE timestamp `0x6a947bf8`, `SizeOfImage 0x22bd000`. Uma atualização do jogo pode exigir manutenção; outro build é recusado.

## Compilar

Na raiz do repositório, execute no PowerShell, informando o caminho do Zig:

```powershell
.\outputs\visual-interno-lotro-v3\compilar-painel.ps1 `
    -ZigExe 'C:\ferramentas\zig\zig.exe' `
    -CacheRoot (Join-Path $PWD '.cache\zig')
```

Isso gera `visual-lotro-x64.dll`, seu manifesto de hash e `Painel-LOTRO.exe` na pasta da v3. O painel incorpora a DLL e o carregador nativo; a execução não depende de Python.

## Usar

Abra `outputs\visual-interno-lotro-v3\Painel-LOTRO.exe` com o jogo aberto e clique em **Carregar**. Use **F6**, os filtros de tipo/nome e **Ligar visual / Desligar visual**. Os estilos disponíveis são cantos discretos, anel pequeno e indicador acima.

**Recarregar quando o jogo reabrir** acompanha novas sessões enquanto o painel permanece aberto, usando PID e hora de criação. Para encerrar o worker interno, use `parar.ps1` ou feche o jogo. A imagem mapeada permanece alocada até o cliente encerrar.

Preferências e registros ficam em `%LOCALAPPDATA%\LotroVisual`; relatórios de sessão ficam em `%TEMP%\LotroVisualInterno`. Eles são arquivos locais gerados durante o uso.

## Limites

- A lista cobre entidades carregadas na região da câmera, dentro do alcance configurado, até 512 simultâneas.
- Não há garantia de cobertura de todos os conteúdos ou de todas as versões do jogo. Dados não resolvidos aparecem como ID ou **Não classificado**.
- A resolução de nomes usa a localização inglesa e comporta até 95 caracteres. Substituições dinâmicas ainda podem ficar pendentes.
- O desenho depende da projeção e não faz teste de oclusão. Tela cheia exclusiva pode impedir a sobreposição.
- O código de velocidade é experimental e fica desligado por padrão. O teste ao vivo causou correções de deslocamento; não constitui uma aceleração utilizável. Não há teleporte implementado.

## Verificações

Após compilar, rode os testes sem carregar a DLL no jogo:

```powershell
.\tests\run.ps1 -ZigExe 'C:\ferramentas\zig\zig.exe'
```

Para incluir os testes de nomes, informe também sua instalação local:

```powershell
.\tests\run.ps1 -ZigExe 'C:\ferramentas\zig\zig.exe' `
    -GameDirectory 'D:\Jogos\LOTRO'
```

Os testes de movimento alteram somente objetos do próprio processo de teste e conferem identidade, tipos, limites, expiração e restauração. Os testes de nomes leem arquivos da instalação local. Os resultados são gravados em `.cache\validation`.

## Código

Os fontes e scripts atuais ficam em `outputs/visual-interno-lotro-v3`, preservando a pasta permanente da v3. O repositório publica somente os fontes, documentação e testes selecionados. Caches, dumps de memória, relatórios de sessões, preferências, ferramentas baixadas, binários e versões antigas ficam ignorados.

O formato do arquivo DAT foi conferido com a [documentação de lotro_mesh_viewer](https://github.com/Mirarkitty/lotro_mesh_viewer/blob/main/datfile.py). A classificação usa `WeenieType` (`0x126`), as enumerações `0x230000c9`/`0x230001ab` e `Entity_IsPlayer` (`0x47d`) dos dados do cliente. Nenhum arquivo do jogo é distribuído neste repositório.
