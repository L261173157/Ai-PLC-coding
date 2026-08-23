#!/usr/bin/env python3
"""Focused LIVE verification (real TIA) of the worker-side fixes that aren't covered by the
offline Fake checks: G11 (project_list finds grouped PLC), G3 (tag_list per-table filter) and
G13 (tia_block_export recovers an inconsistent block instead of throwing).

Usage: python brands/tia/mcp/tests/verify_live_fixes.py <server.dll> <path-to.ap21>
"""
import json, subprocess, sys, time

class Client:
    def __init__(self, dll, project):
        self.p = subprocess.Popen(["dotnet", dll, "--backend", "openness", "--mode", "ReadWrite"],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, encoding="utf-8")
        self._n = 1; self._b = {}; self.project = project
    def _send(self, m, p=None, notif=False):
        o = {"jsonrpc": "2.0", "method": m}
        if not notif: o["id"] = self._n; self._n += 1
        if p is not None: o["params"] = p
        self.p.stdin.write(json.dumps(o) + "\n"); self.p.stdin.flush(); return o.get("id")
    def _wait(self, ids, t=200):
        d = time.time() + t
        while any(i not in self._b for i in ids):
            if time.time() > d: raise TimeoutError(ids)
            r = self.p.stdout.readline()
            if not r: raise RuntimeError("stdout closed")
            m = json.loads(r.strip())
            if "id" in m: self._b[m["id"]] = m
        return {i: self._b[i] for i in ids}
    def init(self):
        i = self._send("initialize", {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "verify-live", "version": "0.1"}})
        self._send("notifications/initialized", notif=True); return self._wait([i])[i]["result"]
    def call(self, name, args=None, t=200):
        mid = self._send("tools/call", {"name": name, "arguments": args or {}})
        r = self._wait([mid], t)[mid]
        res = r.get("result", {})
        txt = res.get("content", [{}])[0].get("text", "") if res.get("content") else ""
        try: return json.loads(txt)
        except: return {"_error": txt[:200] or r.get("error")}
    def close(self):
        try: self.p.stdin.close()
        finally:
            try: self.p.wait(timeout=10)
            except: self.p.kill()

def main():
    dll, project = sys.argv[1], sys.argv[2]
    fails = []
    c = Client(dll, project)
    try:
        c.init(); c.call("tia_connect", {"mode": "headless"})
        c.call("tia_project_open", {"sessionPath": "session:s-openness", "path": project, "visible": False})
        # G11: project_list finds the PLC (it's in a device group, not project.Devices)
        tg = c.call("tia_project_list", {"projectPath": "session:s-openness"})
        plcs = [t for t in tg if t.get("kind") == "Plc"] if isinstance(tg, list) else []
        print(f"[G11] project_list PLCs: {[p['name'] for p in plcs]}")
        if not plcs: fails.append("G11: no PLC found")
        plc = plcs[0]["path"] + "/plc:program"
        # G3: tag_list per-table filter
        tt = c.call("tia_tagtable_list", {"path": plc})
        tables = tt.get("tagTables", []) if isinstance(tt, dict) else []
        for t in tables:
            r = c.call("tia_tag_list", {"path": t["path"], "limit": 500})
            n = r.get("total") if isinstance(r, dict) else None
            print(f"[G3 ] tagtable:{t['name']} -> total={n} (expected {t.get('tagCount')})")
            if n != t.get("tagCount"): fails.append(f"G3: {t['name']} total {n} != {t.get('tagCount')}")
        # G13: export a previously-inconsistent block (AutoWorkCtrlStatus) -> should now succeed
        fbs = c.call("tia_block_list", {"path": plc, "type": "FB", "limit": 500}).get("blocks", [])
        aw = next((b for b in fbs if b["name"] == "AutoWorkCtrlStatus"), (fbs[0] if fbs else None))
        if aw:
            exp = c.call("tia_block_export", {"path": aw["path"], "format": "Xml"})
            ok13 = isinstance(exp, dict) and exp.get("filePath") and "_error" not in exp
            print(f"[G13] export {aw['name']} Xml -> {'OK ' + exp.get('filePath','') if ok13 else 'ERR ' + str(exp.get('_error'))[:120]}")
            if not ok13: fails.append(f"G13: export failed: {exp.get('_error')}")
    finally:
        c.close()
    print("\n" + ("LIVE CHECKS PASSED" if not fails else "FAILURES:"))
    for f in fails: print("  - " + f)
    return 0 if not fails else 1

if __name__ == "__main__":
    sys.exit(main())
