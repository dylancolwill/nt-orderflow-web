# orderflow-web

A phone/iPad web app that mirrors Dylan's NinjaTrader 8 order-flow chart. The heavy
computation stays on the Windows trading PC; only small JSON snapshots are pushed out,
so mobile rendering stays cheap.

**Status: v1 — live candlesticks, end-to-end.** Footprint, volume profile, cumulative
delta, the delta table, and drawn levels are planned for v2 (the snapshot schema and the
WebBridge indicator already leave room for them).

> Why not the NinjaTrader ATI? The ATI (`NTDirect.dll`) only does order routing + basic
> Level-1 reads. Footprint, per-price bid/ask volume, delta, volume profile, and drawn
> levels are all computed inside NinjaScript from the tick stream, so a NinjaScript
> exporter (the `WebBridge` indicator here) is required. It's modeled on the existing
> `Dylan/FlowAnalysis.cs`.

## Architecture

```
[ NinjaTrader 8 / Windows PC ]            [ Linux server ]               [ iPad / phone ]
  WebBridge indicator  --HTTP POST/2s-->   relay (FastAPI)  --WebSocket-->  PWA (Lightweight Charts)
  - reads OHLCV bars     (LAN, token)       - holds latest snapshot          - renders candles
  - (v2: footprint/VP/                       - serves the PWA                 - auto-reconnect
     delta/levels)                           - Cloudflare Tunnel + Access
```

- **Ingest leg** (NT → relay): outbound HTTPS `POST /ingest`, bearer-token auth.
  Outbound-only — no inbound ports opened on Windows. Keep this on the LAN.
- **Serve leg** (relay → browsers): WebSocket fan-out of the latest snapshot. This is the
  only leg published through the Cloudflare Tunnel (behind Cloudflare Access).

## Layout

```
orderflow-web/
├─ NinjaTrader 8/                      # copy this folder into C:\Users\dylan\Documents\
│  └─ bin/Custom/Indicators/Dylan/
│     └─ WebBridge.cs                  # the exporter indicator (sits beside FlowAnalysis.cs)
├─ relay/
│  ├─ app.py                          # FastAPI: /ingest, /stream (WS), serves ../web
│  └─ requirements.txt
├─ web/                                # static PWA, served by the relay
│  ├─ index.html, app.js
│  ├─ manifest.webmanifest, sw.js, icon.svg
│  └─ (v2) footprint-primitive.js
├─ .gitignore
└─ README.md
```

## Setup

Pick a shared secret token and use the **same value** in both places below.

### 1. NinjaTrader (Windows)

1. Copy the `NinjaTrader 8/` folder from this project into `C:\Users\dylan\Documents\`.
   It **merges** into the existing tree — the only new file is
   `bin/Custom/Indicators/Dylan/WebBridge.cs`. Nothing else is overwritten.
2. In NinjaTrader: **New → NinjaScript Editor → Compile** (F5).
3. Open your footprint chart, **Indicators → WebBridge**, and set:
   - **Relay URL** — `http://<linux-lan-ip>:8000` (no trailing `/ingest`)
   - **Auth Token** — your shared secret (matches `RELAY_TOKEN`)
   - **Update Interval (s)** — `2`
   - **Bars To Send** — `120`
   - **Enable Debug** — `true` while testing
4. With Debug on, the NinjaScript **Output** window logs `WebBridge [post] N bytes -> 200`.

### 2. Relay (Linux server)

```bash
cd orderflow-web/relay
python3 -m venv .venv && source .venv/bin/activate
pip install -r requirements.txt
export RELAY_TOKEN='your-shared-secret'
uvicorn app:app --host 0.0.0.0 --port 8000
```

Quick checks:

```bash
curl localhost:8000/healthz                       # {"ok":true,...}
curl -X POST localhost:8000/ingest -H 'Authorization: Bearer wrong'   # 401
```

### 3. Mobile (LAN first)

On the iPad/phone on the same WiFi, open `http://<linux-lan-ip>:8000`. Candles should
appear and update ~every 2s. Use **Share → Add to Home Screen** to install the PWA.

### 4. Expose via Cloudflare (after LAN works)

- Point your existing Cloudflare Tunnel at `http://localhost:8000` on the Linux box.
- Put the public hostname behind **Cloudflare Access (Zero Trust)** so only you can reach
  it (e.g. one-time PIN / Google login).
- Do **not** expose `/ingest` publicly — NT posts to it over the LAN only.

## Snapshot schema (versioned)

v1 populates `bars` only; later fields are additive and won't break older clients.

```jsonc
{
  "type": "snapshot", "v": 1,
  "instrument": "ES 06-26", "tickSize": 0.25, "ts": "2026-06-29T13:45:02Z",
  "bars": [ { "t": 1719668700, "o": 5301.5, "h": 5302, "l": 5300.75, "c": 5301.25, "v": 1234 } ]
  // v2: "footprint", "delta", "vp", "table", "levels"
}
```

## Notes / known v1 limitations

- **Timestamps** are sent as UTC seconds (`b.t`). The chart shows UTC; if you want it in
  exchange/local time we can offset in v2.
- **Charting lib** is pinned to Lightweight Charts **v4.2.3** (has `addCandlestickSeries`).
  v5 renamed this to `addSeries(CandlestickSeries, …)`.
- **Footprint** is not a built-in Lightweight Charts series — v2 adds it as a custom
  canvas primitive that draws bid×ask cells using the chart's coordinate API.
- Relay state is in-memory only; a restart is harmless (NT re-posts within ~2s).

## Roadmap

- **v2** — footprint cells, volume profile (POC/VAH/VAL/VWAP), CVD subpanel, delta table,
  drawn levels. All sourced from the same `WebBridge` indicator (reusing FlowAnalysis's
  footprint/VP/delta/level logic) and added to the snapshot.
- **v3** — TPO + aggression clusters, then optional order actions via the reserved reverse
  WebSocket channel (NinjaScript order endpoint or `SimpleTradeCopier.cs`/ATI).
