#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// WebBridge — exports a light JSON snapshot of the chart to the orderflow-web relay every N seconds.
// v1 payload: instrument + recent OHLCV bars. The snapshot envelope is versioned so footprint /
// volume profile / delta / drawn-levels can be added later without breaking older web clients.
//
// Modeled on Dylan/FlowAnalysis.cs (timer-driven push, cache-on-NT-thread / send-on-timer-thread).
// The web app does the rendering; this indicator does the data work locally and posts small payloads.
namespace NinjaTrader.NinjaScript.Indicators.Dylan
{
    public class WebBridge : Indicator
    {
        public override string ToString() { return Name; }

        #region Fields

        private struct BarData
        {
            public long   T;   // unix seconds, UTC
            public double O, H, L, C;
            public long   V;
        }

        // Bar cache — written on the NT thread (OnBarUpdate), read on the timer thread under _lock.
        private readonly object         _lock = new object();
        private readonly List<BarData>  _bars = new List<BarData>();

        // Footprint cache — per bar (keyed by unix-ts) a price -> [bid, ask] map. Same _lock.
        // Derived from ticks (Last classified vs current bid/ask), like FlowAnalysis — no Volumetric
        // bar type required. Historical footprint needs Tick Replay enabled on the chart.
        private readonly Dictionary<long, Dictionary<double, long[]>> _fp =
            new Dictionary<long, Dictionary<double, long[]>>();

        // Daily (current-session) volume profile — price -> total volume. Reset each session. Same _lock.
        private readonly Dictionary<double, long> _vp = new Dictionary<double, long>();
        private long   _vpTotal;
        private double _vwapNum, _vwapDen;
        private const double ValueAreaPct = 0.70;

        // Developing VWAP / VPOC — value per bar-ts as the session built up. Reset each session.
        private readonly Dictionary<long, double> _devVwap = new Dictionary<long, double>();
        private readonly Dictionary<long, double> _devPoc  = new Dictionary<long, double>();

        // Cumulative volume delta (session). _cvd runs; _cvdBar holds per bar-ts OHLC of the running
        // CVD so the web can draw delta candles. [0]=open [1]=high [2]=low [3]=close.
        private long _cvd;
        private readonly Dictionary<long, double[]> _cvdBar = new Dictionary<long, double[]>();

        // Hand-drawn levels/zones/trendlines, scanned from the chart's DrawObjects. Prebuilt JSON.
        private string   _drawJson = "";
        private DateTime _lastLevelScan = DateTime.MinValue;

        // Connected accounts + realized/unrealized PnL. Prebuilt JSON, refreshed with the level scan.
        private string _accountsJson = "";

        // Historical-footprint diagnostics (EnableDebug)
        private long _dbgHistCalls, _dbgHistTicks, _dbgHistBuy, _dbgHistSell, _dbgHistSkip, _dbgHistZeroQuote;
        private int  _dbgHistSamples;
        private bool _dbgSummaryPrinted;

        private HttpClient _http;
        private Timer      _timer;
        private volatile bool _stopping;

        private string _instrumentName = "";
        private double _tickSize = 0.25;
        private long   _lastSentTs = -1;   // throttle: skip POST if nothing changed

        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "WebBridge — streams light JSON chart snapshots to the orderflow-web relay.";
                Name                     = "WebBridge";
                Calculate                = Calculate.OnEachTick;
                IsOverlay                = true;
                IsChartOnly              = true;
                DisplayInDataBox         = false;
                DrawOnPricePanel         = false;
                IsAutoScale              = false;
                IsSuspendedWhileInactive = false;
                BarsRequiredToPlot       = 0;

