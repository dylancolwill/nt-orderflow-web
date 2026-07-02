# orderflow-web

A phone/iPad web app that mirrors a NinjaTrader 8 order-flow chart. The heavy computation
stays on the Windows trading PC running NinjaTrader; only small JSON snapshots are pushed
out, so mobile rendering stays cheap and battery-friendly.

**Status: v2 — live candlesticks plus footprint, volume profile, developing VWAP/VPOC,
cumulative delta, a delta table, and hand-drawn levels, all end-to-end.** The `WebBridge`
indicator computes everything from the tick stream on the Windows PC; the browser just
renders the latest snapshot.

> **Why not the NinjaTrader ATI?** The ATI (`NTDirect.dll`) only does order routing plus
> basic Level-1 reads. Footprint, per-price bid/ask volume, delta, volume profile, and
> drawn levels are all computed inside NinjaScript from the tick stream, so a NinjaScript
> exporter (the `WebBridge` indicator in this repo) is required.

## Architecture

```
[ NinjaTrader 8 / Windows ]              [ Server ]                     [ iPad / phone ]
  WebBridge indicator  --HTTP POST/2s-->  relay (FastAPI)  --WebSocket-->  PWA (Lightweight Charts)
  - reads OHLCV bars     (LAN, token)      - holds latest snapshot          - renders candles
  - footprint / VP /                        - serves the PWA                 - + overlays
     CVD / levels too                        - reverse proxy + auth (prod)     - auto-reconnect
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
│  └─ footprint-primitive.js, vp-primitive.js, table-primitive.js, draw-primitive.js
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

> **Python 3.10+ is required** — the relay uses `X | None` type-union syntax. On 3.8/3.9
> you'll get `TypeError: unsupported operand type(s) for |: 'type' and 'NoneType'`.

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

## Hand-drawn levels

`WebBridge` scans the chart's drawing objects ~1×/s and pushes them in the snapshot's
`draw` field. Colour, dash style, width, and label text are preserved. What translates:

| Drawing tool | Renders as |
|--------------|------------|
| Horizontal Line | level |
| Ray / Line | level if flat, else a trend segment |
| Labeled Ray / Line / Extended / Arrow Line | same as above, with the label text |
| Rectangle / Extended Rectangle | zone |

**Known limitation:** *Labeled Horizontal Line* and *Labeled Vertical Line* expose only a
single anchor, and the scanner requires ≥2 anchors for line-type tools, so they are
skipped. For a labeled horizontal level, draw a **flat Labeled Ray** (its `DisplayText`
carries over) or a plain Horizontal Line (no label).

## Snapshot schema (versioned)

The `v` field is still `1`; every field beyond `bars` is additive, so older clients keep
working. A field is only present when the exporter has data for it.

```jsonc
{
  "type": "snapshot", "v": 1,
  "instrument": "<symbol>", "tickSize": 0.25, "ts": "<iso-8601 utc>",
  "bars":      [ { "t": 1719668700, "o": 5301.5, "h": 5302, "l": 5300.75, "c": 5301.25, "v": 1234 } ],
  "footprint": { "<barTs>": { "<price>": { "b": 120, "a": 143 } } },
  "vp":        { "poc": 5301, "vah": 5303, "val": 5299, "vwap": 5301.1, "rows": { "<price>": 1234 } },
  "dev":       { "vwap": [ { "t": 1719668700, "v": 5301.1 } ], "poc": [ { "t": 1719668700, "v": 5301 } ] },
  "cvd":       [ { "t": 1719668700, "o": 0, "h": 40, "l": -12, "c": 28 } ],
  "draw":      { "levels": [], "zones": [], "trends": [] }
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
| WebBridge indicator | Footprint Bars | 40 | Per-price bid/ask footprint for the most recent N bars (0 = off; historical needs Tick Replay). Range 0–200 |
| Relay | `RELAY_TOKEN` (env) | `change-me` | Shared secret; set a strong value |
| Relay | `--port` | 8000 | uvicorn bind port — any free port, but it must match the WebBridge Relay URL |

## Roadmap

- **v2 (done)** — footprint cells, volume profile (POC/VAH/VAL/VWAP), developing VWAP/VPOC,
  cumulative-delta subpanel, delta table, and hand-drawn levels/zones/trendlines. All
  sourced from the same `WebBridge` indicator and added to the snapshot.
- **v3** — TPO + aggression clusters, then optional order actions via a reverse WebSocket
  channel (reserved in the message envelope via the `type` field).
