import json
import os
import queue
import shutil
import sqlite3
import subprocess
import threading
import time
import tkinter as tk
from datetime import datetime
from pathlib import Path


APP_NAME = "Codex Usage Widget"
POLL_SECONDS = 60
DB_PATH = Path.home() / ".codex_usage_widget.sqlite3"


def fmt_reset(ts):
    if not ts:
        return "--"
    try:
        dt = datetime.fromtimestamp(int(ts))
        return dt.strftime("%m月%d日 %H:%M")
    except Exception:
        return "--"


def fmt_tokens(n):
    if n >= 1_000_000:
        return f"{n / 1_000_000.0:.1f}M"
    if n >= 1_000:
        return f"{n / 1_000.0:.0f}k"
    return str(n)


def read_24h_token_activity():
    sessions_dir = Path.home() / ".codex" / "sessions"
    now = int(time.time())
    cutoff = now - 24 * 3600
    buckets = [0] * 24
    if not sessions_dir.is_dir():
        return [(cutoff + i * 3600, 0) for i in range(24)], 0, 0

    try:
        for p in sessions_dir.glob("*/*/*/*.jsonl"):
            try:
                if p.stat().st_mtime < cutoff - 3600:
                    continue
                with open(p, "r", encoding="utf-8", errors="replace") as fh:
                    for line in fh:
                        if '"token_usage_record"' not in line:
                            continue
                        try:
                            record = json.loads(line)
                            if record.get("type") == "token_usage_record":
                                payload = record.get("payload") or {}
                                usage = payload.get("usage") or {}
                                tok = usage.get("total_tokens") or 0
                                ts_str = record.get("timestamp")
                                if ts_str:
                                    dt = datetime.fromisoformat(ts_str.replace("Z", "+00:00"))
                                    sec = int(dt.timestamp())
                                    if cutoff <= sec <= now:
                                        hr = (sec - cutoff) // 3600
                                        if 0 <= hr < 24:
                                            buckets[hr] += tok
                        except Exception:
                            pass
            except Exception:
                pass
    except Exception:
        pass

    max_tokens = max(buckets) if buckets else 0
    total_tokens = sum(buckets)
    result = [(cutoff + i * 3600, buckets[i]) for i in range(24)]
    return result, max_tokens, total_tokens


def read_cycle_tokens(resets_at, mins):
    sessions_dir = Path.home() / ".codex" / "sessions"
    now = int(time.time())
    if resets_at and mins:
        cycle_start = resets_at - mins * 60
    else:
        cycle_start = now - 7 * 86400
    if not sessions_dir.is_dir():
        return 0
    total = 0
    cutoff_file = cycle_start - 2 * 86400
    try:
        for p in sessions_dir.glob("*/*/*/*.jsonl"):
            try:
                if p.stat().st_mtime < cutoff_file:
                    continue
                with open(p, "r", encoding="utf-8", errors="replace") as fh:
                    for line in fh:
                        if '"token_usage_record"' not in line:
                            continue
                        try:
                            record = json.loads(line)
                            if record.get("type") == "token_usage_record":
                                ts_str = record.get("timestamp")
                                if ts_str:
                                    dt = datetime.fromisoformat(ts_str.replace("Z", "+00:00"))
                                    sec = int(dt.timestamp())
                                    if cycle_start <= sec <= now:
                                        tok = (record.get("payload") or {}).get("usage", {}).get("total_tokens", 0)
                                        total += tok
                        except Exception:
                            pass
            except Exception:
                pass
    except Exception:
        pass
    return total


def find_codex():
    """Resolve the codex executable path.

    Order: CODEX_PATH env override -> real codex(.exe) on PATH -> the native
    binary inside an npm global install -> bare "codex".
    """
    override = os.environ.get("CODEX_PATH")
    if override:
        p = Path(override)
        if p.is_file():
            return str(p)

    if os.name == "nt":
        # Popen(shell=False) cannot run the npm .cmd shims that
        # shutil.which("codex") would find, so look for a real exe.
        exe = shutil.which("codex.exe")
    else:
        exe = shutil.which("codex")
    if exe:
        return exe

    # npm on Windows only puts .cmd shims on PATH; the actual binary lives in
    # the platform package's vendor directory.
    appdata = os.environ.get("APPDATA")
    if appdata:
        base = Path(appdata) / "npm" / "node_modules"
        for pattern in (
            "@openai/codex/node_modules/@openai/codex-win32-*/vendor/*/bin/codex.exe",
            "@openai/codex-win32-*/vendor/*/bin/codex.exe",
        ):
            hits = sorted(base.glob(pattern))
            if hits:
                return str(hits[0])

    return "codex"


