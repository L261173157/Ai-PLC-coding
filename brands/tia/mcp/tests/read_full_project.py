#!/usr/bin/env python3
"""Full read-through of a real TIA project via the MCP server (ReadWrite), dumping every result
as pretty JSON. Phase A = architecture (hardware / full block+UDT+tag lists / CPU memory / all tags,
none of which compile); Phase B = per-block deep read (info / source / interface / xref), each call in
its own try/except with a consecutive-error breaker so a Portal crash on an inconsistent block cannot
abort the whole run.

Usage:
    python brands/tia/mcp/tests/read_full_project.py <server.dll> [<path-to.ap21>]
"""
import json
import os
import re
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.abspath(os.path.join(HERE, "..", "..", "..", ".."))  # brands/tia/mcp/tests -> repo root
# Generic placeholder — pass your own project explicitly as argument 2:
#   python read_full_project.py <server.dll> <path-to-your.ap21>
PROJECT_DEFAULT = os.path.join(REPO_ROOT, "plc", "_scratch", "MyProject", "MyProject.ap21")
FIRST_CALL_TIMEOUT = 240.0   # first call spawns worker + headless Portal + opens a large real project
CALL_TIMEOUT = 150.0
BLOCK_PAGE = 500
DRILL_BREAKER = 6            # consecutive per-block failures before aborting Phase B


class Client:
    def __init__(self, dll, backend="openness", mode="ReadWrite"):
        import tempfile
        self.err_path = os.path.join(tempfile.gettempdir(), "tiamcp_read_stderr.log")
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
                                       "clientInfo": {"name": "read-full-project", "version": "0.1"}})
        self._send("notifications/initialized", notification=True)
        return self._wait([i1])[i1]["result"]

    def call(self, name, args=None, timeout=CALL_TIMEOUT):
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
            return {"_error": f"non-JSON content: {txt[:300]!r}", "_raw": res}

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


def safe(name):
    return re.sub(r"[^A-Za-z0-9._-]", "_", str(name))


