#!/usr/bin/env python3
"""HTTP variant of the real-TIA openness smoke.

Drives a long-lived  --transport http --backend openness  server at /mcp. Launch the server first:

    dotnet brands/tia/mcp/src/TiaMcp.Server/bin/Debug/net10.0/TiaMcp.Server.dll \
      --transport http --backend openness --mode ReadWrite \
      --workerPath <abs-worker.exe> --urls http://127.0.0.1:5270

Then:

    python brands/tia/mcp/tests/smoke_openness_http.py [path-to.ap21]   (run from the repo root)

Validates the "control real TIA via MCP" link end-to-end over streamable HTTP:
    initialize -> tia_status -> tia_connect -> tia_project_open -> tia_project_list

The first connect spawns the net48 worker + a headless TIA Portal (~30-90s), so
request timeouts are generous (150s).
"""
import json
import os
import sys
import time
import urllib.error
import urllib.request

URL = "http://127.0.0.1:5270/mcp"
HERE = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.abspath(os.path.join(HERE, "..", "..", "..", ".."))  # brands/tia/mcp/tests -> repo root
DEFAULT_PROJECT = os.path.join(REPO_ROOT, "plc", "Project1", "Project1.ap21")

_NEXT = [1]  # JSON-RPC id counter


def post(obj, session_id=None, timeout=150.0):
    data = json.dumps(obj).encode()
    headers = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream"}
    if session_id:
        headers["Mcp-Session-Id"] = session_id
    req = urllib.request.Request(URL, data=data, headers=headers, method="POST")
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return resp.headers.get("Mcp-Session-Id"), resp.headers.get("Content-Type", ""), resp.read().decode()


def parse(ct, body):
    if "event-stream" in ct:
        return [json.loads(l[5:].strip()) for l in body.splitlines() if l.startswith("data:")]
    return [json.loads(body)]


def request(method, params=None, session_id=None, timeout=150.0, notification=False):
    obj = {"jsonrpc": "2.0", "method": method}
    if notification:
        if params is not None:
            obj["params"] = params
    else:
        obj["id"] = _NEXT[0]
        _NEXT[0] += 1
        if params is not None:
            obj["params"] = params
    sid, ct, body = post(obj, session_id, timeout)
    if notification:
        return None, sid
    msg = next(m for m in parse(ct, body) if m.get("id") == obj["id"])
    return msg, sid


def tool(name, args=None, session_id=None, timeout=150.0):
    """Call a tia_* tool; return (parsed_or_raw, error_dict_or_None)."""
    obj = {"jsonrpc": "2.0", "id": _NEXT[0], "method": "tools/call",
           "params": {"name": name, "arguments": args or {}}}
    _NEXT[0] += 1
    sid, ct, body = post(obj, session_id, timeout)
    msg = next(m for m in parse(ct, body) if m.get("id") == obj["id"])
    if "error" in msg:
        return None, msg["error"]
    res = msg.get("result", {})
    if res.get("isError"):
        return None, {"code": -32000, "message": res.get("content", [{}])[0].get("text", "tool error")}
    txt = res["content"][0]["text"]
    try:
        return json.loads(txt), None
    except Exception:
        return txt, None


def main() -> int:
    project = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else DEFAULT_PROJECT)
    print("########## Openness backend over HTTP (real TIA V21) ##########")
    print("project:", project)

    # ---- wait for the HTTP server to come up (initialize probe) ----
    deadline = time.time() + 30
    last_err = None
    while time.time() < deadline:
        try:
            init, sid = request("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                                               "clientInfo": {"name": "openness-http", "version": "0.1"}})
            break
        except Exception as e:
            last_err = e
            time.sleep(0.5)
    else:
        raise SystemExit(f"server never came up at {URL}: {last_err}")
    print("initialize  ->", init["result"]["serverInfo"]["name"], "| session:", sid)
    request("notifications/initialized", notification=True, session_id=sid)

    print("\ntia_status  ... (first call spawns the net48 worker)")
    st, err = tool("tia_status", {}, session_id=sid)
    print("  ->", json.dumps(st, ensure_ascii=False) if st else err)
    if err:
        return 1
    if st.get("tiaVersion") != "V21":
        print(f"  !! expected tiaVersion V21, got {st.get('tiaVersion')}")

    print("\ntia_connect (headless TIA launch, may take 30-90s) ...")
    sess, err = tool("tia_connect", {"mode": "headless"}, session_id=sid)
    print("  ->", json.dumps(sess, ensure_ascii=False) if sess else err)
    if err or not sess or not sess.get("sessionId"):
        return 1
    session_path = sess["path"]
    print("  >>> bridge -> worker -> Siemens -> TIA Portal: CONNECTED (session", sess["sessionId"] + ")")

    print("\ntia_project_open ...")
    proj, err = tool("tia_project_open", {"sessionPath": session_path, "path": project, "visible": False},
                     session_id=sid)
    print("  ->", json.dumps(proj, ensure_ascii=False) if proj else err)
    if err or not proj:
        return 1
    proj_path = proj.get("path")

    print("\ntia_project_list ...")
    tgts, err = tool("tia_project_list", {"projectPath": proj_path}, session_id=sid)
    if err:
        print("  ->", err)
        return 1
    if isinstance(tgts, list):
        print(f"  -> {len(tgts)} target(s):", [t.get("name") for t in tgts])
        if not tgts:
            print("  (project has no PLC/HMI devices yet — expected for an empty Project1)")
    else:
        print("  ->", tgts)

    print("\nOK — real-TIA path validated over HTTP")
    return 0


if __name__ == "__main__":
    sys.exit(main())
