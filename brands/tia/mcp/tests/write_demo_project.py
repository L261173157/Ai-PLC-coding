#!/usr/bin/env python3
"""Write-capability demo on the SCRATCH COPY of a real project (ReadWrite).

Proves the read->understand->write loop by CLONING an existing FB: export its SimaticML XML,
rename it to FB_DemoWrite with a fresh block number, import it into a new '99_DemoWrite' block
group, compile, read it back, and save. Nothing here touches the original template.

Usage:
    python brands/tia/mcp/tests/write_demo_project.py <server.dll> [<path-to.ap21>]
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
#   python write_demo_project.py <server.dll> <path-to-your.ap21>
PROJECT_DEFAULT = os.path.join(REPO_ROOT, "plc", "_scratch", "MyProject", "MyProject.ap21")
NEW_NAME = "FB_DemoWrite"
GROUP_NAME = "99_DemoWrite"
FIRST_CALL_TIMEOUT = 240.0
CALL_TIMEOUT = 180.0


class Client:
    def __init__(self, dll):
        import tempfile
        self.err_path = os.path.join(tempfile.gettempdir(), "tiamcp_write_stderr.log")
        self.err = open(self.err_path, "w", encoding="utf-8", errors="replace")
        self.p = subprocess.Popen(
            ["dotnet", dll, "--backend", "openness", "--mode", "ReadWrite"],
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
                                       "clientInfo": {"name": "write-demo", "version": "0.1"}})
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
            return {"_error": f"non-JSON content: {txt[:300]!r}"}

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


def ok(r):
    return isinstance(r, dict) and "_error" not in r


def main() -> int:
    dll = sys.argv[1]
    project = sys.argv[2] if len(sys.argv) > 2 else PROJECT_DEFAULT
    if not os.path.isfile(project):
        sys.exit(f"Project not found: {project}\n  pass your .ap21 path as argument 2.")
    base = os.path.splitext(os.path.basename(project))[0]
    out_root = os.path.join(os.path.dirname(os.path.abspath(__file__)), "output", base)
    export_dir = os.path.join(out_root, "_export")
    os.makedirs(export_dir, exist_ok=True)
    log = []

    def save(rel, obj):
        with open(os.path.join(out_root, rel), "w", encoding="utf-8") as f:
            json.dump(obj, f, ensure_ascii=False, indent=2, default=str)

    def step(label, r):
        good = ok(r)
        log.append({"step": label, "ok": good, "result": r})
        print(f"  [{'OK ' if good else 'ERR'}] {label}: " +
              (str(r)[:160].replace("\n", " ") if good else r.get("_error")))
        return r

    print(f"########## Write demo: clone an FB -> {NEW_NAME} (on scratch copy) ##########")
    c = Client(dll)
    t0 = time.time()
    try:
        c.initialize()
        c.call("tia_connect", {"mode": "headless"}, timeout=FIRST_CALL_TIMEOUT)
        proj = c.call("tia_project_open", {"sessionPath": "session:s-openness", "path": project, "visible": False},
                      timeout=FIRST_CALL_TIMEOUT)
        project_path = proj["path"]

        targets = c.call("tia_project_list", {"projectPath": project_path})
        plc = next((t for t in targets if t.get("kind") == "Plc"), None)
        if not plc:
            print("  no PLC target!"); return 1
        plc_path = plc["path"] + "/plc:program"
        print(f"  PLC: {plc['name']}")

        # 1) pick a model FB whose source reads cleanly + a free block number (max FB number + 1).
        # NOTE: tia_block_export throws EngineeringTargetInvocationException on checksum-inconsistent
        # blocks; tia_block_read_source returns the SAME SimaticML XML but recovers via a one-off
        # recompile, so it is the reliable way to obtain clone-able XML. Iterate until one reads OK.
        fbs = c.call("tia_block_list", {"path": plc_path, "type": "FB", "limit": 500})
        fb_blocks = fbs.get("blocks", []) if ok(fbs) else []
        if not fb_blocks:
            print("  no FB to clone!"); return 1
        max_num = max((b.get("number", 0) for b in fb_blocks), default=0)
        new_num = max_num + 1
        model, xml = None, None
        for b in fb_blocks:
            src = c.call("tia_block_read_source", {"path": b["path"]}, timeout=240.0)
            if ok(src) and src.get("source"):
                model, xml = b, src["source"]
                break
            step(f"read_source {b['name']} (inconsistent, skip)", src)
        if not model:
            print("  no FB source readable to clone!"); return 1
        print(f"  model FB: {model['name']} (#{model.get('number')})  -> clone as {NEW_NAME} (#{new_num})")
        step("read model source XML", {"bytes": len(xml)})

        # 2) clone-edit: rename <Name> and renumber <Number> (both unique element forms)
        orig_name, orig_num = model["name"], str(model.get("number"))
        before = xml
        xml = xml.replace(f"<Name>{orig_name}</Name>", f"<Name>{NEW_NAME}</Name>")
        xml = xml.replace(f"<Number>{orig_num}</Number>", f"<Number>{new_num}</Number>")
        edited_path = os.path.join(export_dir, NEW_NAME + ".xml")
        with open(edited_path, "w", encoding="utf-8") as f:
            f.write(xml)
        renamed = (f"<Name>{NEW_NAME}</Name>" in xml and f"<Name>{orig_name}</Name>" not in xml)
        renumbered = (f"<Number>{new_num}</Number>" in xml)
        step("edit XML (rename+renumber)", {"renamed": renamed, "renumbered": renumbered,
                                            "bytes": len(xml), "delta": len(xml) - len(before)})

        # 4) create the demo block group, then import the clone into it
        step("create group " + GROUP_NAME, c.call(
            "tia_group_create", {"plcPath": plc_path, "name": GROUP_NAME, "kind": "block"}))
        imp = step("import clone", c.call(
            "tia_block_import", {"plcPath": plc_path + "/blockgroup:" + GROUP_NAME,
                                 "name": NEW_NAME, "source": xml, "type": "FB"}))

        # 5) compile to verify the clone is valid
        comp = step("compile (Software)", c.call(
            "tia_project_compile", {"scopePath": plc_path, "mode": "Software"}, timeout=300.0))

        # 6) read the clone back
        new_path = plc_path + "/block:" + NEW_NAME
        info = step("read-back block_info", c.call("tia_block_info", {"path": new_path}))
        iface = step("read-back interface", c.call("tia_interface_read", {"path": new_path}))

        # 7) save the scratch copy (persist the demo block for GUI inspection)
        save_step = step("save project", c.call("tia_project_save", {"projectPath": project_path}))

        save("write_demo.json", {
            "project": project, "plc": plc["name"], "model": model,
            "clone": {"name": NEW_NAME, "number": new_num, "group": GROUP_NAME, "path": new_path},
            "compile_success": (comp.get("success") if ok(comp) else None),
            "readback_number": (info.get("number") if ok(info) else None),
            "saved_applied": (save_step.get("status") == "Applied"),
            "steps": log, "seconds": round(time.time() - t0, 1)})
    finally:
        c.close()
        try:
            with open(os.path.join(out_root, "write_stderr.log"), "w", encoding="utf-8", errors="replace") as f:
                f.write(c.stderr_text())
        except Exception:
            pass

    print(f"\n  done in {round(time.time()-t0,1)}s. See output/{base}/write_demo.json")
    return 0


if __name__ == "__main__":
    sys.exit(main())
