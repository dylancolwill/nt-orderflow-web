// orderflow-web PWA — v1: live candlesticks from the relay's WebSocket snapshots.
// The engine (NinjaTrader WebBridge) does all computation; we just render the latest snapshot.
(function () {
  'use strict';

  var elDot = document.getElementById('dot');
  var elSym = document.getElementById('sym');
  var elStatus = document.getElementById('status');
  var elAge = document.getElementById('age');
  var elChart = document.getElementById('chart');

  // ── Chart ──────────────────────────────────────────────────────────────
  var chart = LightweightCharts.createChart(elChart, {
    layout: { background: { color: '#0e0f12' }, textColor: '#9aa0aa' },
    grid: { vertLines: { color: '#1b1e24' }, horzLines: { color: '#1b1e24' } },
    timeScale: { timeVisible: true, secondsVisible: false, borderColor: '#23262c' },
    rightPriceScale: { borderColor: '#23262c' },
    crosshair: { mode: LightweightCharts.CrosshairMode.Normal },
    autoSize: true,
  });

  var series = chart.addCandlestickSeries({
    // Hollow candles: transparent bodies (outline only) so footprint shows through.
    upColor: 'rgba(0,0,0,0)', downColor: 'rgba(0,0,0,0)',
    borderUpColor: '#26a69a', borderDownColor: '#ef5350',
    wickUpColor: '#26a69a', wickDownColor: '#ef5350',
  });

  // Footprint overlay (bid/ask per price). Auto-hides when zoomed out.
  var footprint = new FootprintPrimitive();
  series.attachPrimitive(footprint);

  var elFpBtn = document.getElementById('fp-btn');
  elFpBtn.addEventListener('click', function () {
    var on = !footprint.isVisible();
    footprint.setVisible(on);
    elFpBtn.classList.toggle('on', on);
  });

  // Daily volume profile: histogram (primitive, behind candles), VAH/VAL level lines, and
  // developing VWAP / VPOC lines that snake across the session.
  var vpProfile = new VpPrimitive();
  series.attachPrimitive(vpProfile);
  var vpLines = null;

  var devVwapSeries = chart.addLineSeries({
    color: '#42a5f5', lineWidth: 2, priceLineVisible: false, lastValueVisible: true,
    crosshairMarkerVisible: false, title: 'VWAP',
  });
  var devPocSeries = chart.addLineSeries({
    color: '#ffd54f', lineWidth: 2, priceLineVisible: false, lastValueVisible: true,
    crosshairMarkerVisible: false, title: 'VPOC',
  });

  function applyVp(vp, tick) {
    vpProfile.setData(vp || null, tick);
    if (!vp) return;
    var LS = LightweightCharts.LineStyle;
    if (!vpLines) {
      vpLines = {
        vah: series.createPriceLine({ price: vp.vah, color: '#66bb6a', lineWidth: 1, lineStyle: LS.Dashed, axisLabelVisible: true, title: 'VAH' }),
        val: series.createPriceLine({ price: vp.val, color: '#66bb6a', lineWidth: 1, lineStyle: LS.Dashed, axisLabelVisible: true, title: 'VAL' }),
      };
    } else {
      vpLines.vah.applyOptions({ price: vp.vah });
      vpLines.val.applyOptions({ price: vp.val });
    }
  }

  function applyDev(dev) {
    if (!dev) { devVwapSeries.setData([]); devPocSeries.setData([]); return; }
    var toLine = function (a) { return (a || []).map(function (p) { return { time: p.t, value: p.v }; }); };
    devVwapSeries.setData(toLine(dev.vwap));
    devPocSeries.setData(toLine(dev.poc));
  }

  var elVpBtn = document.getElementById('vp-btn');
  elVpBtn.addEventListener('click', function () {
    var on = !vpProfile.isVisible();
    vpProfile.setVisible(on);
    if (vpLines) Object.keys(vpLines).forEach(function (k) {
      vpLines[k].applyOptions({ lineVisible: on, axisLabelVisible: on });
    });
    devVwapSeries.applyOptions({ visible: on });
    devPocSeries.applyOptions({ visible: on });
    elVpBtn.classList.toggle('on', on);
  });

  // Layout (fractions of pane height): candles on top, table strip, then a slim CVD strip.
  series.priceScale().applyOptions({ scaleMargins: { top: 0.05, bottom: 0.28 } });
  var CVD_TOP = 0.87;   // CVD occupies bottom ~13%
  var cvdSeries = chart.addCandlestickSeries({
    priceScaleId: 'cvd',
    upColor: '#26a69a', downColor: '#ef5350',
    borderUpColor: '#26a69a', borderDownColor: '#ef5350',
    wickUpColor: '#26a69a', wickDownColor: '#ef5350',
    priceLineVisible: false, lastValueVisible: true,
  });
  chart.priceScale('cvd').applyOptions({ scaleMargins: { top: CVD_TOP, bottom: 0.0 } });

  function applyCvd(arr) {
    cvdSeries.setData(!arr ? [] : arr.map(function (p) {
      return { time: p.t, open: p.o, high: p.h, low: p.l, close: p.c };
    }));
  }

  var elCvdBtn = document.getElementById('cvd-btn');
  elCvdBtn.addEventListener('click', function () {
    var on = elCvdBtn.classList.toggle('on');
    cvdSeries.applyOptions({ visible: on });
  });

  // Per-bar delta table (Vol / Δ / CVD) under the bars — derived from bars + cvd, no exporter change.
  var deltaTable = new TablePrimitive();
  deltaTable.setBottomFraction(CVD_TOP);   // sit just above the CVD strip
  series.attachPrimitive(deltaTable);

  function applyTable(bars, cvd) {
    var cvdMap = {};
    (cvd || []).forEach(function (p) { cvdMap[p.t] = p; });
    deltaTable.setData(bars.map(function (b) {
      var c = cvdMap[b.t];
      return { t: b.t, vol: b.v, delta: c ? (c.c - c.o) : null, cvd: c ? c.c : null };
    }));
  }

  var elTblBtn = document.getElementById('tbl-btn');
  elTblBtn.addEventListener('click', function () {
    var on = elTblBtn.classList.toggle('on');
    deltaTable.setVisible(on);
  });

  // Hand-drawn levels (price lines) + zones/trends (primitive), colour/dash/width from NinjaTrader.
  var drawPrim = new DrawPrimitive();
  series.attachPrimitive(drawPrim);
  var levelLines = [];
  var drawOn = true;
  var lastDraw = null;

  function styleOf(s) {
    var LS = LightweightCharts.LineStyle;
    return s === 'dashed' ? LS.Dashed : s === 'dotted' ? LS.Dotted : LS.Solid;
  }
  function clearLevelLines() { levelLines.forEach(function (l) { series.removePriceLine(l); }); levelLines = []; }

  function applyDraw(draw) {
    if (draw !== undefined) lastDraw = draw;     // remember latest from snapshot
    clearLevelLines();
    if (!drawOn) { drawPrim.setData(null); return; }
    drawPrim.setData(lastDraw || null);
    if (!lastDraw) return;
    (lastDraw.levels || []).forEach(function (L) {
      levelLines.push(series.createPriceLine({
        price: L.price, color: L.color || '#aaa',
        lineWidth: Math.max(1, Math.round(L.width || 1)), lineStyle: styleOf(L.style),
        axisLabelVisible: true, title: L.label || '',
      }));
    });
  }

  var elLvlBtn = document.getElementById('lvl-btn');
  elLvlBtn.addEventListener('click', function () {
    drawOn = elLvlBtn.classList.toggle('on');
    applyDraw();   // re-render with remembered data
  });

  // Accounts popup: connected accounts + realized/unrealized PnL.
  var elAcctBtn = document.getElementById('acct-btn');
  var elAcctModal = document.getElementById('acct-modal');
  var elAcctBody = document.getElementById('acct-body');
  var elAcctClose = document.getElementById('acct-close');
  var latestAccounts = [];

  function fmtMoney(n) {
    return (n < 0 ? '-' : '') + '$' + Math.abs(n).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  }
  function moneyCls(n) { return n > 0 ? 'pos' : n < 0 ? 'neg' : 'dim'; }
  function escapeHtml(s) { return (s || '').replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }

  function renderAccounts() {
    if (!latestAccounts.length) { elAcctBody.innerHTML = '<div class="dim">No accounts.</div>'; return; }
    var rows = latestAccounts.map(function (a) {
      var total = a.realized + a.unrealized;
      return '<tr>'
        + '<td><span class="dot2' + (a.connected ? ' ok' : '') + '"></span>' + escapeHtml(a.name) + '</td>'
        + '<td class="' + moneyCls(a.realized) + '">' + fmtMoney(a.realized) + '</td>'
        + '<td class="' + moneyCls(a.unrealized) + '">' + fmtMoney(a.unrealized) + '</td>'
        + '<td class="' + moneyCls(total) + '">' + fmtMoney(total) + '</td>'
        + '<td class="dim">' + fmtMoney(a.netliq) + '</td>'
        + '</tr>';
    }).join('');
    elAcctBody.innerHTML = '<table class="acct"><thead><tr><th>Account</th><th>Realized</th><th>Unreal.</th>'
      + '<th>Total</th><th>Net Liq</th></tr></thead><tbody>' + rows + '</tbody></table>';
  }

  elAcctBtn.addEventListener('click', function () { elAcctModal.classList.remove('hidden'); renderAccounts(); });
  elAcctClose.addEventListener('click', function () { elAcctModal.classList.add('hidden'); });
  elAcctModal.addEventListener('click', function (e) { if (e.target === elAcctModal) elAcctModal.classList.add('hidden'); });

  var didFit = false;
  var lastTick = null;

  // Set price precision from the instrument tick size so the axis/crosshair show the full price
  // (e.g. 6E ticks at 0.00005 -> 5 decimals) instead of the default 2.
  function applyPriceFormat(tick) {
    if (!tick || tick === lastTick) return;
    lastTick = tick;
    var s = String(tick), prec;
    if (s.indexOf('e') >= 0) prec = Math.max(0, Math.round(-Math.log10(tick)));
    else { var dot = s.indexOf('.'); prec = dot < 0 ? 0 : s.length - dot - 1; }
    series.applyOptions({ priceFormat: { type: 'price', precision: prec, minMove: tick } });
  }

  function applySnapshot(snap) {
    if (!snap || !snap.bars || !snap.bars.length) return;
    if (snap.instrument) elSym.textContent = snap.instrument;
    applyPriceFormat(snap.tickSize);

    var data = new Array(snap.bars.length);
    for (var i = 0; i < snap.bars.length; i++) {
      var b = snap.bars[i];
      data[i] = { time: b.t, open: b.o, high: b.h, low: b.l, close: b.c };
    }
    series.setData(data);
    footprint.setData(snap.footprint || null, snap.tickSize);
    applyVp(snap.vp, snap.tickSize);
    applyDev(snap.dev);
    applyCvd(snap.cvd);
    applyTable(snap.bars, snap.cvd);
    applyDraw(snap.draw || null);
    if (snap.accounts) {
      latestAccounts = snap.accounts;
      if (!elAcctModal.classList.contains('hidden')) renderAccounts();
    }
    if (!didFit) { chart.timeScale().fitContent(); didFit = true; }
    lastUpdate = Date.now();
  }

  // ── Connection status ──────────────────────────────────────────────────
  var lastUpdate = 0;
  function setConnected(ok, msg) {
    elDot.classList.toggle('ok', ok);
    elStatus.textContent = msg;
  }
  setInterval(function () {
    if (!lastUpdate) { elAge.textContent = ''; return; }
    var s = Math.round((Date.now() - lastUpdate) / 1000);
    elAge.textContent = s + 's ago';
  }, 1000);

  // ── WebSocket with auto-reconnect (capped backoff) ───────────────────────
  var ws = null;
  var backoff = 1000;
  function wsUrl() {
    var proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    return proto + '//' + location.host + '/stream';
  }
  function connect() {
    setConnected(false, 'connecting…');
    try { ws = new WebSocket(wsUrl()); } catch (e) { scheduleReconnect(); return; }

    ws.onopen = function () { backoff = 1000; setConnected(true, 'live'); };
    ws.onmessage = function (ev) {
      try { applySnapshot(JSON.parse(ev.data)); } catch (e) { /* ignore bad frame */ }
    };
    ws.onclose = function () { setConnected(false, 'disconnected'); scheduleReconnect(); };
    ws.onerror = function () { try { ws.close(); } catch (e) {} };
  }
  function scheduleReconnect() {
    setTimeout(connect, backoff);
    backoff = Math.min(backoff * 1.7, 15000);
  }
  connect();

  // Reconnect promptly when the tab/app returns to foreground.
  document.addEventListener('visibilitychange', function () {
    if (!document.hidden && (!ws || ws.readyState > 1)) connect();
  });

  // ── PWA service worker (offline app shell) ───────────────────────────────
  if ('serviceWorker' in navigator) {
    navigator.serviceWorker.register('/sw.js').catch(function () {});
  }
})();
