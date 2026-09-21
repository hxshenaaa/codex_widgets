using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using Microsoft.Windows.Widgets;
using Microsoft.Windows.Widgets.Providers;
using Windows.Storage.Streams;

namespace CodexUsageWidgetProvider;

public class TrackedWidget
{
    public string? WidgetId { get; set; }
    public string? WidgetName { get; set; }
    public bool IsActive { get; set; }
}

internal class WidgetProvider : IWidgetProvider, IWidgetResourceProvider
{
    private const int RefreshSeconds = 60;

    /// <summary>
    /// Web-widget mode: the card template carries both metadata.webUrl (web
    /// mode, when the host supports it) and a full adaptive card body as the
    /// rendered fallback, so the widget is never blank. All content for the
    /// web mode (HTML, data) is served from OnResourceRequested.
    /// </summary>
    private const bool UseWebWidget = false;

    public static readonly Dictionary<string, TrackedWidget> RunningWidgets = new();
    private static readonly ManualResetEvent s_emptyWidgetListEvent = new(false);
    private static readonly CodexUsageService s_usage = new();
    private static readonly object s_timerGate = new();
    private static Timer? s_refreshTimer;

    public static ManualResetEvent GetEmptyWidgetListEvent() => s_emptyWidgetListEvent;

    public WidgetProvider()
    {
        ProviderLog.Write("WidgetProvider constructed");
        // Recover the list of pinned widgets in case the provider was restarted.
        var runningWidgets = WidgetManager.GetDefault().GetWidgetInfos();
        foreach (var widgetInfo in runningWidgets)
        {
            var widgetContext = widgetInfo.WidgetContext;
            var widgetId = widgetContext.Id;
            if (widgetId is not null && !RunningWidgets.ContainsKey(widgetId))
            {
                RunningWidgets[widgetId] = new TrackedWidget
                {
                    WidgetId = widgetId,
                    WidgetName = widgetContext.DefinitionId,
                };
            }
        }
        StartRefreshTimer();
    }

    public void CreateWidget(WidgetContext widgetContext)
    {
        ProviderLog.Write("CreateWidget " + widgetContext.Id);
        var widgetId = widgetContext.Id;
        var info = new TrackedWidget
        {
            WidgetId = widgetId,
            WidgetName = widgetContext.DefinitionId,
            IsActive = true,
        };
        RunningWidgets[widgetId] = info;

        PushTo(info); // immediate "loading" render
        _ = RefreshAndPushAsync();
    }

    public void DeleteWidget(string widgetId, string customState)
    {
        ProviderLog.Write("DeleteWidget " + widgetId);
        RunningWidgets.Remove(widgetId);
        if (RunningWidgets.Count == 0)
        {
            s_usage.Shutdown();
            s_emptyWidgetListEvent.Set();
        }
    }

    public void Activate(WidgetContext widgetContext)
    {
        ProviderLog.Write("Activate " + widgetContext.Id);
        if (widgetContext.Id is not null && RunningWidgets.TryGetValue(widgetContext.Id, out var info))
        {
            info.IsActive = true;
        }
        StartRefreshTimer();
        _ = RefreshAndPushAsync();
    }

    public void Deactivate(string widgetId)
    {
        ProviderLog.Write("Deactivate " + widgetId);
        if (widgetId is not null && RunningWidgets.TryGetValue(widgetId, out var info))
        {
            info.IsActive = false;
        }
    }

    public void OnActionInvoked(WidgetActionInvokedArgs actionInvokedArgs)
    {
        // This widget has no interactive actions.
    }

    public void OnWidgetContextChanged(WidgetContextChangedArgs contextChangedArgs)
    {
        var widgetContext = contextChangedArgs.WidgetContext;
        ProviderLog.Write($"OnWidgetContextChanged {widgetContext.Id} size={widgetContext.Size}");
        if (widgetContext.Id is not null && RunningWidgets.TryGetValue(widgetContext.Id, out var info))
        {
            PushTo(info);
        }
    }

