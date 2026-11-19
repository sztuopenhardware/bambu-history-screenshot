using System;
using System.Drawing;
using System.IO;
using System.Text;

namespace BambuHistoryScreenshot
{
    public enum DuplicatePolicy
    {
        AutoNumber = 0,
        SkipExisting = 1,
        Overwrite = 2
    }

    public enum ScrollDirectionMode
    {
        Auto = 0,
        VerticalDown = 1,
        HorizontalRight = 2
    }

    public class PrintHistoryCard
    {
        public string UniqueKey { get; set; }
        public string Name { get; set; }
        public DateTime PrintDate { get; set; }
        public bool HasTime { get; set; }
        public string RawDateText { get; set; }
        public string Duration { get; set; }
        public string Device { get; set; }
        public string Plate { get; set; }
        public string Status { get; set; }
        public Rectangle Bounds { get; set; }
        public Rectangle ImageBounds { get; set; }
        public bool NeedsVisualTopRefinement { get; set; }
        public bool IsFullyVisible { get; set; }
        public bool IsSelected { get; set; }
        public string SavedFilePath { get; set; }
        public string CaptureState { get; set; }
        public string DetectionTier { get; set; }

        public PrintHistoryCard()
        {
            Name = string.Empty;
            RawDateText = string.Empty;
            Duration = string.Empty;
            Device = string.Empty;
            Plate = string.Empty;
            Status = string.Empty;
            SavedFilePath = string.Empty;
            CaptureState = "待截图";
            DetectionTier = string.Empty;
            IsSelected = true;
        }

        public string BuildUniqueKey()
        {
            string datePart = HasTime
                ? PrintDate.ToString("yyyy-MM-dd HH:mm")
                : PrintDate.ToString("yyyy-MM-dd");
            return string.Format("{0}|{1}|{2}|{3}",
                datePart,
                (Name ?? string.Empty).Trim(),
                (Plate ?? string.Empty).Trim(),
                (Device ?? string.Empty).Trim());
        }

        public string FormatDisplayDate()
        {
            if (PrintDate == DateTime.MinValue)
            {
                return RawDateText ?? string.Empty;
            }
            return HasTime
                ? PrintDate.ToString("yyyy/MM/dd HH:mm")
                : PrintDate.ToString("yyyy/MM/dd");
        }
    }

    public class ScanResult
    {
        public bool BambuWindowFound { get; set; }
        public IntPtr MainWindowHandle { get; set; }
        public IntPtr ContentWindowHandle { get; set; }
        public string WindowTitle { get; set; }
        public Rectangle ViewportRect { get; set; }
        public System.Collections.Generic.List<PrintHistoryCard> Cards { get; set; }
        public string Message { get; set; }
        public int ScannedWebViews { get; set; }
        public int ScannedNodes { get; set; }

        public ScanResult()
        {
            Cards = new System.Collections.Generic.List<PrintHistoryCard>();
            WindowTitle = string.Empty;
            Message = string.Empty;
        }
    }

    public class AppConfig
    {
        public string OutputFolder { get; set; }
        public string NamingTemplate { get; set; }
        public DuplicatePolicy DuplicateMode { get; set; }
        public int PaddingPixels { get; set; }
        public ScrollDirectionMode ScrollMode { get; set; }
        public int ScrollDelayMs { get; set; }
        public int MaxAutoScrollCards { get; set; }

        public AppConfig()
        {
            OutputFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "screenshots");
            NamingTemplate = "{date}-{name}";
            DuplicateMode = DuplicatePolicy.AutoNumber;
            PaddingPixels = 10;
            ScrollMode = ScrollDirectionMode.Auto;
            ScrollDelayMs = 650;
            MaxAutoScrollCards = 200;
        }

