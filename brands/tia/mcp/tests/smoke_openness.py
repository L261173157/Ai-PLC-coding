#!/usr/bin/env python3
"""Real-TIA smoke: drives the MCP server with --backend openness against a live TIA V21.

Validates the full agent path that an agent dev tool would use:
    net10 MCP server  ->  BridgeBackend  ->  net48 worker  ->  Siemens.Engineering  ->  TIA Portal V21

Optional 2nd arg is a .ap1x project path; if given, it also opens it and lists devices/blocks.

Usage:
    python mcp/tests/smoke_openness.py <path-to-server-dll> [path-to.ap21]   (run from the repo root)

The worker + headless TIA Portal startup is slow (~30-90s on first launch), so the slow calls
(status / connect / project open) get generous explicit timeouts.
"""
import os
import sys
import tempfile

from mcp_client import Client


def main() -> int:
    dll = sys.argv[1]
    project = sys.argv[2] if len(sys.argv) > 2 else None

    print("########## Openness backend (real TIA V21) ##########")
    err_path = os.path.join(tempfile.gettempdir(), "tiamcp_openness_stderr.log")
    c = Client(dll, "openness", "ReadWrite", client_name="openness-smoke", stderr_path=err_path)
    proj_path = None
    try:
        tools = c.initialize()
        print(f"  initialized: {len(tools)} tools")

        print("  tia_status  ... (first call spawns the net48 worker; ~30-90s)")
        st = c.call("tia_status", timeout=120)
        print("    ->", st)
        assert st.get("tiaVersion") == "V21", f"expected V21, got {st.get('tiaVersion')}"

        print("  tia_connect (headless) ... (spawns the headless TIA Portal; ~30-90s)")
        sess = c.call("tia_connect", {"mode": "headless"}, timeout=300)
        print("    ->", sess)
        assert sess.get("sessionId") == "s-openness", f"connect failed: {sess}"
        session_path = sess["path"]
        print("  >>> bridge -> worker -> Siemens -> TIA Portal: CONNECTED")

        if project:
            print(f"  tia_project_open {project} ...")
            proj = c.call("tia_project_open", {"sessionPath": session_path, "path": project, "visible": False},
                          timeout=300)
            print("    ->", proj)
            proj_path = proj.get("path")
            if proj_path:
                tgts = c.call("tia_project_list", {"projectPath": proj_path}, timeout=120)
                for t in tgts:
                    print(f"    target: {t.get('name')}  kind={t.get('kind')}  type={t.get('typeIdentifier')}")
                # pick the first programmable PLC (a CPU), not an IO/HMI station
                plc_tgt = next((t for t in tgts if t.get("kind") == "Plc"), None)
                if not plc_tgt:
                    print("    !! no PLC (CPU) device in the project.")
                    print("       Add an S7-1200 or S7-1500 CPU (not an ET200MP station / HMI), save, close.")
                else:
                    plc = plc_tgt["path"] + "/plc:program"
                    blocks = c.call("tia_block_list", {"path": plc, "limit": 100}, timeout=120)
                    names = [b.get("name") for b in blocks.get("blocks", [])]
                    print("    blocks:", blocks.get("total"), names[:20])

                    # read the source of the first block
                    if names:
                        bp = plc + "/block:" + names[0]
                        src = c.call("tia_block_read_source", {"path": bp}, timeout=120)
                        if isinstance(src, dict) and "_error" in src:
                            print("    read_source:", src["_error"])
                        else:
                            text = src.get("source") if isinstance(src, dict) else str(src)
                            print(f"    read_source[{names[0]}]:")
                            for ln in (text or "").splitlines()[:20]:
                                print("      " + ln)
                            print("      ..." if (text or "").count("\n") >= 20 else "")

                    # compile the PLC software
                    comp = c.call("tia_project_compile", {"scopePath": plc, "mode": "Software"}, timeout=300)
                    if isinstance(comp, dict) and "_error" in comp:
                        print("    compile:", comp["_error"])
                    else:
                        print("    compile:", comp)
    finally:
        # Close the project before the server dies, or TIA keeps the "not correctly closed" lock
        # for ~2 minutes and the NEXT script opening this .ap21 fails (live-verified 2026-08-25).
        if proj_path:
            try:
                c.call("tia_project_close", {"projectPath": proj_path, "saveBeforeClose": False},
                       timeout=120)
            except Exception:
                pass
        c.close()
        err = c.stderr_text().strip()
        if err:
            print("\n--- server/worker stderr ---")
            print(err)

    print("\nOK — real-TIA path validated")
    return 0


if __name__ == "__main__":
    sys.exit(main())
