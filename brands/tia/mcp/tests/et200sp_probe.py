#!/usr/bin/env python3
"""LIVE probe: how does the worker handle an ET200SP CPU station (e.g. CPU 1510SP-1 PN,
6ES7 510-1DJ00)? Round-3 e2e_dev_flow accidentally picked one and saw: device_item_list empty,
/plc:program unresolvable - while network_configure DID find its PROFINET interface.

This probe isolates that case with raw dumps (informational; exit 0 always unless connect fails):
  add ET200SP CPU -> device_item_list RAW -> project_list targets -> hardware_read RAW
  -> try tag_create (PLC resolution error text) -> close without saving.

Usage: python -u brands/tia/mcp/tests/et200sp_probe.py <server.dll>
"""
import json
import os
import sys
import tempfile

from mcp_client import Client

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))))
SCRATCH = os.path.join(REPO_ROOT, "plc", "_scratch", "et200sp_probe")


def main() -> int:
    dll = sys.argv[1]
    os.makedirs(SCRATCH, exist_ok=True)
    c = Client(dll, "openness", "ReadWrite", client_name="et200sp-probe",
               stderr_path=os.path.join(tempfile.gettempdir(), "tiamcp_et200sp_stderr.log"))
    try:
        c.initialize()
        c.call("tia_status", timeout=120)
        sess = c.call("tia_connect", {"mode": "headless"}, timeout=400)
        sp = sess.get("path")
        print("connect:", sess.get("sessionId"), flush=True)
        if not sp:
            return 1
        c.call("tia_project_create", {"sessionPath": sp, "projectDirectory": SCRATCH,
                                      "projectName": "Et200Probe"}, timeout=300)
        e2e = sp + "/project:Et200Probe"
        cat = c.call("tia_catalog_search", {"scopePath": sp, "query": "6ES7 511-1AK00"}, timeout=120)
        items = cat if isinstance(cat, list) else (cat.get("results") or cat.get("items") or [])
        print(f"catalog hits: {len(items)}", flush=True)
        ti = next((e.get("typeIdentifier") for e in items if "1AK00" in e.get("articleNumber", "")), None) \
            or (items[0].get("typeIdentifier") if items else None)
        print("typeIdentifier:", ti, flush=True)
        if not ti:
            return 1
        dev = c.call("tia_device_add", {"projectPath": e2e, "typeIdentifier": ti,
                                        "deviceName": "PLC_ET2"}, timeout=300)
        print("device_add:", json.dumps(dev, ensure_ascii=False)[:300], flush=True)

        dpath = (dev.get("path") if isinstance(dev, dict) else None) or (e2e + "/device:PLC_ET2")
        raw = c.call("tia_device_item_list", {"path": dpath}, timeout=120)
        print("device_item_list RAW:", json.dumps(raw, ensure_ascii=False)[:1500], flush=True)

        tg = c.call("tia_project_list", {"projectPath": e2e}, timeout=120)
        print("project_list RAW:", json.dumps(tg, ensure_ascii=False)[:800], flush=True)

        hw = c.call("tia_hardware_read", {"projectPath": e2e}, timeout=180)
        print("hardware_read RAW:", json.dumps(hw, ensure_ascii=False)[:1200], flush=True)

        plc = dpath + "/plc:program"
        tag = c.call("tia_tag_create", {"tagTablePath": plc + "/tagtable:Default",
                                        "name": "ProbeTag", "address": "%I0.0"}, timeout=120)
        print("tag_create (PLC resolution):", json.dumps(tag, ensure_ascii=False)[:300], flush=True)

        bl = c.call("tia_block_list", {"path": plc, "limit": 10}, timeout=120)
        print("block_list:", json.dumps(bl, ensure_ascii=False)[:300], flush=True)

        c.call("tia_project_close", {"projectPath": e2e, "saveBeforeClose": False}, timeout=300)
    finally:
        try:
            c.call("tia_disconnect", timeout=120)
        except Exception:
            pass
        c.close()
    print("probe done (informational - judge from the RAW dumps above)", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