class CodexAppServer:
    def __init__(self):
        self.proc = None
        self.next_id = 1
        self.pending = {}
        self.lock = threading.Lock()
        self.reader_thread = None
        self.stderr_thread = None
        self.notifications = queue.Queue()

    def start(self):
        if self.proc and self.proc.poll() is None:
            return

        # shell=False is important for JSONL stdio on Windows.
        self.proc = subprocess.Popen(
            [find_codex(), "app-server", "--stdio"],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
            bufsize=1,
            shell=False,
            creationflags=(subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0),
        )

        self.reader_thread = threading.Thread(target=self._read_stdout, daemon=True)
        self.reader_thread.start()
        self.stderr_thread = threading.Thread(target=self._drain_stderr, daemon=True)
        self.stderr_thread.start()

        self.request(
            "initialize",
            {
                "clientInfo": {
                    "name": "codex_usage_widget",
                    "title": APP_NAME,
                    "version": "1.0.0",
                },
                "capabilities": {"experimentalApi": True},
            },
            timeout=15,
        )
        self.notify("initialized", {})

    def stop(self):
        if self.proc and self.proc.poll() is None:
            try:
                self.proc.terminate()
                self.proc.wait(timeout=2)
            except Exception:
                try:
                    self.proc.kill()
                except Exception:
                    pass
        self.proc = None

    def _send(self, obj):
        if not self.proc or self.proc.poll() is not None:
            raise RuntimeError("codex app-server is not running")
        line = json.dumps(obj, ensure_ascii=False, separators=(",", ":")) + "\n"
        self.proc.stdin.write(line)
        self.proc.stdin.flush()

    def notify(self, method, params=None):
        msg = {"method": method}
        if params is not None:
            msg["params"] = params
        self._send(msg)

    def request(self, method, params=None, timeout=15):
        with self.lock:
            rid = self.next_id
            self.next_id += 1
            q = queue.Queue(maxsize=1)
            self.pending[rid] = q

        msg = {"id": rid, "method": method}
        if params is not None:
            msg["params"] = params
        self._send(msg)

        try:
            resp = q.get(timeout=timeout)
        except queue.Empty:
            with self.lock:
                self.pending.pop(rid, None)
            raise TimeoutError(f"{method} timed out")

        if "error" in resp:
            err = resp["error"]
            raise RuntimeError(err.get("message") or str(err))
        return resp.get("result")

    def rate_limits(self):
        return self.request("account/rateLimits/read", timeout=20)

    def _read_stdout(self):
        try:
            for raw in self.proc.stdout:
                raw = raw.strip()
                if not raw:
                    continue
                try:
                    msg = json.loads(raw)
                except json.JSONDecodeError:
                    continue

                if "id" in msg:
                    with self.lock:
                        q = self.pending.pop(msg["id"], None)
                    if q:
                        q.put(msg)
                elif "method" in msg:
                    self.notifications.put(msg)
        finally:
            # Unblock pending requests if the process dies.
            with self.lock:
                pending = list(self.pending.items())
                self.pending.clear()
            for rid, q in pending:
                try:
                    q.put_nowait({"id": rid, "error": {"message": "codex app-server exited"}})
                except Exception:
                    pass

    def _drain_stderr(self):
        # Keep stderr drained so the subprocess cannot block on a full pipe.
        try:
            for _ in self.proc.stderr:
                pass
        except Exception:
            pass


class History:
    def __init__(self, path=DB_PATH):
        self.conn = sqlite3.connect(path, check_same_thread=False)
        self.conn.execute(
            """CREATE TABLE IF NOT EXISTS samples(
                ts INTEGER NOT NULL,
                bucket TEXT NOT NULL,
                primary_used INTEGER,
                secondary_used INTEGER
            )"""
        )
        self.conn.commit()

    def add(self, bucket, primary_used, secondary_used):
        now = int(time.time())
        self.conn.execute(
            "INSERT INTO samples(ts,bucket,primary_used,secondary_used) VALUES(?,?,?,?)",
            (now, bucket, primary_used, secondary_used),
        )
        # Keep 30 days only.
        self.conn.execute("DELETE FROM samples WHERE ts < ?", (now - 30 * 86400,))
        self.conn.commit()

    def recent(self, bucket, column, seconds=7 * 86400, limit=300):
        if column not in ("primary_used", "secondary_used"):
            raise ValueError("bad column")
        cutoff = int(time.time()) - seconds
        rows = self.conn.execute(
            f"""SELECT ts,{column} FROM samples
                WHERE bucket=? AND ts>=? AND {column} IS NOT NULL
                ORDER BY ts DESC LIMIT ?""",
            (bucket, cutoff, limit),
        ).fetchall()
        rows.reverse()
        return rows


