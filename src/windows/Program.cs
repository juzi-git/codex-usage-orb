using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Microsoft.Win32;
using ShapePath = System.Windows.Shapes.Path;

namespace CodexUsageOrb
{
    internal sealed class LimitWindow
    {
        public double UsedPercent { get; set; }
        public int WindowMinutes { get; set; }
        public long ResetsAt { get; set; }
        public double RemainingPercent { get { return Math.Max(0, Math.Min(100, 100 - UsedPercent)); } }
    }

    internal sealed class UsageSnapshot
    {
        public DateTime TimestampUtc { get; set; }
        public LimitWindow Primary { get; set; }
        public LimitWindow Secondary { get; set; }
        public string PlanType { get; set; }

        public IEnumerable<LimitWindow> Windows
        {
            get
            {
                if (Primary != null) yield return Primary;
                if (Secondary != null) yield return Secondary;
            }
        }

        public double RemainingPercent { get { return Windows.Min(x => x.RemainingPercent); } }
    }

    /// <summary>
    /// Reads only the tail of recent Codex rollout files and extracts rate-limit fields.
    /// It deliberately avoids auth.json and does not deserialize or retain conversation content.
    /// </summary>
    internal sealed class CodexUsageReader
    {
        private const int TailBytes = 1024 * 1024;
        private readonly string sessionsRoot;
        private static readonly Regex TimestampRegex = new Regex("\\\"timestamp\\\":\\\"(?<v>[^\\\"]+)\\\"", RegexOptions.Compiled);
        private static readonly Regex PlanRegex = new Regex("\\\"plan_type\\\":(?:null|\\\"(?<v>[^\\\"]+)\\\")", RegexOptions.Compiled);
        private static readonly Regex LimitRegex = new Regex(
            "\\\"(?<kind>primary|secondary)\\\":(?<null>null|\\{(?:(?!\\},\\\"(?:primary|secondary|credits|individual_limit|spend_control_reached|plan_type|rate_limit_reached_type)\\\").)*?\\\"used_percent\\\":(?<used>[0-9.]+),(?:(?!\\},\\\"(?:primary|secondary|credits|individual_limit|spend_control_reached|plan_type|rate_limit_reached_type)\\\").)*?\\\"window_minutes\\\":(?<minutes>[0-9]+),(?:(?!\\},\\\"(?:primary|secondary|credits|individual_limit|spend_control_reached|plan_type|rate_limit_reached_type)\\\").)*?\\\"resets_at\\\":(?<reset>[0-9]+)\\})",
            RegexOptions.Compiled);

        public CodexUsageReader()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            sessionsRoot = Path.Combine(home, ".codex", "sessions");
        }

        public UsageSnapshot ReadLatest()
        {
            if (!Directory.Exists(sessionsRoot)) return null;

            IEnumerable<FileInfo> candidates;
            try
            {
                candidates = new DirectoryInfo(sessionsRoot)
                    .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(8)
                    .ToArray();
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }

            UsageSnapshot newest = null;
            foreach (FileInfo file in candidates)
            {
                string tail = ReadTail(file.FullName);
                if (String.IsNullOrEmpty(tail)) continue;
                UsageSnapshot parsed = ParseLatest(tail);
                if (parsed != null && (newest == null || parsed.TimestampUtc > newest.TimestampUtc)) newest = parsed;
            }
            return newest;
        }

