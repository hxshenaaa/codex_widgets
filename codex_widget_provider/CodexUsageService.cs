using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexUsageWidgetProvider;

/// <summary>
/// Data payload bound into CardTemplate by the widgets host.
/// Property names must match the ${...} bindings in the template.
/// </summary>
public class WidgetDisplayData
{
    public string percent { get; set; } = "--";
    public string windowLabel { get; set; } = "正在读取 Codex 额度…";
    public string resetText { get; set; } = "";
    public string primaryLine { get; set; } = "";
    public string secondaryLine { get; set; } = "";
    public string planLine { get; set; } = "";
    public string updatedLine { get; set; } = "";
    public string localUsageLine { get; set; } = "";
    public bool hasLocalUsageLine { get; set; }
    public string peakLabel { get; set; } = "";
    public string peakColor { get; set; } = "Good";
    public string activitySummaryLabel { get; set; } = "";
    public string timeStartLabel { get; set; } = "24小时前";
    public string timeEndLabel { get; set; } = "现在";
    public string color { get; set; } = "Default";
    public string trendCaption { get; set; } = "";
    public string trendLine { get; set; } = "";
    public string trendImage { get; set; } = "";
    public bool hasTrendImage { get; set; }
    public bool hasSecondaryLine { get; set; }
    public bool hasPlanLine { get; set; }

    public bool hasRecentSessions { get; set; }
    public bool hasSession1 { get; set; }
    public string session1_Title { get; set; } = "";
    public string session1_Detail { get; set; } = "";
    public string session1_Tokens { get; set; } = "";
    public string session1_Time { get; set; } = "";

    public bool hasSession2 { get; set; }
    public string session2_Title { get; set; } = "";
    public string session2_Detail { get; set; } = "";
    public string session2_Tokens { get; set; } = "";
    public string session2_Time { get; set; } = "";

    public bool hasSession3 { get; set; }
    public string session3_Title { get; set; } = "";
    public string session3_Detail { get; set; } = "";
    public string session3_Tokens { get; set; } = "";
    public string session3_Time { get; set; } = "";

    public List<RecentSessionItem> recentSessions { get; set; } = new();

    public static WidgetDisplayData FromError(string message) => new()
    {
        percent = "!",
        windowLabel = "无法读取额度",
        updatedLine = message.Length <= 120 ? message : message[..120],
    };
}

internal sealed record ParsedSnapshot(
    WidgetDisplayData Data,
    int? PrimaryRemaining,
    int? SecondaryRemaining,
    bool PreferSecondary,
    int HorizonSeconds,
    string TrendCaption);

internal sealed class CodexUsageService
{
    private readonly CodexAppServerClient _client = new();
    private readonly History _history = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private WidgetDisplayData _current = new();
    private ParsedSnapshot? _lastParsed;

    public WidgetDisplayData Current => _current;