    void IWidgetResourceProvider.OnResourceRequested(WidgetResourceRequestedArgs args)
    {
        ProviderLog.Write("OnResourceRequested enter");
        try
        {
            var deferral = args.GetDeferral();
            try
            {
                var uri = args.Request?.Uri ?? string.Empty;
                ProviderLog.Write("resource: " + uri);
                if (uri.StartsWith(WebWidget.Origin, StringComparison.OrdinalIgnoreCase))
                {
                    if (uri.EndsWith("/widget.html", StringComparison.OrdinalIgnoreCase))
                    {
                        args.Response = ResponseFromString(WebWidget.Html, "text/html");
                        ProviderLog.Write("served widget.html");
                    }
                    else if (uri.EndsWith("/data.json", StringComparison.OrdinalIgnoreCase))
                    {
                        var response = ResponseFromString(s_usage.GetWebDataJson(), "application/json");
                        response.Headers.Add("Cache-Control", "no-store");
                        args.Response = response;
                        ProviderLog.Write("served data.json");
                    }
                }
            }
            finally
            {
                try
                {
                    deferral.Complete();
                }
                catch (Exception ex)
                {
                    ProviderLog.Write("deferral.Complete error: " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            // Log and swallow: leaving the response unset makes the host fall
            // back to fetching from the web, which fails safely for .invalid.
            ProviderLog.Write("resource handler error: " + ex);
        }
    }

    private static WidgetResourceResponse ResponseFromString(string content, string contentType)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var response = new WidgetResourceResponse(
            RandomAccessStreamReference.CreateFromStream(stream.AsRandomAccessStream()), "OK", 200);
        response.Headers.Add("Content-Type", contentType);
        return response;
    }

    private static void StartRefreshTimer()
    {
        lock (s_timerGate)
        {
            if (s_refreshTimer is null)
            {
                s_refreshTimer = new Timer(
                    _ => _ = RefreshAndPushAsync(),
                    null,
                    TimeSpan.FromSeconds(RefreshSeconds),
                    TimeSpan.FromSeconds(RefreshSeconds));
            }
        }
    }

    private static async Task RefreshAndPushAsync()
    {
        WidgetDisplayData data;
        try
        {
            data = await s_usage.GetLatestAsync();
        }
        catch (Exception ex)
        {
            data = WidgetDisplayData.FromError(ex.Message);
        }

        foreach (var info in RunningWidgets.Values.ToList())
        {
            Push(info, data);
        }
    }

    private static void PushTo(TrackedWidget info) => Push(info, s_usage.Current);

    private static void Push(TrackedWidget info, WidgetDisplayData data)
    {
        try
        {
            var updateOptions = new WidgetUpdateRequestOptions(info.WidgetId)
            {
                Template = UseWebWidget ? WebWidget.CardStubJson : CardTemplate.Json,
                Data = JsonSerializer.Serialize(data),
                CustomState = string.Empty,
            };
            WidgetManager.GetDefault().UpdateWidget(updateOptions);
            ProviderLog.Write("push ok");
        }
        catch (Exception ex)
        {
            ProviderLog.Write("push failed: " + ex.Message);
        }
    }
}

/// <summary>Simple file log in %TEMP% for diagnosing host interactions.</summary>
internal static class ProviderLog
{
    private static readonly string s_path = Path.Combine(Path.GetTempPath(), "codex_widget_provider.log");
    private static readonly object s_gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (s_gate)
            {
                File.AppendAllText(s_path, DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never break the provider.
        }
    }
}

/// <summary>
/// Fallback adaptive card template for UseWebWidget=false. Values are bound
/// from WidgetDisplayData, with $host.widgetSize conditionals.
/// </summary>
internal static class CardTemplate
{
    public const string Json = """
    {
      "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
      "type": "AdaptiveCard",
      "version": "1.5",
      "body": [
        {
          "type": "TextBlock",
          "text": "Codex",
          "size": "Medium",
          "weight": "Bolder",
          "spacing": "None",
          "$when": "${$host.widgetSize != \"small\"}"
        },
        {
          "type": "ColumnSet",
          "spacing": "Small",
          "columns": [
            {
              "type": "Column",
              "width": "auto",
              "verticalContentAlignment": "Center",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "${percent}%",
                  "size": "ExtraLarge",
                  "weight": "Bolder",
                  "color": "${color}",
                  "spacing": "None"
                }
              ]
            },
            {
              "type": "Column",
              "width": "stretch",
              "verticalContentAlignment": "Center",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "${windowLabel}",
                  "isSubtle": true,
                  "spacing": "None",
                  "wrap": true
                },
                {
                  "type": "TextBlock",
                  "text": "${resetText}",
                  "spacing": "None",
                  "wrap": true
                }
              ]
            }
          ]
        },
        {
          "type": "ColumnSet",
          "spacing": "Small",
          "$when": "${hasTrendImage && $host.widgetSize != 'small'}",
          "columns": [
            {
              "type": "Column",
              "width": "auto",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "${peakLabel}",
                  "size": "Small",
                  "weight": "Bolder",
                  "color": "${peakColor}",
                  "spacing": "None"
                }
              ]
            },
            {
              "type": "Column",
              "width": "stretch",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "${activitySummaryLabel}",
                  "size": "Small",
                  "isSubtle": true,
                  "horizontalAlignment": "Right",
                  "spacing": "None"
                }
              ]
            }
          ]
        },
        {
          "type": "Image",
          "url": "${trendImage}",
          "altText": "Codex usage trend chart",
          "size": "Stretch",
          "spacing": "None",
          "$when": "${hasTrendImage && $host.widgetSize != 'small'}"
        },
        {
          "type": "ColumnSet",
          "spacing": "None",
          "$when": "${hasTrendImage && $host.widgetSize != 'small'}",
          "columns": [
            {
              "type": "Column",
              "width": "auto",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "${timeStartLabel}",
                  "size": "Small",
                  "isSubtle": true,
                  "spacing": "None"
                }
              ]
            },
            {
              "type": "Column",
              "width": "stretch",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "${timeEndLabel}",
                  "size": "Small",
                  "isSubtle": true,
                  "horizontalAlignment": "Right",
                  "spacing": "None"
                }
              ]
            }
          ]
        },
        {
          "type": "TextBlock",
          "text": "等待历史采样…",
          "size": "Small",
          "isSubtle": true,
          "spacing": "Small",
          "$when": "${!hasTrendImage && $host.widgetSize != 'small'}"
        },
        {
          "type": "TextBlock",
          "text": "${localUsageLine}",
          "size": "Small",
          "weight": "Bolder",
          "spacing": "Small",
          "$when": "${hasLocalUsageLine && $host.widgetSize != 'small'}"
        },
        {
          "type": "TextBlock",
          "text": "${primaryLine}",
          "size": "Small",
          "isSubtle": true,
          "spacing": "None",
          "$when": "${$host.widgetSize != 'small'}"
        },
        {
          "type": "TextBlock",
          "text": "${secondaryLine}",
          "size": "Small",
          "isSubtle": true,
          "spacing": "None",
          "$when": "${hasSecondaryLine && $host.widgetSize != 'small'}"
        },
        {
          "type": "TextBlock",
          "text": "${planLine}",
          "size": "Small",
          "isSubtle": true,
          "spacing": "None",
          "$when": "${hasPlanLine && $host.widgetSize == 'large'}"
        },
        {
          "type": "TextBlock",
          "text": "${updatedLine}",
          "size": "Small",
          "isSubtle": true,
          "spacing": "Medium",
          "wrap": true
        }
      ]
    }
    """;
}
