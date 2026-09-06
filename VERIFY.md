# Verificação da v0.4.4

Esta pasta passou por verificações estáticas antes da distribuição:

- chaves de versão alinhadas em `Program.cs`, `.csproj`, `VERSION.txt` e `publish.bat` (0.4.4);
- delimitadores `{}`, `()`, `[]` e aspas de strings verificados por um analisador estático
  (contagem de balanceamento respeitando comentários, strings normais/verbatim/interpoladas);
- lógica de início do jogo revisada e corrigida para o erro
  "Failed to open descriptor file .../FactoryGameSteam.uproject" (ver README, seção v0.4.4);
- demais funções revisadas por leitura de código (instalar/ativar/desativar/remover mods,
  backups, drag-and-drop, protocolo `smmanager://`, detecção Steam/Epic): sem problemas
  adicionais encontrados nesta revisão.

O ambiente Linux desta sessão não possui o .NET SDK nem o runtime Windows Desktop, portanto
não foi possível executar uma compilação WinForms Windows real aqui. O `publish.bat` faz essa
compilação no seu Windows e para imediatamente caso surja qualquer erro de compilação.

## Sobre o build para Linux

Este projeto usa Windows Forms (`UseWindowsForms=true`, `net8.0-windows`), que não existe fora
do Windows. Por isso `publish.bat linux-x64` não tenta compilar — apenas explica o motivo e
sugere alternativas (Wine/Proton, ou uma reescrita futura com um framework multiplataforma como
Avalonia UI). Isso é intencional: gerar um "build" que não funciona seria pior do que deixar
claro que essa combinação não é suportada pela arquitetura atual do aplicativo.

## Alterações da v0.4.4 (correção do erro ao iniciar o jogo + polimento)

- `LaunchGame()` agora, em ordem: (1) respeita um executável escolhido manualmente pelo usuário
  (`Settings.GameExeOverride`); (2) tenta `steam://rungameid/526870` quando a instalação parece
  ser da Steam e a Steam está instalada; (3) cai para a detecção automática, que agora prioriza
  os "stubs" da raiz da instalação em vez do binário bruto do Engine.
- Novos controles de UI: "Selecionar executável...", "Auto" e a caixa "Preferir iniciar via
  Steam", todos persistidos em `settings.json`.
- `UpdateGameStatus()` agora mostra qual executável/modo será usado para iniciar o jogo.
- Botões com cantos arredondados (`ApplyRoundedRegion`) e fonte Segoe UI 9.5pt.
- `publish.bat` reescrito para aceitar `win-x64` (padrão), `win-x86`, `win-arm64` ou `all`, e
  para explicar (sem falhar silenciosamente) por que `linux-x64` não é suportado.