    public async Task<WidgetDisplayData> GetLatestAsync()
    {
        await _refreshLock.WaitAsync();
        try
        {
            var result = await _client.GetRateLimitsAsync();
            var parsed = SnapshotParser.Parse(result);
            _lastParsed = parsed;

            // Record the sample before charting so the trend includes the
            // current point (same order as the Python app: add, then chart).
            _history.Add(parsed.PrimaryRemaining, parsed.SecondaryRemaining);
            var activity = TokenActivityReader.Get24hHourlyActivity();
            parsed.Data.trendCaption = "24h";
            parsed.Data.trendLine = _history.Sparkline(
                preferSecondary: parsed.PreferSecondary,
                buckets: 24,
                horizonSeconds: parsed.HorizonSeconds);
            parsed.Data.trendImage = TrendChartRenderer.GenerateActivityChart(
                activity.Buckets,
                activity.MaxTokens);
            parsed.Data.hasTrendImage = !string.IsNullOrEmpty(parsed.Data.trendImage);

            bool isAllZero = activity.MaxTokens <= 0;
            parsed.Data.peakLabel = isAllZero ? "峰值: 暂无请求" : $"峰值 {TrendChartRenderer.FormatTokens(activity.MaxTokens)}/h";
            parsed.Data.peakColor = isAllZero ? "Default" : "Good";
            parsed.Data.activitySummaryLabel = activity.TotalTokens > 0
                ? $"24h 消耗 ({TrendChartRenderer.FormatTokens(activity.TotalTokens)})"
                : "24h 消耗 0";
            parsed.Data.timeStartLabel = "24小时前";
            parsed.Data.timeEndLabel = "现在";

            parsed.Data.hasSecondaryLine = !string.IsNullOrEmpty(parsed.Data.secondaryLine);
            parsed.Data.hasPlanLine = !string.IsNullOrEmpty(parsed.Data.planLine);

            var sessions = RecentSessionsReader.GetRecentSessions(3);
            parsed.Data.recentSessions = sessions;
            if (sessions.Count > 0)
            {
                parsed.Data.hasRecentSessions = true;
                if (sessions.Count >= 1)
                {
                    parsed.Data.hasSession1 = true;
                    parsed.Data.session1_Title = sessions[0].title;
                    parsed.Data.session1_Detail = sessions[0].detail;
                    parsed.Data.session1_Tokens = sessions[0].tokens;
                    parsed.Data.session1_Time = sessions[0].time;
                }
                if (sessions.Count >= 2)
                {
                    parsed.Data.hasSession2 = true;
                    parsed.Data.session2_Title = sessions[1].title;
                    parsed.Data.session2_Detail = sessions[1].detail;
                    parsed.Data.session2_Tokens = sessions[1].tokens;
                    parsed.Data.session2_Time = sessions[1].time;
                }
                if (sessions.Count >= 3)
                {
                    parsed.Data.hasSession3 = true;
                    parsed.Data.session3_Title = sessions[2].title;
                    parsed.Data.session3_Detail = sessions[2].detail;
                    parsed.Data.session3_Tokens = sessions[2].tokens;
                    parsed.Data.session3_Time = sessions[2].time;
                }
            }

            _current = parsed.Data;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            throw new InvalidOperationException(
                "找不到 codex 可执行文件。请安装 Codex CLI；若安装在非默认位置，" +
                "可设置环境变量 CODEX_PATH 指向 codex 可执行文件。", ex);
        }
        finally
        {
            _refreshLock.Release();
        }
        return _current;
    }

    /// <summary>
    /// JSON payload served to the web content at data.json:
    /// the display fields plus the chart series (hourly tokens over the last 24h).
    /// </summary>
    public string GetWebDataJson()
    {
        var parsed = _lastParsed;
        var activity = TokenActivityReader.Get24hHourlyActivity();
        var points = activity.Buckets
            .Select(x => new long[] { x.ts, x.tokens })
            .ToArray();

        var data = parsed?.Data ?? new WidgetDisplayData();
        var payload = new
        {
            percent = data.percent,
            windowLabel = data.windowLabel,
            resetText = data.resetText,
            localUsageLine = data.localUsageLine,
            peakLabel = data.peakLabel,
            peakColor = data.peakColor,
            activitySummaryLabel = data.activitySummaryLabel,
            timeStartLabel = data.timeStartLabel,
            timeEndLabel = data.timeEndLabel,
            primaryLine = data.primaryLine,
            secondaryLine = data.secondaryLine,
            planLine = data.planLine,
            updatedLine = data.updatedLine,
            color = data.color,
            trendCaption = parsed?.TrendCaption ?? data.trendCaption,
            points = points,
            recentSessions = data.recentSessions,
        };
        return JsonSerializer.Serialize(payload);
    }

    public void Shutdown()
    {
        _client.Stop();
    }
}

/// <summary>
/// Rolling 30-day sample history, stored as JSON at
/// %USERPROFILE%\.codex_usage_widget_history.json (kept separate from the
/// Python app's sqlite history; the two run side by side).
/// </summary>
internal sealed class History
{
    private const int RetentionSeconds = 30 * 86400;

    public sealed class Sample
    {
        public long ts { get; set; }   // unix seconds
        public int? p { get; set; }   // primary remaining %
        public int? s { get; set; }   // secondary remaining %
    }

