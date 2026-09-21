using System.Text.Encodings.Web;
using System.Text.Json;

namespace CodexUsageWidgetProvider;

internal static class ProbeProgram
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "page")
        {
            return WritePageTest(args[1]);
        }

        CodexAppServerClient.Log = m => Console.Error.WriteLine($"[trace] {m}");
        var service = new CodexUsageService();
        try
        {
            var data = await service.GetLatestAsync();
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            Console.WriteLine(JsonSerializer.Serialize(data, options));
            Console.WriteLine("--- web payload (data.json) ---");
            Console.WriteLine(service.GetWebDataJson());
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"probe failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            service.Shutdown();
        }
    }

    /// <summary>
    /// Writes the web-widget page plus a standalone test copy with sample data
    /// injected, for rendering with a headless browser (fetch does not work
    /// under file://, so the test copy calls apply() directly).
    /// </summary>
    private static int WritePageTest(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "widget.html"), WebWidget.Html);

        var now = DateTimeOffset.Now.ToUnixTimeSeconds();
        var points = Enumerable.Range(0, 120).Select(i => new[]
        {
            now - (120 - i) * 180,
            (long)Math.Clamp(Math.Round(55 + 25 * Math.Sin(i / 7.0) + 8 * Math.Sin(i / 2.3)), 0, 100),
        }).ToArray();
        var payload = new
        {
            percent = "98",
            windowLabel = "Weekly remaining",
            resetText = "09月28日 14:09 重置",
            primaryLine = "Week: 98%",
            secondaryLine = "",
            planLine = "Plan: prolite",
            updatedLine = "更新于 15:39",
            color = "Good",
            points = points,
        };
        var json = JsonSerializer.Serialize(payload);
        File.WriteAllText(Path.Combine(dir, "data.json"), json);

        var injected = WebWidget.Html.Replace(
            "</body>",
            $"<script>apply({json});" +
            "document.querySelector('.title').textContent = 'w=' + window.innerWidth + ' dpr=' + window.devicePixelRatio +" +
            " ' mq200=' + matchMedia('(max-width: 200px)').matches + ' mq340=' + matchMedia('(max-width: 340px)').matches;</script></body>");
        File.WriteAllText(Path.Combine(dir, "pagetest.html"), injected);
        Console.WriteLine($"wrote {dir}\\widget.html, data.json, pagetest.html");
        return 0;
    }
}
