#!/usr/bin/env python3
"""Live Openness check for tia_tagtable_export (2026-07-03). Opens a real project,
lists its tag tables, exports one, and confirms a non-empty SimaticML XML file is written.

Usage:  python brands/tia/mcp/tests/smoke_openness_tagexport.py <dll> <path-to-.ap21>
"""
import json
import os
import subprocess
import sys
import tempfile
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

    def _wait(self, ids, timeout=180.0):
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
                                       "clientInfo": {"name": "tag-export-check", "version": "0.1"}})
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
            self.p.wait(timeout=15)


def main() -> int:
    dll, ap21 = sys.argv[1], sys.argv[2]
    c = Client(dll, "openness", "ReadWrite")
    try:
        c.initialize()
        session = c.call("tia_connect", {"mode": "headless"})["path"]
        proj = c.call("tia_project_open", {"sessionPath": session, "path": ap21, "visible": False})
        print("  opened:", proj["name"], "->", proj["path"])
        dev = c.call("tia_project_list", {"projectPath": proj["path"]})[0]["path"]
        plc = dev + "/plc:program"
        tables = c.call("tia_tagtable_list", {"path": plc})
        print("  tag tables:", [(t["name"], t["tagCount"]) for t in tables["tagTables"]])
        assert tables["total"] > 0, "no tag tables to export"
        target = tables["tagTables"][0]
        out = tempfile.mkdtemp(prefix="tiamcp-tagexp-")
        exp = c.call("tia_tagtable_export", {"path": target["path"], "outDir": out})
        print("  export ->", exp)
        assert exp["format"] == "Xml"
        assert os.path.isfile(exp["filePath"]) and os.path.getsize(exp["filePath"]) > 0
        head = open(exp["filePath"], encoding="utf-8-sig").read(200)
        print("  file: %s (%d bytes)" % (exp["filePath"], exp["bytes"]))
        print("  head:", head.replace("\n", " ")[:160].encode("ascii", "replace").decode())
        # sanity: a real SimaticML export starts with XML and references the tag table type
        assert "<" in head, "export does not look like XML"
        c.call("tia_disconnect", {})
    finally:
        c.close()

    print("\nOK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