    private readonly string _path;
    private readonly object _gate = new();
    private List<Sample> _samples = new();

    public History()
    {
        _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex_usage_widget_history.json");
        try
        {
            if (File.Exists(_path))
            {
                _samples = JsonSerializer.Deserialize<List<Sample>>(File.ReadAllText(_path))
                    ?? new List<Sample>();
            }
        }
        catch
        {
            _samples = new List<Sample>();
        }
    }

    public void Add(int? primaryRemaining, int? secondaryRemaining)
    {
        lock (_gate)
        {
            _samples.Add(new Sample
            {
                ts = DateTimeOffset.Now.ToUnixTimeSeconds(),
                p = primaryRemaining,
                s = secondaryRemaining,
            });
            long cutoff = DateTimeOffset.Now.ToUnixTimeSeconds() - RetentionSeconds;
            _samples.RemoveAll(x => x.ts < cutoff);
            try
            {
                File.WriteAllText(_path, JsonSerializer.Serialize(_samples));
            }
            catch
            {
                // History is best effort; never break the refresh loop.
            }
        }
    }

    /// <summary>
    /// Downsamples the shown window's series into at most <paramref name="buckets"/>
    /// time buckets over the horizon, taking the last sample of each bucket and
    /// skipping empty buckets. Used by the character sparkline and the web chart.
    /// </summary>
    public List<(long ts, int v)> Buckets(bool preferSecondary, int buckets, int horizonSeconds)
    {
        lock (_gate)
        {
            long now = DateTimeOffset.Now.ToUnixTimeSeconds();
            long cutoff = now - horizonSeconds;
            var series = _samples
                .Where(x => x.ts >= cutoff)
                .Select(x => (x.ts, v: preferSecondary ? (x.s ?? x.p) : (x.p ?? x.s)))
                .Where(t => t.v.HasValue)
                .Select(t => (t.ts, t.v!.Value))
                .OrderBy(t => t.ts)
                .ToList();
            if (series.Count < 2)
            {
                return new List<(long, int)>();
            }

            if (series.Count <= buckets)
            {
                return series;
            }

            var result = new List<(long ts, int v)>(buckets);
            double step = (double)(series.Count - 1) / (buckets - 1);
            for (int i = 0; i < buckets; i++)
            {
                int idx = (int)Math.Round(i * step);
                result.Add(series[idx]);
            }
            return result;
        }
    }

