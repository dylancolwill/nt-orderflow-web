# orderflow-web

A phone/iPad web app that mirrors a NinjaTrader 8 order-flow chart. The heavy computation
stays on the Windows trading PC running NinjaTrader; only small JSON snapshots are pushed
out, so mobile rendering stays cheap and battery-friendly.

**Status: v1 — live candlesticks, end-to-end.** Footprint, volume profile, cumulative
delta, a delta table, and hand-drawn levels are planned for v2 (the snapshot schema and the
exporter indicator already leave room for them).

> **Why not the NinjaTrader ATI?** The ATI (`NTDirect.dll`) only does order routing plus
> basic Level-1 reads. Footprint, per-price bid/ask volume, delta, volume profile, and
> drawn levels are all computed inside NinjaScript from the tick stream, so a NinjaScript
> exporter (the `WebBridge` indicator in this repo) is required.

## Architecture

```
[ NinjaTrader 8 / Windows ]              [ Server ]                     [ iPad / phone ]
  WebBridge indicator  --HTTP POST/2s-->  relay (FastAPI)  --WebSocket-->  PWA (Lightweight Charts)
  - reads OHLCV bars     (LAN, token)      - holds latest snapshot          - renders candles
  - (v2: footprint/VP/                      - serves the PWA                 - auto-reconnect
     delta/levels)                          - reverse proxy + auth (prod)
```

- **Ingest leg** (NinjaTrader → relay): outbound HTTP `POST /ingest`, bearer-token auth.
  Outbound-only — no inbound ports opened on the trading PC. Keep this on the LAN.
- **Serve leg** (relay → browsers): WebSocket fan-out of the latest snapshot. This is the
  only leg you expose to the internet, and it should sit behind an authenticating proxy.

## Layout

```
orderflow-web/
├─ NinjaTrader 8/                      # mirrors the NinjaTrader install tree
│  └─ bin/Custom/Indicators/Dylan/
│     └─ WebBridge.cs                  # the exporter indicator
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

Choose a shared secret token and use the **same value** in the indicator and the relay.

### 1. NinjaTrader (Windows)

1. Copy the `NinjaTrader 8/` folder from this repo into your NinjaTrader user-data folder
   (the one containing `bin/Custom/…`). It **merges** into the existing tree — the only new
   file is `bin/Custom/Indicators/Dylan/WebBridge.cs`. Nothing else is overwritten.
2. In NinjaTrader, open the NinjaScript Editor and **Compile** (F5).
3. On your chart, add the **WebBridge** indicator and set:
   - **Relay URL** — `http://<relay-host>:8000` (no trailing `/ingest`)
   - **Auth Token** — your shared secret (must match `RELAY_TOKEN`)
   - **Update Interval (s)** — `2`
   - **Bars To Send** — `120`
   - **Enable Debug** — `true` while testing
4. With Debug on, the NinjaScript **Output** window logs `WebBridge [post] N bytes -> 200`.

### 2. Relay (server)

```bash
cd relay
python -m venv .venv
source .venv/bin/activate          # Windows: .venv\Scripts\activate
pip install -r requirements.txt
export RELAY_TOKEN='your-shared-secret'   # Windows: set RELAY_TOKEN=...
uvicorn app:app --host 0.0.0.0 --port 8000
```

Quick checks:

```bash
curl localhost:8000/healthz                                        # {"ok":true,...}
curl -X POST localhost:8000/ingest -H 'Authorization: Bearer wrong'  # 401
```

### 3. Mobile (LAN first)

On a device on the same network, open `http://<relay-host>:8000`. Candles should appear and
update about every 2s. Use **Share → Add to Home Screen** to install the PWA.

### 4. Exposing to the internet

Once it works on the LAN, put the relay behind a reverse proxy / tunnel with an
authentication layer (so only you can reach it). Expose **only** the serve side — the
`/ingest` endpoint should stay reachable from the trading PC over the LAN, not the public
internet.

## Snapshot schema (versioned)

v1 populates `bars` only; later fields are additive and won't break older clients.

```jsonc
{
  "type": "snapshot", "v": 1,
  "instrument": "<symbol>", "tickSize": 0.25, "ts": "<iso-8601 utc>",
  "bars": [ { "t": 1719668700, "o": 5301.5, "h": 5302, "l": 5300.75, "c": 5301.25, "v": 1234 } ]
  // v2: "footprint", "delta", "vp", "table", "levels"
}
```

`t` is a UTC epoch-seconds timestamp.

## Notes / known v1 limitations

- **Timestamps** are UTC seconds, so the chart displays UTC. A local/exchange-time offset
  can be added in v2.
- **Charting library** is pinned to Lightweight Charts **v4.2.3** (`addCandlestickSeries`).
  v5 renamed this to `addSeries(CandlestickSeries, …)`.
- **Footprint** is not a built-in Lightweight Charts series — v2 adds it as a custom canvas
  primitive that draws bid×ask cells using the chart's coordinate API.
- Relay state is in-memory only; a restart is harmless (the indicator re-posts within ~2s).

## Configuration

| Where | Setting | Default | Notes |
|-------|---------|---------|-------|
| WebBridge indicator | Relay URL | — | Base URL of the relay, no `/ingest` |
| WebBridge indicator | Auth Token | — | Must match `RELAY_TOKEN` |
| WebBridge indicator | Update Interval (s) | 2 | Snapshot cadence |
| WebBridge indicator | Bars To Send | 120 | Trailing bars per snapshot |
| Relay | `RELAY_TOKEN` (env) | `change-me` | Shared secret; set a strong value |
| Relay | `--port` | 8000 | uvicorn bind port |

## Roadmap

- **v2** — footprint cells, volume profile (POC/VAH/VAL/VWAP), cumulative-delta subpanel,
  delta table, and drawn levels. All sourced from the same `WebBridge` indicator and added
  to the snapshot.
- **v3** — TPO + aggression clusters, then optional order actions via a reverse WebSocket
  channel (reserved in the message envelope via the `type` field).