        private static string GetConfigPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini");
        }

        public static AppConfig Load()
        {
            AppConfig cfg = new AppConfig();
            string path = GetConfigPath();
            if (!File.Exists(path))
            {
                return cfg;
            }

            try
            {
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                foreach (string rawLine in lines)
                {
                    if (string.IsNullOrWhiteSpace(rawLine)) continue;
                    string line = rawLine.Trim();
                    if (line.StartsWith("#") || line.StartsWith(";")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();

                    int intVal;
                    switch (key)
                    {
                        case "OutputFolder":
                            if (!string.IsNullOrEmpty(val)) cfg.OutputFolder = val;
                            break;
                        case "NamingTemplate":
                            if (!string.IsNullOrEmpty(val)) cfg.NamingTemplate = val;
                            break;
                        case "DuplicateMode":
                            if (int.TryParse(val, out intVal) && Enum.IsDefined(typeof(DuplicatePolicy), intVal))
                                cfg.DuplicateMode = (DuplicatePolicy)intVal;
                            break;
                        case "PaddingPixels":
                            if (int.TryParse(val, out intVal) && intVal >= 0 && intVal <= 50)
                                cfg.PaddingPixels = intVal;
                            break;
                        case "ScrollMode":
                            if (int.TryParse(val, out intVal) && Enum.IsDefined(typeof(ScrollDirectionMode), intVal))
                                cfg.ScrollMode = (ScrollDirectionMode)intVal;
                            break;
                        case "ScrollDelayMs":
                            if (int.TryParse(val, out intVal) && intVal >= 200 && intVal <= 3000)
                                cfg.ScrollDelayMs = intVal;
                            break;
                        case "MaxAutoScrollCards":
                            if (int.TryParse(val, out intVal) && intVal >= 1 && intVal <= 5000)
                                cfg.MaxAutoScrollCards = intVal;
                            break;
                    }
                }
            }
            catch
            {
                // Ignore malformed config and use defaults
            }

            return cfg;
        }

        public void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# Bambu Studio Print History Screenshot Tool Configuration");
                sb.AppendLine("OutputFolder=" + (OutputFolder ?? string.Empty));
                sb.AppendLine("NamingTemplate=" + (NamingTemplate ?? "{date}-{name}"));
                sb.AppendLine("DuplicateMode=" + ((int)DuplicateMode).ToString());
                sb.AppendLine("PaddingPixels=" + PaddingPixels.ToString());
                sb.AppendLine("ScrollMode=" + ((int)ScrollMode).ToString());
                sb.AppendLine("ScrollDelayMs=" + ScrollDelayMs.ToString());
                sb.AppendLine("MaxAutoScrollCards=" + MaxAutoScrollCards.ToString());
                File.WriteAllText(GetConfigPath(), sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // Non-fatal
            }
        }
    }

    public static class FilenameHelper
    {
        private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

        public static string FormatFileName(PrintHistoryCard card, string template)
        {
            if (string.IsNullOrWhiteSpace(template))
            {
                template = "{date}-{name}";
            }

            string dateStr = card.PrintDate != DateTime.MinValue
                ? card.PrintDate.ToString("yyyy-MM-dd")
                : "unknown-date";
            string dateCompact = card.PrintDate != DateTime.MinValue
                ? card.PrintDate.ToString("yyyyMMdd")
                : "00000000";
            string timeStr = (card.PrintDate != DateTime.MinValue && card.HasTime)
                ? card.PrintDate.ToString("HHmm")
                : "0000";
            string dateTimeStr = (card.PrintDate != DateTime.MinValue && card.HasTime)
                ? card.PrintDate.ToString("yyyy-MM-dd_HHmm")
                : dateStr;

            string safeName = SanitizePart(string.IsNullOrWhiteSpace(card.Name) ? "未命名模型" : card.Name);
            string safePlate = SanitizePart((card.Plate ?? string.Empty).Replace(" ", string.Empty));
            string safeDevice = SanitizePart(card.Device ?? string.Empty);
            string safeStatus = SanitizePart(card.Status ?? string.Empty);
            string safeDuration = SanitizePart(card.Duration ?? string.Empty);

            string result = template
                .Replace("{date}", dateStr)
                .Replace("{date_compact}", dateCompact)
                .Replace("{time}", timeStr)
                .Replace("{datetime}", dateTimeStr)
                .Replace("{name}", safeName)
                .Replace("{plate}", safePlate)
                .Replace("{device}", safeDevice)
                .Replace("{status}", safeStatus)
                .Replace("{duration}", safeDuration);

            result = SanitizeFileName(result);
            if (string.IsNullOrWhiteSpace(result))
            {
                result = dateStr + "-print";
            }

            // Limit filename length safely (leaving room for path and suffix)
            if (result.Length > 150)
            {
                result = result.Substring(0, 150).TrimEnd(' ', '.', '-', '_');
            }

            if (!result.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                result += ".png";
            }

            return result;
        }

        public static string SanitizePart(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            StringBuilder sb = new StringBuilder(input.Length);
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if (c < 32 || Array.IndexOf(InvalidChars, c) >= 0)
                {
                    sb.Append('_');
                }
                else
                {
                    sb.Append(c);
                }
            }
            string cleaned = sb.ToString().Trim();
            while (cleaned.Contains("__"))
            {
                cleaned = cleaned.Replace("__", "_");
            }
            return cleaned.Trim('.', ' ');
        }

        public static string SanitizeFileName(string name)
        {
            string cleaned = SanitizePart(name);
            // Collapse redundant dashes like "2026-11-19--name" when an optional token is empty
            while (cleaned.Contains("--"))
            {
                cleaned = cleaned.Replace("--", "-");
            }
            return cleaned.Trim('-', '_', '.', ' ');
        }

        public static string ResolveTargetPath(string folder, string fileName, DuplicatePolicy policy, out bool shouldSkip)
        {
            shouldSkip = false;
            string fullPath = Path.Combine(folder, fileName);

            if (!File.Exists(fullPath))
            {
                return fullPath;
            }

            if (policy == DuplicatePolicy.Overwrite)
            {
                return fullPath;
            }

            if (policy == DuplicatePolicy.SkipExisting)
            {
                shouldSkip = true;
                return fullPath;
            }

            string baseName = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            int counter = 2;
            while (counter < 10000)
            {
                string candidate = Path.Combine(folder, string.Format("{0}_{1}{2}", baseName, counter, ext));
                if (!File.Exists(candidate))
                {
                    return candidate;
                }
                counter++;
            }

            return Path.Combine(folder, string.Format("{0}_{1}{2}", baseName, DateTime.Now.Ticks, ext));
        }
    }
}