    /// <summary>
    /// Renders the recent trend of the shown window as a block-character
    /// sparkline, one character per time bucket. Uses the same horizon as
    /// the Python chart: 7 days for the weekly (secondary) window,
    /// 6 hours for the primary window. Buckets without samples are skipped.
    /// </summary>
    public string Sparkline(bool preferSecondary, int buckets, int horizonSeconds)
    {
        var values = Buckets(preferSecondary, buckets, horizonSeconds);
        if (values.Count < 2)
        {
            return "";
        }

        const string blocks = "▁▂▃▄▅▆▇█";
        var sb = new StringBuilder();
        foreach (var (_, v) in values)
        {
            int level = (int)Math.Clamp(Math.Round(v / 100.0 * 7), 0, 7);
            sb.Append(blocks[level]);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Ports the snapshot selection and formatting logic of codex_usage_widget.py:
/// prefer rateLimitsByLimitId.codex, fall back to the first entry, then
/// to rateLimits; prefer the secondary (weekly) window for the big number.
/// </summary>
internal static class SnapshotParser
{
    public static ParsedSnapshot Parse(JsonElement result)
    {
        var snap = SelectSnapshot(result);
        JsonElement? primary = GetWindow(snap, "primary");
        JsonElement? secondary = GetWindow(snap, "secondary");
        bool shownSecondary = secondary is not null;

        int? primaryRemaining = primary is not null
            ? Math.Clamp(100 - GetUsedPercent(primary.Value), 0, 100) : null;
        int? secondaryRemaining = secondary is not null
            ? Math.Clamp(100 - GetUsedPercent(secondary.Value), 0, 100) : null;

        var data = new WidgetDisplayData();
        JsonElement? shown = secondary ?? primary;
        int mins = 0;
        int usedPercent = 0;
        long resets = 0;
        if (shown is not null)
        {
            int remaining = shownSecondary ? secondaryRemaining!.Value : primaryRemaining!.Value;
            usedPercent = GetUsedPercent(shown.Value);
            data.percent = remaining.ToString();
            mins = GetInt(shown.Value, "windowDurationMins");
            data.windowLabel = mins switch
            {
                10080 => "Weekly remaining",
                300 => "5-hour remaining",
                > 0 => $"{mins}-min remaining",
                _ => "Remaining",
            };
            long? resetsAt = GetLong(shown.Value, "resetsAt");
            resets = resetsAt ?? 0;
            data.resetText = resetsAt is null ? "" : $"{FormatReset(resetsAt.Value)} 重置";
            data.color = remaining <= 15 ? "Attention" : remaining >= 60 ? "Good" : "Default";
        }
        else
        {
            data.percent = "--";
            data.windowLabel = "No quota window";
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long cycleStart = (resets > now && mins > 0)
            ? (resets - (long)mins * 60)
            : (now - 7 * 86400);

        long cycleTokens = TokenActivityReader.GetTokensInRange(cycleStart, now);
        string tokStr = TrendChartRenderer.FormatTokens(cycleTokens);
        data.localUsageLine = $"账号已用: {usedPercent}% · 本机周消耗: {tokStr}";
        data.hasLocalUsageLine = true;

        data.primaryLine = primary is not null ? LineFor(primary.Value, "Primary") : "";
        data.secondaryLine = secondary is not null ? LineFor(secondary.Value, "Secondary") : "";
        string? plan = GetString(snap, "planType");
        data.planLine = string.IsNullOrEmpty(plan) ? "" : $"Plan: {plan}";
        data.updatedLine = $"更新于 {DateTime.Now:HH:mm:ss}";

        int horizonSeconds = (mins >= 1440 || (mins == 0 && shownSecondary))
            ? 7 * 86400
            : (mins > 0 ? Math.Max(6 * 3600, (mins + 60) * 60) : 6 * 3600);
        string trendCaption = horizonSeconds >= 86400 ? $"{horizonSeconds / 86400}d" : $"{horizonSeconds / 3600}h";

        return new ParsedSnapshot(data, primaryRemaining, secondaryRemaining, shownSecondary, horizonSeconds, trendCaption);
    }

    private static JsonElement SelectSnapshot(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Object)
        {
            if (result.TryGetProperty("rateLimitsByLimitId", out var byId) &&
                byId.ValueKind == JsonValueKind.Object)
            {
                if (byId.TryGetProperty("codex", out var codex))
                {
                    return codex;
                }
                foreach (var prop in byId.EnumerateObject())
                {
                    return prop.Value;
                }
            }
            if (result.TryGetProperty("rateLimits", out var rl))
            {
                return rl;
            }
        }
        return default;
    }

    private static string LineFor(JsonElement win, string fallback)
    {
        int remaining = Math.Clamp(100 - GetUsedPercent(win), 0, 100);
        int mins = GetInt(win, "windowDurationMins");
        string name = mins switch
        {
            300 => "5h",
            10080 => "Week",
            > 0 => $"{mins}m",
            _ => fallback,
        };
        return $"{name}: {remaining}%";
    }

    private static JsonElement? GetWindow(JsonElement snap, string name)
    {
        if (snap.ValueKind == JsonValueKind.Object &&
            snap.TryGetProperty(name, out var el) &&
            el.ValueKind == JsonValueKind.Object)
        {
            return el;
        }
        return null;
    }

    private static int GetUsedPercent(JsonElement win)
    {
        if (win.ValueKind == JsonValueKind.Object && win.TryGetProperty("usedPercent", out var el))
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var i))
            {
                return Math.Clamp(i, 0, 100);
            }
            if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d))
            {
                return (int)Math.Clamp(d, 0, 100);
            }
            if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var s))
            {
                return Math.Clamp(s, 0, 100);
            }
        }
        return 0;
    }

    private static int GetInt(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object &&
        el.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.Number &&
        v.TryGetInt32(out var i) ? i : 0;

    private static long? GetLong(JsonElement el, string name)
    {
        if (el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty(name, out var v) &&
            v.ValueKind == JsonValueKind.Number &&
            v.TryGetInt64(out var l))
        {
            return l;
        }
        return null;
    }

    private static string? GetString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object &&
        el.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string FormatReset(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime.ToString("MM'月'dd'日' HH:mm");
}

internal static class TokenActivityReader
{
    private static readonly string s_sessionDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codex", "sessions");

    public record ActivityResult(List<(long ts, long tokens)> Buckets, long MaxTokens, long TotalTokens);

    public static ActivityResult Get24hHourlyActivity()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long cutoff = now - 24 * 3600;
        var buckets = new long[24];

        if (Directory.Exists(s_sessionDir))
        {
            try
            {
                var minModified = DateTime.UtcNow.AddHours(-36);
                var dirInfo = new DirectoryInfo(s_sessionDir);
                var recentFiles = dirInfo.EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                    .Where(f => f.LastWriteTimeUtc >= minModified)
                    .ToList();

                foreach (var file in recentFiles)
                {
                    using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs, Encoding.UTF8);
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (!line.Contains("\"token_usage_record\"")) continue;
                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            var root = doc.RootElement;
                            if (root.TryGetProperty("timestamp", out var tsProp) &&
                                root.TryGetProperty("payload", out var payload) &&
                                payload.TryGetProperty("usage", out var usage) &&
                                usage.TryGetProperty("total_tokens", out var tokProp))
                            {
                                var tsStr = tsProp.GetString();
                                if (DateTimeOffset.TryParse(tsStr, out var dto))
                                {
                                    long unixSec = dto.ToUnixTimeSeconds();
                                    if (unixSec >= cutoff && unixSec <= now)
                                    {
                                        int hr = (int)((unixSec - cutoff) / 3600);
                                        if (hr >= 0 && hr < 24)
                                        {
                                            buckets[hr] += tokProp.GetInt64();
                                        }
                                    }
                                }
                            }
                        }
                        catch
                        {
                            // Skip malformed lines safely
                        }
                    }
                }
            }
            catch
            {
                // Reading local session records is best-effort.
            }
        }

        long maxTokens = buckets.Length > 0 ? buckets.Max() : 0;
        long totalTokens = buckets.Sum();
        var resultList = new List<(long ts, long tokens)>(24);
        for (int i = 0; i < 24; i++)
        {
            long t = cutoff + i * 3600;
            resultList.Add((t, buckets[i]));
        }
        return new ActivityResult(resultList, maxTokens, totalTokens);
    }

    public static long GetTokensInRange(long startUnixSec, long endUnixSec)
    {
        if (!Directory.Exists(s_sessionDir) || startUnixSec >= endUnixSec)
            return 0;

        long total = 0;
        try
        {
            var minModified = DateTimeOffset.FromUnixTimeSeconds(startUnixSec).UtcDateTime.AddDays(-2);
            var dirInfo = new DirectoryInfo(s_sessionDir);
            var files = dirInfo.EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                .Where(f => f.LastWriteTimeUtc >= minModified)
                .ToList();

            foreach (var file in files)
            {
                try
                {
                    using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs, Encoding.UTF8);
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (!line.Contains("\"token_usage_record\"")) continue;
                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            var root = doc.RootElement;
                            if (root.TryGetProperty("timestamp", out var tsProp) &&
                                root.TryGetProperty("payload", out var payload) &&
                                payload.TryGetProperty("usage", out var usage) &&
                                usage.TryGetProperty("total_tokens", out var tokProp))
                            {
                                var tsStr = tsProp.GetString();
                                if (DateTimeOffset.TryParse(tsStr, out var dto))
                                {
                                    long unixSec = dto.ToUnixTimeSeconds();
                                    if (unixSec >= startUnixSec && unixSec <= endUnixSec)
                                    {
                                        total += tokProp.GetInt64();
                                    }
                                }
                            }
                        }
                        catch
                        {
                            // Skip malformed lines safely
                        }
                    }
                }
                catch
                {
                    // Skip inaccessible files safely
                }
            }
        }
        catch
        {
            // Reading local session records is best-effort.
        }

        return total;
    }
}