                RelayUrl              = "http://192.168.1.100:8000";
                AuthToken             = "change-me";
                UpdateIntervalSeconds = 2;
                BarsToSend            = 120;
                FootprintBars         = 40;
                EnableDebug           = true;
            }
            else if (State == State.Configure)
            {
                // 1-tick series (BarsInProgress == 1) — carries historical bid/ask per tick, which
                // GetCurrentAsk/Bid do NOT. Same approach as VolumeDeltaTable/CurrentCumulativeDelta.
                AddDataSeries(BarsPeriodType.Tick, 1);
            }
            else if (State == State.DataLoaded)
            {
                _tickSize       = Instrument.MasterInstrument.TickSize;
                _instrumentName = Instrument.FullName;
            }
            else if (State == State.Realtime)
            {
                _stopping = false;
                _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                _timer = new Timer(OnTimer, null,
                    UpdateIntervalSeconds * 1000, UpdateIntervalSeconds * 1000);
                if (EnableDebug) Print("WebBridge: started — posting to " + RelayUrl + "/ingest every " + UpdateIntervalSeconds + "s");
                PrintHistoricalSummary();
            }
            else if (State == State.Terminated)
            {
                _stopping = true;
                _timer?.Dispose();
                _timer = null;
                try { _http?.Dispose(); } catch { }
                _http = null;
            }
        }

        // ─── Bar cache (NT thread) ───────────────────────────────────────────────

        protected override void OnBarUpdate()
        {
            // ── primary chart series — build candles ──────────────────────────
            if (BarsInProgress == 0)
            {
                if (CurrentBar < 0) return;
                if (EnableDebug && State == State.Historical) _dbgHistCalls++;

                // New trading day/session — reset the daily volume profile + VWAP + developing lines.
                if (IsFirstTickOfBar && Bars.IsFirstBarOfSession)
                    lock (_lock) { _vp.Clear(); _vpTotal = 0; _vwapNum = 0; _vwapDen = 0; _devVwap.Clear(); _devPoc.Clear(); _cvd = 0; _cvdBar.Clear(); }

                // With Tick Replay/OnEachTick, OnBarUpdate fires per tick, so sampling the first
                // tick gives O=H=L=C. Record the PREVIOUS bar [1] from its final OHLC when a new bar
                // starts; always keep the forming bar [0] up to date.
                if (IsFirstTickOfBar && CurrentBar >= 1)
                {
                    UpsertBar(Time[1], Open[1], High[1], Low[1], Close[1], (long)Volume[1]);
                    lock (_lock) StoreDevLocked(ToUnixUtc(Time[1]));   // snapshot developing VWAP/POC at bar close
                }

                UpsertBar(Time[0], Open[0], High[0], Low[0], Close[0], (long)Volume[0]);

                if (IsFirstTickOfBar) TrimFootprint();

                // Re-scan hand-drawn levels at most ~1/s so newly drawn lines show up promptly.
                if ((DateTime.Now - _lastLevelScan).TotalSeconds >= 1)
                {
                    _lastLevelScan = DateTime.Now;
                    ScanDrawings();
                    ScanAccounts();
                }
            }
            // ── 1-tick series — accumulate footprint ──────────────────────────
            else if (BarsInProgress == 1)
            {
                AccumulateFootprintTick();
            }
        }

        // ─── Hand-drawn levels (NT data thread) ──────────────────────────────────
        // Scan the chart's DrawObjects into prebuilt JSON, preserving colour/dash/width so the web
        // renders them identically. Horizontal lines + flat rays/lines -> levels; rectangles ->
        // zones; sloped rays/lines -> trend segments. Mirrors FlowAnalysis.ScanLevels.
        private void ScanDrawings()
        {
            if (ChartControl == null) return;

            var lv = new StringBuilder(); bool fl = true;
            var zn = new StringBuilder(); bool fz = true;
            var tr = new StringBuilder(); bool ft = true;

            try
            {
            foreach (DrawingTool dt in DrawObjects)
            {
                try
                {
                    if (!dt.IsVisible) continue;
                    string tag = dt.Tag ?? "";
                    // Labeled lines/rays carry the user's typed text — prefer it over the drawing id.
                    if (dt is LabeledLine ll && !string.IsNullOrEmpty(ll.DisplayText)) tag = ll.DisplayText;

                    if (dt is HorizontalLine h)
                    {
                        AppendLevel(lv, ref fl, h.Anchors.First().Price, h.Stroke, tag);
                    }
                    else if (dt is Ray ray && ray.Anchors.Count() >= 2)
                    {
                        var a0 = ray.Anchors.ElementAt(0); var a1 = ray.Anchors.ElementAt(1);
                        if (Math.Abs(a0.Price - a1.Price) < _tickSize * 0.5) AppendLevel(lv, ref fl, a0.Price, ray.Stroke, tag);
                        else AppendTrend(tr, ref ft, a0, a1, ray.Stroke, tag);
                    }
                    else if (dt is Line ln && ln.Anchors.Count() >= 2)
                    {
                        var a0 = ln.Anchors.ElementAt(0); var a1 = ln.Anchors.ElementAt(1);
                        if (Math.Abs(a0.Price - a1.Price) < _tickSize * 0.5) AppendLevel(lv, ref fl, a0.Price, ln.Stroke, tag);
                        else AppendTrend(tr, ref ft, a0, a1, ln.Stroke, tag);
                    }
                    else if (dt is ExtendedRectangle exr)
                    {
                        AppendZone(zn, ref fz, exr.StartAnchor.Price, exr.EndAnchor.Price, exr.AreaBrush, exr.AreaOpacity, tag);
                    }
                    else if (dt is Rectangle rect && rect.Anchors.Count() >= 2)
                    {
                        AppendZone(zn, ref fz, rect.Anchors.ElementAt(0).Price, rect.Anchors.ElementAt(1).Price, rect.AreaBrush, rect.AreaOpacity, tag);
                    }
                }
                catch { }
            }
            }
            catch { return; } // DrawObjects modified mid-scan — keep last good _drawJson

            string j = "\"draw\":{\"levels\":[" + lv + "],\"zones\":[" + zn + "],\"trends\":[" + tr + "]}";
            lock (_lock) { _drawJson = j; }
        }

        // ─── Accounts / PnL (NT data thread) ─────────────────────────────────────
        private void ScanAccounts()
        {
            var sb = new StringBuilder();
            bool first = true;
            try
            {
                lock (Account.All)
                {
                    foreach (Account acc in Account.All)
                    {
                        try
                        {
                            bool connected = acc.Connection != null && acc.Connection.Status == ConnectionStatus.Connected;
                            double realized   = acc.Get(AccountItem.RealizedProfitLoss, acc.Denomination);
                            double netliq     = acc.Get(AccountItem.NetLiquidation,     acc.Denomination);
                            double unrealized = 0;
                            lock (acc.Positions)
                                foreach (Position p in acc.Positions)
                                    unrealized += p.GetUnrealizedProfitLoss(PerformanceUnit.Currency);

                            if (!first) sb.Append(','); first = false;
                            sb.Append("{\"name\":"); AppendJsonString(sb, acc.Name);
                            sb.Append(",\"connected\":").Append(connected ? "true" : "false");
                            sb.Append(",\"realized\":").Append(realized.ToString(CultureInfo.InvariantCulture));
                            sb.Append(",\"unrealized\":").Append(unrealized.ToString(CultureInfo.InvariantCulture));
                            sb.Append(",\"netliq\":").Append(netliq.ToString(CultureInfo.InvariantCulture));
                            sb.Append('}');
                        }
                        catch { }
                    }
                }
            }
            catch { return; }

            lock (_lock) { _accountsJson = "\"accounts\":[" + sb + "]"; }
        }

        private void AppendLevel(StringBuilder sb, ref bool first, double price, Stroke stroke, string tag)
        {
            if (!first) sb.Append(','); first = false;
            sb.Append("{\"price\":").Append(price.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"color\":"); AppendJsonString(sb, ColorCss(stroke?.Brush));
            sb.Append(",\"style\":\"").Append(DashCss(stroke)).Append('"');
            sb.Append(",\"width\":").Append(((stroke != null ? stroke.Width : 1)).ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"label\":"); AppendJsonString(sb, tag);
            sb.Append('}');
        }

        private void AppendTrend(StringBuilder sb, ref bool first, ChartAnchor a0, ChartAnchor a1, Stroke stroke, string tag)
        {
            if (!first) sb.Append(','); first = false;
            sb.Append("{\"t1\":").Append(ToUnixUtc(a0.Time)).Append(",\"p1\":").Append(a0.Price.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"t2\":").Append(ToUnixUtc(a1.Time)).Append(",\"p2\":").Append(a1.Price.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"color\":"); AppendJsonString(sb, ColorCss(stroke?.Brush));
            sb.Append(",\"style\":\"").Append(DashCss(stroke)).Append('"');
            sb.Append(",\"width\":").Append(((stroke != null ? stroke.Width : 1)).ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"label\":"); AppendJsonString(sb, tag);
            sb.Append('}');
        }

        private void AppendZone(StringBuilder sb, ref bool first, double pa, double pb, Brush areaBrush, int areaOpacity, string tag)
        {
            double p1 = Math.Max(pa, pb), p2 = Math.Min(pa, pb);
            if (!first) sb.Append(','); first = false;
            sb.Append("{\"p1\":").Append(p1.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"p2\":").Append(p2.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"color\":"); AppendJsonString(sb, ColorCss(areaBrush, areaOpacity / 100.0));
            sb.Append(",\"label\":"); AppendJsonString(sb, tag);
            sb.Append('}');
        }

        private static string ColorCss(Brush b, double extraOpacity = 1.0)
        {
            var s = b as SolidColorBrush;
            if (s == null) return "rgba(200,200,200,0.6)";
            var c = s.Color;
            double a = (c.A / 255.0) * s.Opacity * extraOpacity;
            return string.Format(CultureInfo.InvariantCulture, "rgba({0},{1},{2},{3:0.###})", c.R, c.G, c.B, a);
        }

        private static string DashCss(Stroke st)
        {
            if (st == null) return "solid";
            switch (st.DashStyleHelper)
            {
                case DashStyleHelper.Dash:       return "dashed";
                case DashStyleHelper.Dot:        return "dotted";
                case DashStyleHelper.DashDot:
                case DashStyleHelper.DashDotDot: return "dashed";
                default:                         return "solid";
            }
        }

        // ─── Footprint from the 1-tick series (NT data thread) ───────────────────
        // Classify each trade tick at its bid/ask from BarsArray[1].GetAsk/GetBid(barIndex), which
        // (unlike GetCurrentAsk/Bid) carry the historical quote. Keyed to the primary bar's time so
        // it lines up with the candles. Mirrors VolumeDeltaTable's AccumulateTick.
        private void AccumulateFootprintTick()
        {
            if (CurrentBars[1] < 0 || CurrentBars[0] < 0) return;

            int    bar   = CurrentBars[1];
            double price = BarsArray[1].GetClose(bar);
            long   vol   = (long)BarsArray[1].GetVolume(bar);
            double ask   = BarsArray[1].GetAsk(bar);
            double bid   = BarsArray[1].GetBid(bar);
            if (vol <= 0) return;

            double rpAll = Instrument.MasterInstrument.RoundToTickSize(price);

            // Daily volume profile + VWAP — count all volume (even ticks between quotes).
            lock (_lock)
            {
                long cur; _vp.TryGetValue(rpAll, out cur); _vp[rpAll] = cur + vol;
                _vpTotal += vol;
                _vwapNum += price * vol;
                _vwapDen += vol;
            }

            bool buy  = ask > 0 && price >= ask;
            bool sell = bid > 0 && price <= bid;

            bool hist = EnableDebug && State == State.Historical;
            if (hist)
            {
                _dbgHistTicks++;
                if (ask <= 0 || bid <= 0) _dbgHistZeroQuote++;
                if (_dbgHistSamples < 8)
                {
                    _dbgHistSamples++;
                    Print(string.Format("WebBridge [hist sample] px={0} ask={1} bid={2} vol={3}",
                          price, ask, bid, vol));
                }
            }

            if (buy == sell) { if (hist) _dbgHistSkip++; return; } // between quotes / locked — skip
            if (hist) { if (buy) _dbgHistBuy++; else _dbgHistSell++; }

            long ts = ToUnixUtc(Times[0][0]);                      // primary bar this tick belongs to
            lock (_lock)
            {
                // Cumulative delta (runs regardless of the footprint toggle) as per-bar OHLC.
                double before = _cvd;
                _cvd += buy ? vol : -vol;
                double[] ohlc;
                if (!_cvdBar.TryGetValue(ts, out ohlc))
                    _cvdBar[ts] = new double[] { before, Math.Max(before, _cvd), Math.Min(before, _cvd), _cvd };
                else
                {
                    if (_cvd > ohlc[1]) ohlc[1] = _cvd;
                    if (_cvd < ohlc[2]) ohlc[2] = _cvd;
                    ohlc[3] = _cvd;
                }

                // Per-bar footprint cells (only when enabled).
                if (FootprintBars > 0)
                {
                    Dictionary<double, long[]> rows;
                    if (!_fp.TryGetValue(ts, out rows)) { rows = new Dictionary<double, long[]>(); _fp[ts] = rows; }
                    long[] ba;
                    if (!rows.TryGetValue(rpAll, out ba)) { ba = new long[2]; rows[rpAll] = ba; }
                    if (buy) ba[1] += vol; else ba[0] += vol;      // [0]=bid (sell), [1]=ask (buy)
                }
            }
        }

        // One-shot diagnostic at the Historical->Realtime boundary: did Tick Replay feed us ticks,
        // and did GetCurrentAsk/Bid classify them? Reveals why historical footprint is empty.
        private void PrintHistoricalSummary()
        {
            if (!EnableDebug || _dbgSummaryPrinted) return;
            _dbgSummaryPrinted = true;

            bool tickReplay = false;
            try { tickReplay = Bars != null && Bars.IsTickReplay; } catch { }

            int fpBars;
            lock (_lock) { fpBars = _fp.Count; }

            Print("WebBridge [HIST SUMMARY] ----------------------------------------");
            Print(string.Format("  primary IsTickReplay = {0}", tickReplay));
            Print(string.Format("  primary historical OnBarUpdate calls = {0}", _dbgHistCalls));
            Print(string.Format("  footprint ticks seen (tick series) = {0}  (buy={1} sell={2} skipped-between={3} zero-quote={4})",
                  _dbgHistTicks, _dbgHistBuy, _dbgHistSell, _dbgHistSkip, _dbgHistZeroQuote));
            Print(string.Format("  footprint bars built = {0}", fpBars));
            Print("  Expect: ticks seen >> 0, buy+sell >> 0, footprint bars > 1.");
            Print("------------------------------------------------------------------");
        }

        // Keep only the most recent FootprintBars bars of footprint (caller may or may not hold _lock).
        private void TrimFootprint()
        {
            lock (_lock)
            {
                if (_fp.Count <= FootprintBars) return;
                var keys = new List<long>(_fp.Keys);
                keys.Sort();
                for (int i = 0; i < keys.Count - FootprintBars; i++)
                    _fp.Remove(keys[i]);
            }
        }

        // Insert-or-update a bar keyed by timestamp. The forming bar [0] is always the last entry
        // (O(1) update); the just-completed bar [1] is found near the end.
        private void UpsertBar(DateTime time, double o, double h, double l, double c, long v)
        {
            long     t  = ToUnixUtc(time);
            BarData  bd = new BarData { T = t, O = o, H = h, L = l, C = c, V = v };

            lock (_lock)
            {
                int n = _bars.Count;
                if (n > 0 && _bars[n - 1].T == t) { _bars[n - 1] = bd; return; }   // update forming/last
                if (n > 0 && _bars[n - 1].T >  t)                                   // update an earlier bar
                {
                    for (int i = n - 1; i >= 0 && i >= n - 4; i--)
                        if (_bars[i].T == t) { _bars[i] = bd; return; }
                    return; // too old to matter
                }
                _bars.Add(bd);                                                      // new bar
                int trim = _bars.Count - BarsToSend;
                if (trim > 0) _bars.RemoveRange(0, trim);
            }
        }

        private static long ToUnixUtc(DateTime dt)
        {
            // NT bar times carry the configured (local/exchange) zone. Normalize to UTC seconds,
            // which is what Lightweight Charts expects for intraday timestamps.
            DateTime utc = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
            return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        // ─── Push (timer thread) ─────────────────────────────────────────────────

        private void OnTimer(object state)
        {
            if (_stopping || _http == null) return;

            string payload;
            long   newestTs;
            lock (_lock)
            {
                if (_bars.Count == 0) return;
                newestTs = _bars[_bars.Count - 1].T;
                payload  = BuildSnapshot();
            }

            // Always send the latest (the forming bar mutates within the same timestamp),
            // but cheaply note it for debugging.
            _lastSentTs = newestTs;

            try
            {
                using (var content = new StringContent(payload, Encoding.UTF8, "application/json"))
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, RelayUrl.TrimEnd('/') + "/ingest")
                    {
                        Content = content
                    };
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + AuthToken);

                    var resp = _http.SendAsync(req).GetAwaiter().GetResult();
                    string respBody = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (EnableDebug)
                        Print(string.Format("WebBridge [post] {0} bytes -> {1}",
                              payload.Length, (int)resp.StatusCode));
                    resp.Dispose();
                    HandleCommandResponse(respBody);
                }
            }
            catch (Exception ex)
            {
                if (EnableDebug) Print("WebBridge [post error] " + ex.Message);
            }
        }

        // ─── Reverse channel: commands from the web (NT timer thread) ─────────────
        // The relay returns pending commands in the /ingest response, e.g. {"setInstrument":"NQ 09-26"}.
        private void HandleCommandResponse(string body)
        {
            if (string.IsNullOrEmpty(body)) return;
            const string key = "\"setInstrument\":\"";
            int i = body.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return;
            i += key.Length;
            int j = body.IndexOf('"', i);
            if (j < 0) return;
            string name = body.Substring(i, j - i);
            if (!string.IsNullOrEmpty(name)) SwitchInstrument(name);
        }

        // Resolve a user-typed value to an instrument. Accepts full names ("ES 09-26"), stocks/forex,
        // or a bare futures root ("es", "6e", "nq") which is mapped to the current chart's contract month.
        private NinjaTrader.Cbi.Instrument ResolveInstrument(string input)
        {
            string s = (input ?? "").Trim().ToUpperInvariant();
            if (s.Length == 0) return null;

            NinjaTrader.Cbi.Instrument inst = null;
            try { inst = NinjaTrader.Cbi.Instrument.GetInstrument(s); } catch { }
            if (inst != null) return inst;

            // Bare futures root: reuse the current chart's expiry (e.g. "NQ" -> "NQ 09-26").
            if (!s.Contains(" ") && Instrument != null && Instrument.Expiry > new DateTime(1900, 1, 1))
            {
                string exp = Instrument.Expiry.ToString("MM-yy", CultureInfo.InvariantCulture);
                try { inst = NinjaTrader.Cbi.Instrument.GetInstrument(s + " " + exp); } catch { }
            }
            return inst;
        }

        private void SwitchInstrument(string name)
        {
            try
            {
                NinjaTrader.Cbi.Instrument inst = ResolveInstrument(name);
                if (inst == null) { Print("WebBridge: unknown instrument '" + name + "'"); return; }

                var cc = ChartControl;
                if (cc == null) { Print("WebBridge: no ChartControl"); return; }

                bool ok = false;
                string uiError = null;
                cc.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        object win = Window.GetWindow(cc);
                        ok = TrySetChartInstrument(win, cc, inst);
                    }
                    catch (Exception ex) { uiError = ex.Message; }
                });
                if (uiError != null) Print("WebBridge switch(ui) error: " + uiError);
                else Print("WebBridge: switch -> " + name + (ok ? " OK" : " (no Instrument setter found — report this)"));
            }
            catch (Exception ex) { Print("WebBridge switch error: " + ex.Message); }
        }

        // Find this chart's ChartTab in the chart window and set its Instrument (via reflection, so it
        // compiles regardless of the exact API). Falls back to a window-level Instrument setter.
        private bool TrySetChartInstrument(object chartWindow, object chartControl, NinjaTrader.Cbi.Instrument inst)
        {
            if (chartWindow == null) return false;

            var mtcProp = chartWindow.GetType().GetProperty("MainTabControl");
            var mtc = mtcProp != null ? mtcProp.GetValue(chartWindow) as TabControl : null;
            if (mtc != null)
            {
                foreach (object item in mtc.Items)
                {
                    object content = (item as TabItem) != null ? ((TabItem)item).Content : item;
                    if (content == null) continue;
                    var ccProp = content.GetType().GetProperty("ChartControl");
                    object cc = ccProp != null ? ccProp.GetValue(content) : null;
                    if (!ReferenceEquals(cc, chartControl)) continue;
                    var instProp = content.GetType().GetProperty("Instrument");
                    if (instProp != null && instProp.CanWrite) { instProp.SetValue(content, inst); return true; }
                }
            }

            var winInst = chartWindow.GetType().GetProperty("Instrument");
            if (winInst != null && winInst.CanWrite) { winInst.SetValue(chartWindow, inst); return true; }
            return false;
        }

        // Build the snapshot under _lock (caller holds it). Manual JSON to avoid serializer deps.
        private string BuildSnapshot()
        {
            var sb = new StringBuilder(256 + _bars.Count * 80);
            sb.Append("{\"type\":\"snapshot\",\"v\":1,\"instrument\":");
            AppendJsonString(sb, _instrumentName);
            sb.Append(",\"tickSize\":").Append(_tickSize.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"ts\":\"").Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append('"');
            sb.Append(",\"bars\":[");
            for (int i = 0; i < _bars.Count; i++)
            {
                BarData b = _bars[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"t\":").Append(b.T);
                sb.Append(",\"o\":").Append(b.O.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"h\":").Append(b.H.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"l\":").Append(b.L.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"c\":").Append(b.C.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"v\":").Append(b.V).Append('}');
            }
            sb.Append(']');

            // footprint: { "<barTs>": { "<price>": {"b":bid,"a":ask}, ... }, ... }
            if (_fp.Count > 0)
            {
                sb.Append(",\"footprint\":{");
                bool firstBar = true;
                foreach (var bar in _fp)
                {
                    if (!firstBar) sb.Append(',');
                    firstBar = false;
                    sb.Append('"').Append(bar.Key).Append("\":{");
                    bool firstRow = true;
                    foreach (var row in bar.Value)
                    {
                        if (!firstRow) sb.Append(',');
                        firstRow = false;
                        sb.Append('"').Append(row.Key.ToString(CultureInfo.InvariantCulture)).Append("\":{");
                        sb.Append("\"b\":").Append(row.Value[0]);
                        sb.Append(",\"a\":").Append(row.Value[1]).Append('}');
                    }
                    sb.Append('}');
                }
                sb.Append('}');
            }

            // vp (daily/current-session volume profile): { poc, vah, val, vwap, rows: { "<price>": vol } }
            if (_vpTotal > 0)
            {
                double poc, vah, val;
                ComputeVp(out poc, out vah, out val);
                double vwap = _vwapDen > 0 ? _vwapNum / _vwapDen : 0;

                sb.Append(",\"vp\":{");
                sb.Append("\"poc\":").Append(poc.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"vah\":").Append(vah.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"val\":").Append(val.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"vwap\":").Append(vwap.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"rows\":{");
                bool firstVp = true;
                foreach (var row in _vp)
                {
                    if (!firstVp) sb.Append(',');
                    firstVp = false;
                    sb.Append('"').Append(row.Key.ToString(CultureInfo.InvariantCulture)).Append("\":").Append(row.Value);
                }
                sb.Append("}}");

                // Developing VWAP / VPOC lines: pin the forming bar's point to the current value,
                // then emit one {t,v} per sent bar that has a developing value.
                if (_bars.Count > 0) { long nt = _bars[_bars.Count - 1].T; _devVwap[nt] = vwap; _devPoc[nt] = poc; }

                sb.Append(",\"dev\":{\"vwap\":[");
                bool fv = true;
                foreach (BarData b in _bars)
                {
                    double v;
                    if (!_devVwap.TryGetValue(b.T, out v)) continue;
                    if (!fv) sb.Append(','); fv = false;
                    sb.Append("{\"t\":").Append(b.T).Append(",\"v\":").Append(v.ToString(CultureInfo.InvariantCulture)).Append('}');
                }
                sb.Append("],\"poc\":[");
                bool fp = true;
                foreach (BarData b in _bars)
                {
                    double v;
                    if (!_devPoc.TryGetValue(b.T, out v)) continue;
                    if (!fp) sb.Append(','); fp = false;
                    sb.Append("{\"t\":").Append(b.T).Append(",\"v\":").Append(v.ToString(CultureInfo.InvariantCulture)).Append('}');
                }
                sb.Append("]}");
            }

            // cvd: cumulative volume delta candles — { t, o, h, l, c } per sent bar. Own pane on the web.
            if (_cvdBar.Count > 0)
            {
                sb.Append(",\"cvd\":[");
                bool fc = true;
                foreach (BarData b in _bars)
                {
                    double[] o;
                    if (!_cvdBar.TryGetValue(b.T, out o)) continue;
                    if (!fc) sb.Append(','); fc = false;
                    sb.Append("{\"t\":").Append(b.T)
                      .Append(",\"o\":").Append(o[0].ToString(CultureInfo.InvariantCulture))
                      .Append(",\"h\":").Append(o[1].ToString(CultureInfo.InvariantCulture))
                      .Append(",\"l\":").Append(o[2].ToString(CultureInfo.InvariantCulture))
                      .Append(",\"c\":").Append(o[3].ToString(CultureInfo.InvariantCulture)).Append('}');
                }
                sb.Append(']');
            }

            // draw: hand-drawn levels / zones / trendlines (prebuilt on the data thread).
            if (_drawJson.Length > 0) sb.Append(',').Append(_drawJson);

            // accounts: connected accounts + realized/unrealized PnL.
            if (_accountsJson.Length > 0) sb.Append(',').Append(_accountsJson);

            sb.Append('}');
            return sb.ToString();
        }

        // Snapshot developing VWAP + VPOC for a bar (caller holds _lock).
        private void StoreDevLocked(long ts)
        {
            double vwap = _vwapDen > 0 ? _vwapNum / _vwapDen : 0;
            double poc = 0; long mx = 0;
            foreach (var kv in _vp) if (kv.Value > mx) { mx = kv.Value; poc = kv.Key; }
            if (vwap > 0) _devVwap[ts] = vwap;
            if (poc  > 0) _devPoc[ts]  = poc;
        }

        // POC / value-area high+low from the daily profile (caller holds _lock). Ports FlowAnalysis.RecalcVP.
        private void ComputeVp(out double poc, out double vah, out double val)
        {
            poc = vah = val = 0;
            if (_vp.Count == 0 || _vpTotal == 0) return;

            long maxVol = 0;
            foreach (var kv in _vp) if (kv.Value > maxVol) { maxVol = kv.Value; poc = kv.Key; }

            var sorted = new List<double>(_vp.Keys);
            sorted.Sort();                       // ascending price
            int pi = sorted.IndexOf(poc);
            if (pi < 0) { vah = val = poc; return; }

            long target = (long)(_vpTotal * ValueAreaPct);
            long vaVol  = _vp[poc];
            int hi = pi, lo = pi;
            while (vaVol < target)
            {
                bool canUp = hi + 1 < sorted.Count;
                bool canDn = lo - 1 >= 0;
                if (!canUp && !canDn) break;
                long upV = canUp ? _vp[sorted[hi + 1]] : 0;
                long dnV = canDn ? _vp[sorted[lo - 1]] : 0;
                if (canUp && (!canDn || upV >= dnV)) vaVol += _vp[sorted[++hi]];
                else                                 vaVol += _vp[sorted[--lo]];
            }
            vah = sorted[hi];
            val = sorted[lo];
        }

        private static void AppendJsonString(StringBuilder sb, string s)
        {
            sb.Append('"');
            if (!string.IsNullOrEmpty(s))
            {
                foreach (char ch in s)
                {
                    switch (ch)
                    {
                        case '"':  sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n");  break;
                        case '\r': sb.Append("\\r");  break;
                        case '\t': sb.Append("\\t");  break;
                        default:   sb.Append(ch);     break;
                    }
                }
            }
            sb.Append('"');
        }

        #region Properties

        [NinjaScriptProperty]
        [Display(Name = "Relay URL", Order = 1, GroupName = "Connection",
                 Description = "Base URL of the orderflow-web relay, e.g. http://192.168.1.100:8000 (no trailing /ingest)")]
        public string RelayUrl { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Auth Token", Order = 2, GroupName = "Connection",
                 Description = "Bearer token; must match RELAY_TOKEN on the relay")]
        public string AuthToken { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name = "Update Interval (s)", Order = 3, GroupName = "Connection")]
        public int UpdateIntervalSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(10, 1000)]
        [Display(Name = "Bars To Send", Order = 4, GroupName = "Data")]
        public int BarsToSend { get; set; }

        [NinjaScriptProperty]
        [Range(0, 200)]
        [Display(Name = "Footprint Bars", Order = 5, GroupName = "Data",
                 Description = "Send per-price bid/ask footprint (from ticks) for the most recent N bars (0 = off). Historical footprint needs Tick Replay enabled on the chart.")]
        public int FootprintBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Debug", Order = 1, GroupName = "Debug")]
        public bool EnableDebug { get; set; }

        #endregion
    }
}
