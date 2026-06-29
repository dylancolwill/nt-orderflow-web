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