        private static string ReadTail(string path)
        {
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long start = Math.Max(0, stream.Length - TailBytes);
                    stream.Seek(start, SeekOrigin.Begin);
                    byte[] bytes = new byte[stream.Length - start];
                    int read = stream.Read(bytes, 0, bytes.Length);
                    return Encoding.UTF8.GetString(bytes, 0, read);
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        internal static UsageSnapshot ParseLatest(string text)
        {
            const string marker = "\"rate_limits\":";
            int cursor = text.LastIndexOf(marker, StringComparison.Ordinal);
            while (cursor >= 0)
            {
                int lineStart = text.LastIndexOf('\n', cursor);
                int lineEnd = text.IndexOf('\n', cursor);
                if (lineEnd < 0) lineEnd = text.Length;
                string line = text.Substring(lineStart + 1, lineEnd - lineStart - 1);
                UsageSnapshot result = ParseLine(line);
                if (result != null) return result;
                if (cursor == 0) break;
                cursor = text.LastIndexOf(marker, cursor - 1, StringComparison.Ordinal);
            }
            return null;
        }

        private static UsageSnapshot ParseLine(string line)
        {
            Match tm = TimestampRegex.Match(line);
            DateTime timestamp;
            if (!tm.Success || !DateTime.TryParse(tm.Groups["v"].Value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp)) return null;

            LimitWindow primary = null;
            LimitWindow secondary = null;
            foreach (Match match in LimitRegex.Matches(line))
            {
                if (match.Groups["null"].Value == "null") continue;
                double used;
                int minutes;
                long reset;
                if (!Double.TryParse(match.Groups["used"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out used) ||
                    !Int32.TryParse(match.Groups["minutes"].Value, out minutes) ||
                    !Int64.TryParse(match.Groups["reset"].Value, out reset)) continue;
                LimitWindow window = new LimitWindow { UsedPercent = used, WindowMinutes = minutes, ResetsAt = reset };
                if (match.Groups["kind"].Value == "primary") primary = window; else secondary = window;
            }
            if (primary == null && secondary == null) return null;
            Match pm = PlanRegex.Match(line);
            return new UsageSnapshot
            {
                TimestampUtc = timestamp,
                Primary = primary,
                Secondary = secondary,
                PlanType = pm.Success ? pm.Groups["v"].Value : null
            };
        }
    }

    internal sealed class OrbSettings
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Size { get; set; }
        public string Accent { get; set; }
        public string Language { get; set; }

        public OrbSettings()
        {
            X = Double.NaN;
            Y = Double.NaN;
            Size = 168;
            Accent = "#61DC18";
            Language = "en";
        }

        public OrbSettings Clone()
        {
            return new OrbSettings { X = X, Y = Y, Size = Size, Accent = Accent, Language = Language };
        }
    }

    internal sealed class SettingsStore
    {
        private readonly string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageOrb", "settings.ini");

        public OrbSettings Load()
        {
            OrbSettings settings = new OrbSettings();
            try
            {
                string content = File.ReadAllText(path);
                if (!content.Contains("="))
                {
                    string[] legacy = content.Split(',');
                    double legacyX, legacyY;
                    if (legacy.Length == 2 && TryDouble(legacy[0], out legacyX) && TryDouble(legacy[1], out legacyY))
                    {
                        settings.X = legacyX;
                        settings.Y = legacyY;
                    }
                    return settings;
                }

                foreach (string line in content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] pair = line.Split(new[] { '=' }, 2);
                    if (pair.Length != 2) continue;
                    double value;
                    if (pair[0] == "X" && TryDouble(pair[1], out value)) settings.X = value;
                    else if (pair[0] == "Y" && TryDouble(pair[1], out value)) settings.Y = value;
                    else if (pair[0] == "Size" && TryDouble(pair[1], out value)) settings.Size = Math.Max(50, Math.Min(300, value));
                    else if (pair[0] == "Accent" && Regex.IsMatch(pair[1], "^#[0-9A-Fa-f]{6}$")) settings.Accent = pair[1].ToUpperInvariant();
                    else if (pair[0] == "Language" && (pair[1] == "en" || pair[1] == "zh")) settings.Language = pair[1];
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return settings;
        }

        public void Save(OrbSettings settings)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, String.Join(Environment.NewLine, new[]
                {
                    "X=" + settings.X.ToString(CultureInfo.InvariantCulture),
                    "Y=" + settings.Y.ToString(CultureInfo.InvariantCulture),
                    "Size=" + settings.Size.ToString(CultureInfo.InvariantCulture),
                    "Accent=" + settings.Accent,
                    "Language=" + settings.Language
                }));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static bool TryDouble(string text, out double value)
        {
            return Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }

    internal sealed class AppearanceWindow : Window
    {
        private readonly Action<OrbSettings> preview;
        private readonly TextBlock sizeValue;
        private readonly Dictionary<string, Button> presetButtons = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);
        private readonly Button customColorButton;
        public OrbSettings Value { get; private set; }

