#!/usr/bin/env python3
"""Live verify: tia_block_delete preview reports the block's dependents (callers / instance DBs).

    python brands/tia/mcp/tests/verify_delete_dependents.py [server.dll] [project.ap21]

With a .ap21 path: headless-spawn a Portal and open that project. Without: attach to a Portal the
user already opened (GUI must be up). Only its own scratch block (ZZ_VerifyLeaf) is created and
deleted — the project's real blocks are only ever PREVIEWED, never deleted:

  * DISCOVERY (2026-08-25): the FB under test is found from live cross-references instead of
    hardcoded names (an earlier revision assumed FB_CylManual/DB_ManA from a work project that
    McpTest doesn't contain, so its asserts failed with empty deps)
  * FB with callers + instance DBs  -> Dependents lists them, plan says "will break"
  * NONEXISTENT block               -> preview is Failed (product guard, 2026-08-25), never
                                       "safe to delete"
  * freshly generated scratch DB    -> "No dependents found" (preview AND applied message)
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

        blocks = c.call("tia_block_list", {"path": plc, "limit": 200}, timeout=120)

        # --- DISCOVERY: the positive cases must not hardcode project content (2026-08-25: this
        # script was written against a work project whose FB_CylManual/DB_ManA don't exist in
        # McpTest, so its asserts failed with empty deps). Find an FB that actually HAS callers
        # or instance DBs, and its instance DB, from the live cross-references. ---
        fbs = [b for b in blocks.get("blocks", []) if b.get("name", "").startswith("FB_")]
        target_fb = None
        fb_deps = []
        for b in fbs:
            xr = c.call("tia_cross_reference", {"path": plc + "/block:" + b["name"], "aggregate": True},
                        timeout=120)
            refs = xr.get("references") or []
            # aggregate entries carry counts = {"UsedBy/Call": n, "TypeInstance/InstanceDB": m, ...}
            interesting = [e for e in refs
                           if any("UsedBy" in k or "TypeInstance" in k for k in (e.get("counts") or {}))]
            if interesting:
                target_fb = b["name"]
                fb_deps = [e.get("name") for e in interesting]
                break
        print(f"discovered FB with dependents: {target_fb} <- {fb_deps}", flush=True)
        assert target_fb, "no FB in this project has callers/instance DBs - pick a richer project"

        # --- positive 1: previewing that FB must report exactly those dependents ---
        r = c.call("tia_block_delete", {"path": plc + "/block:" + target_fb}, timeout=120)
        assert r["status"] == "AwaitingConfirmation", r
        deps = r.get("dependents") or []
        print(f"preview {target_fb}:")
        print("  plan :", r["plan"])
        for d in deps:
            print("  dep  :", d)
        assert deps, "dependents missing although cross-references exist"
        assert all(any(n in d for d in deps) for n in fb_deps), f"xref names missing: {fb_deps} vs {deps}"
        assert "will break" in r["plan"] or "orphaned" in r["plan"], r["plan"]

        # --- positive 2: an instance DB of that FB (if any) previews with member access ---
        inst_db = next((d for d in deps if "InstanceDB" in d), None)
        if inst_db:
            db_name = inst_db.split(" (")[0]
            r2 = c.call("tia_block_delete", {"path": plc + "/block:" + db_name}, timeout=120)
            assert r2["status"] == "AwaitingConfirmation", r2
            deps2 = r2.get("dependents") or []
            print(f"preview {db_name}: {len(deps2)} dependent entries")
            assert deps2, f"instance DB {db_name} previewed as a leaf: {r2['plan']}"
        else:
            print("(no instance DB among dependents - DB preview leg skipped)", flush=True)

        # --- negative (product guard, fixed 2026-08-25): a NONEXISTENT block must preview as
        # Failed, never as AwaitingConfirmation with "safe to delete" ---
        nf = c.call("tia_block_delete", {"path": plc + "/block:ZZ_DefinitelyNotHere"}, timeout=120)
        print("preview nonexistent block:", str(nf)[:200], flush=True)
        assert nf.get("status") == "Failed" and "not found" in str(nf.get("message", "")).lower(), nf

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
        # Close the project before the Portal dies, or the next script opening this .ap21 hits
        # TIA's ~2-minute "not correctly closed" lock (live-verified 2026-08-25 suite cascade).
        try:
            if project:
                name = os.path.splitext(os.path.basename(project))[0]
                c.call("tia_project_close",
                       {"projectPath": "session:s-openness/project:" + name, "saveBeforeClose": False},
                       timeout=120)
        except Exception:
            pass
        c.close()


if __name__ == "__main__":
    sys.exit(main())
