using System.Drawing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Forms;

namespace CodexUsageWidgetProvider;

internal sealed class CodexAccountsDialog : Form
{
    private readonly CodexAccounts _accounts = new();
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
    };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(800, 0) };
    private readonly Label _detail = new() { AutoSize = true, MaximumSize = new Size(800, 0) };
    private readonly Label _accountHint = new() { AutoSize = true, MaximumSize = new Size(800, 0) };
    private readonly Button _add = new() { Text = "添加额度账号", AutoSize = true };
    private readonly Button _save = new() { Text = "收录当前账号", AutoSize = true };
    private readonly Button _refresh = new() { Text = "刷新额度与用量", AutoSize = true };
    private readonly Button _switch = new() { Text = "切换并保留项目", AutoSize = true };
    private readonly Button _switchOnly = new() { Text = "仅切换账号", AutoSize = true };
    private readonly Button _forget = new() { Text = "移除记录", AutoSize = true };
    private readonly Button _verify = new() { Text = "验证切换结果", AutoSize = true };
    private readonly Button _cancel = new() { Text = "取消", AutoSize = true, Visible = false };
    private SavedCodexAccount? _current;
    private CodexProjectLayout? _projectLayout;
    private CancellationTokenSource? _cancelSource;
    private bool _currentSaved;
    private bool _busy;
    private bool _loaded;

    private sealed record Row(CodexAccountProfile Profile, bool Saved);

    public static Thread Open()
    {
        var thread = new Thread(() =>
        {
            try { Run(); }
            catch (Exception ex)
            {
                ProviderLog.Write($"Accounts: startup failed {ex.GetType().Name} hr=0x{ex.HResult:X8}");
                MessageBox.Show("账号窗口启动失败。请重试，或查看临时目录中的 codex_widget_provider.log。",
                    "Codex 账号", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    public static void Run()
    {
        ProviderLog.Write("Accounts: UI thread entered");
        using var activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CodexUsageWidget.ShowAccounts");
        using var instance = new Mutex(initiallyOwned: true, @"Local\CodexUsageWidget.AccountWindow", out var first);
        if (!first)
        {
            ProviderLog.Write("Accounts: activating existing window");
            activation.Set();
            return;
        }

        try
        {
            Application.EnableVisualStyles();
            using var dialog = new CodexAccountsDialog();
            _ = dialog.Handle;
            ProviderLog.Write("Accounts: window created");
            var listener = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) =>
            {
                try { dialog.BeginInvoke(dialog.Reveal); }
                catch (InvalidOperationException) { /* The window is closing. */ }
            }, null, Timeout.Infinite, executeOnlyOnce: false);
            try
            {
                // Protocol launchers may request SW_HIDE. Shown is not a reliable
                // place to undo that startup show state: run after the pump starts.
                using var showTimer = new System.Windows.Forms.Timer { Interval = 100 };
                showTimer.Tick += (_, _) =>
                {
                    showTimer.Stop();
                    dialog.Reveal();
                };
                showTimer.Start();
                Application.Run(dialog);
                ProviderLog.Write("Accounts: message loop exited");
            }
            finally
            {
                listener.Unregister(null);
            }
        }
        finally
        {
            instance.ReleaseMutex();
        }
    }

    private void Reveal()
    {
        Show();
        const int restore = 9;
        ShowWindow(Handle, restore);
        if (!TopMost)
        {
            TopMost = true;
            BeginInvoke(() => TopMost = false);
        }

        Activate();
        ProviderLog.Write($"Accounts: revealed visible={Visible} state={WindowState}");
        if (_loaded && !_busy)
            _ = RunAsync();
    }

    private CodexAccountsDialog()
    {
        Text = "Codex 账号";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(900, 400);
        MinimumSize = new Size(810, 335);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_accountHint, 0, 0);
        _list.Columns.Add("账号和工作区", 255);
        _list.Columns.Add("状态 / 套餐", 120);
        _list.Columns.Add("5 小时剩余 / 重置", 120);
        _list.Columns.Add("周剩余 / 重置", 120);
        _list.Columns.Add("账号总用量", 140);
        _list.Columns.Add("额度采样", 110);
        _list.ShowItemToolTips = true;
        layout.Controls.Add(_list, 0, 1);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([_add, _save, _refresh, _switch, _switchOnly, _verify, _forget, _cancel]);
        layout.Controls.Add(_detail, 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        layout.Controls.Add(_status, 0, 4);
        Controls.Add(layout);

        Shown += async (_, _) =>
        {
            BeginInvoke(Reveal);
            await RunAsync();
        };
        Activated += async (_, _) =>
        {
            if (_loaded && !_busy)
                await RunAsync();
        };
        _list.SelectedIndexChanged += (_, _) =>
        {
            UpdateButtons();
            var profile = Selected?.Profile;
            var days = profile?.Usage?.DailyUsage.TakeLast(7).ToArray();
            _detail.Text = profile?.Error ?? (days is { Length: > 0 }
                ? "近期账号每日用量  " + string.Join("  ·  ", days.Select(day => $"{day.Date} {TrendChartRenderer.FormatTokens(day.Tokens)}"))
                : "");
        };
        _save.Click += async (_, _) => await RunAsync(async _ =>
        {
            await Task.Run(_accounts.SaveCurrent);
            return "已收录当前账号。后续会自动更新较新的原生凭据。";
        });
        _add.Click += async (_, _) => await RunAsync(async token =>
        {
            await Task.Run(() => _accounts.AddAccountAsync(token));
            return "已添加额度账号。";
        }, "请在浏览器完成官方登录…", cancellable: true);
        _refresh.Click += async (_, _) => await RunAsync(async token =>
        {
            await Task.Run(() => _accounts.RefreshSavedAsync(token));
            return "已刷新账号额度与用量。悬停在记录上可查看更新时间和单账号错误。";
        }, "正在读取各账号额度与用量…", cancellable: true);
        _switch.Click += async (_, _) => await SwitchAsync(inheritProjects: true);
        _switchOnly.Click += async (_, _) => await SwitchAsync(inheritProjects: false);
        _verify.Click += async (_, _) => await RunAsync(_ => Task.Run(_accounts.VerifyLastSwitchAsync),
            "正在核对桌面身份、原项目和原会话…");
        _forget.Click += async (_, _) =>
        {
            if (Selected is not { Saved: true } row ||
                MessageBox.Show(this, $"移除 {row.Profile.Label} 的已保存记录？", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            await RunAsync(async _ =>
            {
                await Task.Run(() => _accounts.Forget(row.Profile.Key));
                return "已移除保存的记录。";
            });
        };
        _cancel.Click += (_, _) => _cancelSource?.Cancel();
        UpdateButtons();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
            _cancelSource?.Cancel();
        }
        base.OnFormClosing(e);
    }

    private async Task SwitchAsync(bool inheritProjects)
    {
        if (Selected?.Profile is not { Identity: { } identity } profile)
            return;
        var action = inheritProjects ? "切换并保留本地项目" : "仅切换账号";
        await RunAsync(async _ =>
        {
            var result = await Task.Run(() => _accounts.SwitchToAsync(identity, inheritProjects, running =>
                (bool)Invoke(() => ConfirmSwitch(action, profile.Label, running))));
            return result switch
            {
                CodexSwitchResult.Switched => inheritProjects
                    ? "账号已切换，原项目会话已核对。请重新打开 Codex。"
                    : "账号已切换。请重新打开 Codex。",
                CodexSwitchResult.Cancelled => "已取消切换。",
                _ => "已是当前账号。",
            };
        }, "正在准备切换…");
    }

    private bool ConfirmSwitch(string action, string label, bool running)
    {
        var prompt = $"{action}到 {label}？";
        if (running)
            prompt += "\n\n确认后将关闭 Codex 桌面端及命令行进程，必要时会强制结束。正在进行的任务会停止，未保存的操作可能丢失。";
        var confirmed = MessageBox.Show(this, prompt, Text, MessageBoxButtons.YesNo,
            running ? MessageBoxIcon.Warning : MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        if (confirmed && running) _status.Text = "正在关闭 Codex…";
        return confirmed;
    }

    private Row? Selected => _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as Row : null;

    private async Task RunAsync(Func<CancellationToken, Task<string>>? action = null,
        string pending = "正在读取账号…", bool cancellable = false)
    {
        if (_busy)
            return;
        _busy = true;
        using var cancellation = cancellable ? new CancellationTokenSource() : null;
        _cancelSource = cancellation;
        UpdateButtons();
        _status.Text = pending;
        try
        {
            string? message = null;
            try
            {
                if (action is not null)
                    message = await action(cancellation?.Token ?? CancellationToken.None);
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                message = ErrorMessage(ex);
            }

            SavedCodexAccount? current = null;
            string? loginError = null;
            _projectLayout = null;
            try
            {
                current = await Task.Run(_accounts.ReadCurrent);
                if (current is not null)
                    await Task.Run(_accounts.SyncCurrent);
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                loginError = ErrorMessage(ex);
            }

            try { _projectLayout = await Task.Run(_accounts.ReadProjectLayout); }
            catch (Exception ex) when (IsRecoverable(ex)) { loginError ??= ErrorMessage(ex); }

            var saved = await Task.Run(_accounts.ListSaved);
            Render(current, saved);
            _status.Text = message ?? loginError ?? (saved.Count == 0
                ? "还没有收录的账号。"
                : "账号总用量来自服务端。小组件的全机用量来自本地日志。");
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            _status.Text = ErrorMessage(ex);
        }
        finally
        {
            _loaded = true;
            _cancelSource = null;
            _busy = false;
            UpdateButtons();
        }
    }

    private void Render(SavedCodexAccount? current, IReadOnlyList<CodexAccountProfile> saved)
    {
        var selected = Selected?.Profile.Key;
        _current = current;
        var currentKey = current?.Identity.StorageKey;
        _currentSaved = currentKey is not null && saved.Any(profile => profile.Key == currentKey && profile.Identity is not null);
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            if (current is not null && !_currentSaved)
                AddRow(new CodexAccountProfile(current.Identity.StorageKey, current.Identity,
                    current.DisplayName, current.LastRefresh, null, null), saved: false);
            foreach (var profile in saved.OrderByDescending(profile => profile.Key == currentKey))
                AddRow(profile, saved: true);
            if (selected is not null)
            {
                foreach (ListViewItem item in _list.Items)
                {
                    if (((Row)item.Tag!).Profile.Key == selected)
                    {
                        item.Selected = true;
                        break;
                    }
                }
            }
        }
        finally
        {
            _list.EndUpdate();
        }
    }

    private void AddRow(CodexAccountProfile profile, bool saved)
    {
        var active = profile.Identity is not null && profile.Identity == _current?.Identity;
        var usage = profile.Usage;
        var status = !saved ? "当前 · 尚未收录" :
            profile.Error is not null ? (active ? "当前 · 需检查" : "需检查") :
            string.Join(" · ", new[] { active ? "当前" : "已收录", usage?.Plan }.Where(part => part is not null));
        var sample = usage?.SampledAt.ToLocalTime().ToString("MM-dd HH:mm") ?? "—";
        var tokens = usage?.LifetimeTokens is { } total ? TrendChartRenderer.FormatTokens(total) : "—";
        var row = new ListViewItem([profile.Label, status,
            Window(usage?.FiveHourRemaining, usage?.FiveHourResetAt),
            Window(usage?.WeeklyRemaining, usage?.WeeklyResetAt), tokens, sample])
        {
            Tag = new Row(profile, saved),
            ToolTipText = profile.Error ?? (usage?.DailyUsage.LastOrDefault() is { } last
                ? $"最近一天（{last.Date}）账号用量 {TrendChartRenderer.FormatTokens(last.Tokens)}"
                : profile.Label),
        };
        _list.Items.Add(row);
    }

    private static string Window(int? percent, long? timestamp)
    {
        if (percent is null)
            return "—";
        var reset = timestamp is > 0 and <= 253402300799
            ? DateTimeOffset.FromUnixTimeSeconds(timestamp.Value).ToLocalTime().ToString("MM-dd HH:mm") : "";
        return $"{percent}% {reset}".TrimEnd();
    }

    private static bool IsRecoverable(Exception ex) => ex is AccountSwitchException or CryptographicException or
        IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or
        OperationCanceledException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException;

    private static string ErrorMessage(Exception ex) => ex switch
    {
        AccountSwitchException => ex.Message,
        OperationCanceledException => "已取消。",
        CryptographicException => "Windows 无法读取保存的登录，请重新添加该账号。",
        _ => "操作暂时失败。请检查 Codex 登录状态和文件访问权限后重试。",
    };

    private void UpdateButtons()
    {
        var selected = Selected;
        var target = selected is { Saved: true, Profile.Identity: { } identity } && identity != _current?.Identity;
        var legacy = _projectLayout == CodexProjectLayout.AccountScoped;
        var canSwitchOnly = _projectLayout is CodexProjectLayout.AccountScoped or CodexProjectLayout.Unknown;
        _accountHint.Text = canSwitchOnly
            ? "收录当前账号或添加额度账号。旧版 Codex 的新登录可用「仅切换账号」初始化。"
            : "收录当前账号或添加额度账号。登录切换请使用 Codex 的账号菜单。";
        _switch.Visible = legacy;
        _switchOnly.Visible = canSwitchOnly;
        _verify.Visible = canSwitchOnly;
        _list.Enabled = !_busy;
        _add.Enabled = !_busy;
        _save.Enabled = !_busy && _current is not null;
        _refresh.Enabled = !_busy && _list.Items.Cast<ListViewItem>().Any(item => ((Row)item.Tag!).Saved);
        _switch.Enabled = !_busy && legacy && target && _current is not null;
        _switchOnly.Enabled = !_busy && canSwitchOnly && target;
        _forget.Enabled = !_busy && selected is { Saved: true };
        _verify.Enabled = !_busy && canSwitchOnly && _accounts.HasSwitchReceipt;
        _cancel.Visible = _cancelSource is not null;
    }
}
