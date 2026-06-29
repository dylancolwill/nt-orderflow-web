#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
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
                    lock (_lock) { _vp.Clear(); _vpTotal = 0; _vwapNum = 0; _vwapDen = 0; _devVwap.Clear(); _devPoc.Clear(); }

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
            }
            // ── 1-tick series — accumulate footprint ──────────────────────────
            else if (BarsInProgress == 1)
            {
                AccumulateFootprintTick();
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

            if (FootprintBars <= 0) return;

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

            long   ts = ToUnixUtc(Times[0][0]);                    // primary bar this tick belongs to
            double rp = Instrument.MasterInstrument.RoundToTickSize(price);
            lock (_lock)
            {
                Dictionary<double, long[]> rows;
                if (!_fp.TryGetValue(ts, out rows)) { rows = new Dictionary<double, long[]>(); _fp[ts] = rows; }
                long[] ba;
                if (!rows.TryGetValue(rp, out ba)) { ba = new long[2]; rows[rp] = ba; }
                if (buy) ba[1] += vol; else ba[0] += vol;          // [0]=bid (sell), [1]=ask (buy)
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
                    if (EnableDebug)
                        Print(string.Format("WebBridge [post] {0} bytes -> {1}",
                              payload.Length, (int)resp.StatusCode));
                    resp.Dispose();
                }
            }
            catch (Exception ex)
            {
                if (EnableDebug) Print("WebBridge [post error] " + ex.Message);
            }
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