class Dumper:
    def __init__(self, out_root, client):
        self.out = out_root
        self.c = client
        self.errors = []
        self.consecutive = 0

    def save(self, rel, obj):
        path = os.path.join(self.out, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            json.dump(obj, f, ensure_ascii=False, indent=2, default=str)

    def one(self, tool, args, rel, timeout=CALL_TIMEOUT):
        """Call a tool, save its result, record errors. Returns the parsed result."""
        try:
            r = self.c.call(tool, args, timeout=timeout)
        except Exception as e:
            r = {"_error": f"{type(e).__name__}: {e}"}
        self.save(rel, r)
        if isinstance(r, dict) and "_error" in r:
            self.errors.append({"tool": tool, "path": args.get("path") or args, "error": r["_error"]})
            self.consecutive += 1
        else:
            self.consecutive = 0
        return r


def read_plc(d, plc):
    dev = plc["path"]
    plc_path = dev + "/plc:program"
    sub = f"devices/{safe(plc['name'])}"
    print(f"  -- PLC {plc['name']} ({dev})")

    # Phase A: architecture (no compile risk)
    d.one("tia_device_item_list", {"path": dev}, f"{sub}/device_items.json")
    d.one("tia_cpu_system_clock_memory", {"devicePath": dev}, f"{sub}/cpu_memory.json", timeout=CALL_TIMEOUT)  # G1 probe

    all_blocks = []
    offset = 0
    total = None
    while True:
        page = d.one("tia_block_list", {"path": plc_path, "limit": BLOCK_PAGE, "offset": offset},
                     f"{sub}/blocks/_page_{offset}.json")
        blocks = page.get("blocks", []) if isinstance(page, dict) else []
        total = page.get("total", total) if isinstance(page, dict) else total
        all_blocks.extend(blocks)
        if not blocks or (total is not None and offset + len(blocks) >= total):
            break
        offset += len(blocks)
    groups = sorted({b.get("groupPath", "") for b in all_blocks if b.get("groupPath")})
    by_type = {}
    for b in all_blocks:
        by_type.setdefault(b.get("type", "?"), []).append(b.get("name"))
    d.save(f"{sub}/blocks/_index.json",
           {"total": total, "count": len(all_blocks), "groups": groups, "by_type": {k: len(v) for k, v in by_type.items()}})

    d.one("tia_udt_list", {"path": plc_path}, f"{sub}/udts.json")

    tt = d.one("tia_tagtable_list", {"path": plc_path}, f"{sub}/tagtables.json")
    for table in (tt.get("tagTables", []) if isinstance(tt, dict) else []):
        d.one("tia_tag_list", {"path": table.get("path"), "limit": 500},
              f"{sub}/tags/{safe(table.get('name'))}.json")  # G3: per-table filter
    d.one("tia_tag_list", {"path": plc_path, "limit": 500}, f"{sub}/tags/_all.json")  # G3: all tables

    # Phase B: per-block deep read (source/interface may trigger a recovery compile)
    drilled = 0
    aborted = False
    for b in all_blocks:
        if d.consecutive >= DRILL_BREAKER:
            aborted = True
            d.errors.append({"tool": "_drilldown", "error": f"aborted after {DRILL_BREAKER} consecutive failures (Portal likely dead)"})
            break
        bp = b.get("path")
        name = b.get("name")
        btype = b.get("type")
        base = f"{sub}/blocks/block/{safe(name)}_{btype}"
        d.one("tia_block_info", {"path": bp}, f"{base}/info.json")
        d.one("tia_block_read_source", {"path": bp}, f"{base}/source.json")
        d.one("tia_interface_read", {"path": bp}, f"{base}/interface.json")
        d.one("tia_cross_reference", {"path": bp}, f"{base}/xref.json")
        # Structured code view for LAD/GRAPH bodies: boolean rungs / step sequences (+loop) are
        # far more digestible than the raw SimaticML source; SCL text lands in source.json anyway.
        lang = (b.get("language") or "").upper()
        if lang in ("LAD", "GRAPH"):
            d.one("tia_block_read_code", {"path": bp, "includeInterface": False}, f"{base}/code.json")
        drilled += 1
    print(f"     blocks={len(all_blocks)} groups={len(groups)} drilled={drilled}{(' [ABORTED]' if aborted else '')}")
    return {"name": plc["name"], "path": dev, "block_total": len(all_blocks),
            "by_type": {k: len(v) for k, v in by_type.items()}, "groups": groups,
            "tagtable_count": len(tt.get("tagTables", []) if isinstance(tt, dict) else []),
            "drilled": drilled, "drill_aborted": aborted}


def main() -> int:
    dll = sys.argv[1]
    project = sys.argv[2] if len(sys.argv) > 2 else PROJECT_DEFAULT
    if not os.path.isfile(project):
        sys.exit(f"Project not found: {project}\n  pass your .ap21 path as argument 2.")
    base = os.path.splitext(os.path.basename(project))[0]
    out_root = os.path.join(os.path.dirname(os.path.abspath(__file__)), "output", base)
    os.makedirs(out_root, exist_ok=True)

    print(f"########## Full project read: {project} ##########")
    print(f"  output -> {out_root}")
    c = Client(dll)
    d = Dumper(out_root, c)
    plc_summaries = []
    project_path = None
    t0 = time.time()
    try:
        d.save("initialize.json", c.initialize())
        st = c.call("tia_status", timeout=FIRST_CALL_TIMEOUT)
        d.save("tia_status.json", st)
        print(f"  tia_status: {st}")
        assert st.get("tiaVersion") == "V21", f"expected V21, got {st.get('tiaVersion')}"

        sess = c.call("tia_connect", {"mode": "headless"}, timeout=FIRST_CALL_TIMEOUT)
        d.save("tia_connect.json", sess)
        session_path = sess["path"]
        print(f"  connected: {session_path}")

        proj = c.call("tia_project_open", {"sessionPath": session_path, "path": project, "visible": False},
                      timeout=FIRST_CALL_TIMEOUT)
        d.save("project_open.json", proj)
        project_path = proj["path"]
        print(f"  opened: {project_path}")

        targets = c.call("tia_project_list", {"projectPath": project_path})
        d.save("project_list.json", targets)
        d.one("tia_project_status", {"projectPath": project_path}, "project_status.json")
        d.one("tia_hardware_read", {"projectPath": project_path}, "hardware.json")

        plcs = [t for t in targets if t.get("kind") == "Plc"]
        print(f"  PLC targets: {[p['name'] for p in plcs]}")
        for plc in plcs:
            plc_summaries.append(read_plc(d, plc))

        d.save("summary.json", {
            "project": project, "plcs": plc_summaries,
            "first_call_seconds": round(time.time() - t0, 1),
            "failed_calls": len(d.errors)})
    finally:
        d.save("errors.json", d.errors)
        # Close the project BEFORE killing the server: a Portal that dies holding a project leaves
        # TIA's "not correctly closed" lock, and the next script opening the same .ap21 fails for
        # ~2 minutes ("already been opened by user ... on computer ...").
        if project_path:
            try:
                c.call("tia_project_close", {"projectPath": project_path, "saveBeforeClose": False},
                       timeout=120)
            except Exception:
                pass
        try:
            with open(os.path.join(out_root, "worker_stderr.log"), "w", encoding="utf-8", errors="replace") as f:
                f.write(c.stderr_text())
        except Exception:
            pass
        c.close()

    print(f"\n  done in {round(time.time()-t0,1)}s; {len(d.errors)} failed call(s).")
    print(f"  summary: {json.dumps(plc_summaries, ensure_ascii=False)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
