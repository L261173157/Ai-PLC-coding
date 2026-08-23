"""Shared stdio MCP client for the TiaMcp smoke/verify scripts.

Every longer script in this directory used to carry its own ~100-line copy of this
client (~800 lines duplicated in total). Import it instead:

    from mcp_client import Client, paths

The MCP SDK dispatches requests concurrently, so responses may arrive OUT OF ORDER;
this client matches each response by `id` and buffers the rest.

`call()` returns the parsed JSON result, or a `{"_error": ...}` dict when the server
answers with a protocol error or non-JSON content — assert on the fields you expect
and an `_error` payload will fail the assertion naturally.
"""


class Client:
    def __init__(self, dll, backend="fake", mode="ReadOnly", client_name="smoke"):
        import subprocess
        self.p = subprocess.Popen(
            ["dotnet", dll, "--backend", backend, "--mode", mode],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            text=True, encoding="utf-8")
        self._client_name = client_name
        self._next = 1
        self._buf = {}

    def _send(self, method, params=None, notification=False):
        import json
        obj = {"jsonrpc": "2.0", "method": method}
        if notification:
            if params is not None:
                obj["params"] = params
        else:
            obj["id"] = self._next
            self._next += 1
            if params is not None:
                obj["params"] = params
        self.p.stdin.write(json.dumps(obj) + "\n")
        self.p.stdin.flush()
        return obj.get("id")

    def _wait(self, ids, timeout=60.0):
        import json
        import time
        deadline = time.time() + timeout
        while any(i not in self._buf for i in ids):
            if time.time() > deadline:
                raise TimeoutError(f"missing ids {[i for i in ids if i not in self._buf]}")
            raw = self.p.stdout.readline()
            if not raw:
                raise RuntimeError("server closed stdout unexpectedly")
            msg = json.loads(raw.strip())
            if "id" in msg:
                self._buf[msg["id"]] = msg
        return {i: self._buf[i] for i in ids}

    def initialize(self):
        """Initialize the session and return the tools/list entries."""
        i1 = self._send("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                                       "clientInfo": {"name": self._client_name, "version": "0.1"}})
        self._send("notifications/initialized", notification=True)
        i2 = self._send("tools/list")
        r = self._wait([i1, i2])
        return r[i2]["result"]["tools"]

    def call(self, name, args=None, timeout=60.0):
        import json
        mid = self._send("tools/call", {"name": name, "arguments": args or {}})
        r = self._wait([mid], timeout=timeout)[mid]
        if "error" in r:
            return {"_error": r["error"]}
        res = r.get("result", {})
        if res.get("isError"):
            txt = res["content"][0].get("text", "") if res.get("content") else ""
            return {"_error": txt or res}
        txt = res.get("content", [{}])[0].get("text", "") if res.get("content") else ""
        try:
            return json.loads(txt)
        except Exception:
            return {"_error": f"non-JSON content: {txt!r}"}

    def close(self):
        import subprocess
        try:
            self.p.stdin.close()
        finally:
            try:
                self.p.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.p.kill()


def paths(session):
    """Canonical Fake-world paths: session -> device -> plc program."""
    plc = f"{session}/project:Demo/device:PLC_1/plc:program"
    return {
        "session": session,
        "device": f"{session}/project:Demo/device:PLC_1",
        "plc": plc,
    }
