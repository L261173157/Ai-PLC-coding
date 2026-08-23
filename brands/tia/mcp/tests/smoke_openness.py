#!/usr/bin/env python3
"""Real-TIA smoke: drives the MCP server with --backend openness against a live TIA V21.

Validates the full agent path that an agent dev tool would use:
    net10 MCP server  ->  BridgeBackend  ->  net48 worker  ->  Siemens.Engineering  ->  TIA Portal V21

Optional 2nd arg is a .ap1x project path; if given, it also opens it and lists devices/blocks.

Usage:
    python mcp/tests/smoke_openness.py <path-to-server-dll> [path-to.ap21]   (run from the repo root)

The worker + headless TIA Portal startup is slow (~30-90s on first launch), so request timeouts
are generous (120s).
"""
import json
import subprocess
import sys
import time


class Client:
    def __init__(self, dll, backend, mode):
        import os
        import tempfile
        self.err_path = os.path.join(tempfile.gettempdir(), "tiamcp_openness_stderr.log")
        self.err = open(self.err_path, "w", encoding="utf-8", errors="replace")
        self.p = subprocess.Popen(
            ["dotnet", dll, "--backend", backend, "--mode", mode],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.err,
            text=True, encoding="utf-8")
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
        self.p.stdin.write(json.dumps(obj) + "\n")
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
            msg = json.loads(raw.strip())
            if "id" in msg:
                self._buf[msg["id"]] = msg
        return {i: self._buf[i] for i in ids}

    def initialize(self):
        i1 = self._send("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                                       "clientInfo": {"name": "openness-smoke", "version": "0.1"}})
        self._send("notifications/initialized", notification=True)
        r = self._wait([i1])
        return r[i1]["result"]

    def call(self, name, args=None, timeout=120.0):
        mid = self._send("tools/call", {"name": name, "arguments": args or {}})
        r = self._wait([mid], timeout=timeout)[mid]
        if "error" in r:
            return {"_error": r["error"]}
        res = r.get("result", {})
        if res.get("isError"):
            txt = res["content"][0].get("text", "") if res.get("content") else ""
            return {"_error": txt or res}
        if "content" in res:
            txt = res["content"][0].get("text", "") if res["content"] else ""
            try:
                return json.loads(txt)
            except Exception:
                return {"_error": f"non-JSON content: {txt!r}", "_raw": res}
        return res

    def close(self):
        try:
            self.p.stdin.close()
        finally:
            try:
                self.p.wait(timeout=10)
            except subprocess.TimeoutExpired:
                self.p.kill()
            try:
                self.err.close()
            except Exception:
                pass

    def stderr_text(self):
        try:
            with open(self.err_path, encoding="utf-8", errors="replace") as f:
                return f.read()
        except Exception:
            return ""


def main() -> int:
    dll = sys.argv[1]
    project = sys.argv[2] if len(sys.argv) > 2 else None

    print("########## Openness backend (real TIA V21) ##########")
    c = Client(dll, "openness", "ReadWrite")
    try:
        info = c.initialize()
        print("  initialized:", info.get("serverInfo", {}).get("name"), info.get("protocolVersion"))

        print("  tia_status  ... (first call spawns the net48 worker + headless TIA; ~30-90s)")
        st = c.call("tia_status")
        print("    ->", st)
        assert st.get("tiaVersion") == "V21", f"expected V21, got {st.get('tiaVersion')}"

        print("  tia_connect (headless) ...")
        sess = c.call("tia_connect", {"mode": "headless"})
        print("    ->", sess)
        assert sess.get("sessionId") == "s-openness", f"connect failed: {sess}"
        session_path = sess["path"]
        print("  >>> bridge -> worker -> Siemens -> TIA Portal: CONNECTED")

        if project:
            print(f"  tia_project_open {project} ...")
            proj = c.call("tia_project_open", {"sessionPath": session_path, "path": project, "visible": False})
            print("    ->", proj)
            proj_path = proj.get("path")
            if proj_path:
                tgts = c.call("tia_project_list", {"projectPath": proj_path})
                for t in tgts:
                    print(f"    target: {t.get('name')}  kind={t.get('kind')}  type={t.get('typeIdentifier')}")
                # pick the first programmable PLC (a CPU), not an IO/HMI station
                plc_tgt = next((t for t in tgts if t.get("kind") == "Plc"), None)
                if not plc_tgt:
                    print("    !! no PLC (CPU) device in the project.")
                    print("       Add an S7-1200 or S7-1500 CPU (not an ET200MP station / HMI), save, close.")
                else:
                    plc = plc_tgt["path"] + "/plc:program"
                    blocks = c.call("tia_block_list", {"path": plc, "limit": 100})
                    names = [b.get("name") for b in blocks.get("blocks", [])]
                    print("    blocks:", blocks.get("total"), names[:20])

                    # read the source of the first block
                    if names:
                        bp = plc + "/block:" + names[0]
                        src = c.call("tia_block_read_source", {"path": bp})
                        if isinstance(src, dict) and "_error" in src:
                            print("    read_source:", src["_error"])
                        else:
                            text = src.get("source") if isinstance(src, dict) else str(src)
                            print(f"    read_source[{names[0]}]:")
                            for ln in (text or "").splitlines()[:20]:
                                print("      " + ln)
                            print("      ..." if (text or "").count("\n") >= 20 else "")

                    # compile the PLC software
                    comp = c.call("tia_project_compile", {"scopePath": plc, "mode": "Software"})
                    if isinstance(comp, dict) and "_error" in comp:
                        print("    compile:", comp["_error"])
                    else:
                        print("    compile:", comp)
    finally:
        c.close()
        err = c.stderr_text().strip()
        if err:
            print("\n--- server/worker stderr ---")
            print(err)

    print("\nOK — real-TIA path validated")
    return 0


if __name__ == "__main__":
    sys.exit(main())