internal static class TrendChartRenderer
{
    public static string GenerateActivityChart(List<(long ts, long tokens)> buckets, long maxTokens)
    {
        const int scale = 2; // 2x supersampling for Retina / HiDPI crispness
        const int width = 340 * scale;
        const int height = 64 * scale;
        const float padX = 4f * scale;
        const float padTop = 6f * scale;
        const float baseY = height - 4f * scale;

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        Color strokeColor = Color.FromArgb(20, 156, 88); // Activity Green

        // 0% Baseline
        using var basePen = new Pen(Color.FromArgb(50, 140, 140, 140), 1f * scale);
        g.DrawLine(basePen, 0, baseY, width, baseY);

        // Top guide line
        using var guidePen = new Pen(Color.FromArgb(30, 140, 140, 140), 1f * scale)
        {
            DashPattern = new[] { 3f, 3f }
        };
        g.DrawLine(guidePen, 0, padTop, width, padTop);

        int count = buckets.Count;
        if (count < 2) return "";

        var pts = new PointF[count];
        for (int i = 0; i < count; i++)
        {
            float x = padX + (float)i / (count - 1) * (width - 2 * padX);
            float ratio = maxTokens > 0 ? (float)buckets[i].tokens / maxTokens : 0f;
            ratio = Math.Clamp(ratio, 0f, 1f);
            float y = baseY - ratio * (baseY - padTop);
            pts[i] = new PointF(x, y);
        }

        using var strokePath = new GraphicsPath();
        for (int i = 0; i < pts.Length - 1; i++)
        {
            var p0 = i > 0 ? pts[i - 1] : pts[i];
            var p1 = pts[i];
            var p2 = pts[i + 1];
            var p3 = i + 2 < pts.Length ? pts[i + 2] : p2;

            if (p1.Y >= baseY && p2.Y >= baseY)
            {
                strokePath.AddLine(p1, p2);
            }
            else
            {
                float dx = (p2.X - p1.X) / 3f;
                float d1 = (p2.Y - p0.Y) / 6f;
                float d2 = (p3.Y - p1.Y) / 6f;

                float cp1Y = Math.Min(p1.Y + d1, baseY);
                float cp2Y = Math.Min(p2.Y - d2, baseY);

                var cp1 = new PointF(p1.X + dx, cp1Y);
                var cp2 = new PointF(p2.X - dx, cp2Y);
                strokePath.AddBezier(p1, cp1, cp2, p2);
            }
        }

        // Fill area
        using (var fillPath = new GraphicsPath())
        {
            fillPath.AddLine(pts[0].X, baseY, pts[0].X, pts[0].Y);
            fillPath.AddPath(strokePath, true);
            fillPath.AddLine(pts[^1].X, pts[^1].Y, pts[^1].X, baseY);
            fillPath.AddLine(pts[^1].X, baseY, pts[0].X, baseY);
            fillPath.CloseFigure();

            using var brush = new LinearGradientBrush(
                new Point(0, (int)padTop),
                new Point(0, (int)baseY),
                Color.FromArgb(70, strokeColor),
                Color.FromArgb(5, strokeColor));
            g.FillPath(brush, fillPath);
        }

        // Draw curve stroke
        using (var pen = new Pen(strokeColor, 2f * scale)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        })
        {
            g.DrawPath(pen, strokePath);
        }

