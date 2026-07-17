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

        public OrbSettings()
        {
            X = Double.NaN;
            Y = Double.NaN;
            Size = 168;
            Accent = "#61DC18";
        }

        public OrbSettings Clone()
        {
            return new OrbSettings { X = X, Y = Y, Size = Size, Accent = Accent };
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
                    "Accent=" + settings.Accent
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
        private readonly Border colorPreview;
        public OrbSettings Value { get; private set; }

        public AppearanceWindow(Window owner, OrbSettings initial, Action<OrbSettings> previewAction)
        {
            Owner = owner;
            Value = initial;
            preview = previewAction;
            Title = "悬浮球外观设置";
            Width = 370;
            Height = 320;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            Background = new SolidColorBrush(Color.FromRgb(246, 248, 251));

            Grid root = new Grid { Margin = new Thickness(22) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = root;

            TextBlock heading = new TextBlock { Text = "实时调整大小和颜色", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(28, 38, 52)) };
            root.Children.Add(heading);

            Grid sizeRow = new Grid { Margin = new Thickness(0, 22, 0, 0) };
            sizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            sizeRow.ColumnDefinitions.Add(new ColumnDefinition());
            sizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
            TextBlock sizeLabel = new TextBlock { Text = "尺寸", VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
            Slider sizeSlider = new Slider { Minimum = 50, Maximum = 300, Value = Value.Size, TickFrequency = 10, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
            sizeValue = new TextBlock { Text = Math.Round(Value.Size) + " px", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(sizeSlider, 1);
            Grid.SetColumn(sizeValue, 2);
            sizeRow.Children.Add(sizeLabel);
            sizeRow.Children.Add(sizeSlider);
            sizeRow.Children.Add(sizeValue);
            Grid.SetRow(sizeRow, 1);
            root.Children.Add(sizeRow);

            TextBlock colorLabel = new TextBlock { Text = "主题颜色", Margin = new Thickness(0, 22, 0, 8), FontSize = 13 };
            Grid.SetRow(colorLabel, 2);
            root.Children.Add(colorLabel);

            StackPanel colors = new StackPanel { Orientation = Orientation.Horizontal };
            AddPreset(colors, "绿色", "#61DC18");
            AddPreset(colors, "蓝色", "#37BEFF");
            AddPreset(colors, "紫色", "#A970FF");
            AddPreset(colors, "橙色", "#FF9F35");
            Button custom = new Button { Content = "自定义…", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(4, 0, 0, 0) };
            custom.Click += OnCustomColor;
            colors.Children.Add(custom);
            colorPreview = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Margin = new Thickness(10, 0, 0, 0), BorderBrush = Brushes.White, BorderThickness = new Thickness(2), Background = BrushFromHex(Value.Accent) };
            colors.Children.Add(colorPreview);
            Grid.SetRow(colors, 3);
            root.Children.Add(colors);

            StackPanel footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
            Button reset = new Button { Content = "恢复默认", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 50, 0) };
            reset.Click += delegate { Value.Size = 168; Value.Accent = "#61DC18"; sizeSlider.Value = 168; ApplyPreview(); };
            Button cancel = new Button { Content = "取消", Width = 72, Padding = new Thickness(0, 6, 0, 6), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            Button ok = new Button { Content = "确定", Width = 72, Padding = new Thickness(0, 6, 0, 6), IsDefault = true };
            ok.Click += delegate { DialogResult = true; };
            footer.Children.Add(reset);
            footer.Children.Add(cancel);
            footer.Children.Add(ok);
            Grid.SetRow(footer, 4);
            root.Children.Add(footer);

            sizeSlider.ValueChanged += delegate
            {
                Value.Size = sizeSlider.Value;
                sizeValue.Text = Math.Round(Value.Size) + " px";
                ApplyPreview();
            };
        }

        private void AddPreset(Panel parent, string name, string hex)
        {
            Button button = new Button
            {
                Content = name,
                Tag = hex,
                Foreground = Brushes.White,
                Background = BrushFromHex(hex),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 5, 0)
            };
            button.Click += delegate
            {
                Value.Accent = (string)button.Tag;
                ApplyPreview();
            };
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
            colorPreview.Background = BrushFromHex(Value.Accent);
            preview(Value.Clone());
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
        private readonly MenuItem startupItem;
        private OrbSettings settings;
        private UsageSnapshot snapshot;
        private double phase;
        private bool refreshRunning;

        public OrbWindow()
        {
            settings = settingsStore.Load();
            Title = "Codex 剩余用量";
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
                Text = "正在读取", Foreground = new SolidColorBrush(Color.FromArgb(220, 235, 255, 229)),
                FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 10, TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, -2, 0, 0)
            };
            labels.Children.Add(percentText);
            labels.Children.Add(subtitleText);
            root.Children.Add(labels);
            ApplyAppearance(settings);

            detailTip = new ToolTip { Placement = System.Windows.Controls.Primitives.PlacementMode.Left };
            ToolTip = detailTip;
            ContextMenu = BuildContextMenu();
            startupItem = (MenuItem)ContextMenu.Items[4];
            startupItem.IsCheckable = true;
            startupItem.IsChecked = IsStartupEnabled();

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

        private ContextMenu BuildContextMenu()
        {
            ContextMenu menu = new ContextMenu { FontFamily = new FontFamily("Microsoft YaHei UI") };
            MenuItem title = new MenuItem { Header = "Codex 用量悬浮球", IsEnabled = false, FontWeight = FontWeights.Bold };
            MenuItem refresh = new MenuItem { Header = "立即刷新" };
            refresh.Click += delegate { RefreshNow(); };
            MenuItem details = new MenuItem { Header = "查看详细信息" };
            details.Click += delegate { detailTip.IsOpen = true; };
            MenuItem appearance = new MenuItem { Header = "外观设置…" };
            appearance.Click += OnAppearanceClick;
            MenuItem startup = new MenuItem { Header = "开机自动启动" };
            startup.Click += OnStartupClick;
            MenuItem exit = new MenuItem { Header = "退出" };
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
                subtitleText.Text = "暂无数据";
                detailTip.Content = "尚未找到 Codex 用量数据。\n请先在 Codex 中完成一次对话。";
                return;
            }

            double remaining = snapshot.RemainingPercent;
            percentText.Text = Math.Round(remaining).ToString("0", CultureInfo.InvariantCulture) + "%";
            LimitWindow active = snapshot.Windows.OrderBy(x => x.RemainingPercent).First();
            subtitleText.Text = WindowName(active.WindowMinutes) + "剩余";
            percentText.Foreground = remaining <= 15 ? new SolidColorBrush(Color.FromRgb(255, 238, 225)) : Brushes.White;
            detailTip.Content = BuildDetails(snapshot);
            DrawWaves();
        }

        private static string BuildDetails(UsageSnapshot value)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("Codex 剩余用量");
            foreach (LimitWindow window in value.Windows.OrderBy(x => x.WindowMinutes))
            {
                DateTime reset = DateTimeOffset.FromUnixTimeSeconds(window.ResetsAt).LocalDateTime;
                text.Append(WindowName(window.WindowMinutes)).Append("：")
                    .Append(Math.Round(window.RemainingPercent).ToString("0", CultureInfo.InvariantCulture)).Append("%")
                    .Append("（").Append(reset.ToString("M月d日 HH:mm")).AppendLine(" 重置）");
            }
            if (!String.IsNullOrEmpty(value.PlanType)) text.AppendLine("方案：" + value.PlanType);
            text.Append("数据更新：").Append(value.TimestampUtc.ToLocalTime().ToString("HH:mm:ss"));
            return text.ToString();
        }

        private static string WindowName(int minutes)
        {
            if (minutes >= 10000 && minutes <= 10200) return "周限额";
            if (minutes >= 280 && minutes <= 320) return "5小时限额";
            if (minutes % 1440 == 0) return (minutes / 1440).ToString(CultureInfo.InvariantCulture) + "天限额";
            if (minutes % 60 == 0) return (minutes / 60).ToString(CultureInfo.InvariantCulture) + "小时限额";
            return minutes.ToString(CultureInfo.InvariantCulture) + "分钟限额";
        }

        private void OnAppearanceClick(object sender, RoutedEventArgs e)
        {
            OrbSettings original = settings.Clone();
            AppearanceWindow dialog = new AppearanceWindow(this, settings.Clone(), ApplyAppearance);
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
                ApplyAppearance(settings);
            }
            settingsStore.Save(settings);
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
                MessageBox.Show("无法修改开机启动设置：" + ex.Message, "Codex 用量悬浮球", MessageBoxButton.OK, MessageBoxImage.Warning);
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
