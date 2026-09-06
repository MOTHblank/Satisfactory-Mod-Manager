# Satisfactory Mod Manager

**Versão atual: 0.5.3** — gerenciador/instalador standalone de mods para
*Satisfactory*, com integração nativa ao protocolo `smmanager://` do
ficsit.app, verificação de atualizações e launcher do jogo.

---

## Sumário

- [Recursos](#recursos)
- [Requisitos](#requisitos)
- [Instalação (usuário final)](#instalação-usuário-final)
- [Integração com o ficsit.app](#integração-com-o-ficsitapp)
- [Verificação de atualizações](#verificação-de-atualizações)
- [Estrutura de dados](#estrutura-de-dados)
- [Compilar a partir do código-fonte](#compilar-a-partir-do-código-fonte)
- [Publicar (gerar o instalador)](#publicar-gerar-o-instalador)
- [Limitações conhecidas](#limitações-conhecidas)
- [Verificação/QA](#verificaçãoqa)
- [Histórico de versões](#histórico-de-versões)

---

## Recursos

- **Gerenciamento de mods**: instalar via `.zip`/`.smod`, pasta ou arquivo
  solto (drag-and-drop ou pelos botões "Adicionar mod"/"Adicionar pasta"),
  ativar, desativar e remover, com backup automático dos arquivos
  substituídos.
- **Integração com o ficsit.app**: clique em **Install** na página de um mod
  no site e a instalação é entregue diretamente a este gerenciador via
  protocolo `smmanager://`.
- **Verificação de atualizações**: consulta a SMR (API do ficsit.app) pela
  versão mais recente publicada de cada mod e avisa quando há algo mais novo,
  sem instalar nada automaticamente.
- **Acesso rápido à página do mod**: abre a página do mod no ficsit.app
  diretamente pelo gerenciador (útil para changelog e download manual).
- **Launcher do jogo**: detecta a instalação do Satisfactory, prioriza
  iniciar via Steam quando aplicável (evitando o erro clássico de
  `FactoryGameSteam.uproject`) e permite selecionar manualmente o executável.
- **Instalação automática do SML**: quando ausente, o *Satisfactory Mod
  Loader* é baixado e instalado automaticamente antes de iniciar o jogo.
- **Interface**: tema claro/escuro persistente, com barra de título do
  Windows adaptada via DWM quando suportado.
- **Single-instance**: uma segunda chamada do protocolo (ex.: clicar em
  Install de novo) é encaminhada para a janela já aberta, em vez de abrir
  outra instância.

## Requisitos

| Uso                          | Requisito                                   |
|-------------------------------|----------------------------------------------|
| Executar o app publicado       | Windows 10/11 (x64, x86 ou ARM64). Nenhuma dependência extra — o executável é self-contained. |
| Compilar/depurar a partir do código-fonte | [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) |

> O aplicativo usa Windows Forms (`net8.0-windows`) e roda apenas no
> Windows. Veja [Limitações conhecidas](#limitações-conhecidas) para
> alternativas em Linux.

## Instalação (usuário final)

1. Baixe o pacote correspondente à sua arquitetura
   (`SatisfactoryModManager-v0.5.3-win-x64.zip`, `-win-x86` ou `-win-arm64`).
2. Extraia o `.zip` em uma pasta de sua preferência.
3. Execute `SatisfactoryModManager.exe` uma vez — isso registra
   automaticamente o protocolo `smmanager://` para o seu usuário do Windows
   (não exige administrador).
4. Aponte o gerenciador para a pasta de instalação do Satisfactory (botão
   **Detectar** ou **Procurar...**).

## Integração com o ficsit.app

O esquema de protocolo utilizado é o mesmo do Satisfactory Mod Manager
oficial:

```text
smmanager://install?modID=<referência>&version=<versão>
```

Fluxo de uso:

1. Abra o `SatisfactoryModManager.exe` uma vez para registrar o protocolo.
2. Abra a página de um mod no `ficsit.app`.
3. Clique em **Install** na versão desejada.
4. O navegador entrega a solicitação ao Satisfactory Mod Manager — se ele já
   estiver aberto, a instância existente recebe o pedido; senão, uma nova
   janela é aberta.
5. O gerenciador resolve o pacote pela API GraphQL da SMR (`api.ficsit.app`),
   baixa o arquivo, valida-o como ZIP/SMOD e o instala pelo mesmo instalador
   local usado para arquivos manuais (`.uplugin`, GameFeature, `Mods`,
   `Configs`, backups e ativação).

Caso a API não exponha um link de download direto para uma versão específica,
o gerenciador cai automaticamente para a página do mod no ficsit.app e
informa que o download deve ser feito manualmente.

**Segurança do protocolo**: apenas solicitações `smmanager://install` com
`modID` e `version` dentro de um conjunto seguro de caracteres são aceitas.
Arquivos `.exe`/`.msi` recebidos como "mod" nunca são executados.

Se a associação do protocolo for perdida ou sobrescrita por outro programa,
use o botão **🔗 Registrar ficsit.app** na interface para repará-la, ou
remova-a manualmente com:

```bat
SatisfactoryModManager.exe --unregister-protocol
```

## Verificação de atualizações

Introduzida na v0.5.3. Para cada mod cadastrado é possível:

- **🔄 Verificar atualização** (mod selecionado) ou **🔄 Verificar todas
  atualizações** (todos de uma vez): consulta a SMR pela versão mais recente
  publicada e compara com a versão instalada. O resultado aparece na coluna
  **Atualização** da lista (`em dia`, `nova versão: X.Y.Z` ou `não foi
  possível verificar`) e é registrado no log de atividades.
- **🌐 Página do mod**: abre `https://ficsit.app/mod/<id>` no navegador
  padrão, de onde é possível ver o changelog e baixar a nova versão
  manualmente.

Ambas as ações também estão disponíveis no menu de contexto (clique com o
botão direito sobre um mod na lista).

> A verificação **não baixa nem instala nada automaticamente** — ela apenas
> informa que existe uma versão mais nova. O resultado fica em memória
> (não é salvo em `mods.json`); ao reabrir o gerenciador, a coluna volta a
> mostrar "não verificado" até a próxima checagem.

## Estrutura de dados

| Conteúdo                | Local                                              |
|--------------------------|-----------------------------------------------------|
| Configurações e banco de mods | `%APPDATA%\SatisfactoryModManager`             |
| Downloads temporários     | `%APPDATA%\SatisfactoryModManager\Downloads`       |
| Backups de arquivos substituídos | `%APPDATA%\SatisfactoryModManager\Backups`  |
| Mods desativados           | `%APPDATA%\SatisfactoryModManager\Disabled`        |

## Compilar a partir do código-fonte

Requer o [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0):

```bat
dotnet build -c Release
dotnet run -c Release
```

## Publicar (gerar o instalador)

```bat
publish.bat                REM equivalente a "publish.bat win-x64"
publish.bat win-x86
publish.bat win-arm64
publish.bat all            REM gera os três de uma vez
```

Para cada alvo, o script gera:

```text
publish-final-<alvo>\SatisfactoryModManager.exe
SatisfactoryModManager-v0.5.3-<alvo>.zip
```

O executável publicado é **self-contained e single-file** — não exige o
.NET instalado na máquina de quem for apenas usar o programa.

`publish.bat` interrompe a publicação caso o .NET SDK esteja ausente ou a
compilação falhe.

### Sobre um build para Linux

`publish.bat linux-x64` **não gera um binário funcional**. Este aplicativo
usa Windows Forms (`UseWindowsForms=true`, `net8.0-windows`), que depende do
runtime "Windows Desktop" e só existe no Windows — não há
`System.Windows.Forms` para Linux, então a publicação para `linux-x64`
falharia na compilação (ou, se forçada, resultaria num binário que não
abre). Rodar `publish.bat linux-x64` apenas explica esse motivo, sem gerar
arquivo.

Alternativas reais para uso em Linux:

1. Rodar o `.exe` publicado para Windows sob **Wine/Proton** — a interface e
   o instalador de mods funcionam bem dessa forma.
2. Reescrever a interface com um framework realmente multiplataforma (ex.:
   **Avalonia UI**) — mudança de arquitetura maior, fora do escopo atual.

## Limitações conhecidas

- **API da SMR sujeita a mudanças**: o esquema GraphQL de `api.ficsit.app`
  não é oficialmente versionado. Caso mude novamente no futuro, tanto a
  instalação automática via protocolo quanto a verificação de atualização
  podem falhar para mods específicos; nesses casos o programa explica o
  erro e permite o download/instalação manual.
- **Identificador de mod usado na verificação de atualização**: é o `Id`
  interno do gerenciador (normalmente o nome da pasta do plugin, tirado do
  `.uplugin`). Isso coincide com a referência do ficsit.app para a maioria
  dos mods, mas mods muito antigos com ID "opaco" na SMR (como o próprio
  SML) ou mods instalados como arquivo solto/pasta bruta (sem `.uplugin`)
  podem não ser encontrados — a coluna "Atualização" mostra "não foi
  possível verificar" nesse caso.
- **Sem resolução completa de dependências ou perfis**: o gerenciador não
  reproduz todas as funções do Satisfactory Mod Manager oficial, como
  resolução automática de árvore de dependências entre mods ou perfis de
  mods múltiplos. Compatibilidade de versão do jogo com o SML/Unreal
  continua sendo responsabilidade do ecossistema de modding.
- **Apenas Windows**: ver seção [Sobre um build para Linux](#sobre-um-build-para-linux).

## Verificação/QA

Consulte `VERIFY.md` para o checklist de validação estático realizado antes
de cada pacote (balanceamento de sintaxe, alinhamento de versão entre
arquivos, revisão de lógica). `publish.bat` também interrompe a publicação
caso o SDK esteja ausente ou a compilação falhe — não há distribuição de um
build quebrado.

## Histórico de versões

### v0.5.3
- **Verificação de atualizações** por mod e em lote, nova coluna
  "Atualização" na lista, e atalho **🌐 Página do mod** para abrir a página
  do mod no ficsit.app — disponíveis como botões e no menu de contexto
  (clique direito). Ver [Verificação de atualizações](#verificação-de-atualizações)
  para detalhes e limitações.

### v0.5.2
- Corrige a franja de cor "fantasma" no texto do badge de status (coluna
  Status, pílulas ● ATIVO / ○ OFF), causada pelo ClearType do
  `TextRenderer.DrawText` sobre fundo saturado; trocado por
  `Graphics.DrawString` (GDI+) com anti-aliasing normal.
- Ativa double buffering na lista de mods, evitando frames "rasgados" ao
  atualizar o status ou rolar a lista (a lista usa `OwnerDraw`).
- Linhas da lista um pouco mais altas (`SmallImageList` de 28px), dando
  folga vertical à pílula de status.

### v0.5.1
- Interface redesenhada, inspirada no Satisfactory Mod Manager oficial:
  cabeçalho com botão **▶ JOGAR** grande, cartões separados para ações de
  "Mods" e "Jogo e utilitários", chips de resumo (mods cadastrados/ativos),
  badges em pílula na coluna de status e cabeçalho estilo console para o
  log de atividades.
- Botão "Remover" com destaque visual de ação destrutiva (vermelho).

### v0.5.0
- Corrige o ID incorreto do SML usado na instalação automática (o
  identificador real na SMR é `rpLvf1Q5igJXc6`, não o texto `"SML"`).
- Corrige mods instalados com nome de pasta aleatório quando o `.uplugin`
  está solto na raiz do pacote; o nome de destino agora sempre vem do
  próprio `.uplugin`. Instalações já afetadas são reparadas automaticamente
  ao validar a pasta do jogo.
- Corrige o erro "Failed to open descriptor file .../FactoryGameSteam.uproject"
  ao iniciar o jogo: passa a preferir `steam://rungameid/526870` para
  instalações Steam (com opção "Preferir iniciar via Steam"), prioriza os
  "stubs" da raiz da instalação na detecção automática de executável, e
  adiciona o botão **Selecionar executável...** (com **Auto** para reverter).
- Interface modernizada (fonte Segoe UI, cantos arredondados, tooltips).
- `publish.bat` passa a suportar `win-x64`, `win-x86`, `win-arm64` e `all`,
  além de explicar por que `linux-x64` não é suportado em vez de gerar um
  binário quebrado.

### v0.4.3
- Corrige a expressão regular de resolução de downloads.
- Remove referência de UI não declarada que impedia a compilação.
- Adiciona modo escuro persistente, com alternância para modo claro
  (aplicado também à lista de mods e à barra de título via DWM).
- Melhora contraste e aparência de botões, listas e log.
