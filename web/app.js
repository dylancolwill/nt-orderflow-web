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
    upColor: '#26a69a', downColor: '#ef5350',
    borderUpColor: '#26a69a', borderDownColor: '#ef5350',
    wickUpColor: '#26a69a', wickDownColor: '#ef5350',
  });

  var didFit = false;

  function applySnapshot(snap) {
    if (!snap || !snap.bars || !snap.bars.length) return;
    if (snap.instrument) elSym.textContent = snap.instrument;

    var data = new Array(snap.bars.length);
    for (var i = 0; i < snap.bars.length; i++) {
      var b = snap.bars[i];
      data[i] = { time: b.t, open: b.o, high: b.h, low: b.l, close: b.c };
    }
    series.setData(data);
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