        public AppearanceWindow(Window owner, OrbSettings initial, Action<OrbSettings> previewAction)
        {
            Owner = owner;
            Value = initial;
            preview = previewAction;
            bool chinese = Value.Language == "zh";
            Title = chinese ? "悬浮球外观设置" : "Orb Appearance Settings";
            Width = 350;
            Height = 300;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            Background = new SolidColorBrush(Color.FromRgb(246, 248, 251));

            Grid root = new Grid { Margin = new Thickness(18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = root;

            TextBlock heading = new TextBlock { Text = chinese ? "调整大小、颜色和语言" : "Adjust size, color, and language", FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(28, 38, 52)) };
            root.Children.Add(heading);

            Grid languageRow = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            languageRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            languageRow.ColumnDefinitions.Add(new ColumnDefinition());
            TextBlock languageLabel = new TextBlock { Text = chinese ? "语言" : "Language", VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
            ComboBox languageBox = new ComboBox { Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
            ComboBoxItem englishItem = new ComboBoxItem { Content = "English", Tag = "en" };
            ComboBoxItem chineseItem = new ComboBoxItem { Content = "中文", Tag = "zh" };
            languageBox.Items.Add(englishItem);
            languageBox.Items.Add(chineseItem);
            languageBox.SelectedIndex = chinese ? 1 : 0;
            Grid.SetColumn(languageBox, 1);
            languageRow.Children.Add(languageLabel);
            languageRow.Children.Add(languageBox);
            Grid.SetRow(languageRow, 1);
            root.Children.Add(languageRow);

            Grid sizeRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            sizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            sizeRow.ColumnDefinitions.Add(new ColumnDefinition());
            sizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
            TextBlock sizeLabel = new TextBlock { Text = chinese ? "尺寸" : "Size", VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
            Slider sizeSlider = new Slider { Minimum = 50, Maximum = 300, Value = Value.Size, TickFrequency = 10, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
            sizeValue = new TextBlock { Text = Math.Round(Value.Size) + " px", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(sizeSlider, 1);
            Grid.SetColumn(sizeValue, 2);
            sizeRow.Children.Add(sizeLabel);
            sizeRow.Children.Add(sizeSlider);
            sizeRow.Children.Add(sizeValue);
            Grid.SetRow(sizeRow, 2);
            root.Children.Add(sizeRow);

            TextBlock colorLabel = new TextBlock { Text = chinese ? "主题颜色" : "Theme color", Margin = new Thickness(0, 12, 0, 5), FontSize = 13 };
            Grid.SetRow(colorLabel, 3);
            root.Children.Add(colorLabel);

            StackPanel colors = new StackPanel { Orientation = Orientation.Horizontal };
            AddPreset(colors, chinese ? "绿色" : "Green", "#61DC18");
            AddPreset(colors, chinese ? "蓝色" : "Blue", "#37BEFF");
            AddPreset(colors, chinese ? "紫色" : "Purple", "#A970FF");
            AddPreset(colors, chinese ? "橙色" : "Orange", "#FF9F35");
            customColorButton = new Button
            {
                Width = 42,
                Height = 42,
                Content = "+",
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(0),
                Margin = new Thickness(3),
                Background = new SolidColorBrush(Color.FromRgb(218, 218, 218)),
                Foreground = new SolidColorBrush(Color.FromRgb(28, 38, 52)),
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(3),
                ToolTip = chinese ? "自定义颜色" : "Custom color"
            };
            customColorButton.Click += OnCustomColor;
            colors.Children.Add(customColorButton);
            Grid.SetRow(colors, 4);
            root.Children.Add(colors);

            Grid footer = new Grid { Margin = new Thickness(0, 18, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Button reset = new Button { Content = chinese ? "恢复默认" : "Reset", Width = 70, Height = 30, HorizontalAlignment = HorizontalAlignment.Left };
            reset.Click += delegate { Value.Size = 168; Value.Accent = "#61DC18"; Value.Language = "en"; sizeSlider.Value = 168; languageBox.SelectedIndex = 0; ApplyPreview(); };
            StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal };
            Button cancel = new Button { Content = chinese ? "取消" : "Cancel", Width = 70, Height = 30, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            Button ok = new Button { Content = chinese ? "确定" : "OK", Width = 70, Height = 30, IsDefault = true };
            ok.Click += delegate { DialogResult = true; };
            footer.Children.Add(reset);
            actions.Children.Add(cancel);
            actions.Children.Add(ok);
            Grid.SetColumn(actions, 1);
            footer.Children.Add(actions);
            Grid.SetRow(footer, 5);
            root.Children.Add(footer);

            sizeSlider.ValueChanged += delegate
            {
                Value.Size = sizeSlider.Value;
                sizeValue.Text = Math.Round(Value.Size) + " px";
                ApplyPreview();
            };
            languageBox.SelectionChanged += delegate
            {
                ComboBoxItem selected = languageBox.SelectedItem as ComboBoxItem;
                if (selected == null) return;
                Value.Language = (string)selected.Tag;
                ApplyPreview();
            };
            UpdateColorSelection();
        }

        private void AddPreset(Panel parent, string name, string hex)
        {
            Button button = new Button
            {
                Width = 42,
                Height = 42,
                Content = "",
                Tag = hex,
                Foreground = Brushes.White,
                Background = BrushFromHex(hex),
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(3),
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(0),
                Margin = new Thickness(3),
                ToolTip = name
            };
            button.Click += delegate
            {
                Value.Accent = (string)button.Tag;
                ApplyPreview();
            };
            presetButtons[hex] = button;
            parent.Children.Add(button);
        }

        private void OnCustomColor(object sender, RoutedEventArgs e)
        {
            Color current = ColorFromHex(Value.Accent);
            using (System.Windows.Forms.ColorDialog dialog = new System.Windows.Forms.ColorDialog())
            {
                dialog.FullOpen = true;
                dialog.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                Value.Accent = String.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", dialog.Color.R, dialog.Color.G, dialog.Color.B);
                ApplyPreview();
            }
        }

        private void ApplyPreview()
        {
            UpdateColorSelection();
            preview(Value.Clone());
        }

        private void UpdateColorSelection()
        {
            bool presetSelected = false;
            foreach (KeyValuePair<string, Button> item in presetButtons)
            {
                bool selected = String.Equals(item.Key, Value.Accent, StringComparison.OrdinalIgnoreCase);
                if (selected) presetSelected = true;
                item.Value.Content = selected ? "✓" : "";
                item.Value.BorderBrush = selected
                    ? new SolidColorBrush(Color.FromRgb(28, 38, 52))
                    : Brushes.Transparent;
            }

            bool customSelected = !presetSelected;
            customColorButton.Content = customSelected ? "✓" : "+";
            customColorButton.Background = customSelected
                ? BrushFromHex(Value.Accent)
                : new SolidColorBrush(Color.FromRgb(218, 218, 218));
            customColorButton.Foreground = customSelected
                ? ContrastBrush(ColorFromHex(Value.Accent))
                : new SolidColorBrush(Color.FromRgb(28, 38, 52));
            customColorButton.BorderBrush = customSelected
                ? new SolidColorBrush(Color.FromRgb(28, 38, 52))
                : Brushes.Transparent;
        }

        private static Brush ContrastBrush(Color color)
        {
            double luminance = (0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B);
            return luminance > 150 ? Brushes.Black : Brushes.White;
        }

        internal static Color ColorFromHex(string hex)
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }

        private static Brush BrushFromHex(string hex)
        {
            return new SolidColorBrush(ColorFromHex(hex));
        }
    }

    internal sealed class OrbWindow : Window
    {
        private const double OrbSize = 168;
        private const double LiquidSize = 150;
        private readonly CodexUsageReader reader = new CodexUsageReader();
        private readonly SettingsStore settingsStore = new SettingsStore();
        private readonly Viewbox viewport;
        private readonly Grid root;
        private readonly Grid liquid;
        private readonly TextBlock percentText;
        private readonly TextBlock subtitleText;
        private readonly ShapePath waveBack;
        private readonly ShapePath waveFront;
        private readonly ToolTip detailTip;
        private readonly System.Windows.Threading.DispatcherTimer refreshTimer;
        private readonly System.Windows.Threading.DispatcherTimer waveTimer;
        private MenuItem startupItem;
        private OrbSettings settings;
        private UsageSnapshot snapshot;
        private string displayedLanguage;
        private double phase;
        private bool refreshRunning;

        public OrbWindow()
        {
            settings = settingsStore.Load();
            displayedLanguage = settings.Language;
            Title = Text(settings.Language, "Codex Usage Orb", "Codex 用量悬浮球");
            Width = settings.Size;
            Height = settings.Size;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;

            root = new Grid
            {
                Width = OrbSize,
                Height = OrbSize,
                Background = Brushes.Transparent
            };
            viewport = new Viewbox
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.Both,
                Child = root
            };
            Content = viewport;

            liquid = new Grid
            {
                Width = LiquidSize, Height = LiquidSize,
                Clip = new EllipseGeometry(new Rect(0, 0, LiquidSize, LiquidSize)),
                Background = new LinearGradientBrush(Color.FromRgb(26, 55, 62), Color.FromRgb(20, 41, 48), 90)
            };
            root.Children.Add(liquid);

            waveBack = new ShapePath { Fill = new SolidColorBrush(Color.FromArgb(180, 111, 224, 30)) };
            waveFront = new ShapePath { Fill = new LinearGradientBrush(Color.FromRgb(97, 220, 24), Color.FromRgb(42, 106, 48), 90) };
            liquid.Children.Add(waveBack);
            liquid.Children.Add(waveFront);

            StackPanel labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            percentText = new TextBlock
            {
                Text = "--", Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI"),
                FontSize = 37, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center,
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 5, ShadowDepth = 1, Opacity = 0.55 }
            };
            subtitleText = new TextBlock
            {
                Text = Text(settings.Language, "Loading", "正在读取"), Foreground = new SolidColorBrush(Color.FromArgb(220, 235, 255, 229)),
                FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 10, TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, -2, 0, 0)
            };
            labels.Children.Add(percentText);
            labels.Children.Add(subtitleText);
            root.Children.Add(labels);
            ApplyAppearance(settings);

            detailTip = new ToolTip { Placement = System.Windows.Controls.Primitives.PlacementMode.Left };
            ToolTip = detailTip;
            ContextMenu = BuildContextMenu(settings.Language);
            startupItem = (MenuItem)ContextMenu.Items[4];
            startupItem.IsCheckable = true;
            startupItem.IsChecked = IsStartupEnabled();
            ApplyLanguage(settings.Language);

            MouseLeftButtonDown += OnMouseLeftButtonDown;
            MouseDoubleClick += delegate { RefreshNow(); };
            LocationChanged += delegate
            {
                if (!IsLoaded || Double.IsNaN(Left) || Double.IsNaN(Top)) return;
                settings.X = Left;
                settings.Y = Top;
                settingsStore.Save(settings);
            };
            Loaded += OnLoaded;

            refreshTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            refreshTimer.Tick += delegate { RefreshNow(); };
            waveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(45) };
            waveTimer.Tick += delegate { phase += 0.10; DrawWaves(); };
        }

        private ContextMenu BuildContextMenu(string language)
        {
            ContextMenu menu = new ContextMenu { FontFamily = new FontFamily("Microsoft YaHei UI") };
            MenuItem title = new MenuItem { Header = Text(language, "Codex Usage Orb", "Codex 用量悬浮球"), IsEnabled = false, FontWeight = FontWeights.Bold };
            MenuItem refresh = new MenuItem { Header = Text(language, "Refresh now", "立即刷新") };
            refresh.Click += delegate { RefreshNow(); };
            MenuItem details = new MenuItem { Header = Text(language, "View details", "查看详细信息") };
            details.Click += delegate { detailTip.IsOpen = true; };
            MenuItem appearance = new MenuItem { Header = Text(language, "Appearance settings…", "外观设置…") };
            appearance.Click += OnAppearanceClick;
            MenuItem startup = new MenuItem { Header = Text(language, "Launch at startup", "开机自动启动") };
            startup.Click += OnStartupClick;
            MenuItem exit = new MenuItem { Header = Text(language, "Quit", "退出") };
            exit.Click += delegate { Close(); };
            menu.Items.Add(title);
            menu.Items.Add(refresh);
            menu.Items.Add(details);
            menu.Items.Add(appearance);
            menu.Items.Add(startup);
            menu.Items.Add(new Separator());
            menu.Items.Add(exit);
            return menu;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Rect work = SystemParameters.WorkArea;
            Left = !Double.IsNaN(settings.X) ? Math.Max(work.Left, Math.Min(settings.X, work.Right - Width)) : work.Right - Width - 28;
            Top = !Double.IsNaN(settings.Y) ? Math.Max(work.Top, Math.Min(settings.Y, work.Bottom - Height)) : work.Top + 52;
            DrawWaves();
            waveTimer.Start();
            refreshTimer.Start();
            RefreshNow();
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 1)
            {
                try { DragMove(); } catch (InvalidOperationException) { }
            }
        }

        private async void RefreshNow()
        {
            if (refreshRunning) return;
            refreshRunning = true;
            try
            {
                UsageSnapshot latest = await Task.Run(() => reader.ReadLatest());
                if (latest != null) snapshot = latest;
                UpdateDisplay();
            }
            finally { refreshRunning = false; }
        }

        private void UpdateDisplay()
        {
            if (snapshot == null)
            {
                percentText.Text = "--";
                subtitleText.Text = Text(displayedLanguage, "No data", "暂无数据");
                detailTip.Content = Text(displayedLanguage,
                    "No Codex usage data was found.\nComplete at least one Codex conversation first.",
                    "尚未找到 Codex 用量数据。\n请先在 Codex 中完成一次对话。");
                return;
            }

            double remaining = snapshot.RemainingPercent;
            percentText.Text = Math.Round(remaining).ToString("0", CultureInfo.InvariantCulture) + "%";
            LimitWindow active = snapshot.Windows.OrderBy(x => x.RemainingPercent).First();
            subtitleText.Text = Text(displayedLanguage,
                WindowName(active.WindowMinutes, displayedLanguage) + " remaining",
                WindowName(active.WindowMinutes, displayedLanguage) + "剩余");
            percentText.Foreground = remaining <= 15 ? new SolidColorBrush(Color.FromRgb(255, 238, 225)) : Brushes.White;
            detailTip.Content = BuildDetails(snapshot, displayedLanguage);
            DrawWaves();
        }

        private static string BuildDetails(UsageSnapshot value, string language)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine(Text(language, "Codex remaining usage", "Codex 剩余用量"));
            foreach (LimitWindow window in value.Windows.OrderBy(x => x.WindowMinutes))
            {
                DateTime reset = DateTimeOffset.FromUnixTimeSeconds(window.ResetsAt).LocalDateTime;
                if (language == "zh")
                {
                    text.Append(WindowName(window.WindowMinutes, language)).Append("：")
                        .Append(Math.Round(window.RemainingPercent).ToString("0", CultureInfo.InvariantCulture)).Append("%")
                        .Append("（").Append(reset.ToString("M月d日 HH:mm")).AppendLine(" 重置）");
                }
                else
                {
                    text.Append(WindowName(window.WindowMinutes, language)).Append(": ")
                        .Append(Math.Round(window.RemainingPercent).ToString("0", CultureInfo.InvariantCulture)).Append("%")
                        .Append(" (resets ").Append(reset.ToString("MMM d, HH:mm", CultureInfo.InvariantCulture)).AppendLine(")");
                }
            }
            if (!String.IsNullOrEmpty(value.PlanType)) text.AppendLine(Text(language, "Plan: ", "方案：") + value.PlanType);
            text.Append(Text(language, "Updated: ", "数据更新：")).Append(value.TimestampUtc.ToLocalTime().ToString("HH:mm:ss"));
            return text.ToString();
        }

        private static string WindowName(int minutes, string language)
        {
            if (minutes >= 10000 && minutes <= 10200) return Text(language, "Weekly limit", "周限额");
            if (minutes >= 280 && minutes <= 320) return Text(language, "5-hour limit", "5小时限额");
            if (minutes % 1440 == 0) return (minutes / 1440).ToString(CultureInfo.InvariantCulture) + Text(language, "-day limit", "天限额");
            if (minutes % 60 == 0) return (minutes / 60).ToString(CultureInfo.InvariantCulture) + Text(language, "-hour limit", "小时限额");
            return minutes.ToString(CultureInfo.InvariantCulture) + Text(language, "-minute limit", "分钟限额");
        }

        private void OnAppearanceClick(object sender, RoutedEventArgs e)
        {
            OrbSettings original = settings.Clone();
            AppearanceWindow dialog = new AppearanceWindow(this, settings.Clone(), PreviewSettings);
            bool? accepted = dialog.ShowDialog();
            if (accepted == true)
            {
                settings = dialog.Value.Clone();
                settings.X = Left;
                settings.Y = Top;
            }
            else
            {
                settings = original;
                PreviewSettings(settings);
            }
            settingsStore.Save(settings);
        }

        private void PreviewSettings(OrbSettings value)
        {
            ApplyAppearance(value);
            if (displayedLanguage != value.Language) ApplyLanguage(value.Language);
        }

        private void ApplyLanguage(string language)
        {
            displayedLanguage = language == "zh" ? "zh" : "en";
            Title = Text(displayedLanguage, "Codex Usage Orb", "Codex 用量悬浮球");
            ContextMenu = BuildContextMenu(displayedLanguage);
            startupItem = (MenuItem)ContextMenu.Items[4];
            startupItem.IsCheckable = true;
            startupItem.IsChecked = IsStartupEnabled();
            UpdateDisplay();
        }

        private static string Text(string language, string english, string chinese)
        {
            return language == "zh" ? chinese : english;
        }

        private void ApplyAppearance(OrbSettings value)
        {
            double size = Math.Max(50, Math.Min(300, value.Size));
            Width = size;
            Height = size;

            Color accent = AppearanceWindow.ColorFromHex(value.Accent);
            Color accentDark = ScaleColor(accent, 0.42);
            Color backgroundTop = ScaleColor(accent, 0.24);
            Color backgroundBottom = ScaleColor(accent, 0.14);
            liquid.Background = new LinearGradientBrush(backgroundTop, backgroundBottom, 90);
            waveBack.Fill = new SolidColorBrush(Color.FromArgb(175, accent.R, accent.G, accent.B));
            waveFront.Fill = new LinearGradientBrush(accent, accentDark, 90);
            Color softText = MixColor(accent, Colors.White, 0.84);
            subtitleText.Foreground = new SolidColorBrush(Color.FromArgb(225, softText.R, softText.G, softText.B));

            if (IsLoaded)
            {
                Rect work = SystemParameters.WorkArea;
                Left = Math.Max(work.Left, Math.Min(Left, work.Right - Width));
                Top = Math.Max(work.Top, Math.Min(Top, work.Bottom - Height));
            }
        }

        private static Color ScaleColor(Color color, double factor)
        {
            return Color.FromRgb(
                (byte)Math.Max(0, Math.Min(255, color.R * factor)),
                (byte)Math.Max(0, Math.Min(255, color.G * factor)),
                (byte)Math.Max(0, Math.Min(255, color.B * factor)));
        }

        private static Color MixColor(Color first, Color second, double secondWeight)
        {
            double firstWeight = 1.0 - secondWeight;
            return Color.FromRgb(
                (byte)(first.R * firstWeight + second.R * secondWeight),
                (byte)(first.G * firstWeight + second.G * secondWeight),
                (byte)(first.B * firstWeight + second.B * secondWeight));
        }

        private void DrawWaves()
        {
            double remaining = snapshot == null ? 50 : snapshot.RemainingPercent;
            waveBack.Data = CreateWaveGeometry(remaining, phase + 1.8, 5.0, 25.0);
            waveFront.Data = CreateWaveGeometry(remaining, phase, 6.5, 31.0);
        }

        private static Geometry CreateWaveGeometry(double percent, double wavePhase, double amplitude, double wavelength)
        {
            double baseline = LiquidSize * (1.0 - Math.Max(0.04, Math.Min(0.96, percent / 100.0)));
            PathFigure figure = new PathFigure { StartPoint = new Point(0, LiquidSize), IsClosed = true, IsFilled = true };
            PolyLineSegment segment = new PolyLineSegment();
            segment.Points.Add(new Point(0, baseline + Math.Sin(wavePhase) * amplitude));
            for (double x = 2; x <= LiquidSize; x += 2)
                segment.Points.Add(new Point(x, baseline + Math.Sin((x / wavelength) + wavePhase) * amplitude));
            segment.Points.Add(new Point(LiquidSize, LiquidSize));
            figure.Segments.Add(segment);
            return new PathGeometry(new[] { figure });
        }

        private void OnStartupClick(object sender, RoutedEventArgs e)
        {
            bool enable = !IsStartupEnabled();
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", true))
                {
                    if (enable) key.SetValue("CodexUsageOrb", "\"" + Assembly.GetExecutingAssembly().Location + "\"");
                    else key.DeleteValue("CodexUsageOrb", false);
                }
                startupItem.IsChecked = enable;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    Text(displayedLanguage, "Unable to update launch-at-startup settings: ", "无法修改开机启动设置：") + ex.Message,
                    Text(displayedLanguage, "Codex Usage Orb", "Codex 用量悬浮球"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                startupItem.IsChecked = IsStartupEnabled();
            }
        }

        private static bool IsStartupEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", false))
                    return key != null && key.GetValue("CodexUsageOrb") != null;
            }
            catch { return false; }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Run(new OrbWindow());
        }
    }
}
