"""
orderflow-web relay.

Runs on the Linux server. Three jobs:
  1. POST /ingest  - receive a JSON snapshot from the NinjaTrader WebBridge indicator
                     (bearer-token auth), keep the latest in memory, and fan it out.
  2. WS   /stream  - browser clients connect here; get the latest snapshot on connect,
                     then every new snapshot as it arrives.
  3. GET  /        - serve the static PWA in ../web.

Deploy notes:
  - Set RELAY_TOKEN to a strong secret; the WebBridge "Auth Token" must match.
  - Bind the ingest side to the LAN only. Publish ONLY the client side through the
    Cloudflare Tunnel, fronted by Cloudflare Access.
  - In-memory only; no database. Restart = clean slate (NT re-posts within ~2s).
"""

from __future__ import annotations

import asyncio
import os
from pathlib import Path

from fastapi import FastAPI, Request, WebSocket, WebSocketDisconnect, HTTPException
from fastapi.responses import FileResponse
from fastapi.staticfiles import StaticFiles

RELAY_TOKEN = os.environ.get("RELAY_TOKEN", "change-me")
WEB_DIR = Path(__file__).resolve().parent.parent / "web"

app = FastAPI(title="orderflow-web relay")

# Shared state: the latest snapshot and the set of connected websockets.
_latest: dict | None = None
_clients: set[WebSocket] = set()
_clients_lock = asyncio.Lock()


def _check_auth(request: Request) -> None:
    auth = request.headers.get("authorization", "")
    if not auth.startswith("Bearer ") or auth[7:] != RELAY_TOKEN:
        raise HTTPException(status_code=401, detail="bad token")


@app.post("/ingest")
async def ingest(request: Request):
    _check_auth(request)
    try:
        snapshot = await request.json()
    except Exception:
        raise HTTPException(status_code=400, detail="invalid json")

    global _latest
    _latest = snapshot

    # Fan out to all connected clients; drop any that error.
    dead = []
    async with _clients_lock:
        targets = list(_clients)
    for ws in targets:
        try:
            await ws.send_json(snapshot)
        except Exception:
            dead.append(ws)
    if dead:
        async with _clients_lock:
            for ws in dead:
                _clients.discard(ws)

    return {"ok": True, "clients": len(targets) - len(dead)}


@app.websocket("/stream")
async def stream(ws: WebSocket):
    await ws.accept()
    async with _clients_lock:
        _clients.add(ws)
    try:
        if _latest is not None:
            await ws.send_json(_latest)
        # Keep the socket open. We don't expect client messages in v1, but reading
        # lets us detect disconnects promptly (and reserves the reverse channel).
        while True:
            await ws.receive_text()
    except WebSocketDisconnect:
        pass
    except Exception:
        pass
    finally:
        async with _clients_lock:
            _clients.discard(ws)


@app.get("/healthz")
async def healthz():
    return {"ok": True, "has_snapshot": _latest is not None, "clients": len(_clients)}


# Serve the PWA. index.html at "/", everything else as static assets.
@app.get("/")
async def index():
    return FileResponse(WEB_DIR / "index.html")


app.mount("/", StaticFiles(directory=str(WEB_DIR)), name="web")
