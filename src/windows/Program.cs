using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Threading;
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
    internal static class OrbLogger
    {
        private static readonly object Sync = new object();
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexUsageOrb", "logs", "orb.log");

        public static void Info(string message)
        {
            Write("INFO", message, null);
        }

        public static void Error(string message, Exception exception)
        {
            Write("ERROR", message, exception);
        }

        private static void Write(string level, string message, Exception exception)
        {
            try
            {
                string directory = Path.GetDirectoryName(LogPath);
                if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                string detail = exception == null ? String.Empty : Environment.NewLine + exception;
                string line = String.Format(
                    CultureInfo.InvariantCulture,
                    "{0:O} [{1}] {2}{3}{4}",
                    DateTime.UtcNow,
                    level,
                    message,
                    detail,
                    Environment.NewLine);
                lock (Sync)
                {
                    File.AppendAllText(LogPath, line, Encoding.UTF8);
                }
            }
            catch
            {
                // Diagnostics must never become the reason the orb exits.
            }
        }
    }

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
        public string LimitId { get; set; }
        public string LimitName { get; set; }

        public string ScopeKey
        {
            get { return (LimitId ?? String.Empty) + "\u001f" + (LimitName ?? String.Empty); }
        }

        public IEnumerable<LimitWindow> Windows
        {
            get
            {
                if (Primary != null) yield return Primary;
                if (Secondary != null) yield return Secondary;
            }
        }

        public double RemainingPercent
        {
            get
            {
                LimitWindow[] windows = Windows.ToArray();
                return windows.Length == 0 ? 0 : windows.Min(x => x.RemainingPercent);
            }
        }

        public LimitWindow FiveHour
        {
            get
            {
                LimitWindow exact = Windows.FirstOrDefault(x => x.WindowMinutes >= 280 && x.WindowMinutes <= 320);
                if (exact != null) return exact;
                LimitWindow[] ordered = Windows.OrderBy(x => x.WindowMinutes).ToArray();
                if (ordered.Length > 1) return ordered[0];
                return ordered.Length == 1 && ordered[0].WindowMinutes < 1440 ? ordered[0] : null;
            }
        }

        public LimitWindow Weekly
        {
            get
            {
                LimitWindow exact = Windows.FirstOrDefault(x => x.WindowMinutes >= 10000 && x.WindowMinutes <= 10200);
                if (exact != null) return exact;
                LimitWindow[] ordered = Windows.OrderBy(x => x.WindowMinutes).ToArray();
                if (ordered.Length > 1) return ordered[ordered.Length - 1];
                return ordered.Length == 1 && ordered[0].WindowMinutes >= 1440 ? ordered[0] : null;
            }
        }
    }

    internal static class OrbStyles
    {
        public const string Concentric = "concentric";
        public const string MainWeekArc = "main-week-arc";

        public static string Normalize(string value)
        {
            return value == MainWeekArc ? MainWeekArc : Concentric;
        }
    }

    /// <summary>
    /// Reads the account rate-limit snapshot from Codex app-server and falls
    /// back to rollout files when the local CLI is unavailable.  Authentication
    /// remains inside Codex; this process never opens auth.json.
    /// </summary>
    internal sealed class CodexUsageReader : IDisposable
    {
        private const int TailBytes = 1024 * 1024;
        private const int CandidateFileCount = 32;
        private const int AppServerTimeoutMilliseconds = 15000;
        private readonly string sessionsRoot;
        private readonly object appServerLock = new object();
        private Process appServerProcess;
        private int nextAppServerRequestId = 2;
        private bool appServerEverSucceeded;
        private static readonly Regex TimestampRegex = new Regex("\\\"timestamp\\\":\\\"(?<v>[^\\\"]+)\\\"", RegexOptions.Compiled);
        private static readonly Regex PlanRegex = new Regex("\\\"plan_type\\\":(?:null|\\\"(?<v>[^\\\"]+)\\\")", RegexOptions.Compiled);
        private static readonly Regex LimitIdRegex = new Regex("\\\"limit_id\\\":(?:null|\\\"(?<v>[^\\\"]*)\\\")", RegexOptions.Compiled);
        private static readonly Regex LimitNameRegex = new Regex("\\\"limit_name\\\":(?:null|\\\"(?<v>[^\\\"]*)\\\")", RegexOptions.Compiled);
        private static readonly Regex LimitRegex = new Regex(
            "\\\"(?<kind>primary|secondary)\\\":(?<null>null|\\{(?:(?!\\},\\\"(?:primary|secondary|credits|individual_limit|spend_control_reached|plan_type|rate_limit_reached_type)\\\").)*?\\\"used_percent\\\":(?<used>[0-9.]+),(?:(?!\\},\\\"(?:primary|secondary|credits|individual_limit|spend_control_reached|plan_type|rate_limit_reached_type)\\\").)*?\\\"window_minutes\\\":(?<minutes>[0-9]+),(?:(?!\\},\\\"(?:primary|secondary|credits|individual_limit|spend_control_reached|plan_type|rate_limit_reached_type)\\\").)*?\\\"resets_at\\\":(?<reset>[0-9]+)\\})",
            RegexOptions.Compiled);
        private static readonly Regex AppServerLimitIdRegex = new Regex("\\\"limitId\\\":(?:null|\\\"(?<v>[^\\\"]*)\\\")", RegexOptions.Compiled);
        private static readonly Regex AppServerPlanRegex = new Regex("\\\"planType\\\":(?:null|\\\"(?<v>[^\\\"]+)\\\")", RegexOptions.Compiled);
        private static readonly Regex AppServerWindowRegex = new Regex(
            "\\\"(?<kind>primary|secondary)\\\":(?<null>null|\\{.*?\\\"usedPercent\\\":(?<used>[0-9.]+).*?\\\"windowDurationMins\\\":(?<minutes>[0-9]+).*?\\\"resetsAt\\\":(?<reset>[0-9]+).*?\\})",
            RegexOptions.Compiled);

        public CodexUsageReader()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            sessionsRoot = Path.Combine(home, ".codex", "sessions");
        }

        public UsageSnapshot ReadLatest()
        {
            try
            {
                UsageSnapshot appServerSnapshot = ReadFromAppServer();
                if (appServerSnapshot != null)
                {
                    appServerEverSucceeded = true;
                    return appServerSnapshot;
                }

                // Once the authoritative source has succeeded, do not replace it
                // with an older JSONL snapshot during a transient RPC/network error.
                return appServerEverSucceeded ? null : ReadFromSessionFiles();
            }
            catch (Exception exception)
            {
                OrbLogger.Error("用量读取失败，保留上一次有效数据", exception);
                return null;
            }
        }

        public void Dispose()
        {
            lock (appServerLock)
            {
                StopAppServer();
            }
        }

        private UsageSnapshot ReadFromSessionFiles()
        {
            if (!Directory.Exists(sessionsRoot)) return null;

            IEnumerable<FileInfo> candidates;
            try
            {
                candidates = new DirectoryInfo(sessionsRoot)
                    .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(CandidateFileCount)
                    .ToArray();
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }

            List<UsageSnapshot> parsedSnapshots = new List<UsageSnapshot>();
            foreach (FileInfo file in candidates)
            {
                string tail = ReadTail(file.FullName);
                if (String.IsNullOrEmpty(tail)) continue;
                parsedSnapshots.AddRange(ParseAll(tail));
            }
            return SelectBest(parsedSnapshots);
        }

        /// <summary>
        /// Reads the same account/rateLimits endpoint used by the Codex desktop
        /// client.  Session JSONL files are snapshots written during turns and
        /// can be stale, so the app-server response is the authoritative source.
        /// </summary>
        private UsageSnapshot ReadFromAppServer()
        {
            lock (appServerLock)
            {
                if (!EnsureAppServer()) return null;

                Process process = appServerProcess;
                int requestId = nextAppServerRequestId++;
                try
                {
                    process.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":" + requestId + ",\"method\":\"account/rateLimits/read\",\"params\":{}}");
                    process.StandardInput.Flush();

                    Task<UsageSnapshot> responseTask = Task.Factory.StartNew(() =>
                    {
                        string line;
                        while ((line = process.StandardOutput.ReadLine()) != null)
                        {
                            UsageSnapshot snapshot = ParseAppServerResponse(line);
                            if (snapshot != null) return snapshot;
                        }
                        return null;
                    });
                    if (!responseTask.Wait(AppServerTimeoutMilliseconds))
                    {
                        StopAppServer();
                        return null;
                    }
                    UsageSnapshot result = responseTask.Result;
                    if (result == null) StopAppServer();
                    return result;
                }
                catch (Exception)
                {
                    StopAppServer();
                    return null;
                }
            }
        }

        private bool EnsureAppServer()
        {
            if (appServerProcess != null)
            {
                try
                {
                    if (!appServerProcess.HasExited) return true;
                }
                catch (Exception) { }
                StopAppServer();
            }

            string executable = FindCodexExecutable();
            if (String.IsNullOrEmpty(executable)) return false;
            Process process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = "app-server --analytics-default-enabled",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true
                }
            };
            try
            {
                if (!process.Start()) return false;
                process.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"codex-usage-orb\",\"version\":\"1.0.0\"},\"capabilities\":{}}}");
                process.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"method\":\"initialized\",\"params\":{}}");
                process.StandardInput.Flush();
                appServerProcess = process;
                nextAppServerRequestId = 2;
                return true;
            }
            catch (Exception)
            {
                try { process.Dispose(); } catch (Exception) { }
                return false;
            }
        }

        private void StopAppServer()
        {
            Process process = appServerProcess;
            appServerProcess = null;
            if (process == null) return;
            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch (Exception) { }
            try { process.Dispose(); } catch (Exception) { }
        }

        private static UsageSnapshot ParseAppServerResponse(string line)
        {
            if (String.IsNullOrEmpty(line) || line.IndexOf("\"result\":", StringComparison.Ordinal) < 0 ||
                line.IndexOf("\"rateLimits\":", StringComparison.Ordinal) < 0) return null;

            string limitsBody = ExtractJsonObject(line, "\"rateLimits\":");
            if (String.IsNullOrEmpty(limitsBody)) return null;
            LimitWindow primary = null;
            LimitWindow secondary = null;
            foreach (Match match in AppServerWindowRegex.Matches(limitsBody))
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

            Match id = AppServerLimitIdRegex.Match(limitsBody);
            Match plan = AppServerPlanRegex.Match(limitsBody);
            return new UsageSnapshot
            {
                TimestampUtc = DateTime.UtcNow,
                Primary = primary,
                Secondary = secondary,
                PlanType = plan.Success && plan.Groups["v"].Success ? plan.Groups["v"].Value : null,
                LimitId = id.Success && id.Groups["v"].Success ? id.Groups["v"].Value : null,
                LimitName = null
            };
        }

        private static string ExtractJsonObject(string text, string propertyMarker)
        {
            int marker = text.IndexOf(propertyMarker, StringComparison.Ordinal);
            if (marker < 0) return null;
            int start = text.IndexOf('{', marker + propertyMarker.Length);
            if (start < 0) return null;

            int depth = 0;
            bool inString = false;
            bool escaped = false;
            for (int i = start; i < text.Length; i++)
            {
                char character = text[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (character == '\\') escaped = true;
                    else if (character == '\"') inString = false;
                    continue;
                }
                if (character == '\"') { inString = true; continue; }
                if (character == '{') depth++;
                else if (character == '}' && --depth == 0) return text.Substring(start, i - start + 1);
            }
            return null;
        }

        private static string FindCodexExecutable()
        {
            string configured = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
            if (!String.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string binRoot = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
            try
            {
                if (Directory.Exists(binRoot))
                {
                    string[] files = Directory.GetFiles(binRoot, "codex.exe", SearchOption.AllDirectories);
                    string newest = files
                        .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                        .FirstOrDefault();
                    if (!String.IsNullOrEmpty(newest)) return newest;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            string pathValue = Environment.GetEnvironmentVariable("PATH");
            if (!String.IsNullOrEmpty(pathValue))
            {
                foreach (string directory in pathValue.Split(Path.PathSeparator))
                {
                    if (String.IsNullOrWhiteSpace(directory)) continue;
                    string candidate = Path.Combine(directory.Trim(), "codex.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            return null;
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
            return ParseAll(text).OrderByDescending(x => x.TimestampUtc).FirstOrDefault();
        }

        internal static List<UsageSnapshot> ParseAll(string text)
        {
            List<UsageSnapshot> results = new List<UsageSnapshot>();
            const string marker = "\"rate_limits\":";
            int cursor = text.LastIndexOf(marker, StringComparison.Ordinal);
            while (cursor >= 0)
            {
                int lineStart = text.LastIndexOf('\n', cursor);
                int lineEnd = text.IndexOf('\n', cursor);
                if (lineEnd < 0) lineEnd = text.Length;
                string line = text.Substring(lineStart + 1, lineEnd - lineStart - 1);
                UsageSnapshot result = ParseLine(line);
                if (result != null) results.Add(result);
                if (cursor == 0) break;
                cursor = text.LastIndexOf(marker, cursor - 1, StringComparison.Ordinal);
            }
            return results;
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
            Match lm = LimitIdRegex.Match(line);
            Match lnm = LimitNameRegex.Match(line);
            return new UsageSnapshot
            {
                TimestampUtc = timestamp,
                Primary = primary,
                Secondary = secondary,
                PlanType = pm.Success ? pm.Groups["v"].Value : null,
                LimitId = lm.Success && lm.Groups["v"].Success ? lm.Groups["v"].Value : null,
                LimitName = lnm.Success && lnm.Groups["v"].Success ? lnm.Groups["v"].Value : null
            };
        }

        /// <summary>
        /// Selects the rate-limit scope that represents the Codex allowance and merges
        /// the newest value for each window. Model-specific scopes can omit the 5-hour
        /// window, so they must not replace a scope that still reports both windows.
        /// </summary>
        private static UsageSnapshot SelectBest(IEnumerable<UsageSnapshot> records)
        {
            List<UsageSnapshot> snapshots = records == null ? new List<UsageSnapshot>() : records.ToList();
            if (snapshots.Count == 0) return null;

            var selectedScope = snapshots
                .GroupBy(x => x.ScopeKey)
                .Select(group => new
                {
                    Records = group.ToList(),
                    HasFiveHour = group.Any(x => x.Windows.Any(w => w.WindowMinutes >= 280 && w.WindowMinutes <= 320)),
                    Latest = group.Max(x => x.TimestampUtc)
                })
                .OrderByDescending(x => x.HasFiveHour)
                .ThenByDescending(x => x.Latest)
                .First();

            UsageSnapshot latest = selectedScope.Records.OrderByDescending(x => x.TimestampUtc).First();
            List<LimitWindow> windows = selectedScope.Records
                .SelectMany(snapshot => snapshot.Windows.Select(window => new { snapshot.TimestampUtc, Window = window }))
                .GroupBy(x => x.Window.WindowMinutes)
                .Select(group => group.OrderByDescending(x => x.TimestampUtc).First().Window)
                .OrderBy(x => x.WindowMinutes)
                .Take(2)
                .ToList();

            return new UsageSnapshot
            {
                TimestampUtc = latest.TimestampUtc,
                Primary = windows.Count > 0 ? windows[0] : null,
                Secondary = windows.Count > 1 ? windows[1] : null,
                PlanType = selectedScope.Records.Select(x => x.PlanType).FirstOrDefault(x => !String.IsNullOrEmpty(x)),
                LimitId = latest.LimitId,
                LimitName = latest.LimitName
            };
        }
    }

    internal sealed class OrbSettings
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Size { get; set; }
        public string Accent { get; set; }
        public string WeeklyAccent { get; set; }
        public string Language { get; set; }
        public string Style { get; set; }
        public double ConcentricRingWidth { get; set; }
        public double WeeklyArcWidth { get; set; }

        public OrbSettings()
        {
            X = Double.NaN;
            Y = Double.NaN;
            Size = 168;
            Accent = "#61DC18";
            WeeklyAccent = "#A970FF";
            Language = "en";
            Style = OrbStyles.Concentric;
            ConcentricRingWidth = 5;
            WeeklyArcWidth = 5;
        }

        public OrbSettings Clone()
        {
            return new OrbSettings { X = X, Y = Y, Size = Size, Accent = Accent, WeeklyAccent = WeeklyAccent, Language = Language, Style = Style, ConcentricRingWidth = ConcentricRingWidth, WeeklyArcWidth = WeeklyArcWidth };
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
                    else if (pair[0] == "WeeklyAccent" && Regex.IsMatch(pair[1], "^#[0-9A-Fa-f]{6}$")) settings.WeeklyAccent = pair[1].ToUpperInvariant();
                    else if (pair[0] == "Language" && (pair[1] == "en" || pair[1] == "zh")) settings.Language = pair[1];
                    else if (pair[0] == "Style") settings.Style = OrbStyles.Normalize(pair[1]);
                    else if (pair[0] == "ConcentricRingWidth" && TryDouble(pair[1], out value)) settings.ConcentricRingWidth = Math.Max(2, Math.Min(14, value));
                    else if (pair[0] == "WeeklyArcWidth" && TryDouble(pair[1], out value)) settings.WeeklyArcWidth = Math.Max(2, Math.Min(14, value));
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
                    "WeeklyAccent=" + settings.WeeklyAccent,
                    "Language=" + settings.Language,
                    "Style=" + OrbStyles.Normalize(settings.Style),
                    "ConcentricRingWidth=" + settings.ConcentricRingWidth.ToString(CultureInfo.InvariantCulture),
                    "WeeklyArcWidth=" + settings.WeeklyArcWidth.ToString(CultureInfo.InvariantCulture)
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
        private readonly TextBlock concentricRingWidthValue;
        private readonly TextBlock weeklyArcWidthValue;
        private readonly Dictionary<string, Button> fiveHourPresetButtons = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Button> weeklyPresetButtons = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);
        private readonly Button fiveHourCustomColorButton;
        private readonly Button weeklyCustomColorButton;
        public OrbSettings Value { get; private set; }

        public AppearanceWindow(Window owner, OrbSettings initial, Action<OrbSettings> previewAction)
        {
            Owner = owner;
            Value = initial;
            preview = previewAction;
            bool chinese = Value.Language == "zh";
            Title = chinese ? "悬浮球外观设置" : "Orb Appearance Settings";
            Width = 350;
            Height = 500;
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
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = root;

            TextBlock heading = new TextBlock { Text = chinese ? "调整样式、大小、宽度和颜色" : "Adjust style, size, width, and color", FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(28, 38, 52)) };
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

            Grid styleRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            styleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            styleRow.ColumnDefinitions.Add(new ColumnDefinition());
            TextBlock styleLabel = new TextBlock { Text = chinese ? "显示样式" : "Display style", VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
            ComboBox styleBox = new ComboBox { Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
            ComboBoxItem concentricItem = new ComboBoxItem { Content = chinese ? "同心双环" : "Concentric rings", Tag = OrbStyles.Concentric };
            ComboBoxItem mainWeekArcItem = new ComboBoxItem { Content = chinese ? "主值 + 周弧" : "Main value + weekly arc", Tag = OrbStyles.MainWeekArc };
            styleBox.Items.Add(concentricItem);
            styleBox.Items.Add(mainWeekArcItem);
            styleBox.SelectedIndex = OrbStyles.Normalize(Value.Style) == OrbStyles.MainWeekArc ? 1 : 0;
            Grid.SetColumn(styleBox, 1);
            styleRow.Children.Add(styleLabel);
            styleRow.Children.Add(styleBox);
            Grid.SetRow(styleRow, 2);
            root.Children.Add(styleRow);

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
            Grid.SetRow(sizeRow, 3);
            root.Children.Add(sizeRow);

            Grid concentricWidthRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            concentricWidthRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            concentricWidthRow.ColumnDefinitions.Add(new ColumnDefinition());
            concentricWidthRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
            TextBlock concentricWidthLabel = new TextBlock { Text = chinese ? "同心环宽" : "Ring width", VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
            Slider concentricWidthSlider = new Slider { Minimum = 2, Maximum = 14, Value = Value.ConcentricRingWidth, TickFrequency = 1, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
            concentricRingWidthValue = new TextBlock { Text = Math.Round(Value.ConcentricRingWidth) + " px", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(concentricWidthSlider, 1);
            Grid.SetColumn(concentricRingWidthValue, 2);
            concentricWidthRow.Children.Add(concentricWidthLabel);
            concentricWidthRow.Children.Add(concentricWidthSlider);
            concentricWidthRow.Children.Add(concentricRingWidthValue);
            Grid.SetRow(concentricWidthRow, 4);
            root.Children.Add(concentricWidthRow);

            Grid weeklyArcWidthRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            weeklyArcWidthRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            weeklyArcWidthRow.ColumnDefinitions.Add(new ColumnDefinition());
            weeklyArcWidthRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
            TextBlock weeklyArcWidthLabel = new TextBlock { Text = chinese ? "周弧宽度" : "Arc width", VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
            Slider weeklyArcWidthSlider = new Slider { Minimum = 2, Maximum = 14, Value = Value.WeeklyArcWidth, TickFrequency = 1, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
            weeklyArcWidthValue = new TextBlock { Text = Math.Round(Value.WeeklyArcWidth) + " px", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(weeklyArcWidthSlider, 1);
            Grid.SetColumn(weeklyArcWidthValue, 2);
            weeklyArcWidthRow.Children.Add(weeklyArcWidthLabel);
            weeklyArcWidthRow.Children.Add(weeklyArcWidthSlider);
            weeklyArcWidthRow.Children.Add(weeklyArcWidthValue);
            Grid.SetRow(weeklyArcWidthRow, 5);
            root.Children.Add(weeklyArcWidthRow);

            TextBlock fiveHourColorLabel = new TextBlock { Text = chinese ? "5小时颜色" : "5-hour color", Margin = new Thickness(0, 12, 0, 5), FontSize = 13 };
            Grid.SetRow(fiveHourColorLabel, 6);
            root.Children.Add(fiveHourColorLabel);

            StackPanel fiveHourColors = new StackPanel { Orientation = Orientation.Horizontal };
            AddPreset(fiveHourColors, chinese ? "绿色" : "Green", "#61DC18", false);
            AddPreset(fiveHourColors, chinese ? "蓝色" : "Blue", "#37BEFF", false);
            AddPreset(fiveHourColors, chinese ? "紫色" : "Purple", "#A970FF", false);
            AddPreset(fiveHourColors, chinese ? "橙色" : "Orange", "#FF9F35", false);
            fiveHourCustomColorButton = CreateCustomColorButton(chinese, false);
            fiveHourColors.Children.Add(fiveHourCustomColorButton);
            Grid.SetRow(fiveHourColors, 7);
            root.Children.Add(fiveHourColors);

            TextBlock weeklyColorLabel = new TextBlock { Text = chinese ? "每周颜色" : "Weekly color", Margin = new Thickness(0, 8, 0, 5), FontSize = 13 };
            Grid.SetRow(weeklyColorLabel, 8);
            root.Children.Add(weeklyColorLabel);

            StackPanel weeklyColors = new StackPanel { Orientation = Orientation.Horizontal };
            AddPreset(weeklyColors, chinese ? "绿色" : "Green", "#61DC18", true);
            AddPreset(weeklyColors, chinese ? "蓝色" : "Blue", "#37BEFF", true);
            AddPreset(weeklyColors, chinese ? "紫色" : "Purple", "#A970FF", true);
            AddPreset(weeklyColors, chinese ? "橙色" : "Orange", "#FF9F35", true);
            weeklyCustomColorButton = CreateCustomColorButton(chinese, true);
            weeklyColors.Children.Add(weeklyCustomColorButton);
            Grid.SetRow(weeklyColors, 9);
            root.Children.Add(weeklyColors);

            Grid footer = new Grid { Margin = new Thickness(0, 18, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Button reset = new Button { Content = chinese ? "恢复默认" : "Reset", Width = 70, Height = 30, HorizontalAlignment = HorizontalAlignment.Left };
            reset.Click += delegate { Value.Size = 168; Value.Accent = "#61DC18"; Value.WeeklyAccent = "#A970FF"; Value.Language = "en"; Value.Style = OrbStyles.Concentric; Value.ConcentricRingWidth = 5; Value.WeeklyArcWidth = 5; sizeSlider.Value = 168; concentricWidthSlider.Value = 5; weeklyArcWidthSlider.Value = 5; languageBox.SelectedIndex = 0; styleBox.SelectedIndex = 0; ApplyPreview(); };
            StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal };
            Button cancel = new Button { Content = chinese ? "取消" : "Cancel", Width = 70, Height = 30, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            Button ok = new Button { Content = chinese ? "确定" : "OK", Width = 70, Height = 30, IsDefault = true };
            ok.Click += delegate { DialogResult = true; };
            footer.Children.Add(reset);
            actions.Children.Add(cancel);
            actions.Children.Add(ok);
            Grid.SetColumn(actions, 1);
            footer.Children.Add(actions);
            Grid.SetRow(footer, 10);
            root.Children.Add(footer);

            sizeSlider.ValueChanged += delegate
            {
                Value.Size = sizeSlider.Value;
                sizeValue.Text = Math.Round(Value.Size) + " px";
                ApplyPreview();
            };
            concentricWidthSlider.ValueChanged += delegate
            {
                Value.ConcentricRingWidth = concentricWidthSlider.Value;
                concentricRingWidthValue.Text = Math.Round(Value.ConcentricRingWidth) + " px";
                ApplyPreview();
            };
            weeklyArcWidthSlider.ValueChanged += delegate
            {
                Value.WeeklyArcWidth = weeklyArcWidthSlider.Value;
                weeklyArcWidthValue.Text = Math.Round(Value.WeeklyArcWidth) + " px";
                ApplyPreview();
            };
            languageBox.SelectionChanged += delegate
            {
                ComboBoxItem selected = languageBox.SelectedItem as ComboBoxItem;
                if (selected == null) return;
                Value.Language = (string)selected.Tag;
                ApplyPreview();
            };
            styleBox.SelectionChanged += delegate
            {
                ComboBoxItem selected = styleBox.SelectedItem as ComboBoxItem;
                if (selected == null) return;
                Value.Style = OrbStyles.Normalize((string)selected.Tag);
                ApplyPreview();
            };
            UpdateColorSelection();
        }

        private void AddPreset(Panel parent, string name, string hex, bool weekly)
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
                if (weekly) Value.WeeklyAccent = (string)button.Tag;
                else Value.Accent = (string)button.Tag;
                ApplyPreview();
            };
            (weekly ? weeklyPresetButtons : fiveHourPresetButtons)[hex] = button;
            parent.Children.Add(button);
        }

        private Button CreateCustomColorButton(bool chinese, bool weekly)
        {
            Button button = new Button
            {
                Width = 42,
                Height = 42,
                Content = "+",
                Tag = weekly,
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
            button.Click += OnCustomColor;
            return button;
        }

        private void OnCustomColor(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            bool weekly = button != null && button.Tag is bool && (bool)button.Tag;
            Color current = ColorFromHex(weekly ? Value.WeeklyAccent : Value.Accent);
            using (System.Windows.Forms.ColorDialog dialog = new System.Windows.Forms.ColorDialog())
            {
                dialog.FullOpen = true;
                dialog.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                string selected = String.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", dialog.Color.R, dialog.Color.G, dialog.Color.B);
                if (weekly) Value.WeeklyAccent = selected;
                else Value.Accent = selected;
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
            UpdateColorSelection(fiveHourPresetButtons, fiveHourCustomColorButton, Value.Accent);
            UpdateColorSelection(weeklyPresetButtons, weeklyCustomColorButton, Value.WeeklyAccent);
        }

        private static void UpdateColorSelection(Dictionary<string, Button> buttons, Button customButton, string selectedColor)
        {
            bool presetSelected = false;
            foreach (KeyValuePair<string, Button> item in buttons)
            {
                bool selected = String.Equals(item.Key, selectedColor, StringComparison.OrdinalIgnoreCase);
                if (selected) presetSelected = true;
                item.Value.Content = selected ? "✓" : "";
                item.Value.BorderBrush = selected
                    ? new SolidColorBrush(Color.FromRgb(28, 38, 52))
                    : Brushes.Transparent;
            }

            bool customSelected = !presetSelected;
            customButton.Content = customSelected ? "✓" : "+";
            customButton.Background = customSelected
                ? BrushFromHex(selectedColor)
                : new SolidColorBrush(Color.FromRgb(218, 218, 218));
            customButton.Foreground = customSelected
                ? ContrastBrush(ColorFromHex(selectedColor))
                : new SolidColorBrush(Color.FromRgb(28, 38, 52));
            customButton.BorderBrush = customSelected
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
        private readonly TextBlock weeklyText;
        private readonly ShapePath waveBack;
        private readonly ShapePath waveFront;
        private readonly ShapePath fiveHourTrack;
        private readonly ShapePath fiveHourRing;
        private readonly ShapePath weeklyTrack;
        private readonly ShapePath weeklyRing;
        private readonly ShapePath weeklyArcTrack;
        private readonly ShapePath weeklyArc;
        private readonly ToolTip detailTip;
        private readonly System.Windows.Threading.DispatcherTimer refreshTimer;
        private readonly System.Windows.Threading.DispatcherTimer waveTimer;
        private MenuItem startupItem;
        private OrbSettings settings;
        private UsageSnapshot snapshot;
        private string displayedLanguage;
        private string displayedStyle;
        private Color displayedAccent;
        private Color displayedWeeklyAccent;
        private double displayedConcentricRingWidth;
        private double displayedWeeklyArcWidth;
        private double phase;
        private bool refreshRunning;
        private bool closing;

        public OrbWindow()
        {
            settings = settingsStore.Load();
            displayedLanguage = settings.Language;
            displayedStyle = OrbStyles.Normalize(settings.Style);
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

            fiveHourTrack = CreateMeterPath(5);
            fiveHourRing = CreateMeterPath(5);
            weeklyTrack = CreateMeterPath(4);
            weeklyRing = CreateMeterPath(4);
            weeklyArcTrack = CreateMeterPath(5);
            weeklyArc = CreateMeterPath(5);
            root.Children.Add(fiveHourTrack);
            root.Children.Add(fiveHourRing);
            root.Children.Add(weeklyTrack);
            root.Children.Add(weeklyRing);
            root.Children.Add(weeklyArcTrack);
            root.Children.Add(weeklyArc);

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

            weeklyText = new TextBlock
            {
                Text = "W --", Foreground = new SolidColorBrush(Color.FromArgb(230, 220, 205, 255)),
                FontFamily = new FontFamily("Segoe UI"), FontSize = 10, FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 24)
            };
            root.Children.Add(weeklyText);
            Grid.SetZIndex(labels, 10);
            Grid.SetZIndex(weeklyText, 11);
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
            Closed += delegate
            {
                closing = true;
                refreshTimer.Stop();
                waveTimer.Stop();
                reader.Dispose();
                OrbLogger.Info("悬浮球已关闭");
            };

            refreshTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            refreshTimer.Tick += delegate { RefreshNow(); };
            waveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(45) };
            waveTimer.Tick += delegate { phase += 0.10; DrawWaves(); };
        }

        private static ShapePath CreateMeterPath(double thickness)
        {
            return new ShapePath
            {
                Fill = Brushes.Transparent,
                Stroke = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false
            };
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
            if (refreshRunning || closing) return;
            refreshRunning = true;
            try
            {
                UsageSnapshot latest = await Task.Run(() => reader.ReadLatest());
                if (closing) return;
                if (latest != null) snapshot = latest;
                UpdateDisplay();
            }
            catch (Exception exception)
            {
                OrbLogger.Error("刷新悬浮球失败", exception);
                try
                {
                    if (!closing)
                    {
                        subtitleText.Text = Text(displayedLanguage, "Refresh failed", "刷新失败");
                        detailTip.Content = Text(displayedLanguage,
                            "Refresh failed. See the log for details.",
                            "刷新失败，详细信息请查看日志。");
                    }
                }
                catch (Exception displayException)
                {
                    OrbLogger.Error("显示刷新错误失败", displayException);
                }
            }
            finally { refreshRunning = false; }
        }

        private void UpdateDisplay()
        {
            if (snapshot == null)
            {
                percentText.Text = "--";
                subtitleText.Text = Text(displayedLanguage, "No data", "暂无数据");
                weeklyText.Text = Text(displayedLanguage, "W --", "周 --");
                percentText.Foreground = Brushes.White;
                detailTip.Content = Text(displayedLanguage,
                    "No Codex usage data was found.\nComplete at least one Codex conversation first.",
                    "尚未找到 Codex 用量数据。\n请先在 Codex 中完成一次对话。");
                DrawWaves();
                return;
            }

            LimitWindow fiveHour = snapshot.FiveHour;
            LimitWindow weekly = snapshot.Weekly;
            percentText.Text = FormatPercent(fiveHour);
            subtitleText.Text = fiveHour == null
                ? Text(displayedLanguage, "5-hour unavailable", "5小时暂无数据")
                : Text(displayedLanguage, "5-hour remaining", "5小时剩余");
            weeklyText.Text = Text(displayedLanguage, "W ", "周 ") + FormatPercent(weekly);
            percentText.Foreground = fiveHour != null && fiveHour.RemainingPercent <= 15
                ? new SolidColorBrush(Color.FromRgb(255, 238, 225))
                : Brushes.White;
            detailTip.Content = BuildDetails(snapshot, displayedLanguage);
            DrawWaves();
        }

        private static string FormatPercent(LimitWindow window)
        {
            return window == null
                ? "--"
                : Math.Round(window.RemainingPercent).ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        private static string BuildDetails(UsageSnapshot value, string language)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine(Text(language, "Codex remaining usage", "Codex 剩余用量"));
            foreach (LimitWindow window in value.Windows.OrderBy(x => x.WindowMinutes))
            {
                string reset = FormatResetTime(window.ResetsAt, language);
                if (language == "zh")
                {
                    text.Append(WindowName(window.WindowMinutes, language)).Append("：")
                        .Append(Math.Round(window.RemainingPercent).ToString("0", CultureInfo.InvariantCulture)).Append("%")
                        .Append("（").Append(reset).AppendLine(" 重置）");
                }
                else
                {
                    text.Append(WindowName(window.WindowMinutes, language)).Append(": ")
                        .Append(Math.Round(window.RemainingPercent).ToString("0", CultureInfo.InvariantCulture)).Append("%")
                        .Append(" (resets ").Append(reset).AppendLine(")");
                }
            }
            if (!String.IsNullOrEmpty(value.PlanType)) text.AppendLine(Text(language, "Plan: ", "方案：") + value.PlanType);
            text.Append(Text(language, "Updated: ", "数据更新：")).Append(value.TimestampUtc.ToLocalTime().ToString("HH:mm:ss"));
            return text.ToString();
        }

        private static string FormatResetTime(long resetAt, string language)
        {
            try
            {
                DateTime reset = DateTimeOffset.FromUnixTimeSeconds(resetAt).LocalDateTime;
                return language == "zh"
                    ? reset.ToString("M月d日 HH:mm")
                    : reset.ToString("MMM d, HH:mm", CultureInfo.InvariantCulture);
            }
            catch (ArgumentOutOfRangeException)
            {
                return Text(language, "unknown", "未知");
            }
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
            displayedStyle = OrbStyles.Normalize(value.Style);
            displayedConcentricRingWidth = Math.Max(2, Math.Min(14, value.ConcentricRingWidth));
            displayedWeeklyArcWidth = Math.Max(2, Math.Min(14, value.WeeklyArcWidth));

            Color accent = AppearanceWindow.ColorFromHex(value.Accent);
            displayedAccent = accent;
            displayedWeeklyAccent = AppearanceWindow.ColorFromHex(value.WeeklyAccent);
            Color accentDark = ScaleColor(accent, 0.42);
            Color backgroundTop = ScaleColor(accent, 0.24);
            Color backgroundBottom = ScaleColor(accent, 0.14);
            liquid.Background = new LinearGradientBrush(backgroundTop, backgroundBottom, 90);
            waveBack.Fill = new SolidColorBrush(Color.FromArgb(175, accent.R, accent.G, accent.B));
            waveFront.Fill = new LinearGradientBrush(accent, accentDark, 90);
            Color softText = MixColor(accent, Colors.White, 0.84);
            subtitleText.Foreground = new SolidColorBrush(Color.FromArgb(225, softText.R, softText.G, softText.B));
            bool showSecondaryLabels = size >= 80;
            subtitleText.Visibility = showSecondaryLabels ? Visibility.Visible : Visibility.Collapsed;
            weeklyText.Visibility = showSecondaryLabels ? Visibility.Visible : Visibility.Collapsed;
            percentText.FontSize = displayedStyle == OrbStyles.Concentric ? 32 : 37;
            weeklyText.Margin = displayedStyle == OrbStyles.Concentric
                ? new Thickness(0, 0, 0, 31)
                : new Thickness(0, 0, 0, 24);
            DrawWaves();

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
            LimitWindow fiveHour = snapshot == null ? null : snapshot.FiveHour;
            LimitWindow weekly = snapshot == null ? null : snapshot.Weekly;
            Color warning = Color.FromRgb(255, 122, 69);
            Color fiveHourColor = fiveHour != null && fiveHour.RemainingPercent <= 15 ? warning : displayedAccent;
            Color weeklyColor = weekly != null && weekly.RemainingPercent <= 15 ? warning : displayedWeeklyAccent;
            Brush trackBrush = new SolidColorBrush(Color.FromArgb(72, 255, 255, 255));
            double outerRingWidth = displayedConcentricRingWidth;
            double innerRingWidth = Math.Max(1.5, outerRingWidth * 0.8);
            double arcWidth = displayedWeeklyArcWidth;

            fiveHourTrack.StrokeThickness = outerRingWidth;
            fiveHourRing.StrokeThickness = outerRingWidth;
            weeklyTrack.StrokeThickness = innerRingWidth;
            weeklyRing.StrokeThickness = innerRingWidth;
            weeklyArcTrack.StrokeThickness = arcWidth;
            weeklyArc.StrokeThickness = arcWidth;

            fiveHourTrack.Stroke = trackBrush;
            weeklyTrack.Stroke = trackBrush;
            weeklyArcTrack.Stroke = trackBrush;
            fiveHourRing.Stroke = new SolidColorBrush(fiveHourColor);
            weeklyRing.Stroke = new SolidColorBrush(weeklyColor);
            weeklyArc.Stroke = new SolidColorBrush(weeklyColor);
            weeklyText.Foreground = weekly == null
                ? new SolidColorBrush(Color.FromArgb(150, 220, 225, 232))
                : new SolidColorBrush(weeklyColor);

            bool concentric = displayedStyle == OrbStyles.Concentric;
            fiveHourTrack.Visibility = concentric ? Visibility.Visible : Visibility.Collapsed;
            fiveHourRing.Visibility = concentric ? Visibility.Visible : Visibility.Collapsed;
            weeklyTrack.Visibility = concentric ? Visibility.Visible : Visibility.Collapsed;
            weeklyRing.Visibility = concentric ? Visibility.Visible : Visibility.Collapsed;
            weeklyArcTrack.Visibility = concentric ? Visibility.Collapsed : Visibility.Visible;
            weeklyArc.Visibility = concentric ? Visibility.Collapsed : Visibility.Visible;
            waveBack.Visibility = !concentric && fiveHour != null ? Visibility.Visible : Visibility.Collapsed;
            waveFront.Visibility = !concentric && fiveHour != null ? Visibility.Visible : Visibility.Collapsed;

            if (concentric)
            {
                double outerRadius = OrbSize / 2 - outerRingWidth / 2 - 2;
                double innerRadius = Math.Max(34, outerRadius - outerRingWidth / 2 - innerRingWidth / 2 - 5.5);
                fiveHourTrack.Data = new EllipseGeometry(new Point(OrbSize / 2, OrbSize / 2), outerRadius, outerRadius);
                weeklyTrack.Data = new EllipseGeometry(new Point(OrbSize / 2, OrbSize / 2), innerRadius, innerRadius);
                fiveHourRing.Data = fiveHour == null ? Geometry.Empty : CreateArcGeometry(outerRadius, fiveHour.RemainingPercent, -90, 360);
                weeklyRing.Data = weekly == null ? Geometry.Empty : CreateArcGeometry(innerRadius, weekly.RemainingPercent, -90, 360);
                weeklyArcTrack.Data = Geometry.Empty;
                weeklyArc.Data = Geometry.Empty;
                waveBack.Data = Geometry.Empty;
                waveFront.Data = Geometry.Empty;
                return;
            }

            fiveHourTrack.Data = Geometry.Empty;
            fiveHourRing.Data = Geometry.Empty;
            weeklyTrack.Data = Geometry.Empty;
            weeklyRing.Data = Geometry.Empty;
            double weeklyArcRadius = Math.Max(50, 68 - arcWidth / 2);
            weeklyArcTrack.Data = CreateArcGeometry(weeklyArcRadius, 100, 30, 120);
            weeklyArc.Data = weekly == null ? Geometry.Empty : CreateArcGeometry(weeklyArcRadius, weekly.RemainingPercent, 30, 120);
            if (fiveHour == null)
            {
                waveBack.Data = Geometry.Empty;
                waveFront.Data = Geometry.Empty;
                return;
            }

            waveBack.Data = CreateWaveGeometry(fiveHour.RemainingPercent, phase + 1.8, 5.0, 25.0);
            waveFront.Data = CreateWaveGeometry(fiveHour.RemainingPercent, phase, 6.5, 31.0);
        }

        private static Geometry CreateArcGeometry(double radius, double percent, double startAngle, double maximumSweep)
        {
            double clamped = Math.Max(0, Math.Min(100, percent));
            if (clamped <= 0) return Geometry.Empty;

            double sweep = maximumSweep * clamped / 100.0;
            Point center = new Point(OrbSize / 2, OrbSize / 2);
            PathFigure figure = new PathFigure { StartPoint = PointOnCircle(center, radius, startAngle), IsClosed = false, IsFilled = false };
            double currentAngle = startAngle;
            while (sweep > 0.001)
            {
                double step = Math.Min(180, sweep);
                currentAngle += step;
                figure.Segments.Add(new ArcSegment
                {
                    Point = PointOnCircle(center, radius, currentAngle),
                    Size = new Size(radius, radius),
                    IsLargeArc = false,
                    SweepDirection = SweepDirection.Clockwise
                });
                sweep -= step;
            }
            return new PathGeometry(new[] { figure });
        }

        private static Point PointOnCircle(Point center, double radius, double angle)
        {
            double radians = angle * Math.PI / 180.0;
            return new Point(center.X + (Math.Cos(radians) * radius), center.Y + (Math.Sin(radians) * radius));
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
            bool createdNew;
            using (Mutex singleInstance = new Mutex(true, @"Local\CodexUsageOrb", out createdNew))
            {
                if (!createdNew)
                {
                    OrbLogger.Info("检测到已有实例，忽略重复启动");
                    return;
                }

                Application app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                app.DispatcherUnhandledException += delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs args)
                {
                    OrbLogger.Error("WPF 未处理异常", args.Exception);
                    args.Handled = true;
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs args)
                {
                    Exception exception = args.ExceptionObject as Exception;
                    if (exception != null)
                        OrbLogger.Error("应用域未处理异常", exception);
                    else
                        OrbLogger.Info("应用域未处理异常: " + String.Concat(args.ExceptionObject));
                };

                try
                {
                    OrbLogger.Info("悬浮球启动");
                    app.Run(new OrbWindow());
                }
                catch (Exception exception)
                {
                    OrbLogger.Error("悬浮球启动失败", exception);
                }
            }
        }
    }
}
