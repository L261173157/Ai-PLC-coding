#!/usr/bin/env python3
"""Live Openness check for the 2026-07-03 fixes (run on a TIA V21 machine).

  * tia_status BEFORE connect must report tiaAvailable=true (registry-installed) — the headline
    fix; previously it returned IsPortalAlive()=false pre-connect and agents aborted.
  * tia_disconnect must release the headless Portal (status stays sane).

Usage:  python brands/tia/mcp/tests/smoke_openness_status.py <path-to-dll>
"""
import json
import subprocess
import sys
import time


class Client:
    def __init__(self, dll, backend, mode):
        self.p = subprocess.Popen(
            ["dotnet", dll, "--backend", backend, "--mode", mode],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
        self._next = 1
        self._buf = {}

    def _send(self, method, params=None, notification=False):
        obj = {"jsonrpc": "2.0", "method": method}
        if notification:
            if params is not None:
                obj["params"] = params
        else:
            obj["id"] = self._next
            self._next += 1
            if params is not None:
                obj["params"] = params
        self.p.stdin.write((json.dumps(obj) + "\n").encode("utf-8"))
        self.p.stdin.flush()
        return obj.get("id")

    def _wait(self, ids, timeout=120.0):
        deadline = time.time() + timeout
        while any(i not in self._buf for i in ids):
            if time.time() > deadline:
                raise TimeoutError(f"missing ids {[i for i in ids if i not in self._buf]}")
            raw = self.p.stdout.readline()
            if not raw:
                raise RuntimeError("server closed stdout unexpectedly")
            msg = json.loads(raw.decode("utf-8").strip())
            if "id" in msg:
                self._buf[msg["id"]] = msg
        return {i: self._buf[i] for i in ids}

    def initialize(self):
        i1 = self._send("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                                       "clientInfo": {"name": "openness-check", "version": "0.1"}})
        self._send("notifications/initialized", notification=True)
        self._wait([i1])

    def call(self, name, args):
        mid = self._send("tools/call", {"name": name, "arguments": args})
        r = self._wait([mid])[mid]
        res = r.get("result", {})
        return json.loads(res["content"][0]["text"]) if "content" in res else res

    def close(self):
        try:
            self.p.stdin.close()
        finally:
            self.p.wait(timeout=10)


def main() -> int:
    dll = sys.argv[1]
    c = Client(dll, "openness", "ReadWrite")
    try:
        c.initialize()
        st = c.call("tia_status", {})
        print("  status (pre-connect):", st)
        assert st["backend"] == "Openness", st
        assert st["tiaAvailable"] is True, "HEADLINE FIX FAILED: tiaAvailable should be true on an installed machine"
        print("  OK: tiaAvailable=true pre-connect (registry-installed), openSessions=%d" % st["openSessions"])

        disc = c.call("tia_disconnect", {})
        print("  disconnect ->", disc)
        assert disc["status"] == "disconnected"
        st2 = c.call("tia_status", {})
        print("  status (post-disconnect):", st2)
        assert st2["openSessions"] == 0
    finally:
        c.close()

    print("\nOK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
