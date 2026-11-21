using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

internal static class GitCommitFiles
{
    [STAThread]
    private static void Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (sender, args) => ReportError(args.Exception);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try { Application.Run(new CommitWindow()); }
        catch (Exception error) { ReportError(error); }
    }

    private static void ReportError(Exception error)
    {
        var path = Path.Combine(Path.GetDirectoryName(typeof(GitCommitFiles).Assembly.Location), "errors.log");
        try
        {
            File.AppendAllText(path, DateTimeOffset.Now.ToString("O") + Environment.NewLine + error + Environment.NewLine + Environment.NewLine);
        }
        catch (IOException) { path = null; }
        catch (UnauthorizedAccessException) { path = null; }
        var message = path == null ? error.Message : error.Message + "\n\nПодробиці: " + path;
        MessageBox.Show(message, "Git Commit Guru", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private static Process StartGit(string folder, string arguments)
    {
        var start = new ProcessStartInfo("git.exe", arguments);
        var installedGit = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "cmd", "git.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "cmd", "git.exe")
        }.FirstOrDefault(File.Exists);
        if (installedGit != null)
        {
            start.FileName = installedGit;
        }
        start.WorkingDirectory = folder;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        return Process.Start(start);
    }

    internal static string Git(string folder, string arguments)
    {
        using (var process = StartGit(folder, arguments))
        {
            var errors = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException(errors.GetAwaiter().GetResult().Trim());
            return output;
        }
    }

    internal static List<Commit> ReadCommits(string folder)
    {
        var history = Git(folder, "log -n 20 --format=%H%x00%h%x00%P%x00%ct%x00%s HEAD");
        return history.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            var fields = line.TrimEnd('\r').Split('\0');
            return new Commit
            {
                Hash = fields[0], ShortHash = fields[1], Parent = fields[2].Split(' ')[0],
                Time = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(long.Parse(fields[3])).ToLocalTime(),
                Subject = fields[4]
            };
        }).ToList();
    }

    internal static List<Change> ReadChanges(string folder, Commit commit)
    {
        const string options = " --name-status -z -M ";
        var arguments = commit.Parent.Length == 0
            ? "diff-tree --root --no-commit-id -r" + options + commit.Hash
            : "diff --no-ext-diff" + options + commit.Parent + " " + commit.Hash;
        var fields = Git(folder, arguments + " --").Split('\0');
        var changes = new List<Change>();
        for (int index = 0; index < fields.Length - 1;)
        {
            var kind = fields[index++][0];
            var path = fields[index++];
            var previous = "";
            if (kind == 'R' || kind == 'C')
            {
                previous = path;
                path = fields[index++];
            }
            changes.Add(new Change { Kind = kind, Path = path, PreviousPath = previous });
        }
        return changes;
    }

    internal static string PrepareDrop(string repository, Commit commit, IEnumerable<Change> changes)
    {
        var root = Path.Combine(Path.GetTempPath(), "GitCommitFiles", Guid.NewGuid().ToString("N"));
        var rootPrefix = root + Path.DirectorySeparatorChar;
        foreach (var change in changes)
        {
            var destination = Path.GetFullPath(Path.Combine(root, change.Path));
            if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
                change.Path.IndexOfAny(new[] { '"', ':', '\\' }) >= 0)
                throw new InvalidOperationException("Цей шлях не підтримується у Windows: " + change.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            using (var process = StartGit(repository, "cat-file blob \"" + commit.Hash + ":" + change.Path + "\""))
            {
                var errors = process.StandardError.ReadToEndAsync();
                using (var file = File.Create(destination)) process.StandardOutput.BaseStream.CopyTo(file);
                process.WaitForExit();
                if (process.ExitCode != 0) throw new InvalidOperationException(errors.GetAwaiter().GetResult().Trim());
            }
        }
        return root;
    }
}

internal sealed class Commit
{
    internal string Hash, ShortHash, Parent, Subject;
    internal DateTime Time;
}

internal sealed class Change
{
    internal char Kind;
    internal string Path;
    internal string PreviousPath;
}