        // End circle
        var last = pts[^1];
        float dotRadius = 3.5f * scale;
        using (var dotBrush = new SolidBrush(strokeColor))
        {
            g.FillEllipse(dotBrush, last.X - dotRadius, last.Y - dotRadius, dotRadius * 2, dotRadius * 2);
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
    }

    public static string FormatTokens(long n)
    {
        if (n >= 1_000_000) return $"{n / 1_000_000.0:F1}M";
        if (n >= 1_000) return $"{n / 1_000.0:F0}k";
        return n.ToString();
    }
}

public class RecentSessionItem
{
    public string title { get; set; } = "";
    public string detail { get; set; } = "";
    public string tokens { get; set; } = "";
    public string time { get; set; } = "";
}

internal static class RecentSessionsReader
{
    public static List<RecentSessionItem> GetRecentSessions(int maxCount = 3)
    {
        var result = new List<RecentSessionItem>();
        var codexDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var dbPath = Path.Combine(codexDir, "sqlite", "codex-dev.db");
        var sessionsDir = Path.Combine(codexDir, "sessions");

        if (!File.Exists(dbPath)) return result;

        try
        {
            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;");
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT thread_id, display_title, source_updated_at, cwd, git_branch
                FROM local_thread_catalog
                WHERE display_title IS NOT NULL AND display_title != ''
                ORDER BY source_updated_at DESC
                LIMIT @limit";
            cmd.Parameters.AddWithValue("@limit", maxCount);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var threadId = reader.GetString(0);
                var title = reader.GetString(1);
                var updatedAtSec = Convert.ToInt64(reader.GetDouble(2));
                var cwd = reader.IsDBNull(3) ? "" : reader.GetString(3);
                var branch = reader.IsDBNull(4) ? "" : reader.GetString(4);

                var folderName = string.IsNullOrEmpty(cwd) ? "" : Path.GetFileName(cwd.TrimEnd('\\', '/'));
                var detail = string.IsNullOrEmpty(branch)
                    ? folderName
                    : (!string.IsNullOrEmpty(folderName) ? $"{folderName} ({branch})" : branch);

                long tokens = GetThreadTokens(sessionsDir, threadId, updatedAtSec);

                result.Add(new RecentSessionItem
                {
                    title = title,
                    detail = string.IsNullOrEmpty(detail) ? "Codex 会话" : detail,
                    tokens = tokens > 0 ? $"⚡ {TrendChartRenderer.FormatTokens(tokens)}" : "",
                    time = FormatSessionTime(updatedAtSec)
                });
            }
        }
        catch
        {
            // Reading session records is best-effort.
        }