class Widget(tk.Tk):
    BG = "#2397c9"
    FG = "#dcefff"
    MUTED = "#b7e2f7"
    LINE = "#e6f5ff"

    def __init__(self):
        super().__init__()
        self.title(APP_NAME)
        self.geometry("330x300+40+80")
        self.configure(bg=self.BG)
        self.overrideredirect(True)
        self.attributes("-topmost", True)
        try:
            self.attributes("-alpha", 0.96)
        except tk.TclError:
            pass

        self.server = CodexAppServer()
        self.history = History()
        self.current_bucket = "codex"
        self.current_snapshot = None
        self.drag_start = None

        self._build()
        self.bind("<ButtonPress-1>", self._drag_begin)
        self.bind("<B1-Motion>", self._drag_move)
        self.bind("<Button-3>", self._show_menu)
        self.protocol("WM_DELETE_WINDOW", self.close)

        self.menu = tk.Menu(self, tearoff=0)
        self.menu.add_command(label="立即刷新", command=self.refresh_async)
        self.menu.add_command(label="关闭", command=self.close)

        self.refresh_async()

    def _build(self):
        self.header = tk.Label(
            self, text="Codex", font=("Segoe UI", 12, "bold"),
            bg=self.BG, fg=self.FG
        )
        self.header.place(x=18, y=14)

        self.percent = tk.Label(
            self, text="--%", font=("Segoe UI", 31, "bold"),
            bg=self.BG, fg=self.FG
        )
        self.percent.place(relx=0.5, y=42, anchor="n")

        self.window_label = tk.Label(
            self, text="Weekly remaining", font=("Segoe UI", 10),
            bg=self.BG, fg=self.MUTED
        )
        self.window_label.place(relx=0.5, y=96, anchor="n")

        self.reset = tk.Label(
            self, text="重置时间 --", font=("Segoe UI", 12, "bold"),
            bg=self.BG, fg=self.FG
        )
        self.reset.place(relx=0.5, y=121, anchor="n")

        self.canvas = tk.Canvas(
            self, width=292, height=90, bg=self.BG,
            highlightthickness=0, bd=0
        )
        self.canvas.place(x=18, y=154)

        self.detail = tk.Label(
            self, text="正在读取 Codex 额度…", font=("Segoe UI", 9),
            bg=self.BG, fg=self.MUTED, justify="left", anchor="w"
        )
        self.detail.place(x=18, y=250, width=292)

        self.status = tk.Label(
            self, text="", font=("Segoe UI", 8),
            bg=self.BG, fg=self.MUTED, anchor="e"
        )
        self.status.place(x=18, y=278, width=292)

    def _drag_begin(self, event):
        self.drag_start = (event.x_root, event.y_root, self.winfo_x(), self.winfo_y())

    def _drag_move(self, event):
        if not self.drag_start:
            return
        sx, sy, wx, wy = self.drag_start
        self.geometry(f"+{wx + event.x_root - sx}+{wy + event.y_root - sy}")

    def _show_menu(self, event):
        self.menu.tk_popup(event.x_root, event.y_root)

    def close(self):
        try:
            self.server.stop()
        finally:
            self.destroy()

    def refresh_async(self):
        self.status.config(text="刷新中…")
        threading.Thread(target=self._refresh_worker, daemon=True).start()

    def _refresh_worker(self):
        try:
            self.server.start()
            data = self.server.rate_limits()
            self.after(0, lambda: self._apply(data))
        except FileNotFoundError:
            self.after(0, lambda: self._error(
                "找不到 codex 可执行文件。请安装 Codex CLI；"
                "若安装在非默认位置，可设置环境变量 CODEX_PATH 指向 codex 可执行文件。"
            ))
        except Exception as e:
            self.server.stop()
            self.after(0, lambda: self._error(str(e)))

    def _select_snapshot(self, data):
        by_id = (data or {}).get("rateLimitsByLimitId") or {}
        if "codex" in by_id:
            return "codex", by_id["codex"]
        if by_id:
            key = next(iter(by_id))
            return key, by_id[key]
        return "codex", (data or {}).get("rateLimits") or {}

    def _apply(self, data):
        bucket, snap = self._select_snapshot(data)
        self.current_bucket = bucket
        self.current_snapshot = snap

        primary = snap.get("primary")
        secondary = snap.get("secondary")
        p_used = primary.get("usedPercent") if primary else None
        s_used = secondary.get("usedPercent") if secondary else None
        self.history.add(bucket, p_used, s_used)

        # Prefer the secondary/weekly view; if it is absent, show primary.
        shown = secondary or primary
        shown_kind = "secondary" if secondary else "primary"
        if shown:
            remaining = max(0, min(100, 100 - int(shown.get("usedPercent", 0))))
            self.percent.config(text=f"{remaining}%")
            mins = shown.get("windowDurationMins")
            if mins == 10080:
                title = "Weekly remaining"
            elif mins == 300:
                title = "5-hour remaining"
            elif mins:
                title = f"{mins:g}-min remaining"
            else:
                title = "Remaining"
            self.window_label.config(text=title)
            self.reset.config(text=f"{fmt_reset(shown.get('resetsAt'))} 重置")
            buckets, max_tokens, total_tokens = read_24h_token_activity()
            self._draw_chart(buckets, max_tokens, total_tokens)
        else:
            self.percent.config(text="--%")
            self.window_label.config(text="No quota window")
            self.reset.config(text="--")
            self._draw_chart([], 0, 0)

        details = []
        if shown:
            used_pct = int(shown.get("usedPercent", 0))
            cycle_tokens = read_cycle_tokens(shown.get("resetsAt"), shown.get("windowDurationMins"))
            details.append(f"账号已用: {used_pct}% · 本机周消耗: {fmt_tokens(cycle_tokens)}")

        if primary:
            details.append(self._line_for(primary, "Primary"))
        if secondary:
            details.append(self._line_for(secondary, "Secondary"))

        credits = snap.get("credits")
        if credits:
            if credits.get("unlimited"):
                details.append("Credits: unlimited")
            elif credits.get("hasCredits"):
                details.append(f"Credits: {credits.get('balance') or '--'}")

        plan = snap.get("planType")
        if plan:
            details.append(f"Plan: {plan}")

        self.detail.config(text="  ·  ".join(details) if details else "未返回额度窗口")
        self.status.config(text=f"更新于 {datetime.now().strftime('%H:%M:%S')}")
        self.after(POLL_SECONDS * 1000, self.refresh_async)

    def _line_for(self, win, fallback):
        used = int(win.get("usedPercent", 0))
        rem = max(0, 100 - used)
        mins = win.get("windowDurationMins")
        if mins == 300:
            name = "5h"
        elif mins == 10080:
            name = "Week"
        elif mins:
            name = f"{mins}m"
        else:
            name = fallback
        return f"{name}: {rem}%"

    def _error(self, msg):
        self.percent.config(text="!")
        self.window_label.config(text="无法读取额度")
        self.reset.config(text="")
        self.detail.config(text=msg[:120])
        self.status.config(text="右键可立即刷新")
        self.after(POLL_SECONDS * 1000, self.refresh_async)

    def _draw_chart(self, rows, max_tokens=0, total_tokens=0):
        c = self.canvas
        c.delete("all")
        w, h = 292, 90
        pad_top = 16
        baseline = h - 18
        c.create_line(0, baseline, w, baseline, fill=self.MUTED, width=1)
        c.create_line(0, pad_top, w, pad_top, fill=self.MUTED, width=1, dash=(2, 2))

        if len(rows) < 2:
            c.create_text(
                w / 2, h / 2, text="暂无数据",
                fill=self.MUTED, font=("Segoe UI", 9)
            )
            return

        coords = []
        for i, (ts, tok) in enumerate(rows):
            x = (i / (len(rows) - 1)) * (w - 16) + 8
            ratio = (tok / max_tokens) if max_tokens > 0 else 0.0
            ratio = max(0.0, min(1.0, ratio))
            y = baseline - ratio * (baseline - pad_top)
            coords.extend((x, y))

        is_all_zero = max_tokens <= 0
        c.create_line(*coords, fill=self.LINE, width=2, smooth=not is_all_zero)
        c.create_oval(
            coords[-2] - 3.5, coords[-1] - 3.5,
            coords[-2] + 3.5, coords[-1] + 3.5,
            fill=self.LINE, outline=self.LINE
        )

        peak_text = f"峰值 {fmt_tokens(max_tokens)}/h" if max_tokens > 0 else "暂无请求"
        c.create_text(8, 2, text=peak_text, anchor="nw",
                      fill=self.LINE if max_tokens > 0 else self.MUTED, font=("Segoe UI", 8))

        bottom_text = f"24h 消耗 ({fmt_tokens(total_tokens)})" if total_tokens > 0 else "24h 消耗"
        c.create_text(8, h - 2, text=bottom_text, anchor="sw",
                      fill=self.MUTED, font=("Segoe UI", 8))
        c.create_text(w - 2, h - 2, text="现在", anchor="se",
                      fill=self.FG, font=("Segoe UI", 8))


if __name__ == "__main__":
    Widget().mainloop()