internal sealed class ShellFiles : IDisposable
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ILCreateFromPath(string path);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILFindLastID(IntPtr item);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr item);

    [DllImport("shell32.dll")]
    private static extern int SHCreateDataObject(IntPtr parent, uint count, IntPtr[] children,
        IntPtr inner, ref Guid interfaceId, out System.Runtime.InteropServices.ComTypes.IDataObject data);

    [DllImport("shell32.dll")]
    private static extern int SHDoDragDrop(IntPtr sourceWindow, System.Runtime.InteropServices.ComTypes.IDataObject data,
        IntPtr dropSource, uint allowedEffects, out uint effect);

    private readonly List<IntPtr> items = new List<IntPtr>();
    internal System.Runtime.InteropServices.ComTypes.IDataObject Data { get; private set; }

    internal ShellFiles(string[] paths)
    {
        try
        {
            var parent = Path.GetDirectoryName(paths[0]);
            if (paths.Any(path => !string.Equals(Path.GetDirectoryName(path), parent, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Файли для перенесення мають бути в одній підготовленій папці.");
            var parentId = GetItem(parent);
            var children = paths.Select(path => ILFindLastID(GetItem(path))).ToArray();
            var interfaceId = typeof(System.Runtime.InteropServices.ComTypes.IDataObject).GUID;
            System.Runtime.InteropServices.ComTypes.IDataObject data;
            Marshal.ThrowExceptionForHR(SHCreateDataObject(parentId, (uint)children.Length, children, IntPtr.Zero, ref interfaceId, out data));
            Data = data;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private IntPtr GetItem(string path)
    {
        var item = ILCreateFromPath(path);
        if (item == IntPtr.Zero) throw new IOException("Windows не знайшла шлях: " + path);
        items.Add(item);
        return item;
    }

    internal bool DragFrom(IntPtr window)
    {
        uint effect;
        Marshal.ThrowExceptionForHR(SHDoDragDrop(window, Data, IntPtr.Zero, 1, out effect));
        return effect != 0;
    }

    public void Dispose()
    {
        if (Data != null)
        {
            Marshal.ReleaseComObject(Data);
            Data = null;
        }
        foreach (var item in items) ILFree(item);
        items.Clear();
    }
}

internal sealed class CommitWindow : Form
{
    private bool dark = true;
    private bool sidebarCollapsed;
    private string language = "en";
    private readonly string settingsFile;
    private readonly Dictionary<Control, string> translated = new Dictionary<Control, string>();
    private readonly Dictionary<Button, bool> buttons = new Dictionary<Button, bool>();
    private readonly List<Panel> contentPanes = new List<Panel>();
    private Panel sidebar, caption;
    private Label projectsHeading;
    private Button themeButton, sidebarButton, maximizeButton, languageButton;
    private readonly ContextMenuStrip languageMenu = new ContextMenuStrip { ShowImageMargin = false };
    private readonly ToolTip tooltips = new ToolTip();
    private Color WindowColor { get { return dark ? Color.FromArgb(17, 20, 29) : Color.FromArgb(238, 242, 250); } }
    private Color SidebarColor { get { return dark ? Color.FromArgb(22, 26, 38) : Color.FromArgb(224, 230, 244); } }
    private Color PanelColor { get { return dark ? Color.FromArgb(25, 30, 44) : Color.White; } }
    private Color ButtonColor { get { return dark ? Color.FromArgb(34, 41, 59) : Color.FromArgb(219, 226, 243); } }
    private Color HoverColor { get { return dark ? Color.FromArgb(45, 55, 80) : Color.FromArgb(202, 213, 240); } }
    private Color TextColor { get { return dark ? Color.FromArgb(234, 239, 251) : Color.FromArgb(28, 39, 64); } }
    private Color MutedColor { get { return dark ? Color.FromArgb(156, 170, 196) : Color.FromArgb(77, 91, 119); } }
    private Color AccentColor { get { return dark ? Color.FromArgb(157, 167, 255) : Color.FromArgb(60, 74, 171); } }
    private Color AccentInk { get { return dark ? Color.FromArgb(21, 26, 55) : Color.White; } }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    private static readonly Dictionary<char, string> Labels = new Dictionary<char, string>
    {
        { 'A', "Новий" }, { 'M', "Змінений" }, { 'D', "Видалений" },
        { 'R', "Перейменований" }, { 'C', "Скопійований" }, { 'T', "Змінений тип" }
    };

    private static readonly Dictionary<string, string[]> Translations = new Dictionary<string, string[]>
    {
        { "Проєкти", new[] { "Projects", "Проекты" } },
        { "＋ Додати проєкт…", new[] { "+ Add project…", "+ Добавить проект…" } },
        { "Прибрати зі списку", new[] { "Remove from list", "Убрать из списка" } },
        { "Оновити", new[] { "Refresh", "Обновить" } },
        { "Час коміту", new[] { "Commit time", "Время коммита" } },
        { "Коміт", new[] { "Commit", "Коммит" } },
        { "Коміти", new[] { "Commits", "Коммиты" } },
        { "Файли коміту", new[] { "Changed files", "Файлы коммита" } },
        { "Папки та файли", new[] { "Folders and files", "Папки и файлы" } },
        { "Зміна", new[] { "Change", "Изменение" } },
        { "Відкрити в проєкті", new[] { "Open in project", "Открыть в проекте" } },
        { "Копіювати шляхи", new[] { "Copy paths", "Копировать пути" } },
        { "Вибери проєкт", new[] { "Select a project", "Выбери проект" } },
        { "Додай локальну папку Git через бічну панель", new[] { "Add a local Git folder from the sidebar", "Добавь локальную папку Git через боковую панель" } },
        { "Вибери локальну папку Git-проєкту", new[] { "Select a local Git project folder", "Выбери локальную папку Git-проекта" } },
        { "Світла тема", new[] { "Light theme", "Светлая тема" } },
        { "Темна тема", new[] { "Dark theme", "Тёмная тема" } },
        { "Показати проєкти", new[] { "Show projects", "Показать проекты" } },
        { "Згорнути проєкти", new[] { "Hide projects", "Свернуть проекты" } },
        { "Мова", new[] { "Language", "Язык" } },
        { "Новий", new[] { "Added", "Добавлен" } },
        { "Змінений", new[] { "Modified", "Изменён" } },
        { "Видалений", new[] { "Deleted", "Удалён" } },
        { "Перейменований", new[] { "Renamed", "Переименован" } },
        { "Скопійований", new[] { "Copied", "Скопирован" } },
        { "Змінений тип", new[] { "Type changed", "Изменён тип" } },
        { "Подвійний клік: згорнути або розгорнути", new[] { "Double-click to collapse or expand", "Двойной щелчок: свернуть или развернуть" } },
        { "{0} файлів · Ctrl, Shift або рамка", new[] { "Files: {0}  ·  Ctrl, Shift or drag a selection box", "Файлов: {0}  ·  Ctrl, Shift или рамка" } },
        { "Проєкт прибрано зі списку", new[] { "Project removed from the list", "Проект убран из списка" } },
        { "Не вдалося прочитати список проєктів", new[] { "Could not read saved projects", "Не удалось прочитать список проектов" } },
        { "Не вдалося зберегти проєкти поруч із програмою.", new[] { "Could not save projects beside the application.", "Не удалось сохранить проекты рядом с приложением." } },
        { "Список проєктів", new[] { "Project list", "Список проектов" } },
        { "Не вдалося запустити git.exe. Перевір встановлення Git for Windows.", new[] { "Could not start git.exe. Check your Git for Windows installation.", "Не удалось запустить git.exe. Проверь установку Git for Windows." } },
        { "Git недоступний", new[] { "Git unavailable", "Git недоступен" } },
        { "Не вдалося прочитати Git", new[] { "Could not read Git repository", "Не удалось прочитать Git-репозиторий" } },
        { "Не вдалося прочитати коміт", new[] { "Could not read commit", "Не удалось прочитать коммит" } },
        { "Файли передано в цільове вікно", new[] { "Files handed to the destination window", "Файлы переданы в целевое окно" } },
        { "Цільове вікно не прийняло файли", new[] { "The destination window did not accept the files", "Целевое окно не приняло файлы" } },
        { "Не вдалося підготувати файли", new[] { "Could not prepare files", "Не удалось подготовить файлы" } },
        { "Файли у буфері обміну", new[] { "Files copied to clipboard", "Файлы скопированы в буфер обмена" } },
        { "Шляхів скопійовано: {0}", new[] { "Paths copied: {0}", "Путей скопировано: {0}" } },
    };

    private string T(string text)
    {
        string[] localized;
        return language != "uk" && Translations.TryGetValue(text, out localized) ? localized[language == "ru" ? 1 : 0] : text;
    }

    private void SetCountStatus() { status.Text = string.Format(T("{0} файлів · Ctrl, Shift або рамка"), changes.Count); }

    private string folder;
    private readonly string projectsFile;
    private readonly List<string> projects = new List<string>();
    private bool updatingProjectList;
    private readonly ListBox projectList = new ListBox
    {
        Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed,
        ItemHeight = 57, IntegralHeight = false
    };
    private Commit selectedCommit;
    private List<Change> changes = new List<Change>();
    private readonly Label projectTitle = new Label { Dock = DockStyle.Top, Height = 38, AutoEllipsis = true, Text = "Вибери проєкт" };
    private readonly Label folderLabel = new Label { Dock = DockStyle.Top, Height = 26, AutoEllipsis = true, Text = "Додай локальну папку Git через бічну панель" };
    private readonly Label commitLabel = new Label { Dock = DockStyle.Top, Height = 32, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label status = new Label { AutoSize = true, Margin = new Padding(12, 8, 0, 0) };
    private readonly ListView history = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, ShowItemToolTips = true };
    private readonly TreeNode fileRoot = new TreeNode();
    private readonly ListView tree = new ListView
    {
        Dock = DockStyle.Fill, View = View.Details, HeaderStyle = ColumnHeaderStyle.Nonclickable,
        MultiSelect = true, FullRowSelect = false, HideSelection = false, ShowItemToolTips = true
    };

    internal CommitWindow() : this(Path.Combine(Path.GetDirectoryName(typeof(CommitWindow).Assembly.Location), "projects.txt")) { }

    internal CommitWindow(string projectsFile)
    {
        this.projectsFile = projectsFile;
        settingsFile = Path.Combine(Path.GetDirectoryName(projectsFile), "settings.txt");
        ReadAppearance();
        Text = "Git Commit Guru";
        Icon = Icon.ExtractAssociatedIcon(typeof(CommitWindow).Assembly.Location);
        FormBorderStyle = FormBorderStyle.None;
        Padding = new Padding(6);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1250, 735);
        MinimumSize = new Size(940, 480);
        Font = new Font("Segoe UI", 10);
        BackColor = WindowColor;
        ForeColor = TextColor;
        folderLabel.ForeColor = MutedColor;
        status.ForeColor = MutedColor;
        projectTitle.Font = new Font("Segoe UI Semibold", 20);
        commitLabel.Font = new Font("Segoe UI Semibold", 10);
        commitLabel.ForeColor = AccentColor;
        sidebar = CreateSidebar();
        var refresh = Button("Оновити", delegate { if (folder != null) LoadFolder(folder); });
        refresh.Dock = DockStyle.Right;
        var header = new Panel { Dock = DockStyle.Top, Height = 77 };
        var identity = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 12, 0) };
        identity.Controls.AddRange(new Control[] { folderLabel, projectTitle });
        var headerActions = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 124, FlowDirection = FlowDirection.RightToLeft };
        headerActions.Controls.Add(refresh);
        header.Controls.AddRange(new Control[] { identity, headerActions });

        history.Columns.Add("Час коміту", 126);
        history.Columns.Add("Коміт", 245);
        history.SizeChanged += delegate { history.Columns[1].Width = Math.Max(80, history.ClientSize.Width - history.Columns[0].Width - 3); };
        history.SelectedIndexChanged += SelectCommit;
        tree.Columns.Add("Папки та файли", 380);
        tree.Columns.Add("Зміна", 130);
        tree.SizeChanged += delegate { tree.Columns[0].Width = Math.Max(125, tree.ClientSize.Width - tree.Columns[1].Width - 3); };
        tree.SmallImageList = CreateFileIcons();
        tree.ItemDrag += DragFiles;
        tree.DoubleClick += OpenSelected;
        tree.KeyDown += HandleTreeKeys;
        StyleList(history);
        StyleList(tree);
        var panes = new SplitContainer { Dock = DockStyle.Fill };
        panes.Width = 930;
        panes.SplitterDistance = 335;
        panes.SplitterWidth = 12;
        panes.Panel1MinSize = 233;
        panes.Panel2MinSize = 280;
        panes.Panel1.Controls.Add(CreatePane("Коміти", history));
        panes.Panel2.Controls.Add(CreatePane("Файли коміту", tree, commitLabel));

        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(0, 8, 0, 0) };
        footer.Controls.AddRange(new Control[] { Button("Відкрити в проєкті", OpenInProject, true), Button("Копіювати шляхи", CopyPaths), status });
        var workspace = new Panel { Dock = DockStyle.Fill, Padding = new Padding(19, 17, 19, 12) };
        workspace.Controls.AddRange(new Control[] { panes, footer, header });
        caption = CreateCaption();
        Controls.AddRange(new Control[] { workspace, sidebar, caption });
        ApplyAppearance();

        if (File.Exists(projectsFile))
        {
            try
            {
                projects.AddRange(File.ReadAllLines(projectsFile, Encoding.UTF8)
                    .Where(path => !string.IsNullOrWhiteSpace(path)).Select(NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase));
            }
            catch (IOException error) { status.Text = T("Не вдалося прочитати список проєктів") + ": " + error.Message; }
            catch (UnauthorizedAccessException error) { status.Text = T("Не вдалося прочитати список проєктів") + ": " + error.Message; }
            UpdateProjectsList();
            var available = projects.FirstOrDefault(Directory.Exists);
            if (available != null) LoadFolder(available);
            return;
        }

        var executableFolder = Path.GetDirectoryName(typeof(CommitWindow).Assembly.Location);
        for (var directory = new DirectoryInfo(executableFolder); directory != null; directory = directory.Parent)
        {
            var gitPath = Path.Combine(directory.FullName, ".git");
            if (!Directory.Exists(gitPath) && !File.Exists(gitPath)) continue;
            LoadFolder(directory.FullName);
            break;
        }
    }

    private Panel CreateSidebar()
    {
        var sidebar = new Panel { Dock = DockStyle.Left, Width = 253, BackColor = SidebarColor, Padding = new Padding(13, 17, 13, 14) };
        var branding = new Panel { Dock = DockStyle.Top, Height = 66 };
        var brand = new Label
        {
            Dock = DockStyle.Top, Height = 31, Text = "Git Commit Guru", ForeColor = TextColor,
            Font = new Font("Segoe UI Semibold", 17), Padding = new Padding(7, 0, 0, 0)
        };
        projectsHeading = new Label
        {
            Dock = DockStyle.Top, Height = 31, Text = "Проєкти", ForeColor = MutedColor,
            Padding = new Padding(8, 10, 0, 0), Font = new Font("Segoe UI Semibold", 10)
        };
        translated.Add(projectsHeading, "Проєкти");
        branding.Controls.AddRange(new Control[] { projectsHeading, brand });

        var sidebarActions = new Panel { Dock = DockStyle.Bottom, Height = 84, Padding = new Padding(4, 0, 4, 0) };
        var add = Button("＋ Додати проєкт…", ChooseFolder, true);
        add.Dock = DockStyle.Top;
        add.Height = 38;
        var remove = Button("Прибрати зі списку", delegate
        {
            if (projectList.SelectedItem is string) RemoveProject((string)projectList.SelectedItem);
        });
        remove.Dock = DockStyle.Bottom;
        remove.Height = 35;
        sidebarActions.Controls.AddRange(new Control[] { remove, add });
        projectList.DrawItem += DrawProject;
        projectList.SelectedIndexChanged += delegate
        {
            var path = projectList.SelectedItem as string;
            if (!updatingProjectList && path != null && !string.Equals(path, folder, StringComparison.OrdinalIgnoreCase))
                LoadFolder(path);
        };
        sidebar.Controls.AddRange(new Control[] { projectList, sidebarActions, branding });
        return sidebar;
    }

    private Panel CreateCaption()
    {
        var bar = new Panel { Dock = DockStyle.Top, Height = 43, Padding = new Padding(0, 3, 0, 4) };
        sidebarButton = Button("☰", delegate
        {
            sidebarCollapsed = !sidebarCollapsed;
            sidebar.Visible = !sidebarCollapsed;
            UpdateAppearanceLabels();
            SaveAppearance();
        });
        sidebarButton.AutoSize = false;
        sidebarButton.Width = 44;
        sidebarButton.Dock = DockStyle.Left;
        var brand = new Label { Dock = DockStyle.Fill, Text = "Git Commit Guru", TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(11, 0, 0, 0) };
        var mark = new PictureBox { Dock = DockStyle.Left, Width = 28, Padding = new Padding(8, 7, 0, 7), SizeMode = PictureBoxSizeMode.Zoom, Image = Icon.ToBitmap() };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Width = 350 };
        var close = Button("×", delegate { Close(); });
        maximizeButton = Button("□", delegate { ToggleMaximize(); });
        var minimize = Button("−", delegate { WindowState = FormWindowState.Minimized; });
        foreach (var button in new[] { close, maximizeButton, minimize })
        {
            button.AutoSize = false;
            button.Size = new Size(34, 32);
            button.Margin = new Padding(2, 0, 0, 0);
            actions.Controls.Add(button);
        }
        themeButton = Button("", delegate { dark = !dark; ApplyAppearance(); SaveAppearance(); });
        themeButton.AutoSize = false;
        themeButton.Size = new Size(110, 32);
        themeButton.Margin = new Padding(5, 0, 4, 0);
        actions.Controls.Add(themeButton);
        languageButton = Button("", delegate { languageMenu.Show(languageButton, new Point(0, languageButton.Height + 3)); });
        languageButton.AutoSize = false;
        languageButton.Size = new Size(105, 32);
        languageButton.Margin = new Padding(0);
        foreach (var option in new[] { "English", "Українська", "Русский" })
        {
            var item = new ToolStripMenuItem(option);
            item.Tag = new[] { "en", "uk", "ru" }[languageMenu.Items.Count];
            item.Click += delegate
            {
                language = (string)item.Tag;
                ApplyAppearance();
                SaveAppearance();
            };
            languageMenu.Items.Add(item);
        }
        actions.Controls.Add(languageButton);
        bar.Controls.AddRange(new Control[] { brand, actions, mark, sidebarButton });
        bar.MouseDown += MoveWindow;
        brand.MouseDown += MoveWindow;
        mark.MouseDown += MoveWindow;
        return bar;
    }

    private void MoveWindow(object sender, MouseEventArgs args)
    {
        if (args.Button != MouseButtons.Left) return;
        if (args.Clicks > 1) { ToggleMaximize(); return; }
        if (WindowState != FormWindowState.Normal) return;
        ReleaseCapture();
        SendMessage(Handle, 0x00A1, new IntPtr(2), IntPtr.Zero);
    }

    private void ToggleMaximize()
    {
        MaximizedBounds = Screen.FromControl(this).WorkingArea;
        WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        maximizeButton.Text = WindowState == FormWindowState.Maximized ? "❐" : "□";
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg != 0x0084 || WindowState != FormWindowState.Normal || message.Result.ToInt64() != 1) return;
        var point = message.LParam.ToInt64();
        var local = PointToClient(new Point(unchecked((short)point), unchecked((short)(point >> 16))));
        var left = local.X < Padding.Left;
        var right = local.X >= ClientSize.Width - Padding.Right;
        var top = local.Y < Padding.Top;
        var bottom = local.Y >= ClientSize.Height - Padding.Bottom;
        if (top) message.Result = new IntPtr(left ? 13 : right ? 14 : 12);
        else if (bottom) message.Result = new IntPtr(left ? 16 : right ? 17 : 15);
        else if (left) message.Result = new IntPtr(10);
        else if (right) message.Result = new IntPtr(11);
    }

    private void ReadAppearance()
    {
        try
        {
            if (!File.Exists(settingsFile)) return;
            foreach (var line in File.ReadAllLines(settingsFile, Encoding.UTF8))
            {
                if (line == "theme=light") dark = false;
                if (line == "sidebar=collapsed") sidebarCollapsed = true;
                if (line == "language=uk") language = "uk";
                if (line == "language=ru") language = "ru";
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void SaveAppearance()
    {
        try
        {
            File.WriteAllLines(settingsFile, new[] { "theme=" + (dark ? "dark" : "light"), "language=" + language,
                "sidebar=" + (sidebarCollapsed ? "collapsed" : "open") });
        }
        catch (IOException error) { status.Text = error.Message; }
        catch (UnauthorizedAccessException error) { status.Text = error.Message; }
    }

    private void UpdateAppearanceLabels()
    {
        themeButton.Text = T(dark ? "Світла тема" : "Темна тема");
        sidebarButton.AccessibleName = T(sidebarCollapsed ? "Показати проєкти" : "Згорнути проєкти");
        tooltips.SetToolTip(sidebarButton, sidebarButton.AccessibleName);
        languageButton.AccessibleName = T("Мова");
        tooltips.SetToolTip(languageButton, languageButton.AccessibleName);
        foreach (ToolStripMenuItem item in languageMenu.Items)
            if ((string)item.Tag == language) languageButton.Text = item.Text + " ▾";
    }

    private void ApplyAppearance()
    {
        SuspendLayout();
        BackColor = WindowColor;
        ForeColor = TextColor;
        caption.BackColor = SidebarColor;
        sidebar.BackColor = SidebarColor;
        sidebar.Visible = !sidebarCollapsed;
        projectList.BackColor = SidebarColor;
        projectList.ForeColor = TextColor;
        projectList.Invalidate();
        languageMenu.Renderer = new ToolStripProfessionalRenderer(new MenuColors(PanelColor, HoverColor, ButtonColor));
        languageMenu.BackColor = PanelColor;
        foreach (ToolStripMenuItem item in languageMenu.Items) item.ForeColor = TextColor;
        projectsHeading.ForeColor = MutedColor;
        folderLabel.ForeColor = MutedColor;
        status.ForeColor = MutedColor;
        commitLabel.ForeColor = AccentColor;
        foreach (var pane in contentPanes) pane.BackColor = PanelColor;
        foreach (var pair in buttons)
        {
            pair.Key.BackColor = pair.Value ? AccentColor : ButtonColor;
            pair.Key.ForeColor = pair.Value ? AccentInk : TextColor;
            pair.Key.FlatAppearance.MouseOverBackColor = pair.Value
                ? (dark ? Color.FromArgb(184, 192, 255) : Color.FromArgb(74, 90, 194)) : HoverColor;
        }
        foreach (var pair in translated) pair.Key.Text = T(pair.Value);
        foreach (var list in new[] { history, tree })
        {
            list.BackColor = PanelColor;
            list.ForeColor = TextColor;
            list.Invalidate(true);
        }
        history.Columns[0].Text = T("Час коміту");
        history.Columns[1].Text = T("Коміт");
        tree.Columns[0].Text = T("Папки та файли");
        tree.Columns[1].Text = T("Зміна");
        var oldIcons = tree.SmallImageList;
        tree.SmallImageList = CreateFileIcons();
        if (oldIcons != null) oldIcons.Dispose();
        UpdateAppearanceLabels();
        if (selectedCommit != null) { RefreshFileRows(); SetCountStatus(); }
        else { projectTitle.Text = T("Вибери проєкт"); folderLabel.Text = T("Додай локальну папку Git через бічну панель"); }
        ResumeLayout(true);
    }

    private sealed class MenuColors : ProfessionalColorTable
    {
        private readonly Color surface, selection, border;
        internal MenuColors(Color surface, Color selection, Color border)
        {
            this.surface = surface;
            this.selection = selection;
            this.border = border;
            UseSystemColors = false;
        }
        public override Color ToolStripDropDownBackground { get { return surface; } }
        public override Color MenuItemSelected { get { return selection; } }
        public override Color MenuItemBorder { get { return border; } }
        public override Color MenuBorder { get { return border; } }
    }

    private void DrawProject(object sender, DrawItemEventArgs args)
    {
        if (args.Index < 0) return;
        var path = (string)projectList.Items[args.Index];
        var name = ProjectName(path);
        var selected = (args.State & DrawItemState.Selected) != 0;
        using (var background = new SolidBrush(SidebarColor)) args.Graphics.FillRectangle(background, args.Bounds);
        var tile = Rectangle.Inflate(args.Bounds, -2, -3);
        using (var background = new SolidBrush(selected ? HoverColor : SidebarColor)) args.Graphics.FillRectangle(background, tile);
        if (selected)
        {
            using (var accent = new SolidBrush(AccentColor)) args.Graphics.FillRectangle(accent, tile.Left, tile.Top + 7, 3, tile.Height - 14);
        }
        var mark = new Rectangle(tile.Left + 10, tile.Top + 10, 30, 30);
        using (var background = new SolidBrush(ButtonColor)) args.Graphics.FillRectangle(background, mark);
        TextRenderer.DrawText(args.Graphics, name.Substring(0, 1).ToUpperInvariant(), Font, mark, AccentColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        var title = new Rectangle(tile.Left + 48, tile.Top + 4, tile.Width - 56, 22);
        var location = new Rectangle(title.Left, tile.Top + 25, title.Width, 19);
        TextRenderer.DrawText(args.Graphics, name, Font, title, TextColor, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(args.Graphics, path, Font, location, MutedColor, TextFormatFlags.PathEllipsis | TextFormatFlags.NoPrefix);
    }

    private static string ProjectName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('/', '\\'));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private static string NormalizePath(string path)
    {
        return path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    private Control CreatePane(string heading, Control content, Control detail = null)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = PanelColor, Padding = new Padding(11, 6, 11, 10) };
        var label = new Label
        {
            Dock = DockStyle.Top, Height = 31, Text = heading, TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI Semibold", 11)
        };
        translated.Add(label, heading);
        contentPanes.Add(panel);
        panel.Controls.Add(content);
        if (detail != null) panel.Controls.Add(detail);
        panel.Controls.Add(label);
        return panel;
    }

    private Button Button(string text, EventHandler click, bool primary = false)
    {
        var button = new Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            BackColor = primary ? AccentColor : ButtonColor, ForeColor = primary ? AccentInk : TextColor,
            Padding = new Padding(9, 3, 9, 3)
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(184, 192, 255) : HoverColor;
        button.Click += click;
        buttons.Add(button, primary);
        if (Translations.ContainsKey(text)) translated.Add(button, text);
        return button;
    }

    private void StyleList(ListView list)
    {
        list.BackColor = PanelColor;
        list.ForeColor = TextColor;
        list.BorderStyle = BorderStyle.None;
        list.OwnerDraw = true;
        list.DrawItem += (sender, args) => args.DrawDefault = true;
        list.DrawSubItem += (sender, args) => args.DrawDefault = true;
        list.DrawColumnHeader += (sender, args) =>
        {
            using (var background = new SolidBrush(ButtonColor)) args.Graphics.FillRectangle(background, args.Bounds);
            var inset = Rectangle.Inflate(args.Bounds, -8, 0);
            TextRenderer.DrawText(args.Graphics, args.Header.Text, list.Font, inset, TextColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
    }

    private ImageList CreateFileIcons()
    {
        var images = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
        var handle = images.Handle;
        for (int icon = 0; icon < 3; icon++)
        using (var bitmap = new Bitmap(16, 16))
        using (var graphics = Graphics.FromImage(bitmap))
        using (var pen = new Pen(icon == 2 ? MutedColor : AccentColor))
        {
            if (icon == 2)
            {
                graphics.DrawPolygon(pen, new[] { new Point(4, 2), new Point(9, 2), new Point(12, 5), new Point(12, 13), new Point(4, 13) });
                graphics.DrawLine(pen, 6, 8, 10, 8);
                graphics.DrawLine(pen, 6, 10, 10, 10);
            }
            else
            {
                graphics.DrawLines(pen, new[] { new Point(2, 12), new Point(2, 4), new Point(6, 4), new Point(8, 6), new Point(14, 6) });
                graphics.DrawPolygon(pen, new[] { new Point(2, 12), new Point(4, 7), new Point(14, 7), new Point(icon == 0 ? 14 : 12, 12) });
            }
            images.Images.Add(bitmap);
        }
        return images;
    }

    private void ChooseFolder(object sender, EventArgs args)
    {
        using (var dialog = new FolderBrowserDialog { Description = T("Вибери локальну папку Git-проєкту"), ShowNewFolderButton = false })
        {
            if (folder != null) dialog.SelectedPath = folder;
            if (dialog.ShowDialog(this) == DialogResult.OK) LoadFolder(dialog.SelectedPath);
        }
    }

    private void UpdateProjectsList()
    {
        updatingProjectList = true;
        projectList.BeginUpdate();
        try
        {
            projectList.Items.Clear();
            projectList.Items.AddRange(projects.ToArray());
            projectList.SelectedIndex = projects.FindIndex(path => string.Equals(path, folder, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            projectList.EndUpdate();
            updatingProjectList = false;
        }
    }

    private void RememberProject(string path)
    {
        if (projects.Count > 0 && string.Equals(projects[0], path, StringComparison.OrdinalIgnoreCase))
        {
            UpdateProjectsList();
            return;
        }
        projects.RemoveAll(project => string.Equals(project, path, StringComparison.OrdinalIgnoreCase));
        projects.Insert(0, path);
        SaveProjects();
    }

    private void SaveProjects()
    {
        UpdateProjectsList();
        var temporary = projectsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllLines(temporary, projects, new UTF8Encoding(false));
            if (File.Exists(projectsFile)) File.Replace(temporary, projectsFile, null);
            else File.Move(temporary, projectsFile);
        }
        catch (IOException error) { ShowSaveError(error); }
        catch (UnauthorizedAccessException error) { ShowSaveError(error); }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void ShowSaveError(Exception error)
    {
        MessageBox.Show(this, T("Не вдалося зберегти проєкти поруч із програмою.") + "\n\n" + error.Message, T("Список проєктів"));
    }

    private void RemoveProject(string path)
    {
        projects.RemoveAll(project => string.Equals(project, path, StringComparison.OrdinalIgnoreCase));
        SaveProjects();
        if (!string.Equals(folder, path, StringComparison.OrdinalIgnoreCase)) return;
        folder = null;
        selectedCommit = null;
        changes.Clear();
        history.Items.Clear();
        tree.Items.Clear();
        fileRoot.Nodes.Clear();
        projectTitle.Text = T("Вибери проєкт");
        commitLabel.Text = "";
        folderLabel.Text = T("Додай локальну папку Git через бічну панель");
        status.Text = T("Проєкт прибрано зі списку");
        var next = projects.FirstOrDefault(Directory.Exists);
        if (next != null) LoadFolder(next);
    }

    private void LoadFolder(string selected)
    {
        try
        {
            var root = NormalizePath(GitCommitFiles.Git(selected, "rev-parse --show-toplevel").TrimEnd('\r', '\n'));
            var commits = GitCommitFiles.ReadCommits(root);
            var previous = selectedCommit;
            var sameProject = string.Equals(folder, root, StringComparison.OrdinalIgnoreCase);
            folder = root;
            projectTitle.Text = ProjectName(root);
            folderLabel.Text = root;
            history.BeginUpdate();
            try
            {
                history.Items.Clear();
                foreach (var commit in commits)
                {
                    var item = new ListViewItem(new[] { commit.Time.ToString("dd.MM.yyyy HH:mm"), commit.ShortHash + "  " + commit.Subject });
                    item.Tag = commit;
                    item.ToolTipText = commit.Subject;
                    history.Items.Add(item);
                }
            }
            finally { history.EndUpdate(); }
            var selectedItem = history.Items.Cast<ListViewItem>().FirstOrDefault(item =>
                sameProject && previous != null && ((Commit)item.Tag).Hash == previous.Hash) ?? history.Items[0];
            selectedItem.Selected = true;
            selectedItem.EnsureVisible();
            ShowCommit((Commit)selectedItem.Tag);
            RememberProject(root);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, T("Не вдалося запустити git.exe. Перевір встановлення Git for Windows."), T("Git недоступний"));
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, T("Не вдалося прочитати Git")); }
    }

    private void SelectCommit(object sender, EventArgs args)
    {
        if (history.SelectedItems.Count > 0) ShowCommit((Commit)history.SelectedItems[0].Tag);
    }

    private void ShowCommit(Commit commit)
    {
        try
        {
            changes = GitCommitFiles.ReadChanges(folder, commit);
            selectedCommit = commit;
            commitLabel.Text = commit.ShortHash + "  " + commit.Subject;
            fileRoot.Nodes.Clear();
            foreach (var change in changes.OrderBy(change => change.Path, StringComparer.Ordinal))
            {
                var parts = change.Path.Split('/');
                var nodes = fileRoot.Nodes;
                for (var index = 0; index < parts.Length - 1; index++)
                {
                    var node = nodes[parts[index]] ?? nodes.Add(parts[index], parts[index]);
                    nodes = node.Nodes;
                }
                nodes.Add(new TreeNode(parts[parts.Length - 1]) { Tag = change });
            }
            collapsed.Clear();
            RefreshFileRows();
            SetCountStatus();
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, T("Не вдалося прочитати коміт")); }
    }

    private readonly HashSet<TreeNode> collapsed = new HashSet<TreeNode>();

    private void RefreshFileRows(TreeNode focus = null)
    {
        var selection = new HashSet<TreeNode>(SelectedNodes());
        tree.BeginUpdate();
        try
        {
            tree.Items.Clear();
            AddFileRows(fileRoot.Nodes, 0, selection, focus);
        }
        finally { tree.EndUpdate(); }
    }

    private void AddFileRows(TreeNodeCollection nodes, int depth, HashSet<TreeNode> selection, TreeNode focus)
    {
        foreach (TreeNode node in nodes.Cast<TreeNode>().OrderBy(node => node.Tag != null).ThenBy(node => node.Text))
        {
            var change = node.Tag as Change;
            var label = "";
            if (change != null && !Labels.TryGetValue(change.Kind, out label)) label = change.Kind.ToString();
            var item = new ListViewItem(new[] { node.Text, T(label) }, change != null ? 2 : collapsed.Contains(node) ? 0 : 1)
            {
                Tag = node,
                IndentCount = depth,
                ForeColor = change != null && change.Kind == 'D' ? MutedColor : TextColor,
                ToolTipText = change == null ? T("Подвійний клік: згорнути або розгорнути") :
                    change.PreviousPath.Length == 0 ? change.Path : change.PreviousPath + " → " + change.Path
            };
            tree.Items.Add(item);
            item.Selected = selection.Contains(node) || node == focus;
            if (node == focus) item.Focused = true;
            if (change == null && !collapsed.Contains(node)) AddFileRows(node.Nodes, depth + 1, selection, focus);
        }
    }

    private TreeNode[] SelectedNodes()
    {
        return tree.SelectedItems.Cast<ListViewItem>().Select(item => (TreeNode)item.Tag).ToArray();
    }

    private void ToggleFolder(TreeNode node)
    {
        if (!collapsed.Add(node)) collapsed.Remove(node);
        RefreshFileRows(node);
    }

    private void HandleTreeKeys(object sender, KeyEventArgs args)
    {
        if (args.Control && args.KeyCode == Keys.C)
        {
            args.SuppressKeyPress = true;
            CopySelectedFiles();
        }
        else if (args.Control && args.KeyCode == Keys.A)
        {
            args.SuppressKeyPress = true;
            foreach (ListViewItem item in tree.Items) item.Selected = true;
        }
    }

    private static IEnumerable<TreeNode> Walk(TreeNodeCollection nodes)
    {
        foreach (TreeNode node in nodes)
        {
            yield return node;
            foreach (var child in Walk(node.Nodes)) yield return child;
        }
    }

    private static IEnumerable<Change> FilesIn(TreeNode node)
    {
        var change = node.Tag as Change;
        if (change != null) return new[] { change };
        return Walk(node.Nodes).Select(child => child.Tag).OfType<Change>();
    }

    private static string FolderPath(TreeNode node)
    {
        var segments = new Stack<string>();
        for (; node != null && node.Name.Length > 0; node = node.Parent) segments.Push(node.Name);
        return string.Join(Path.DirectorySeparatorChar.ToString(), segments);
    }

    private static string[] DropPaths(string root, TreeNode[] nodes, Change[] files)
    {
        if (nodes.Length != 1) return Directory.GetFileSystemEntries(root);
        if (nodes[0].Tag is Change) return new[] { Path.GetFullPath(Path.Combine(root, files[0].Path)) };
        return new[] { Path.Combine(root, FolderPath(nodes[0])) };
    }

    private static Change[] TransferFiles(TreeNode[] nodes)
    {
        return nodes.SelectMany(FilesIn).Where(change => change.Kind != 'D').Distinct().ToArray();
    }

    private void DragFiles(object sender, ItemDragEventArgs args)
    {
        if (selectedCommit == null || args.Button != MouseButtons.Left) return;
        var dragged = (ListViewItem)args.Item;
        var nodes = dragged.Selected ? SelectedNodes() : new[] { (TreeNode)dragged.Tag };
        var files = TransferFiles(nodes);
        if (files.Length == 0) return;
        try
        {
            var root = GitCommitFiles.PrepareDrop(folder, selectedCommit, files);
            using (var shell = new ShellFiles(DropPaths(root, nodes, files)))
            {
                status.Text = T(shell.DragFrom(tree.Handle) ? "Файли передано в цільове вікно" : "Цільове вікно не прийняло файли");
            }
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, T("Не вдалося підготувати файли")); }
    }

    private void CopySelectedFiles()
    {
        var nodes = SelectedNodes();
        var files = TransferFiles(nodes);
        if (selectedCommit == null || files.Length == 0) return;
        try
        {
            var root = GitCommitFiles.PrepareDrop(folder, selectedCommit, files);
            var paths = DropPaths(root, nodes, files);
            var list = new System.Collections.Specialized.StringCollection();
            list.AddRange(paths);
            Clipboard.SetFileDropList(list);
            status.Text = T("Файли у буфері обміну");
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, T("Не вдалося підготувати файли")); }
    }

    private void CopyPaths(object sender, EventArgs args)
    {
        var paths = changes.Where(change => change.Kind != 'D').Select(change => change.Path).ToArray();
        if (paths.Length > 0) Clipboard.SetText(string.Join(Environment.NewLine, paths));
        status.Text = string.Format(T("Шляхів скопійовано: {0}"), paths.Length);
    }

    private void OpenSelected(object sender, EventArgs args)
    {
        if (tree.FocusedItem == null) return;
        var node = (TreeNode)tree.FocusedItem.Tag;
        if (node.Tag == null) ToggleFolder(node);
    }

    private void OpenInProject(object sender, EventArgs args)
    {
        if (folder == null) return;
        var selection = SelectedNodes();
        var destination = folder;
        if (selection.Length == 1)
        {
            var node = selection[0];
            var file = node.Tag as Change;
            if (file != null)
            {
                var path = Path.GetFullPath(Path.Combine(folder, file.Path));
                if (File.Exists(path))
                {
                    Process.Start("explorer.exe", "/select,\"" + path + "\"");
                    return;
                }
                destination = Path.GetDirectoryName(path);
            }
            else destination = Path.Combine(folder, FolderPath(node));
            while (destination != null && !Directory.Exists(destination)) destination = Path.GetDirectoryName(destination);
        }
        Process.Start(new ProcessStartInfo(destination ?? folder) { UseShellExecute = true });
    }
}
