using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO.Compression;
using System.IO.Pipes;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SatisfactoryModManager;

internal static class Program
{
    private const string ProtocolScheme = "smmanager";
    private const string MutexName = "Local\\SatisfactoryModManager_0_4_2";
    internal const string PipeName = "SatisfactoryModManager_0_4_2";
    private static Mutex? _singleInstanceMutex;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--unregister-protocol", StringComparison.OrdinalIgnoreCase)))
        {
            UnregisterProtocolHandler();
            return;
        }

        _singleInstanceMutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            foreach (var arg in args.Where(IsExternalArgument))
                SendToExistingInstance(arg);
            return;
        }

        RegisterProtocolHandler();
        ApplicationConfiguration.Initialize();

        using var form = new MainForm();
        form.StartIpcServer();
        foreach (var arg in args.Where(IsExternalArgument))
            form.QueueExternalArgument(arg);

        Application.Run(form);
    }

    private static bool IsExternalArgument(string value)
    {
        return value.StartsWith(ProtocolScheme + "://", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool RegisterProtocolHandler()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProtocolScheme);
            if (key == null)
                throw new InvalidOperationException("Não foi possível criar a associação do protocolo no registro do Windows.");

            key.SetValue("", "URL:Satisfactory Mod Manager Protocol");
            key.SetValue("URL Protocol", "");

            using (var icon = key.CreateSubKey("DefaultIcon"))
                icon?.SetValue("", $"\"{GetApplicationPath()}\",0");

            using (var command = key.CreateSubKey(@"shell\open\command"))
                command?.SetValue("", BuildProtocolCommand());

            return true;
        }
        catch
        {
            // A associação é uma melhoria de integração; o aplicativo continua funcionando mesmo se o registro falhar.
            return false;
        }
    }

    private static void UnregisterProtocolHandler()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ProtocolScheme, false);
        }
        catch
        {
            // Nada a fazer se a associação já não existir.
        }
    }

    private static string GetApplicationPath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
            return processPath;

        var args = Environment.GetCommandLineArgs();
        return args.Length > 0 ? args[0] : Application.ExecutablePath;
    }

    private static string BuildProtocolCommand()
    {
        var processPath = GetApplicationPath();
        var fileName = Path.GetFileName(processPath);

        if (fileName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var dllPath = Path.Combine(AppContext.BaseDirectory, "SatisfactoryModManager.dll");
            return $"\"{processPath}\" \"{dllPath}\" \"%1\"";
        }

        return $"\"{processPath}\" \"%1\"";
    }

    private static void SendToExistingInstance(string argument)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(1200);
                using var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, leaveOpen: false);
                writer.WriteLine(argument);
                writer.Flush();
                return;
            }
            catch
            {
                Thread.Sleep(150);
            }
        }
    }
}

public sealed class Settings
{
    public string? GameRoot { get; set; }
    public bool LaunchAfterInstall { get; set; }
    public bool DarkMode { get; set; } = true;

    /// <summary>
    /// Caminho de um executável escolhido manualmente pelo usuário para iniciar o jogo,
    /// substituindo a detecção automática. Null/vazio = usar detecção automática.
    /// </summary>
    public string? GameExeOverride { get; set; }

    /// <summary>
    /// Quando verdadeiro (padrão), preferir iniciar via "steam://rungameid/526870" para
    /// instalações Steam, em vez de chamar o binário do Unreal Engine diretamente.
    /// Isso evita o erro "Failed to open descriptor file .../FactoryGameSteam.uproject",
    /// que ocorre porque o executável do Engine espera ser iniciado pelo Steam.
    /// </summary>
    public bool PreferSteamLaunch { get; set; } = true;
}

public sealed class ModRecord
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string Type { get; set; } = "Unknown";
    public List<string> InstalledFiles { get; set; } = new List<string>();
    public Dictionary<string, string> Backups { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class Database
{
    public List<ModRecord> Mods { get; set; } = new List<ModRecord>();
}

public sealed class PackageAnalysis
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Type { get; set; } = "Unknown";
    public bool GameFeature { get; set; }
    public string? PluginDirectory { get; set; }

    /// <summary>
    /// Nome estável da pasta de destino do plugin, derivado do nome do próprio arquivo
    /// .uplugin (a convenção da modding do Satisfactory exige que a pasta do plugin tenha
    /// o mesmo nome do seu "Mod Reference"/arquivo .uplugin). NÃO usar o nome da pasta que
    /// contém o .uplugin (PluginDirectory) para isso: quando o .uplugin está solto na raiz
    /// do pacote extraído, essa pasta é a própria pasta temporária de extração, com nome
    /// aleatório (ex.: "SMM_8f3c1ac5fca7447596f4dd5e2f4aea38") — usá-la resultava em mods
    /// instalados com nomes de pasta sem sentido em vez do nome real do mod.
    /// </summary>
    public string? PluginFolderName { get; set; }
}

public sealed class MainForm : Form
{
    private const string AppVersion = "0.5.3";

    private readonly string _dataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SatisfactoryModManager");

    private string SettingsFile => Path.Combine(_dataRoot, "settings.json");
    private string DatabaseFile => Path.Combine(_dataRoot, "mods.json");
    private string BackupRoot => Path.Combine(_dataRoot, "Backups");
    private string DisabledRootBase => Path.Combine(_dataRoot, "Disabled");

    private Settings _settings = new Settings();
    private Database _db = new Database();

    private TextBox _gamePath = new TextBox();
    private ListView _mods = new ListView();
    private readonly ImageList _modsRowSpacer = new ImageList { ImageSize = new Size(1, 28), ColorDepth = ColorDepth.Depth32Bit };
    private TextBox _log = new TextBox();
    private Label _status = new Label();
    private Label _gameStatus = new Label();
    private CheckBox _launchAfterInstall = new CheckBox();
    private CheckBox _preferSteamCheck = new CheckBox();
    private Button _enableButton = new Button();
    private Button _disableButton = new Button();
    private Button _removeButton = new Button();
    private Button _checkUpdateButton = new Button();
    private Button _openPageButton = new Button();
    private Button _launchButton = new Button();
    private Button _themeButton = new Button();
    private Label _modsCountChip = new Label();
    private Label _activeCountChip = new Label();
    private Label _logHeader = new Label();
    private Label _titleLabel = new Label();
    private readonly List<Panel> _cardPanels = new List<Panel>();
    private readonly List<Label> _chipLabels = new List<Label>();
    private readonly List<Label> _sectionLabels = new List<Label>();
    private ProgressBar _progress = new ProgressBar();
    private CancellationTokenSource? _ipcCts;

    // Cache em memória (não persistido) do resultado da última verificação de atualização
    // de cada mod, indexado por ModRecord.Id. Não precisa sobreviver a um reinício do app:
    // uma nova checagem é barata e a informação fica desatualizada rápido de qualquer forma.
    private readonly Dictionary<string, FicsitApiClient.ModUpdateInfo> _updateCache =
        new(StringComparer.OrdinalIgnoreCase);

    private enum StatusSeverity { Neutral, Success, Warning }

