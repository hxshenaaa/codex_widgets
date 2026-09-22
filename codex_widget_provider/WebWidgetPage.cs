namespace CodexUsageWidgetProvider;

/// <summary>
/// Web-widget payload: the adaptive card stub that points the widgets host at
/// our intercepted origin, and the HTML page served from there. The host
/// intercepts every request to https://codexwidget.invalid/* (per
/// WebRequestFilter in the manifest) and routes it to
/// IWidgetResourceProvider.OnResourceRequested in WidgetProvider.cs.
/// </summary>
internal static class WebWidget
{
    public const string Origin = "https://codexwidget.invalid";

    public const string CardStubJson = """
    {
      "type": "AdaptiveCard",
      "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
      "version": "1.6",
      "body": [],
      "metadata": {
        "webUrl": "https://codexwidget.invalid/widget.html"
      }
    }
    """;

    public const string Html = """
    <!DOCTYPE html>
    <html>
    <head>
    <meta charset="utf-8">
    <style>
      :root { color-scheme: light dark; }
      * { margin: 0; padding: 0; box-sizing: border-box; }
      html, body { height: 100%; }
      body {
        font-family: "Segoe UI", system-ui, sans-serif;
        background: transparent;
        color: #222;
        display: flex; flex-direction: column;
        padding: 14px 16px 10px;
        overflow: hidden;
      }
      @media (prefers-color-scheme: dark) { body { color: #eee; } }
      .title { font-size: 12px; font-weight: 600; opacity: .7; margin-bottom: 2px; }
      .row { display: flex; align-items: center; gap: 10px; }
      .percent { font-size: 34px; font-weight: 700; line-height: 1.05; }
      .percent.attention { color: #e2452c; }
      .percent.good { color: #149c58; }
      @media (prefers-color-scheme: dark) {
        .percent.attention { color: #ff7261; }
        .percent.good { color: #43cc7f; }
      }
      .meta { display: flex; flex-direction: column; min-width: 0; }
      .windowLabel { font-size: 11.5px; opacity: .72; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
      .reset { font-size: 11.5px; font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
      .lines { font-size: 11px; opacity: .68; margin-top: 3px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
      .chart { flex: 1; min-height: 54px; margin-top: 6px; color: #333; }
      @media (prefers-color-scheme: dark) { .chart { color: #ddd; } }
      .chart svg { width: 100%; height: 100%; display: block; }
      .chart .placeholder { height: 100%; display: flex; align-items: center; justify-content: center; font-size: 11px; opacity: .55; }
      .updated { font-size: 10px; opacity: .55; text-align: right; margin-top: 2px; }
      .sessions { margin-top: 8px; border-top: 1px solid rgba(140, 140, 140, 0.2); padding-top: 6px; display: none; }
      @media (min-height: 250px) { .sessions { display: block; } }
      .sessions-hdr { font-size: 11px; font-weight: 600; opacity: .65; margin-bottom: 4px; }
      .session-row { display: flex; justify-content: space-between; align-items: baseline; margin-bottom: 4px; }
      .session-left { min-width: 0; flex: 1; margin-right: 8px; }
      .session-title { font-size: 11px; font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
      .session-detail { font-size: 10px; opacity: .65; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
      .session-right { text-align: right; white-space: nowrap; }
      .session-tok { font-size: 10px; font-weight: 600; color: #149c58; }
      @media (prefers-color-scheme: dark) { .session-tok { color: #43cc7f; } }
      .session-time { font-size: 9.5px; opacity: .55; }
      @media (max-width: 200px) { .lines, .chart, .updated, .sessions { display: none; } .percent { font-size: 30px; } }
    </style>
    </head>
    <body>
      <div class="title">Codex</div>
      <div class="row">
        <div class="percent" id="percent">--%</div>
        <div class="meta">
          <div class="windowLabel" id="windowLabel">loading…</div>
          <div class="reset" id="reset"></div>
        </div>
      </div>
      <div class="lines" id="lines"></div>
      <div class="chart" id="chart"></div>
      <div class="sessions" id="sessions">
        <div class="sessions-hdr">最近任务记录</div>
        <div id="sessionsList"></div>
      </div>
      <div class="updated" id="updated"></div>
    <script>
    function smoothPath(pts) {
      if (!pts || pts.length === 0) return '';
      if (pts.length === 1) return 'M ' + pts[0][0].toFixed(1) + ' ' + pts[0][1].toFixed(1);
      if (pts.length === 2) {
        return 'M ' + pts[0][0].toFixed(1) + ' ' + pts[0][1].toFixed(1) + ' L ' + pts[1][0].toFixed(1) + ' ' + pts[1][1].toFixed(1);
      }
      var n = pts.length;
      var dxs = [], dys = [], ms = [];
      for (var i = 0; i < n - 1; i++) {
        var dx = pts[i + 1][0] - pts[i][0];
        var dy = pts[i + 1][1] - pts[i][1];
        dxs.push(dx);
        dys.push(dy);
        ms.push(dx === 0 ? 0 : dy / dx);
      }
      var tangents = [ms[0]];
      for (var i = 1; i < n - 1; i++) {
        var m0 = ms[i - 1], m1 = ms[i];
        if (m0 * m1 <= 0) {
          tangents.push(0);
        } else {
          tangents.push((m0 + m1) / 2);
        }
      }
      tangents.push(ms[n - 2]);

      var d = 'M ' + pts[0][0].toFixed(1) + ' ' + pts[0][1].toFixed(1);
      for (var i = 0; i < n - 1; i++) {
        var x0 = pts[i][0], y0 = pts[i][1];
        var x1 = pts[i + 1][0], y1 = pts[i + 1][1];
        var dx = dxs[i] / 3;
        var c1x = x0 + dx;
        var c1y = y0 + tangents[i] * dx;
        var c2x = x1 - dx;
        var c2y = y1 - tangents[i + 1] * dx;
        var minY = Math.min(y0, y1), maxY = Math.max(y0, y1);
        c1y = Math.max(minY, Math.min(maxY, c1y));
        c2y = Math.max(minY, Math.min(maxY, c2y));
        d += ' C ' + c1x.toFixed(1) + ' ' + c1y.toFixed(1) + ' ' + c2x.toFixed(1) + ' ' + c2y.toFixed(1) + ' ' + x1.toFixed(1) + ' ' + y1.toFixed(1);
      }
      return d;
    }

    function renderChart(points, accent, caption) {
      var el = document.getElementById('chart');
      if (!points || points.length < 2) {
        el.innerHTML = '<div class="placeholder">采样中（需至少2个采样点）…</div>';
        return;
      }
      var w = 600, h = 180, padX = 10, padTop = 14, baseY = h - 24;
      function yOf(v) {
        var clamped = Math.max(0, Math.min(100, v));
        return baseY - (clamped / 100.0) * (baseY - padTop);
      }

      points.sort(function (a, b) { return a[0] - b[0]; });
      var t0 = points[0][0], t1 = points[points.length - 1][0];
      var dt = t1 - t0;
      var xs = points.map(function (p) {
        var x = dt <= 0 ? w - padX : padX + (p[0] - t0) / dt * (w - 2 * padX);
        return [x, yOf(p[1])];
      });

      var filtered = [xs[0]];
      for (var k = 1; k < xs.length; k++) {
        if (Math.abs(xs[k][0] - filtered[filtered.length - 1][0]) >= 1.0 || k === xs.length - 1) {
          filtered.push(xs[k]);
        }
      }
      xs = filtered;

      var line = smoothPath(xs);
      var first = xs[0], last = xs[xs.length - 1];
      var area = line + ' L ' + last[0].toFixed(1) + ' ' + baseY + ' L ' + first[0].toFixed(1) + ' ' + baseY + ' Z';
      var capText = caption || '';

      el.innerHTML =
        '<svg viewBox="0 0 ' + w + ' ' + h + '" preserveAspectRatio="none">' +
          '<defs>' +
            '<linearGradient id="fill" x1="0" y1="0" x2="0" y2="1">' +
              '<stop offset="0" stop-color="' + accent + '" stop-opacity="0.32"/>' +
              '<stop offset="1" stop-color="' + accent + '" stop-opacity="0.03"/>' +
            '</linearGradient>' +
          '</defs>' +
          '<line x1="0" y1="' + baseY + '" x2="' + w + '" y2="' + baseY + '" stroke="currentColor" stroke-opacity="0.22" stroke-width="1"/>' +
          '<line x1="0" y1="' + padTop + '" x2="' + w + '" y2="' + padTop + '" stroke="currentColor" stroke-opacity="0.10" stroke-width="1" stroke-dasharray="4 4"/>' +
          '<path d="' + area + '" fill="url(#fill)"/>' +
          '<path d="' + line + '" fill="none" stroke="' + accent + '" stroke-width="2.6" stroke-linejoin="round" stroke-linecap="round"/>' +
          '<circle cx="' + last[0].toFixed(1) + '" cy="' + last[1].toFixed(1) + '" r="4.5" fill="' + accent + '"/>' +
          (capText ? '<text x="' + padX + '" y="' + (h - 6) + '" font-size="11" fill="currentColor" opacity="0.55">' + capText + '</text>' : '') +
          '<text x="' + (w - padX) + '" y="' + (h - 6) + '" font-size="11" text-anchor="end" fill="currentColor" opacity="0.55">现在</text>' +
        '</svg>';
    }

    function accentFor(color) {
      if (color === 'Attention') return '#e2452c';
      if (color === 'Good') return '#149c58';
      return matchMedia('(prefers-color-scheme: dark)').matches ? '#54aef0' : '#1185d8';
    }

    function apply(d) {
      var pc = document.getElementById('percent');
      pc.textContent = d.percent + '%';
      pc.className = 'percent' + (d.color === 'Attention' ? ' attention' : d.color === 'Good' ? ' good' : '');
      document.getElementById('windowLabel').textContent = d.windowLabel;
      document.getElementById('reset').textContent = d.resetText;
      document.getElementById('lines').textContent = [d.primaryLine, d.secondaryLine, d.planLine].filter(function (x) { return x; }).join('   ');
      document.getElementById('updated').textContent = d.updatedLine;
      renderChart(d.points, accentFor(d.color), d.trendCaption);
      if (d.recentSessions && d.recentSessions.length > 0) {
        var sHtml = '';
        for (var i = 0; i < d.recentSessions.length; i++) {
          var s = d.recentSessions[i];
          sHtml += '<div class="session-row">' +
            '<div class="session-left">' +
              '<div class="session-title">' + (s.title || '') + '</div>' +
              (s.detail ? '<div class="session-detail">' + s.detail + '</div>' : '') +
            '</div>' +
            '<div class="session-right">' +
              (s.tokens ? '<div class="session-tok">' + s.tokens + '</div>' : '') +
              '<div class="session-time">' + (s.time || '') + '</div>' +
            '</div>' +
          '</div>';
        }
        document.getElementById('sessionsList').innerHTML = sHtml;
      }
    }

    function poll() {
      fetch('data.json', { cache: 'no-store' })
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (d) { if (d) apply(d); })
        .catch(function () { /* keep last state */ });
    }

    poll();
    setInterval(poll, 60000);
    </script>
    </body>
    </html>
    """;
}
