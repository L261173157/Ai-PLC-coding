#!/usr/bin/env python3
"""Live verify: tia_block_delete preview reports the block's dependents (callers / instance DBs).

    python brands/tia/mcp/tests/verify_delete_dependents.py [server.dll] [project.ap21]

With a .ap21 path: headless-spawn a Portal and open that project. Without: attach to a Portal the
user already opened (GUI must be up). Only its own scratch block (ZZ_VerifyLeaf) is created and
deleted — the project's real blocks are only ever PREVIEWED, never deleted:

  * FB with callers + instance DBs  -> Dependents lists them, plan says "will break"
  * instance DB in active use       -> dependents include UsedBy/InstanceDB + member R/W
  * freshly generated scratch DB    -> "No dependents found" (preview AND applied message)
Also prints (informational) whether FB CylDoubleSol2Sen (FB411) is still present.
"""
import sys

from mcp_client import Client


def main() -> int:
    dll = sys.argv[1] if len(sys.argv) > 1 else \
        "brands/tia/mcp/src/TiaMcp.Server/bin/Debug/net10.0/TiaMcp.Server.dll"
    project = sys.argv[2] if len(sys.argv) > 2 else None

    import os
    import tempfile
    err_path = os.path.join(tempfile.gettempdir(), "tiamcp_verify_delete_deps_stderr.log")
    c = Client(dll, backend="openness", mode="ReadWrite", client_name="verify-delete-deps",
               stderr_path=err_path)
    c.initialize()
    try:
        if project:
            # Client timeout sits ABOVE the server-side Connect watchdog (300s): if the worker
            # wedges in a native call, the server kills it and returns its structured timeout
            # error — that response is what we want to see, not a client-side cutoff.
            s = c.call("tia_connect", {"mode": "headless"}, timeout=400)
            print("tia_connect(headless) ->", s.get("sessionId") or s, flush=True)
            if "sessionId" not in s:
                print(f"(stderr log: {err_path})", flush=True)
                return 1
            session = s["sessionId"]
            o = c.call("tia_project_open", {"path": project, "sessionPath": session}, timeout=300)
            print("tia_project_open ->", o.get("name") or o, flush=True)
        else:
            s = c.call("tia_connect", {"mode": "attach"}, timeout=120)
            print("tia_connect(attach) ->", s.get("sessionId") or s, flush=True)
            if "sessionId" not in s:
                print(f"(stderr log: {err_path})", flush=True)
                return 1
            session = s["sessionId"]

        targets = c.call("tia_project_list", {"projectPath": session}, timeout=120)
        device = next(t["path"] for t in targets if t["kind"] == "Plc")
        plc = device + "/plc:program"

        blocks = c.call("tia_block_list", {"path": plc}, timeout=120)
        fb411 = [b for b in blocks["blocks"] if b["name"] == "CylDoubleSol2Sen"]
        print(f"FB411 CylDoubleSol2Sen present: {bool(fb411)} (informational)", flush=True)

        # --- positive 1: FB_CylManual is called by OB1 (x5) and typed by 5 instance DBs ---
        r = c.call("tia_block_delete", {"path": plc + "/block:FB_CylManual"}, timeout=120)
        assert r["status"] == "AwaitingConfirmation", r
        deps = r.get("dependents") or []
        print("preview FB_CylManual:")
        print("  plan :", r["plan"])
        for d in deps:
            print("  dep  :", d)
        assert any("Main" in d and "Call" in d for d in deps), f"OB1 caller missing: {deps}"
        assert sum("InstanceDB" in d for d in deps) == 5, f"expected 5 instance DBs: {deps}"
        assert "will break" in r["plan"], r["plan"]

        # --- positive 2: DB_ManA is NOT a leaf — Main calls FB_CylManual through it and its
        # members are read/written; the report must surface those (UsedBy/InstanceDB + member R/W) ---
        r2 = c.call("tia_block_delete", {"path": plc + "/block:DB_ManA"}, timeout=120)
        assert r2["status"] == "AwaitingConfirmation", r2
        deps2 = r2.get("dependents") or []
        print("preview DB_ManA:", len(deps2), "dependent entries")
        assert any("Main" in d and "InstanceDB" in d for d in deps2), \
            f"Main UsedBy/InstanceDB missing: {deps2}"
        assert any("FB_CylManual" in d and "UsedBy" in d for d in deps2), \
            f"member-access UsedBy missing: {deps2}"

        # --- negative: a freshly generated scratch DB nobody references is a true leaf ---
        leaf = "ZZ_VerifyLeaf"
        g = c.call("tia_block_generate_from_source", {
            "plcPath": plc, "sourceName": leaf + ".scl",
            "sourceText": f'DATA_BLOCK "{leaf}"\n{{ S7_Optimized_Access := \'TRUE\' }}\n'
                          "VERSION : 0.1\nNON_RETAIN\n\nBEGIN\nEND_DATA_BLOCK",
        }, timeout=120)
        assert g["status"] == "Applied", g
        r3 = c.call("tia_block_delete", {"path": plc + f"/block:{leaf}"}, timeout=120)
        assert r3["status"] == "AwaitingConfirmation", r3
        assert (r3.get("dependents") or []) == [], r3
        assert "No dependents found" in r3["plan"], r3["plan"]
        print(f"preview {leaf}: no dependents, as expected")

        # --- cleanup: delete the scratch leaf for real (net-zero change to the project) ---
        d3 = c.call("tia_block_delete", {"path": plc + f"/block:{leaf}", "confirm": True}, timeout=120)
        assert d3["status"] == "Applied", d3
        assert "No dependents found" in d3["message"], d3
        print(f"deleted {leaf} (cleanup):", d3["message"])

        # --- tia_project_open accepts a bare project name (resolves the already-open project) ---
        proj_name = blocks["scopePath"].split("/project:")[1].split("/")[0]
        o2 = c.call("tia_project_open", {"path": proj_name, "sessionPath": session}, timeout=120)
        assert o2.get("name") == proj_name, o2
        print(f"project_open by bare name '{proj_name}' -> OK")

        # --- tag delete preview carries dependents from the tag's cross-references ---
        tags = c.call("tia_tag_list", {"path": plc, "limit": 20}, timeout=120)
        for t in tags.get("tags", [])[:3]:
            tp = c.call("tia_tag_delete", {"path": t["path"]}, timeout=120)
            assert tp["status"] == "AwaitingConfirmation", tp
            print(f"preview tag {t['name']}: {(tp.get('dependents') or ['no dependents'])[0]}")

        print("\nOK")
        return 0
    finally:
        c.close()


if __name__ == "__main__":
    sys.exit(main())