        return result;
    }

    private static long GetThreadTokens(string sessionsDir, string threadId, long updatedAtSec)
    {
        if (!Directory.Exists(sessionsDir)) return 0;
        try
        {
            var dto = DateTimeOffset.FromUnixTimeSeconds(updatedAtSec).ToLocalTime();
            var dayDir = Path.Combine(sessionsDir, dto.Year.ToString(), dto.Month.ToString("D2"), dto.Day.ToString("D2"));
            string[] files = Array.Empty<string>();
            if (Directory.Exists(dayDir))
            {
                files = Directory.GetFiles(dayDir, $"*{threadId}*.jsonl");
            }
            if (files.Length == 0)
            {
                files = Directory.GetFiles(sessionsDir, $"*{threadId}*.jsonl", SearchOption.AllDirectories);
            }

            if (files.Length == 0) return 0;

            long tokens = 0;
            using var stream = File.Open(files[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (!line.Contains("\"token_usage_record\"")) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("payload", out var payload) &&
                        payload.TryGetProperty("usage", out var usage) &&
                        usage.TryGetProperty("total_tokens", out var tokProp))
                    {
                        tokens += tokProp.GetInt64();
                    }
                }
                catch
                {
                    // Skip malformed lines safely
                }
            }
            return tokens;
        }
        catch
        {
            return 0;
        }
    }

    private static string FormatSessionTime(long unixSec)
    {
        try
        {
            var dt = DateTimeOffset.FromUnixTimeSeconds(unixSec).ToLocalTime();
            var now = DateTimeOffset.Now;
            var span = now - dt;

            if (span.TotalMinutes < 1) return "刚刚";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}分钟前";
            if (dt.Date == now.Date) return $"今天 {dt:HH:mm}";
            if (dt.Date == now.Date.AddDays(-1)) return $"昨天 {dt:HH:mm}";
            return dt.ToString("MM-dd HH:mm");
        }
        catch
        {
            return "";
        }
    }
}