    public MainForm()
    {
        Text = $"Satisfactory Mod Manager {AppVersion} — installer/launcher";
        Width = 1180;
        Height = 760;
        MinimumSize = new Size(980, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = SystemIcons.Application;
        AllowDrop = true;
        try { Font = new Font("Segoe UI", 9.5f, FontStyle.Regular); } catch { /* mantém a fonte padrão do sistema */ }

        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(BackupRoot);
        Directory.CreateDirectory(DisabledRootBase);

        LoadState();
        BuildUi();
        ApplyTheme();
        RefreshMods();
        AutoDetectGame(false);
        Program.RegisterProtocolHandler();

        DragEnter += MainForm_DragEnter;
        DragDrop += MainForm_DragDrop;
        FormClosing += (_, _) =>
        {
            _ipcCts?.Cancel();
            SaveState();
        };
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(14),
            BackColor = Color.Transparent
        };

        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));  // cabeçalho + botão jogar
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));   // cartões de ações
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));   // chips de resumo
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // lista de mods
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));  // log
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));   // barra de status
        root.Margin = new Padding(0);
        Controls.Add(root);

        // ---------------------------------------------------------------
        // Cabeçalho: título/pasta do jogo à esquerda, botão "Jogar" grande à direita.
        // ---------------------------------------------------------------
        var header = CardPanel();
        var headerLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(14, 10, 14, 10)
        };
        headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        header.Controls.Add(headerLayout);

        var infoStack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0)
        };
        infoStack.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        infoStack.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        infoStack.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));

        var title = new Label
        {
            Text = $"🛠  SATISFACTORY MOD MANAGER  ·  v{AppVersion}",
            AutoSize = false,
            Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 10.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        _titleLabel = title;
        infoStack.Controls.Add(title, 0, 0);

        var pathRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Margin = new Padding(0)
        };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));

        var pathLabel = new Label
        {
            Text = "Pasta do jogo",
            Anchor = AnchorStyles.Left,
            AutoSize = true,
            Margin = new Padding(0, 8, 4, 0)
        };
        pathRow.Controls.Add(pathLabel, 0, 0);

        _gamePath = new TextBox
        {
            Dock = DockStyle.Fill,
            Text = _settings.GameRoot ?? string.Empty,
            AllowDrop = true,
            Margin = new Padding(0, 3, 6, 3)
        };
        pathRow.Controls.Add(_gamePath, 1, 0);

        var browse = Btn("Procurar...", (_, _) => PickGameRoot(), ButtonKind.Secondary);
        pathRow.Controls.Add(browse, 2, 0);

        var detect = Btn("Detectar", (_, _) => AutoDetectGame(true), ButtonKind.Secondary);
        pathRow.Controls.Add(detect, 3, 0);
        infoStack.Controls.Add(pathRow, 0, 1);

        _gameStatus = new Label
        {
            Text = "Jogo não definido",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            Padding = new Padding(90, 0, 0, 0)
        };
        infoStack.Controls.Add(_gameStatus, 0, 2);
        headerLayout.Controls.Add(infoStack, 0, 0);

        var launchStack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(12, 0, 0, 0)
        };
        launchStack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        launchStack.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));

        _launchButton = Btn("▶   JOGAR", (_, _) => LaunchGame(), ButtonKind.Accent);
        _launchButton.Dock = DockStyle.Fill;
        _launchButton.Font = new Font(Font.FontFamily, 11f, FontStyle.Bold);
        launchStack.Controls.Add(_launchButton, 0, 0);

        _launchAfterInstall = new CheckBox
        {
            Text = "Iniciar após instalar",
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Checked = _settings.LaunchAfterInstall,
            Margin = new Padding(0, 4, 0, 0)
        };
        _launchAfterInstall.CheckedChanged += (_, _) =>
        {
            _settings.LaunchAfterInstall = _launchAfterInstall.Checked;
            SaveState();
        };
        launchStack.Controls.Add(_launchAfterInstall, 0, 1);
        headerLayout.Controls.Add(launchStack, 1, 0);

        var tips = new ToolTip { AutoPopDelay = 8000, InitialDelay = 400, ReshowDelay = 200 };
        tips.SetToolTip(_launchButton, "Inicia o jogo com os mods ativos atuais. Usa Steam quando possível para evitar erros de inicialização.");

        root.Controls.Add(header, 0, 0);

        // ---------------------------------------------------------------
        // Dois cartões lado a lado: ações de mods e ações do jogo/app.
        // ---------------------------------------------------------------
        var cardsRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 8, 0, 0)
        };
        cardsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        cardsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));

        var modsCard = CardPanel();
        var modsCardLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(12, 8, 12, 8), Margin = new Padding(0) };
        modsCardLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        modsCardLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var modsCardTitle = SectionLabel("MODS");
        modsCardLayout.Controls.Add(modsCardTitle, 0, 0);
        var modActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false, Margin = new Padding(0) };
        modActions.Controls.Add(Btn("＋ Adicionar mod", (_, _) => AddMod(), ButtonKind.Secondary));
        modActions.Controls.Add(Btn("＋ Adicionar pasta", (_, _) => AddFolderMod(), ButtonKind.Secondary));
        _enableButton = Btn("Ativar", (_, _) => EnableSelected(), ButtonKind.Secondary);
        _disableButton = Btn("Desativar", (_, _) => DisableSelected(), ButtonKind.Secondary);
        _removeButton = Btn("Remover", (_, _) => RemoveSelected(), ButtonKind.Danger);
        _checkUpdateButton = Btn("🔄 Verificar atualização", (_, _) => CheckSelectedModUpdate(), ButtonKind.Secondary);
        _openPageButton = Btn("🌐 Página do mod", (_, _) => OpenSelectedModPage(), ButtonKind.Secondary);
        modActions.Controls.Add(_enableButton);
        modActions.Controls.Add(_disableButton);
        modActions.Controls.Add(_removeButton);
        modActions.Controls.Add(_checkUpdateButton);
        modActions.Controls.Add(_openPageButton);
        var checkAllUpdatesButton = Btn("🔄 Verificar todas atualizações", (_, _) => CheckAllModsForUpdates(), ButtonKind.Secondary);
        modActions.Controls.Add(checkAllUpdatesButton);
        modActions.Controls.Add(Btn("↻ Atualizar lista", (_, _) => RefreshMods(), ButtonKind.Secondary));
        tips.SetToolTip(_checkUpdateButton, "Consulta a SMR (ficsit.app) pela versão mais recente publicada do mod selecionado e compara com a versão instalada.");
        tips.SetToolTip(_openPageButton, "Abre a página do mod selecionado no ficsit.app, de onde também dá para baixar manualmente.");
        tips.SetToolTip(checkAllUpdatesButton, "Verifica, um por um, se há uma versão mais nova publicada de cada mod cadastrado.");
        modsCardLayout.Controls.Add(modActions, 0, 1);
        modsCard.Controls.Add(modsCardLayout);
        cardsRow.Controls.Add(modsCard, 0, 0);

        var gameCard = CardPanel();
        var gameCardLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(12, 8, 12, 8), Margin = new Padding(0) };
        gameCardLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        gameCardLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var gameCardTitle = SectionLabel("JOGO E UTILITÁRIOS");
        gameCardLayout.Controls.Add(gameCardTitle, 0, 0);
        var gameActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false, Margin = new Padding(0) };
        var pickExeButton = Btn("Selecionar executável...", (_, _) => PickGameExe(), ButtonKind.Secondary);
        var autoExeButton = Btn("Auto", (_, _) => UseAutoGameExe(), ButtonKind.Secondary);
        gameActions.Controls.Add(pickExeButton);
        gameActions.Controls.Add(autoExeButton);
        tips.SetToolTip(pickExeButton, "Escolha manualmente qual .exe deve ser usado para iniciar o jogo (útil se a detecção automática abrir o executável errado).");
        tips.SetToolTip(autoExeButton, "Remove a escolha manual de executável e volta a usar detecção automática/Steam.");
        gameActions.Controls.Add(Btn("📁 Abrir Mods", (_, _) => OpenModsFolder(), ButtonKind.Secondary));
        gameActions.Controls.Add(Btn("📁 Abrir dados", (_, _) => OpenDataFolder(), ButtonKind.Secondary));
        gameActions.Controls.Add(Btn("🔗 Registrar ficsit.app", (_, _) => RegisterFicsitProtocol(), ButtonKind.Secondary));
        _themeButton = Btn(_settings.DarkMode ? "☀ Modo claro" : "🌙 Modo escuro", (_, _) => ToggleTheme(), ButtonKind.Secondary);
        gameActions.Controls.Add(_themeButton);

        _preferSteamCheck = new CheckBox
        {
            Text = "Preferir iniciar via Steam",
            AutoSize = true,
            Checked = _settings.PreferSteamLaunch,
            Margin = new Padding(12, 10, 0, 0)
        };
        _preferSteamCheck.CheckedChanged += (_, _) =>
        {
            _settings.PreferSteamLaunch = _preferSteamCheck.Checked;
            SaveState();
        };
        gameActions.Controls.Add(_preferSteamCheck);
        gameCardLayout.Controls.Add(gameActions, 0, 1);
        gameCard.Controls.Add(gameCardLayout);
        cardsRow.Controls.Add(gameCard, 1, 0);
        root.Controls.Add(cardsRow, 0, 1);

        // ---------------------------------------------------------------
        // Chips de resumo (contagem de mods) + dica de uso.
        // ---------------------------------------------------------------
        var chipsRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            Margin = new Padding(0, 6, 0, 0)
        };
        _modsCountChip = ChipLabel("0 mods cadastrados");
        _activeCountChip = ChipLabel("0 ativos");
        chipsRow.Controls.Add(_modsCountChip);
        chipsRow.Controls.Add(_activeCountChip);
        var help = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Text = "Arraste .zip/.smod ou uma pasta de mod para a janela  ·  ficsit.app: clique em Instalar e este gerenciador recebe a solicitação",
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(10, 4, 0, 0)
        };
        chipsRow.Controls.Add(help);
        root.Controls.Add(chipsRow, 0, 2);

        // ---------------------------------------------------------------
        // Lista de mods.
        // ---------------------------------------------------------------
        _mods = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            MultiSelect = false,
            HideSelection = false,
            UseCompatibleStateImageBehavior = false,
            BorderStyle = BorderStyle.FixedSingle
        };
        // Truque clássico do WinForms: a ListView em modo Details usa a altura do
        // SmallImageList (quando existe) para calcular a altura das linhas. Como
        // pintamos tudo manualmente (OwnerDraw), nenhum ícone deste ImageList chega a
        // ser desenhado — ele só existe para dar mais altura à linha, o que deixa a
        // pílula de status com espaço confortável em vez de espremida.
        _modsRowSpacer.Images.Add(new Bitmap(1, 28));
        _mods.SmallImageList = _modsRowSpacer;
        _mods.Columns.Add("Status", 96);
        _mods.Columns.Add("Nome", 260);
        _mods.Columns.Add("Versão", 110);
        _mods.Columns.Add("Atualização", 190);
        _mods.Columns.Add("Tipo", 170);
        _mods.Columns.Add("Arquivos", 76);
        _mods.Columns.Add("Origem", 420);
        _mods.SelectedIndexChanged += (_, _) => { UpdateButtons(); _mods.Invalidate(); };
        _mods.DoubleClick += (_, _) => ToggleSelected();
        _mods.DrawColumnHeader += Mods_DrawColumnHeader;
        _mods.DrawItem += Mods_DrawItem;
        _mods.DrawSubItem += Mods_DrawSubItem;
        _mods.Paint += Mods_PaintEmptyState;
        _mods.MouseUp += Mods_MouseUp;

        // Menu de contexto (clique direito): ações rápidas para o mod sob o cursor, sem
        // precisar sair do teclado/mouse na lista. Mods_MouseUp acima garante que o item
        // sob o cursor já esteja selecionado quando o menu abre.
        var modsMenu = new ContextMenuStrip();
        modsMenu.Items.Add(new ToolStripMenuItem("🌐 Abrir página do mod no ficsit.app", null, (_, _) => OpenSelectedModPage()));
        modsMenu.Items.Add(new ToolStripMenuItem("🔄 Verificar atualização deste mod", null, (_, _) => CheckSelectedModUpdate()));
        modsMenu.Opening += (_, e) => e.Cancel = SelectedMod() == null;
        _mods.ContextMenuStrip = modsMenu;
        // A ListView não expõe DoubleBuffered publicamente, mas como usamos OwnerDraw
        // (pintamos pílulas e texto nós mesmos célula a célula), sem isso o controle
        // repinta direto na tela e uma atualização (troca de status, scroll, resize)
        // pode ser capturada "pela metade", misturando o frame antigo com o novo —
        // exatamente o tipo de texto sobreposto/borrado visto na coluna Status.
        EnableDoubleBuffering(_mods);
        root.Controls.Add(_mods, 0, 3);

        // ---------------------------------------------------------------
        // Log de atividades, com cabeçalho estilo "console".
        // ---------------------------------------------------------------
        var logCard = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 6, 0, 0) };
        logCard.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        logCard.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _logHeader = new Label
        {
            Text = "▾  REGISTRO DE ATIVIDADES",
            Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 8f, FontStyle.Bold),
            TextAlign = ContentAlignment.BottomLeft
        };
        _sectionLabels.Add(_logHeader);
        logCard.Controls.Add(_logHeader, 0, 0);

        _log = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 9f),
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0)
        };
        logCard.Controls.Add(_log, 0, 1);
        root.Controls.Add(logCard, 0, 4);

        // ---------------------------------------------------------------
        // Barra de status inferior.
        // ---------------------------------------------------------------
        var statusPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 4, 0, 0)
        };
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));

        _status = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(4, 0, 0, 0),
            Margin = new Padding(0)
        };
        statusPanel.Controls.Add(_status, 0, 0);

        _progress = new ProgressBar
        {
            Dock = DockStyle.Fill,
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
            Visible = false,
            Margin = new Padding(4, 3, 0, 3)
        };
        statusPanel.Controls.Add(_progress, 1, 0);
        root.Controls.Add(statusPanel, 0, 5);
    }

    private enum ButtonKind { Secondary, Accent, Danger }

    /// <summary>Cria um painel "cartão" com cantos arredondados, usado para agrupar seções da UI.</summary>
    private Panel CardPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 0) };
        panel.SizeChanged += (_, _) => ApplyRoundedRegion(panel, 10);
        _cardPanels.Add(panel);
        return panel;
    }

    /// <summary>Pequeno "chip" (pílula) usado para mostrar contagens/resumos.</summary>
    private Label ChipLabel(string text)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleCenter,
            Padding = new Padding(12, 4, 12, 4),
            Margin = new Padding(0, 0, 8, 0),
            Font = new Font(Font.FontFamily, 8.5f, FontStyle.Bold)
        };
        label.SizeChanged += (_, _) => ApplyRoundedRegion(label, label.Height / 2);
        _chipLabels.Add(label);
        return label;
    }

    /// <summary>Rótulo pequeno em caixa alta usado como título de seção dentro de um cartão.</summary>
    private Label SectionLabel(string text)
    {
        var label = new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 8f, FontStyle.Bold),
            TextAlign = ContentAlignment.BottomLeft
        };
        _sectionLabels.Add(label);
        return label;
    }

    private void Mods_PaintEmptyState(object? sender, PaintEventArgs e)
    {
        if (_mods.Items.Count > 0)
            return;

        var dark = _settings.DarkMode;
        var muted = dark ? Color.FromArgb(140, 146, 152) : Color.FromArgb(140, 144, 148);
        const int headerHeight = 24;
        var area = new Rectangle(16, headerHeight + 16, Math.Max(0, _mods.Width - 32), Math.Max(0, _mods.Height - headerHeight - 32));

        const string message = "Nenhum mod instalado ainda.\r\nArraste um .zip/.smod aqui, use \"Adicionar mod\" ou clique em Install no ficsit.app.";
        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak;
        TextRenderer.DrawText(e.Graphics, message, Font, area, muted, flags);
    }

    private Button Btn(string text, EventHandler onClick, ButtonKind kind = ButtonKind.Secondary)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            Height = 34,
            Margin = new Padding(4, 3, 4, 3),
            Padding = new Padding(14, 0, 14, 0),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            Tag = kind
        };
        button.Click += onClick;
        button.SizeChanged += (_, _) => ApplyRoundedRegion(button, 8);
        ApplyRoundedRegion(button, 8);
        return button;
    }

    /// <summary>Dá um visual mais moderno aos controles, arredondando os cantos (botões, cartões, chips).</summary>
    private static void ApplyRoundedRegion(Control control, int radius)
    {
        if (control.Width <= 0 || control.Height <= 0)
            return;

        var rect = new Rectangle(0, 0, control.Width, control.Height);
        var diameter = Math.Max(1, radius * 2);

        using var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        control.Region?.Dispose();
        control.Region = new Region(path);
    }

    private void ToggleTheme()
    {
        _settings.DarkMode = !_settings.DarkMode;
        SaveState();
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        var dark = _settings.DarkMode;
        var background = dark ? Color.FromArgb(24, 26, 29) : Color.FromArgb(238, 240, 242);
        var surface = dark ? Color.FromArgb(40, 43, 47) : Color.White;
        var surfaceAlt = dark ? Color.FromArgb(33, 36, 39) : Color.FromArgb(250, 250, 250);
        var cardBack = dark ? Color.FromArgb(35, 38, 42) : Color.White;
        var foreground = dark ? Color.FromArgb(235, 237, 240) : Color.FromArgb(32, 33, 36);
        var muted = dark ? Color.FromArgb(160, 167, 174) : Color.FromArgb(105, 110, 116);
        var border = dark ? Color.FromArgb(60, 64, 69) : Color.FromArgb(210, 213, 217);
        var accent = dark ? Color.FromArgb(232, 141, 40) : Color.FromArgb(214, 122, 30);
        var accentBorder = dark ? Color.FromArgb(255, 165, 70) : Color.FromArgb(180, 100, 20);
        var accentHover = dark ? Color.FromArgb(244, 156, 58) : Color.FromArgb(224, 135, 45);
        var accentPressed = dark ? Color.FromArgb(202, 118, 28) : Color.FromArgb(190, 105, 20);
        var danger = dark ? Color.FromArgb(198, 82, 74) : Color.FromArgb(178, 58, 50);
        var chipBack = dark ? Color.FromArgb(52, 56, 61) : Color.FromArgb(226, 229, 233);
        var chipText = dark ? Color.FromArgb(220, 224, 228) : Color.FromArgb(50, 53, 57);

        BackColor = background;
        ForeColor = foreground;

        void Apply(Control control)
        {
            control.ForeColor = foreground;
            control.BackColor = background;

            switch (control)
            {
                case Button button:
                    var kind = button.Tag as ButtonKind? ?? ButtonKind.Secondary;
                    button.FlatStyle = FlatStyle.Flat;
                    button.FlatAppearance.BorderColor = kind switch
                    {
                        ButtonKind.Accent => accentBorder,
                        ButtonKind.Danger => danger,
                        _ => border
                    };
                    button.BackColor = kind switch
                    {
                        ButtonKind.Accent => accent,
                        ButtonKind.Danger => surface,
                        _ => surface
                    };
                    button.ForeColor = kind switch
                    {
                        ButtonKind.Accent => Color.White,
                        ButtonKind.Danger => danger,
                        _ => foreground
                    };
                    button.FlatAppearance.MouseOverBackColor = kind switch
                    {
                        ButtonKind.Accent => accentHover,
                        ButtonKind.Danger => dark ? Color.FromArgb(60, 40, 40) : Color.FromArgb(250, 232, 230),
                        _ => dark ? Color.FromArgb(52, 57, 62) : Color.FromArgb(230, 233, 237)
                    };
                    button.FlatAppearance.MouseDownBackColor = kind switch
                    {
                        ButtonKind.Accent => accentPressed,
                        ButtonKind.Danger => dark ? Color.FromArgb(70, 46, 46) : Color.FromArgb(244, 220, 217),
                        _ => dark ? Color.FromArgb(62, 67, 73) : Color.FromArgb(218, 222, 227)
                    };
                    break;
                case TextBox textBox:
                    textBox.BackColor = surfaceAlt;
                    textBox.ForeColor = foreground;
                    textBox.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case CheckBox checkBox:
                    checkBox.BackColor = Color.Transparent;
                    checkBox.ForeColor = muted;
                    break;
                case Label label:
                    label.BackColor = Color.Transparent;
                    label.ForeColor = muted;
                    break;
                case TableLayoutPanel or FlowLayoutPanel:
                    control.BackColor = Color.Transparent;
                    break;
            }

            foreach (Control child in control.Controls)
                Apply(child);
        }

        foreach (Control control in Controls)
            Apply(control);

        // Cartões: fundo "surface", levemente destacado do fundo da janela.
        foreach (var card in _cardPanels)
        {
            card.BackColor = cardBack;
            foreach (Control child in card.Controls)
                Apply(child);
        }

        // Chips de resumo: pílulas com fundo neutro.
        foreach (var chip in _chipLabels)
        {
            chip.BackColor = chipBack;
            chip.ForeColor = chipText;
        }

        // Títulos de seção (dentro dos cartões, log): texto mudo, discreto.
        foreach (var section in _sectionLabels)
            section.ForeColor = muted;

        // Título principal do cabeçalho: usa a cor de destaque (laranja), como uma logo.
        _titleLabel.ForeColor = accent;

        _mods.BackColor = surfaceAlt;
        _mods.ForeColor = foreground;
        _mods.BorderStyle = BorderStyle.FixedSingle;
        _mods.OwnerDraw = true;
        _mods.Invalidate();
        _log.BackColor = dark ? Color.FromArgb(18, 20, 22) : Color.White;
        _log.ForeColor = dark ? Color.FromArgb(210, 214, 218) : foreground;
        _status.ForeColor = muted;

        ApplyWindowChromeTheme(dark);

        _themeButton.Text = dark ? "☀ Modo claro" : "🌙 Modo escuro";

        // Recalcula texto/cor do status do jogo e os chips de contagem para o tema atual.
        UpdateGameStatus();
        RefreshMods();
    }

    private void Mods_DrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        var dark = _settings.DarkMode;
        var headerBack = dark ? Color.FromArgb(49, 53, 58) : Color.FromArgb(236, 238, 241);
        var headerText = dark ? Color.FromArgb(238, 240, 243) : Color.FromArgb(35, 37, 40);
        var border = dark ? Color.FromArgb(74, 79, 85) : Color.FromArgb(205, 208, 212);

        using var backBrush = new SolidBrush(headerBack);
        using var textBrush = new SolidBrush(headerText);
        using var pen = new Pen(border);
        e.Graphics.FillRectangle(backBrush, e.Bounds);
        e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);

        var textBounds = Rectangle.Inflate(e.Bounds, -8, 0);
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? string.Empty, Font, textBounds, headerText, flags);
    }

    private void Mods_DrawItem(object? sender, DrawListViewItemEventArgs e)
    {
        // In Details view the row background is drawn per subitem in Mods_DrawSubItem.
        // Draw the focus rectangle here so keyboard navigation remains visible.
        if (e.Item.Selected && _mods.Focused)
            e.DrawFocusRectangle();
    }

    private void Mods_DrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        var dark = _settings.DarkMode;
        var selected = e.Item?.Selected ?? false;
        var alternate = (e.Item?.Index ?? 0) % 2 == 1;
        var rowBack = dark
            ? (alternate ? Color.FromArgb(38, 42, 46) : Color.FromArgb(33, 36, 39))
            : (alternate ? Color.FromArgb(250, 250, 250) : Color.White);

        var background = selected
            ? (dark ? Color.FromArgb(70, 60, 45) : Color.FromArgb(250, 232, 210))
            : rowBack;
        var foreground = selected
            ? (dark ? Color.White : Color.FromArgb(35, 37, 40))
            : (dark ? Color.FromArgb(225, 228, 232) : Color.FromArgb(35, 37, 40));

        using var backBrush = new SolidBrush(background);
        e.Graphics.FillRectangle(backBrush, e.Bounds);

        // Coluna 0 (Status): desenha um "badge" em pílula em vez de texto simples.
        if (e.ColumnIndex == 0)
        {
            var active = string.Equals(e.Item?.Text, "ATIVO", StringComparison.OrdinalIgnoreCase);
            var pillBack = active
                ? (dark ? Color.FromArgb(40, 74, 52) : Color.FromArgb(214, 240, 222))
                : (dark ? Color.FromArgb(58, 61, 65) : Color.FromArgb(226, 228, 231));
            var pillText = active
                ? (dark ? Color.FromArgb(123, 221, 164) : Color.FromArgb(24, 125, 72))
                : (dark ? Color.FromArgb(180, 185, 190) : Color.FromArgb(100, 104, 108));

            var pillRect = Rectangle.Inflate(e.Bounds, -10, -6);
            if (pillRect.Width > 0 && pillRect.Height > 0)
            {
                using var pillPath = RoundedRect(pillRect, pillRect.Height / 2);
                using var pillBrush = new SolidBrush(pillBack);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.FillPath(pillBrush, pillPath);

                // Nota: TextRenderer.DrawText usa o rasterizador de texto do GDI, que por
                // padrão aplica ClearType. O ClearType assume texto escrito sobre um fundo
                // neutro (preto/branco) e faz o "blending" de subpixel com base nisso; em
                // cima de um fundo saturado (verde/cinza da pílula) o resultado é a franja
                // de cor fantasma sobreposta ao texto ("ATIVO" com um "eco" verde-oliva por
                // trás) que aparecia nesta coluna. Usar Graphics.DrawString (GDI+) com
                // anti-aliasing normal resolve isso, porque o GDI+ mistura o texto com a cor
                // que já está pintada atrás dele em vez de presumir um fundo neutro.
                var previousHint = e.Graphics.TextRenderingHint;
                e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using var pillTextBrush = new SolidBrush(pillText);
                using var pillFormat = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                    Trimming = StringTrimming.None,
                    FormatFlags = StringFormatFlags.NoWrap
                };
                e.Graphics.DrawString(active ? "● ATIVO" : "○ OFF", Font, pillTextBrush, pillRect, pillFormat);
                e.Graphics.TextRenderingHint = previousHint;
            }
            return;
        }

        // Coluna "Atualização": mesmo texto simples das demais colunas, mas colorido
        // conforme o resultado da última verificação (verde = tem atualização, âmbar =
        // não foi possível verificar), reaproveitando as cores já usadas na pílula ATIVO.
        if (e.ColumnIndex == 3)
        {
            var mod = e.Item?.Tag as ModRecord;
            var info = mod != null && _updateCache.TryGetValue(mod.Id, out var cached) ? cached : null;
            var updateColor = foreground;
            if (info?.Error != null)
                updateColor = dark ? Color.FromArgb(214, 172, 108) : Color.FromArgb(150, 100, 20);
            else if (info?.UpdateAvailable == true)
                updateColor = dark ? Color.FromArgb(123, 221, 164) : Color.FromArgb(24, 125, 72);

            var updateBounds = Rectangle.Inflate(e.Bounds, -8, 0);
            var updateFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? string.Empty, Font, updateBounds, updateColor, updateFlags);
            return;
        }

        var bounds = Rectangle.Inflate(e.Bounds, -8, 0);
        var textFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? string.Empty, Font, bounds, foreground, textFlags);
    }

    private static void EnableDoubleBuffering(Control control)
    {
        try
        {
            typeof(Control).InvokeMember(
                "DoubleBuffered",
                System.Reflection.BindingFlags.SetProperty | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null,
                control,
                new object[] { true });
        }
        catch
        {
            // Se a reflexão falhar por algum motivo (versão futura do runtime, etc.),
            // o pior caso é voltar ao comportamento anterior (sem double buffer);
            // não é motivo para travar a inicialização da janela.
        }
    }

    private static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        var diameter = Math.Max(1, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyWindowChromeTheme(_settings.DarkMode);
    }

    private void ApplyWindowChromeTheme(bool dark)
    {
        try
        {
            if (!IsHandleCreated)
                return;

            var useDarkMode = dark ? 1 : 0;
            var result = Program.DwmSetWindowAttribute(Handle, 20, ref useDarkMode, sizeof(int));
            if (result != 0)
                _ = Program.DwmSetWindowAttribute(Handle, 19, ref useDarkMode, sizeof(int));
        }
        catch
        {
            // Older Windows/DWM implementations can simply ignore this cosmetic feature.
        }
    }

    internal void StartIpcServer()
    {
        _ipcCts = new CancellationTokenSource();
        _ = Task.Run(() => IpcLoopAsync(_ipcCts.Token));
    }

    internal void QueueExternalArgument(string argument)
    {
        if (IsDisposed)
            return;

        try
        {
            BeginInvoke(new Action(() => HandleExternalArgument(argument)));
        }
        catch (InvalidOperationException)
        {
            // O formulário ainda não entrou no loop de mensagens.
        }
    }

    private async Task IpcLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    Program.PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: false);
                var argument = await reader.ReadLineAsync(cancellationToken);

                if (!string.IsNullOrWhiteSpace(argument))
                    QueueExternalArgument(argument);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                QueueExternalArgument("smmanager://__error?message=" + Uri.EscapeDataString(ex.Message));
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void RegisterFicsitProtocol()
    {
        var ok = Program.RegisterProtocolHandler();
        MessageBox.Show(
            this,
            ok
                ? "A integração do protocolo smmanager:// foi registrada para o seu usuário do Windows.\n\nAgora, ao clicar em Install no ficsit.app, o navegador poderá encaminhar a instalação para este gerenciador."
                : "Não foi possível registrar a integração do ficsit.app no Windows. Tente executar novamente ou verifique as permissões do seu usuário.",
            "Integração ficsit.app",
            MessageBoxButtons.OK,
            ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private void HandleExternalArgument(string argument)
    {
        if (argument.StartsWith("smmanager://__error", StringComparison.OrdinalIgnoreCase))
        {
            Log("IPC: " + argument);
            return;
        }

        if (!FicsitInstallRequest.TryParse(argument, out var request))
        {
            Log("Solicitação externa ignorada: formato de protocolo não reconhecido.");
            return;
        }

        Log($"ficsit.app solicitou instalação: {request.ModId} {FormatVersion(request.Version)}");

        if (!EnsureGameRootForExternal())
            return;

        _ = InstallExternalModAsync(request);
    }

    private bool EnsureGameRootForExternal()
    {
        var current = _gamePath.Text.Trim().Trim('"');
        if (!IsGameRoot(current))
        {
            AutoDetectGame(false);
            current = _gamePath.Text.Trim().Trim('"');
        }

        if (!IsGameRoot(current))
        {
            var answer = MessageBox.Show(
                this,
                "Recebi uma instalação do ficsit.app, mas não encontrei automaticamente a pasta do Satisfactory.\n\nDeseja selecionar a pasta do jogo agora?",
                "Pasta do jogo",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (answer == DialogResult.Yes)
                PickGameRoot();

            current = _gamePath.Text.Trim().Trim('"');
        }

        return IsGameRoot(current);
    }

    private async Task InstallExternalModAsync(FicsitInstallRequest request)
    {
        Cursor = Cursors.WaitCursor;
        _progress.Visible = true;
        try
        {
            Log("Consultando o SMR/ficsit.app para localizar o arquivo da versão solicitada...");
            var downloaded = await FicsitApiClient.DownloadModAsync(request.ModId, request.Version, _dataRoot);

            Log("Download concluído: " + downloaded.FilePath);
            InstallPath(downloaded.FilePath);
            RefreshMods();
            SaveState();

            MessageBox.Show(
                this,
                $"Mod instalado com sucesso.\n\nMod: {downloaded.Name}\nVersão: {downloaded.Version}",
                "ficsit.app",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            try { File.Delete(downloaded.FilePath); } catch { }

            if (_settings.LaunchAfterInstall)
                LaunchGame();
        }
        catch (Exception ex)
        {
            Log("Falha na instalação via ficsit.app: " + ex.Message);
            MessageBox.Show(
                this,
                "Não foi possível concluir a instalação automática.\n\n" + ex.Message + "\n\nA solicitação foi recebida corretamente; você pode baixar o pacote no ficsit.app e usar Adicionar mod/arrastar e soltar.",
                "ficsit.app",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            Cursor = Cursors.Default;
            _progress.Visible = false;
        }
    }

    private void Log(string message)
    {
        if (_log.IsDisposed)
            return;

        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(_dataRoot);
            var options = new JsonSerializerOptions { WriteIndented = true };
            AtomicWrite(SettingsFile, JsonSerializer.Serialize(_settings, options));
            AtomicWrite(DatabaseFile, JsonSerializer.Serialize(_db, options));
        }
        catch (Exception ex)
        {
            Log("Falha ao salvar estado: " + ex.Message);
        }
    }

    private static void AtomicWrite(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, true);
    }

    private void LoadState()
    {
        try
        {
            if (File.Exists(SettingsFile))
                _settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsFile)) ?? new Settings();

            if (File.Exists(DatabaseFile))
                _db = JsonSerializer.Deserialize<Database>(File.ReadAllText(DatabaseFile)) ?? new Database();

            foreach (var mod in _db.Mods)
            {
                mod.InstalledFiles ??= new List<string>();
                mod.Backups ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            _settings = new Settings();
            _db = new Database();
            Log("Estado inválido; iniciando banco vazio: " + ex.Message);
        }
    }

    private void RefreshMods()
    {
        _mods.Items.Clear();

        foreach (var mod in _db.Mods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            var row = new ListViewItem(mod.Enabled ? "ATIVO" : "OFF");
            row.SubItems.Add(mod.Name);
            row.SubItems.Add(string.IsNullOrWhiteSpace(mod.Version) ? "-" : mod.Version);
            row.SubItems.Add(FormatUpdateStatus(mod));
            row.SubItems.Add(mod.Type);
            row.SubItems.Add(mod.InstalledFiles.Count.ToString());
            row.SubItems.Add(mod.Source);
            row.Tag = mod;
            _mods.Items.Add(row);
        }

        _status.Text = $"SMM {AppVersion}  |  Arraste arquivos para instalar";
        if (_modsCountChip.Parent != null)
            _modsCountChip.Text = $"🧩 {_db.Mods.Count} mods cadastrados";
        if (_activeCountChip.Parent != null)
            _activeCountChip.Text = $"✅ {_db.Mods.Count(m => m.Enabled)} ativos";
        UpdateButtons();
        _mods.Invalidate();
    }

    private void UpdateButtons()
    {
        var mod = SelectedMod();
        _enableButton.Enabled = mod != null && !mod.Enabled;
        _disableButton.Enabled = mod != null && mod.Enabled;
        _removeButton.Enabled = mod != null;
        _checkUpdateButton.Enabled = mod != null;
        _openPageButton.Enabled = mod != null;
    }

    /// <summary>Texto mostrado na coluna "Atualização" para um mod, a partir do cache em memória.</summary>
    private string FormatUpdateStatus(ModRecord mod)
    {
        if (!_updateCache.TryGetValue(mod.Id, out var info))
            return "não verificado";
        if (info.Error != null)
            return "não foi possível verificar";
        if (info.UpdateAvailable)
            return string.IsNullOrWhiteSpace(info.LatestVersion) ? "nova versão disponível" : $"nova versão: {info.LatestVersion}";
        return "em dia";
    }

    private void Mods_MouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right)
            return;

        var hit = _mods.HitTest(e.Location);
        if (hit.Item == null)
            return;

        hit.Item.Selected = true;
        hit.Item.Focused = true;
    }

    private void OpenSelectedModPage()
    {
        var mod = SelectedMod();
        if (mod == null)
            return;
        OpenModPage(mod);
    }

    private void OpenModPage(ModRecord mod)
    {
        try
        {
            var url = FicsitApiClient.GetModPageUrl(mod.Id);
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            Log($"Abrindo página de '{mod.Name}' no ficsit.app...");
        }
        catch (Exception ex)
        {
            Log("Não foi possível abrir a página do mod: " + ex.Message);
        }
    }

    private async void CheckSelectedModUpdate()
    {
        var mod = SelectedMod();
        if (mod == null)
            return;

        await CheckModUpdateAsync(mod, announceUpToDate: true).ConfigureAwait(true);
        RefreshMods();
    }

    private async void CheckAllModsForUpdates()
    {
        if (_db.Mods.Count == 0)
        {
            Log("Nenhum mod cadastrado para verificar.");
            return;
        }

        _progress.Visible = true;
        Log($"Verificando atualizações de {_db.Mods.Count} mod(s)...");
        var withUpdate = 0;
        var withError = 0;

        foreach (var mod in _db.Mods.ToList())
        {
            var info = await FicsitApiClient.CheckForUpdateAsync(mod.Id, mod.Version).ConfigureAwait(true);
            _updateCache[mod.Id] = info;
            if (info.Error != null)
                withError++;
            else if (info.UpdateAvailable)
                withUpdate++;
        }

        _progress.Visible = false;
        Log(withUpdate == 0
            ? $"Nenhuma atualização encontrada ({withError} não verificado(s))."
            : $"{withUpdate} mod(s) com atualização disponível ({withError} não verificado(s)).");
        RefreshMods();
    }

    /// <summary>
    /// Consulta a SMR pela versão mais recente do mod e guarda o resultado no cache em
    /// memória (mostrado na coluna "Atualização"). Não baixa nem instala nada — apenas
    /// informa. O usuário decide se quer atualizar manualmente pela página do mod.
    /// </summary>
    private async Task CheckModUpdateAsync(ModRecord mod, bool announceUpToDate)
    {
        _progress.Visible = true;
        try
        {
            var info = await FicsitApiClient.CheckForUpdateAsync(mod.Id, mod.Version).ConfigureAwait(true);
            _updateCache[mod.Id] = info;

            if (info.Error != null)
                Log($"Não foi possível verificar atualização de '{mod.Name}': {info.Error}");
            else if (info.UpdateAvailable)
                Log($"Atualização disponível para '{mod.Name}': {FormatVersion(mod.Version)} → v{info.LatestVersion}. Use \"Página do mod\" para baixar.");
            else if (announceUpToDate)
                Log($"'{mod.Name}' já está na versão mais recente publicada ({info.LatestVersion ?? mod.Version}).");
        }
        finally
        {
            _progress.Visible = false;
        }
    }

    private ModRecord? SelectedMod()
    {
        return _mods.SelectedItems.Count == 0 ? null : _mods.SelectedItems[0].Tag as ModRecord;
    }

    private void ToggleSelected()
    {
        var mod = SelectedMod();
        if (mod == null)
            return;
        if (mod.Enabled)
            DisableSelected();
        else
            EnableSelected();
    }

    private bool EnsureGameRoot()
    {
        var root = _gamePath.Text.Trim().Trim('"');
        if (!IsGameRoot(root))
        {
            MessageBox.Show(this,
                "Selecione a pasta raiz do Satisfactory (a pasta que contém FactoryGame).",
                "Pasta do jogo",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        _settings.GameRoot = Path.GetFullPath(root);
        Directory.CreateDirectory(ModsRoot);
        Directory.CreateDirectory(ConfigRoot);
        Directory.CreateDirectory(ModsGameFeaturesRoot);
        RepairMisnamedModFolders();
        SaveState();
        UpdateGameStatus();
        return true;
    }

    /// <summary>
    /// Corrige automaticamente mods que ficaram instalados com o nome de pasta errado por
    /// causa de um bug já corrigido (quando o .uplugin está solto na raiz do pacote, a pasta
    /// de destino recebia o nome aleatório da extração temporária, tipo "SMM_&lt;guid&gt;", em
    /// vez do nome real do plugin). Renomeia a pasta para o nome correto (tirado do próprio
    /// .uplugin) e atualiza os registros internos — sem exigir nenhuma ação manual do usuário.
    /// </summary>
    private void RepairMisnamedModFolders()
    {
        foreach (var record in _db.Mods.ToList())
        {
            if (!record.Enabled || record.InstalledFiles.Count == 0)
                continue;

            var pluginFile = record.InstalledFiles.FirstOrDefault(f =>
                f.EndsWith(".uplugin", StringComparison.OrdinalIgnoreCase) && File.Exists(f));
            if (pluginFile == null)
                continue;

            var currentDir = Path.GetDirectoryName(pluginFile)!;
            var currentFolderName = Path.GetFileName(currentDir);
            if (!currentFolderName.StartsWith("SMM_", StringComparison.OrdinalIgnoreCase))
                continue; // só mexe em pastas que batem com o padrão do bug, nunca em nomes escolhidos pelo usuário.

            var correctName = Path.GetFileNameWithoutExtension(pluginFile);
            if (correctName.Equals(currentFolderName, StringComparison.OrdinalIgnoreCase))
                continue;

            var parent = Path.GetDirectoryName(currentDir)!;
            var newDir = Path.Combine(parent, correctName);
            if (Directory.Exists(newDir))
                continue; // evita sobrescrever uma pasta já existente com esse nome.

            try
            {
                Directory.Move(currentDir, newDir);
                record.InstalledFiles = record.InstalledFiles
                    .Select(f => f.StartsWith(currentDir, StringComparison.OrdinalIgnoreCase)
                        ? newDir + f[currentDir.Length..]
                        : f)
                    .ToList();
                record.Id = SafeId(correctName);
                Log($"Corrigido automaticamente: pasta do mod \"{record.Name}\" renomeada de \"{currentFolderName}\" para \"{correctName}\".");
            }
            catch (Exception ex)
            {
                Log($"Aviso: não foi possível corrigir automaticamente a pasta do mod \"{record.Name}\" ({ex.Message}). " +
                    "Você pode renomear manualmente a pasta \"" + currentFolderName + "\" para \"" + correctName + "\" dentro de Mods.");
            }
        }
    }

    private string ModsRoot => Path.Combine(_settings.GameRoot ?? string.Empty, "FactoryGame", "Mods");
    private string ModsGameFeaturesRoot => Path.Combine(ModsRoot, "GameFeatures");
    private string ConfigRoot => Path.Combine(_settings.GameRoot ?? string.Empty, "FactoryGame", "Configs");

    private static bool IsGameRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return false;

        var factoryGame = Path.Combine(path, "FactoryGame");
        return Directory.Exists(factoryGame);
    }

    private void PickGameRoot()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Selecione a pasta raiz do Satisfactory"
        };

        if (IsGameRoot(_gamePath.Text))
            dlg.SelectedPath = _gamePath.Text;

        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        if (!IsGameRoot(dlg.SelectedPath))
        {
            MessageBox.Show(this,
                "Essa pasta não parece ser uma instalação do Satisfactory.",
                "Aviso",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        _settings.GameRoot = dlg.SelectedPath;
        _gamePath.Text = dlg.SelectedPath;
        SaveState();
        UpdateGameStatus();
        Log("Pasta do jogo definida: " + dlg.SelectedPath);
    }

    private void UpdateGameStatus()
    {
        var root = _gamePath.Text.Trim().Trim('"');
        if (!IsGameRoot(root))
        {
            SetGameStatus("Jogo não definido", StatusSeverity.Neutral);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_settings.GameExeOverride))
        {
            if (File.Exists(_settings.GameExeOverride))
            {
                SetGameStatus("Instalação válida. Executável manual: " + _settings.GameExeOverride, StatusSeverity.Success);
                return;
            }

            SetGameStatus("Executável manual configurado não existe mais; use \"Auto\" ou selecione outro.", StatusSeverity.Warning);
            return;
        }

        if (_settings.PreferSteamLaunch && LooksLikeSteamInstall(root) && IsSteamAvailable())
        {
            SetGameStatus("Instalação válida detectada. Inicia via Steam (recomendado).", StatusSeverity.Success);
            return;
        }

        var exe = FindGameExe(root);
        if (exe == null)
        {
            SetGameStatus("Pasta encontrada, mas o executável do jogo não foi localizado.", StatusSeverity.Warning);
        }
        else
        {
            SetGameStatus("Instalação válida detectada. Executável: " + exe, StatusSeverity.Success);
        }
    }

    private void SetGameStatus(string text, StatusSeverity severity)
    {
        _gameStatus.Text = text;
        var dark = _settings.DarkMode;
        _gameStatus.ForeColor = severity switch
        {
            StatusSeverity.Success => dark ? Color.FromArgb(123, 221, 164) : Color.FromArgb(24, 125, 72),
            StatusSeverity.Warning => dark ? Color.FromArgb(240, 196, 120) : Color.FromArgb(156, 97, 8),
            _ => dark ? Color.FromArgb(175, 181, 188) : Color.FromArgb(90, 95, 100)
        };
    }

    private void AutoDetectGame(bool showFailure)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(_settings.GameRoot))
            candidates.Add(_settings.GameRoot!);

        candidates.AddRange(new[]
        {
            @"C:\Program Files (x86)\Steam\steamapps\common\Satisfactory",
            @"C:\Program Files\Steam\steamapps\common\Satisfactory",
            @"C:\Program Files (x86)\Epic Games\Satisfactory",
            @"C:\Program Files\Epic Games\Satisfactory",
            @"C:\Games\Satisfactory",
            @"C:\Jogos\Satisfactory",
            @"D:\Games\Satisfactory",
            @"D:\Jogos\Satisfactory",
            @"D:\SteamLibrary\steamapps\common\Satisfactory",
            @"E:\Games\Satisfactory",
            @"E:\Jogos\Satisfactory"
        });

        candidates.AddRange(FindSteamLibraryCandidates());
        candidates.AddRange(FindEpicLibraryCandidates());

        var found = candidates
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(IsGameRoot);

        if (found != null)
        {
            _settings.GameRoot = found;
            _gamePath.Text = found;
            SaveState();
            UpdateGameStatus();
            Log("Satisfactory detectado em: " + found);
            return;
        }

        UpdateGameStatus();
        if (showFailure)
        {
            MessageBox.Show(this,
                "Não encontrei automaticamente. Use Procurar... para apontar para a pasta raiz do jogo.",
                "Detecção",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }

    private static IEnumerable<string> FindSteamLibraryCandidates()
    {
        var result = new List<string>();
        var roots = new[]
        {
            (RegistryHive.CurrentUser, @"SOFTWARE\Valve\Steam"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam")
        };

        foreach (var (hive, keyPath) in roots)
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey(keyPath);
                    var install = key?.GetValue("InstallPath") as string;
                    if (string.IsNullOrWhiteSpace(install))
                        continue;

                    result.Add(Path.Combine(install, "steamapps", "common", "Satisfactory"));
                    var vdf = Path.Combine(install, "steamapps", "libraryfolders.vdf");
                    if (!File.Exists(vdf))
                        continue;

                    foreach (var path in ExtractVdfPaths(File.ReadAllText(vdf)))
                        result.Add(Path.Combine(path, "steamapps", "common", "Satisfactory"));
                }
                catch
                {
                    // Alguns nós do registro podem não existir ou estar inacessíveis.
                }
            }
        }

        return result;
    }

    private static IEnumerable<string> FindEpicLibraryCandidates()
    {
        var result = new List<string>();

        // O launcher da Epic Games grava um arquivo .item (JSON) por jogo instalado
        // em ProgramData, contendo o campo "InstallLocation". Em vez de tentar acertar
        // o nome interno exato do Satisfactory na Epic, lemos todos os manifests e
        // deixamos o IsGameRoot (chamado por quem consome este método) validar qual
        // pasta realmente é uma instalação do jogo.
        try
        {
            var manifestsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic", "EpicGamesLauncher", "Data", "Manifests");

            if (!Directory.Exists(manifestsDir))
                return result;

            foreach (var file in Directory.EnumerateFiles(manifestsDir, "*.item"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    if (doc.RootElement.TryGetProperty("InstallLocation", out var loc))
                    {
                        var path = loc.GetString();
                        if (!string.IsNullOrWhiteSpace(path))
                            result.Add(path);
                    }
                }
                catch (Exception) { /* manifest individual corrompido/ilegível: ignora e segue */ }
            }
        }
        catch (Exception) { /* pasta de manifests inacessível */ }

        return result;
    }

    private static IEnumerable<string> ExtractVdfPaths(string text)
    {
        foreach (Match match in Regex.Matches(
                     text,
                     "\\\"path\\\"\\s*\\\"([^\\\"]+)\\\"",
                     RegexOptions.IgnoreCase))
        {
            yield return match.Groups[1].Value.Replace("\\\\", "\\");
        }
    }

    private void AddMod()
    {
        if (!EnsureGameRoot())
            return;

        using var dlg = new OpenFileDialog
        {
            Title = "Selecione um ou mais mods",
            Filter = "Pacotes de mod|*.smod;*.zip|Arquivos suportados|*.pak;*.sig;*.dll;*.pdb;*.cfg;*.ini|Todos os arquivos|*.*",
            Multiselect = true,
            CheckFileExists = true
        };

        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        InstallMultiple(dlg.FileNames);
    }

    private void AddFolderMod()
    {
        if (!EnsureGameRoot())
            return;

        using var dlg = new FolderBrowserDialog
        {
            Description = "Selecione uma pasta de mod ou pacote extraído"
        };

        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        InstallMultiple(new[] { dlg.SelectedPath });
    }

    private void InstallMultiple(IEnumerable<string> sources)
    {
        var installedAny = false;

        foreach (var source in sources)
        {
            try
            {
                InstallPath(source);
                installedAny = true;
            }
            catch (Exception ex)
            {
                Log($"ERRO {Path.GetFileName(source)}: {ex.Message}");
            }
        }

        RefreshMods();
        SaveState();

        if (installedAny && _settings.LaunchAfterInstall)
            LaunchGame();
    }

    private void MainForm_DragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void MainForm_DragDrop(object? sender, DragEventArgs e)
    {
        if (!e.Data!.GetDataPresent(DataFormats.FileDrop))
            return;

        var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        if (!EnsureGameRoot())
            return;

        InstallMultiple(paths);
    }

    private void InstallPath(string source)
    {
        if (File.Exists(source))
        {
            var ext = Path.GetExtension(source).ToLowerInvariant();
            if (ext is ".zip" or ".smod")
            {
                InstallArchive(source);
                return;
            }

            InstallLooseFile(source);
            return;
        }

        if (Directory.Exists(source))
        {
            InstallDirectory(source);
            return;
        }

        throw new FileNotFoundException("Pacote não encontrado", source);
    }

    private void InstallArchive(string archive)
    {
        var temp = Path.Combine(Path.GetTempPath(), "SMM_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            ZipFile.ExtractToDirectory(archive, temp, true);
            var analysis = AnalyzeDirectory(temp);

            if (analysis.PluginDirectory != null)
            {
                var pluginDir = analysis.PluginDirectory;
                var folderName = analysis.PluginFolderName ?? Path.GetFileName(pluginDir);
                var destinationRoot = analysis.GameFeature ? ModsGameFeaturesRoot : ModsRoot;
                var destination = Path.Combine(destinationRoot, folderName);
                var record = PrepareRecord(
                    folderName,
                    analysis.Name,
                    analysis.Version,
                    analysis.Type,
                    archive);

                CopyDirectoryChecked(pluginDir, destination, record);
                record.Enabled = true;
                AddConfigFilesFromTree(pluginDir, record);
                SaveState();
                Log($"Instalado: {record.Name} {FormatVersion(record.Version)} ({record.Type})");
                return;
            }

            var packageRoots = Directory.EnumerateDirectories(temp).ToList();
            var modsDir = packageRoots.FirstOrDefault(x => Path.GetFileName(x).Equals("Mods", StringComparison.OrdinalIgnoreCase));
            var configDir = packageRoots.FirstOrDefault(x => Path.GetFileName(x).Equals("Configs", StringComparison.OrdinalIgnoreCase));

            if (modsDir != null || configDir != null)
            {
                var record = PrepareRecord(
                    Path.GetFileNameWithoutExtension(archive),
                    Path.GetFileNameWithoutExtension(archive),
                    string.Empty,
                    "Pacote estruturado",
                    archive);

                if (modsDir != null)
                    CopyDirectoryChecked(modsDir, ModsRoot, record, preserveRelativeRoot: true);
                if (configDir != null)
                    CopyDirectoryChecked(configDir, ConfigRoot, record, preserveRelativeRoot: true);

                record.Enabled = true;
                SaveState();
                Log($"Instalado pacote estruturado: {record.Name}");
                return;
            }

            InstallRawTree(temp, Path.GetFileNameWithoutExtension(archive), archive);
        }
        catch (InvalidDataException)
        {
            throw new InvalidOperationException("O arquivo não parece ser um ZIP/.SMOD válido ou está corrompido.");
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    private PackageAnalysis AnalyzeDirectory(string root)
    {
        var uplugin = Directory.EnumerateFiles(root, "*.uplugin", SearchOption.AllDirectories).FirstOrDefault();
        if (uplugin == null)
        {
            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();
            var type = files.Any(f => Path.GetExtension(f).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                ? "C++/DLL"
                : files.Any(f => Path.GetExtension(f).Equals(".pak", StringComparison.OrdinalIgnoreCase))
                    ? "Pak"
                    : "Raw";
            return new PackageAnalysis
            {
                Name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                Type = type
            };
        }

        var gameFeature = false;
        var version = string.Empty;
        var name = Path.GetFileNameWithoutExtension(uplugin);

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(uplugin));
            var json = doc.RootElement;

            if (json.TryGetProperty("GameFeature", out var gf) && gf.ValueKind == JsonValueKind.True)
                gameFeature = true;
            if (json.TryGetProperty("VersionName", out var versionName) && versionName.ValueKind == JsonValueKind.String)
                version = versionName.GetString() ?? string.Empty;
            if (json.TryGetProperty("FriendlyName", out var friendlyName) && friendlyName.ValueKind == JsonValueKind.String)
                name = friendlyName.GetString() ?? name;
        }
        catch (Exception ex)
        {
            Log($"Aviso: não foi possível ler {Path.GetFileName(uplugin)}: {ex.Message}");
        }

        return new PackageAnalysis
        {
            Name = name,
            Version = version,
            Type = "uPlugin" + (gameFeature ? "/GameFeature" : string.Empty),
            GameFeature = gameFeature,
            PluginDirectory = Path.GetDirectoryName(uplugin),
            // Sempre baseado no nome do arquivo .uplugin (o "Mod Reference"), nunca no nome
            // da pasta que o contém — ver comentário em PackageAnalysis.PluginFolderName.
            PluginFolderName = Path.GetFileNameWithoutExtension(uplugin)
        };
    }

    private void InstallDirectory(string source)
    {
        var analysis = AnalyzeDirectory(source);
        if (analysis.PluginDirectory != null)
        {
            var pluginDir = analysis.PluginDirectory;
            var folderName = analysis.PluginFolderName ?? Path.GetFileName(pluginDir);
            var destinationRoot = analysis.GameFeature ? ModsGameFeaturesRoot : ModsRoot;
            var destination = Path.Combine(destinationRoot, folderName);
            var record = PrepareRecord(
                folderName,
                analysis.Name,
                analysis.Version,
                analysis.Type,
                source);

            CopyDirectoryChecked(pluginDir, destination, record);
            record.Enabled = true;
            AddConfigFilesFromTree(pluginDir, record);
            SaveState();
            Log($"Instalado: {record.Name} {FormatVersion(record.Version)}");
            return;
        }

        InstallRawTree(source, Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar)), source);
    }

    private void InstallRawTree(string sourceRoot, string modName, string source)
    {
        var record = PrepareRecord(SafeId(modName), modName, string.Empty, "Raw", source);

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            var targetRoot = ext is ".cfg" or ".ini" ? ConfigRoot : ModsRoot;
            var relative = Path.GetRelativePath(sourceRoot, file);
            var target = Path.Combine(targetRoot, relative);
            InstallFileChecked(file, target, record);
        }

        record.Enabled = true;
        SaveState();
        Log($"Instalado pacote bruto: {record.Name}");
    }

    private void InstallLooseFile(string file)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext is ".exe" or ".msi")
            throw new InvalidOperationException("Executáveis de terceiros não são executados automaticamente. Instale-os manualmente apenas quando souber exatamente o que fazem.");

        if (ext is not (".cfg" or ".ini" or ".pak" or ".sig" or ".dll" or ".pdb"))
            throw new InvalidOperationException("Extensão não suportada como arquivo solto. Adicione o pacote .zip/.smod ou a pasta completa do mod.");

        var targetRoot = ext is ".cfg" or ".ini" ? ConfigRoot : ModsRoot;
        var name = Path.GetFileNameWithoutExtension(file);
        var record = PrepareRecord(SafeId(name), name, string.Empty, ext.TrimStart('.').ToUpperInvariant(), file);
        InstallFileChecked(file, Path.Combine(targetRoot, Path.GetFileName(file)), record);
        record.Enabled = true;
        SaveState();
        Log($"Instalado arquivo: {Path.GetFileName(file)}");
    }

    private ModRecord PrepareRecord(string id, string name, string version, string type, string source)
    {
        var record = _db.Mods.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (record == null)
        {
            record = new ModRecord
            {
                Id = id,
                Name = name,
                Version = version,
                Type = type,
                Source = source,
                Enabled = false
            };
            _db.Mods.Add(record);
            return record;
        }

        // Reinstalação limpa: remove os arquivos da instalação anterior antes de gravar a nova.
        RemoveRecordFiles(record);
        record.Name = name;
        record.Version = version;
        record.Type = type;
        record.Source = source;
        record.Enabled = false;
        record.InstalledFiles.Clear();
        record.Backups.Clear();
        return record;
    }

    private void CopyDirectoryChecked(string source, string destination, ModRecord record, bool preserveRelativeRoot = false)
    {
        Directory.CreateDirectory(destination);

        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, dir);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            InstallFileChecked(file, target, record);
        }
    }

    private void AddConfigFilesFromTree(string source, ModRecord record)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file);
            if (!ext.Equals(".cfg", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".ini", StringComparison.OrdinalIgnoreCase))
                continue;

            var target = Path.Combine(ConfigRoot, Path.GetFileName(file));
            if (!record.InstalledFiles.Any(f => Path.GetFullPath(f).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)))
                InstallFileChecked(file, target, record);
        }
    }

    private void InstallFileChecked(string source, string target, ModRecord record)
    {
        var normalizedTarget = Path.GetFullPath(target);
        Directory.CreateDirectory(Path.GetDirectoryName(normalizedTarget)!);

        var owner = _db.Mods.FirstOrDefault(m =>
            m.Id != record.Id &&
            m.InstalledFiles.Any(f => Path.GetFullPath(f).Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase)));

        if (owner != null)
            throw new IOException($"Conflito: {normalizedTarget} já pertence ao mod '{owner.Name}'.");

        if (File.Exists(normalizedTarget) && !record.InstalledFiles.Any(f => Path.GetFullPath(f).Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase)))
        {
            var backup = CreateBackup(normalizedTarget);
            record.Backups[normalizedTarget] = backup;
            Log("Backup criado: " + backup);
        }

        File.Copy(source, normalizedTarget, true);
        if (!record.InstalledFiles.Any(f => Path.GetFullPath(f).Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase)))
            record.InstalledFiles.Add(normalizedTarget);
    }

    private string CreateBackup(string originalFile)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var relative = GetGameRelativePath(originalFile).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var safe = relative.Replace(':', '_');
        var backup = Path.Combine(BackupRoot, stamp, safe);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Copy(originalFile, backup, true);
        return backup;
    }

    private string GetGameRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(_settings.GameRoot))
            return Path.GetFileName(path);
        return Path.GetRelativePath(_settings.GameRoot, path);
    }

    private void EnableSelected()
    {
        var mod = SelectedMod();
        if (mod == null || mod.Enabled)
            return;
        if (!EnsureGameRoot())
            return;

        try
        {
            var disabledFiles = DisabledFilesFor(mod).ToList();
            var conflicts = disabledFiles
                .Select(f => new
                {
                    Source = f,
                    Target = Path.Combine(_settings.GameRoot!, Path.GetRelativePath(DisabledRoot(mod), f))
                })
                .Where(x => File.Exists(x.Target))
                .ToList();

            if (conflicts.Count > 0)
                throw new IOException("Não foi possível ativar porque um ou mais arquivos de destino já existem.");

            foreach (var item in disabledFiles)
            {
                var target = Path.Combine(_settings.GameRoot!, Path.GetRelativePath(DisabledRoot(mod), item));
                RestoreFile(item, target);
            }

            mod.Enabled = true;
            SaveState();
            RefreshMods();
            Log("Mod ativado: " + mod.Name);
        }
        catch (Exception ex)
        {
            Log("Falha ao ativar: " + ex.Message);
        }
    }

    private void DisableSelected()
    {
        var mod = SelectedMod();
        if (mod == null || !mod.Enabled)
            return;
        if (!EnsureGameRoot())
            return;

        try
        {
            var root = DisabledRoot(mod);
            Directory.CreateDirectory(root);

            foreach (var target in mod.InstalledFiles.ToList())
            {
                if (!File.Exists(target))
                    continue;

                var rel = Path.GetRelativePath(_settings.GameRoot!, target);
                var disabled = Path.Combine(root, rel);
                MoveOrCopy(target, disabled);
            }

            mod.Enabled = false;
            SaveState();
            RefreshMods();
            Log("Mod desativado: " + mod.Name);
        }
        catch (Exception ex)
        {
            Log("Falha ao desativar: " + ex.Message);
        }
    }

    private string DisabledRoot(ModRecord mod) => Path.Combine(DisabledRootBase, SafeId(mod.Id));

    private IEnumerable<string> DisabledFilesFor(ModRecord mod)
    {
        var root = DisabledRoot(mod);
        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            : Enumerable.Empty<string>();
    }

    private static void RestoreFile(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
            throw new IOException("Conflito ao reativar: " + target);
        MoveOrCopy(source, target);
    }

    private static void MoveOrCopy(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            if (File.Exists(target))
                File.Delete(target);
            File.Move(source, target);
        }
        catch (IOException)
        {
            File.Copy(source, target, true);
            File.Delete(source);
        }
    }

    private void RemoveSelected()
    {
        var mod = SelectedMod();
        if (mod == null)
            return;

        var answer = MessageBox.Show(
            this,
            $"Remover '{mod.Name}' do gerenciador e do jogo?\n\nArquivos originais sobrescritos pelo mod terão seus backups restaurados quando disponíveis.",
            "Confirmar remoção",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (answer != DialogResult.Yes)
            return;

        try
        {
            RemoveRecordFiles(mod);
            _db.Mods.Remove(mod);
            SaveState();
            RefreshMods();
            Log("Mod removido: " + mod.Name);
        }
        catch (Exception ex)
        {
            Log("Falha ao remover: " + ex.Message);
        }
    }

    private void RemoveRecordFiles(ModRecord mod)
    {
        foreach (var file in mod.InstalledFiles.ToList())
        {
            try
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
            catch (Exception ex)
            {
                Log("Não foi possível remover " + file + ": " + ex.Message);
            }
        }

        foreach (var backup in mod.Backups.ToList())
        {
            try
            {
                if (!File.Exists(backup.Value))
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(backup.Key)!);
                if (File.Exists(backup.Key))
                    File.Delete(backup.Key);
                File.Copy(backup.Value, backup.Key, true);
            }
            catch (Exception ex)
            {
                Log("Não foi possível restaurar backup " + backup.Key + ": " + ex.Message);
            }
        }

        var disabled = DisabledRoot(mod);
        if (Directory.Exists(disabled))
        {
            try { Directory.Delete(disabled, true); } catch { }
        }
    }

    // App ID do Satisfactory na Steam. Usado para iniciar via protocolo "steam://rungameid/"
    // quando a cópia detectada for da Steam, evitando chamar o binário do Engine diretamente.
    private const string SteamAppId = "526870";

    // Referência do Satisfactory Mod Loader no ficsit.app. Praticamente todo mod uPlugin
    // depende dele; se estiver ausente, o jogo mostra "Missing Plugin ... missing
    // dependency on the 'SML' plugin" para cada mod instalado, mesmo que os mods em si
    // estejam corretos.
    //
    // IMPORTANTE: o texto "SML" NÃO é o identificador real do mod na API (a API responde
    // "ent: mod not found" para ele). O ficsit.app dá a mods recentes uma referência legível
    // (ex.: "AreaActions"), mas o SML é um dos mods mais antigos da plataforma e ficou com um
    // ID opaco antigo, confirmado na URL pública de uma de suas versões
    // (ficsit.app/mod/rpLvf1Q5igJXc6/version/...). É esse ID que a consulta GraphQL precisa.
    private const string SmlModId = "rpLvf1Q5igJXc6";

    private async void LaunchGame()
    {
        if (!EnsureGameRoot())
            return;

        await EnsureSmlInstalledAsync();

        var root = _settings.GameRoot!;

        // 1) Se o usuário escolheu manualmente um executável, sempre respeita essa escolha.
        var overridePath = _settings.GameExeOverride;
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (File.Exists(overridePath))
            {
                StartProcess(overridePath, "Jogo iniciado (executável selecionado manualmente): " + overridePath);
                return;
            }

            Log("Aviso: o executável selecionado manualmente não existe mais (" + overridePath + "). Voltando à detecção automática.");
        }

        // 2) Prefer iniciar via Steam quando a instalação parecer ser da Steam: isso deixa o
        // próprio Steam preparar o ambiente (DRM/overlay/paths) e evita o erro clássico
        // "Failed to open descriptor file .../FactoryGameSteam.uproject", que acontece quando
        // o binário Engine\Binaries\Win64\FactoryGameSteam-Win64-Shipping.exe é executado
        // diretamente: ele é compilado como o "Target" FactoryGameSteam, mas a pasta do
        // projeto no disco chama-se apenas "FactoryGame" — o Unreal só resolve esse
        // descompasso de nomes corretamente quando o Steam faz a chamada.
        if (_settings.PreferSteamLaunch && LooksLikeSteamInstall(root) && IsSteamAvailable())
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"steam://rungameid/{SteamAppId}",
                    UseShellExecute = true
                });
                Log("Jogo iniciado via Steam (app " + SteamAppId + ").");
                return;
            }
            catch (Exception ex)
            {
                Log("Não foi possível iniciar via Steam (" + ex.Message + "); tentando executável local...");
            }
        }

        // 3) Detecção automática do executável local, priorizando os "stubs" da raiz da
        // instalação (que fazem a preparação correta antes de chamar o Engine) em vez do
        // binário Shipping bruto.
        var exe = FindGameExe(root);
        if (exe == null)
        {
            MessageBox.Show(this,
                "Não encontrei o executável do jogo. Verifique a instalação ou use \"Selecionar executável\" para apontar manualmente.",
                "Satisfactory",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        StartProcess(exe, "Jogo iniciado: " + exe);
    }

    /// <summary>
    /// Garante que o SML (Satisfactory Mod Loader) esteja instalado antes de iniciar o jogo.
    /// Sem ele, todo mod uPlugin falha ao carregar com "missing dependency on the 'SML' plugin".
    /// Baixa e instala automaticamente pelo ficsit.app quando ausente.
    /// </summary>
    private async Task EnsureSmlInstalledAsync()
    {
        if (IsSmlInstalled())
            return;

        Log("SML (Satisfactory Mod Loader) não encontrado; baixando automaticamente do ficsit.app...");
        _progress.Visible = true;
        try
        {
            var downloaded = await FicsitApiClient.DownloadModAsync(SmlModId, string.Empty, _dataRoot);
            InstallPath(downloaded.FilePath);
            RefreshMods();
            SaveState();
            Log($"SML instalado automaticamente: versão {downloaded.Version}.");
        }
        catch (Exception ex)
        {
            Log("Não foi possível instalar o SML automaticamente (" + ex.Message + "). " +
                "Baixe manualmente em ficsit.app/mod/SML e arraste o pacote para esta janela, " +
                "ou os mods continuarão mostrando \"missing dependency on the 'SML' plugin\".");
        }
        finally
        {
            _progress.Visible = false;
        }
    }

    private bool IsSmlInstalled()
    {
        // 1) Já cadastrado no banco do gerenciador e com arquivos existentes.
        var record = _db.Mods.FirstOrDefault(m =>
            m.Id.Equals(SmlModId, StringComparison.OrdinalIgnoreCase) ||
            m.Name.Contains("Mod Loader", StringComparison.OrdinalIgnoreCase) ||
            m.Name.Equals("SML", StringComparison.OrdinalIgnoreCase));
        if (record != null && record.Enabled && record.InstalledFiles.Any(File.Exists))
            return true;

        // 2) Verificação direta no disco: procura um .uplugin de SML dentro de Mods/,
        // independente do que está (ou não) registrado no banco do gerenciador.
        try
        {
            if (!Directory.Exists(ModsRoot))
                return false;

            foreach (var uplugin in Directory.EnumerateFiles(ModsRoot, "*.uplugin", SearchOption.AllDirectories))
            {
                var name = Path.GetFileNameWithoutExtension(uplugin);
                if (name.Equals("SML", StringComparison.OrdinalIgnoreCase))
                    return true;

                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(uplugin));
                    if (doc.RootElement.TryGetProperty("FriendlyName", out var friendly) &&
                        friendly.ValueKind == JsonValueKind.String &&
                        (friendly.GetString() ?? string.Empty).Contains("Mod Loader", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    // .uplugin ilegível: ignora e segue verificando os demais.
                }
            }
        }
        catch (Exception ex)
        {
            Log("Aviso: não foi possível verificar a presença do SML: " + ex.Message);
        }

        return false;
    }

    private void StartProcess(string exe, string successLogMessage)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = true
            });
            Log(successLogMessage);
        }
        catch (Exception ex)
        {
            Log("Falha ao iniciar: " + ex.Message);
            MessageBox.Show(this,
                "Não foi possível iniciar o executável selecionado:\n" + exe + "\n\n" + ex.Message,
                "Satisfactory",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    /// <summary>Escolhe manualmente o executável que o botão "Iniciar jogo" deve usar.</summary>
    private void PickGameExe()
    {
        var root = _settings.GameRoot;
        using var dlg = new OpenFileDialog
        {
            Title = "Selecione o executável do jogo (ex.: FactoryGameSteam.exe na raiz da instalação)",
            Filter = "Executáveis (*.exe)|*.exe|Todos os arquivos|*.*",
            CheckFileExists = true
        };

        if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            dlg.InitialDirectory = root;

        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        _settings.GameExeOverride = dlg.FileName;
        SaveState();
        UpdateGameStatus();
        Log("Executável do jogo definido manualmente: " + dlg.FileName);
    }

    /// <summary>Limpa a escolha manual e volta a usar detecção automática/Steam.</summary>
    private void UseAutoGameExe()
    {
        if (string.IsNullOrWhiteSpace(_settings.GameExeOverride))
            return;

        _settings.GameExeOverride = null;
        SaveState();
        UpdateGameStatus();
        Log("Voltando à detecção automática do executável do jogo.");
    }

    private static bool LooksLikeSteamInstall(string root)
    {
        var normalized = root.Replace('/', '\\');
        return normalized.IndexOf("steamapps", StringComparison.OrdinalIgnoreCase) >= 0
               || File.Exists(Path.Combine(root, "FactoryGameSteam.exe"));
    }

    private static bool IsSteamAvailable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam");
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindGameExe(string root)
    {
        // Desde a atualização 1.0 (set/2024), o binário do jogo mudou de
        // "<root>\FactoryGame\Binaries\Win64" para "<root>\Engine\Binaries\Win64",
        // e o nome do executável passou a variar por loja. A pasta "FactoryGame"
        // continua existindo (é onde ficam Mods/Configs), só não é mais onde o .exe mora.
        //
        // IMPORTANTE: os "stubs" na raiz (FactoryGameSteam.exe / FactoryGameEGS.exe /
        // FactoryGame.exe) vêm PRIMEIRO na lista de propósito. Chamar diretamente o
        // binário em Engine\Binaries\Win64\*-Win64-Shipping.exe pode falhar com
        // "Failed to open descriptor file .../FactoryGameSteam.uproject", porque esse
        // binário é o "Target" de build FactoryGameSteam, e o Unreal tenta localizar um
        // uproject numa pasta com esse mesmo nome — que não existe (a pasta real chama-se
        // "FactoryGame"). O stub da raiz faz a inicialização correta (incluindo o aviso à
        // Steam/EGS) antes de repassar para o Engine, então é a forma mais confiável de
        // iniciar o jogo fora da própria Steam/Epic Games Launcher.
        var candidates = new[]
        {
            // Stubs na raiz (preferidos: inicializam Steam/EGS corretamente antes do Engine):
            Path.Combine(root, "FactoryGameSteam.exe"),
            Path.Combine(root, "FactoryGameEGS.exe"),
            Path.Combine(root, "FactoryGame.exe"),
            // Layout atual (1.0+), por loja — usado apenas se não houver stub na raiz:
            Path.Combine(root, "Engine", "Binaries", "Win64", "FactoryGameSteam-Win64-Shipping.exe"),
            Path.Combine(root, "Engine", "Binaries", "Win64", "FactoryGameEGS-Win64-Shipping.exe"),
            Path.Combine(root, "Engine", "Binaries", "Win64", "FactoryGame-Win64-Shipping.exe"),
            // Layout antigo (pré-1.0 / builds Experimental legadas):
            Path.Combine(root, "FactoryGame", "Binaries", "Win64", "FactoryGame-Win64-Shipping.exe"),
            Path.Combine(root, "FactoryGame", "Binaries", "Win64", "FactoryGame.exe"),
        };

        var direct = candidates.FirstOrDefault(File.Exists);
        if (direct != null)
            return direct;

        // Fallback: varre as pastas conhecidas por qualquer "*-Win64-Shipping.exe".
        // Cobre lojas/variações futuras sem precisar saber o nome exato de antemão.
        foreach (var dir in new[]
                 {
                     Path.Combine(root, "Engine", "Binaries", "Win64"),
                     Path.Combine(root, "FactoryGame", "Binaries", "Win64")
                 })
        {
            if (!Directory.Exists(dir))
                continue;

            try
            {
                var match = Directory.EnumerateFiles(dir, "*-Win64-Shipping.exe").FirstOrDefault();
                if (match != null)
                    return match;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return null;
    }

    private void OpenModsFolder()
    {
        if (!EnsureGameRoot())
            return;

        Directory.CreateDirectory(ModsRoot);
        Process.Start(new ProcessStartInfo
        {
            FileName = ModsRoot,
            UseShellExecute = true
        });
    }

    private void OpenDataFolder()
    {
        Directory.CreateDirectory(_dataRoot);
        Process.Start(new ProcessStartInfo
        {
            FileName = _dataRoot,
            UseShellExecute = true
        });
    }

    private static string FormatVersion(string version)
    {
        return string.IsNullOrWhiteSpace(version) ? string.Empty : $"v{version}";
    }

    private static string SafeId(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(result) ? "mod" : result;
    }
}
