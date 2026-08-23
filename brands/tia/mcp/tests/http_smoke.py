#!/usr/bin/env python3
"""Streamable-HTTP liveness test for the TiaMcp server (--transport http).

POSTs JSON-RPC to /mcp, handling either a single application/json response or an SSE
(text/event-stream) stream, and threads the Mcp-Session-Id header.
"""
import json
import time
import urllib.error
import urllib.request

URL = "http://127.0.0.1:5270/mcp"


def post(obj, session_id=None):
    data = json.dumps(obj).encode()
    headers = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream"}
    if session_id:
        headers["Mcp-Session-Id"] = session_id
    req = urllib.request.Request(URL, data=data, headers=headers, method="POST")
    with urllib.request.urlopen(req, timeout=10) as resp:
        body = resp.read().decode()
        return resp.headers.get("Mcp-Session-Id"), resp.headers.get("Content-Type", ""), body


def parse(ct, body):
    if "event-stream" in ct:
        return [json.loads(l[5:].strip()) for l in body.splitlines() if l.startswith("data:")]
    return [json.loads(body)]


# Wait for the server to come up.
deadline = time.time() + 30
last_err = None
while time.time() < deadline:
    try:
        sid, ct, body = post({"jsonrpc": "2.0", "id": 1, "method": "initialize",
                              "params": {"protocolVersion": "2024-11-05", "capabilities": {},
                                         "clientInfo": {"name": "http-smoke", "version": "0.1"}}})
        break
    except Exception as e:
        last_err = e
        time.sleep(0.5)
else:
    raise SystemExit(f"server never came up: {last_err}")

msgs = parse(ct, body)
init = next(m for m in msgs if m.get("id") == 1)
print("initialize :", init["result"]["serverInfo"], "| session:", sid)

post({"jsonrpc": "2.0", "method": "notifications/initialized"}, session_id=sid)

_, ct, body = post({"jsonrpc": "2.0", "id": 2, "method": "tools/list"}, session_id=sid)
tl = next(m for m in parse(ct, body) if m.get("id") == 2)
print(f"tools/list : {len(tl['result']['tools'])} tools")

_, ct, body = post({"jsonrpc": "2.0", "id": 3, "method": "tools/call",
                    "params": {"name": "tia_status", "arguments": {}}}, session_id=sid)
ts = next(m for m in parse(ct, body) if m.get("id") == 3)
print("tia_status :", ts["result"]["content"][0]["text"])

print("\nHTTP OK")
