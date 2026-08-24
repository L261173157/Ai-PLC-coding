#!/usr/bin/env python3
"""Reliable stdio smoke test for the TiaMcp MCP server (P0-P4).

The MCP SDK dispatches requests concurrently, so responses may arrive OUT OF ORDER; this
client matches each response by `id` and buffers the rest.

Three scenarios, one per access tier:
  * ReadOnly    -> a write is blocked (Denied).
  * ReadWrite   -> block/tag writes + confirm + device items + download blocked (needs Unrestricted).
  * Unrestricted-> online connect/download/run/stop (confirm).

Usage:  python mcp/tests/smoke_mcp.py <path-to-dll>   (run from the repo root)
"""
import glob
import json
import os
import sys
import tempfile

from mcp_client import Client, paths

TEST_BLOCK_SOURCE = "FUNCTION_BLOCK \"FB_Test\"\nVERSION : 0.1\nBEGIN\nEND_FUNCTION_BLOCK"


def main() -> int:
    dll = sys.argv[1]

    print("########## ReadOnly: writes denied ##########")
    c = Client(dll, "fake", "ReadOnly")
    try:
        tools = c.initialize()
        print("  tools (" + str(len(tools)) + "):", ", ".join(t["name"] for t in tools))
        s = paths(c.call("tia_connect", {"mode": "headless"})["path"])
        r = c.call("tia_block_import", {"plcPath": s["plc"], "name": "FB_Test", "source": TEST_BLOCK_SOURCE, "type": "FB"})
        print("  block_import ->", r["status"])
        assert r["status"] == "Denied"
    finally:
        c.close()

    print("\n########## ReadWrite: writes + confirm + device + download-blocked ##########")
    c = Client(dll, "fake", "ReadWrite")
    try:
        c.initialize()
        s = paths(c.call("tia_connect", {"mode": "headless"})["path"])
        blk = s["plc"] + "/block:FB_Test"
        c.call("tia_block_import", {"plcPath": s["plc"], "name": "FB_Test", "source": TEST_BLOCK_SOURCE, "type": "FB"})
        # delete previews report dependents: seeded FB_Motor is called by FC_Stop, typed by DB_Motor,
        # declares UDT_MotorParams; the imported FB_Test is a leaf with no dependents.
        r = c.call("tia_block_delete", {"path": s["plc"] + "/block:FB_Motor"})
        deps = r.get("dependents") or []
        print("  FB_Motor dependents:", "; ".join(deps))
        assert any("FC_Stop" in d and "Call" in d for d in deps), deps
        assert any("DB_Motor" in d and "InstanceDB" in d for d in deps), deps
        assert "UDT_MotorParams" in r["plan"], r["plan"]
        assert c.call("tia_tag_delete", {"path": s["plc"] + "/tagtable:Default/tag:Start"})["dependents"] == [
            "FB_Motor (SCL-Function block): UsedBy/Read x1"]
        assert c.call("tia_block_delete", {"path": blk})["status"] == "AwaitingConfirmation"
        assert c.call("tia_block_delete", {"path": blk, "confirm": True})["status"] == "Applied"
        items = c.call("tia_device_item_list", {"path": s["device"]})
        print(f"  device items: {len(items)} ({', '.join(i['name'] for i in items)})")
        assert len(items) == 4
        r = c.call("tia_download", {"path": s["device"], "scope": "Software", "confirm": True})
        print("  download (ReadWrite) ->", r["status"], "|", r["message"])
        assert r["status"] == "Denied"  # needs Unrestricted
    finally:
        c.close()

    print("\n########## Unrestricted: online connect/download/run/stop ##########")
    c = Client(dll, "fake", "Unrestricted")
    try:
        c.initialize()
        s = paths(c.call("tia_connect", {"mode": "headless"})["path"])
        st = c.call("tia_online_status", {"path": s["device"]})
        print("  online_status  -> online=" + str(st["online"]), "state=" + st["plcState"])
        assert c.call("tia_online_connect", {"path": s["device"]})["status"] == "Applied"
        st = c.call("tia_online_status", {"path": s["device"]})
        print("  after connect  -> online=" + str(st["online"]), "state=" + st["plcState"])
        assert st["online"] is True
        assert c.call("tia_download", {"path": s["device"], "confirm": False})["status"] == "AwaitingConfirmation"
        assert c.call("tia_download", {"path": s["device"], "confirm": True})["status"] == "Applied"
        assert c.call("tia_plc_run", {"path": s["device"], "confirm": True})["status"] == "Applied"
        assert c.call("tia_online_status", {"path": s["device"]})["plcState"] == "Run"
        assert c.call("tia_plc_stop", {"path": s["device"], "confirm": True})["status"] == "Applied"
        assert c.call("tia_online_status", {"path": s["device"]})["plcState"] == "Stop"
        print("  run/stop state transitions OK")
    finally:
        c.close()

    print("\n########## audit log ##########")
    adir = os.path.join(tempfile.gettempdir(), "tiamcp-audit")
    latest = max(glob.glob(os.path.join(adir, "*.jsonl")), key=os.path.getmtime)
    ops = sorted({json.loads(l).get("op") for l in open(latest, encoding="utf-8") if l.strip()})
    print("  ops:", ops)
    assert "download" in ops and "online_connect" in ops

    print("\nOK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
