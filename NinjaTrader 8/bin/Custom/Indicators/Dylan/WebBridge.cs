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
                EnableDebug           = true;
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
            if (BarsInProgress != 0) return;
            if (CurrentBar < 0) return;

            // With Tick Replay on, OnBarUpdate fires per tick during State.Historical, so sampling
            // the first tick gives O=H=L=C (flat bars). Instead: when a new bar starts, record the
            // PREVIOUS bar [1] from its now-final OHLC; always keep the forming bar [0] up to date.
            if (IsFirstTickOfBar && CurrentBar >= 1)
                UpsertBar(Time[1], Open[1], High[1], Low[1], Close[1], (long)Volume[1]);

            UpsertBar(Time[0], Open[0], High[0], Low[0], Close[0], (long)Volume[0]);
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
            sb.Append("]}");
            return sb.ToString();
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
        [Display(Name = "Enable Debug", Order = 1, GroupName = "Debug")]
        public bool EnableDebug { get; set; }

        #endregion
    }
}
