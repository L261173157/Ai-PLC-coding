#!/usr/bin/env python3
"""Smoke test for the 2026-07-03 additions: tia_disconnect + tia_tagtable_export (Fake backend).

Verifies, over stdio MCP against the Fake backend:
  * both new tools are registered (tools/list);
  * tia_status still works (TiaAvailable is Fake's own value);
  * tia_tagtable_export writes a <Name>.xml file (Fake simulates the export);
  * tia_disconnect returns a disconnected status without error.

Usage:  python brands/tia/mcp/tests/smoke_new_tools.py <path-to-dll>
"""
import os
import sys
import tempfile

from mcp_client import Client


def main() -> int:
    dll = sys.argv[1]
    c = Client(dll, "fake", "ReadWrite")
    try:
        tools = c.initialize()
        names = {t["name"] for t in tools}
        print("  tools:", len(tools), "total")
        for want in ("tia_disconnect", "tia_tagtable_export"):
            assert want in names, f"tool {want} not registered"
            print(f"  registered: {want}")

        session = c.call("tia_connect", {"mode": "headless"})["path"]
        plc = f"{session}/project:Demo/device:PLC_1/plc:program"
        table_path = f"{plc}/tagtable:Default"

        st = c.call("tia_status", {})
        print("  status:", st)
        assert st["backend"] == "Fake"

        out = tempfile.mkdtemp(prefix="tiamcp-newtools-")
        exp = c.call("tia_tagtable_export", {"path": table_path, "outDir": out})
        print("  tagtable_export ->", exp)
        assert exp["format"] == "Xml"
        assert os.path.isfile(exp["filePath"]), "export file not written"
        assert os.path.getsize(exp["filePath"]) > 0
        print("  wrote", exp["filePath"], "(%d bytes)" % exp["bytes"])

        disc = c.call("tia_disconnect", {})
        print("  disconnect ->", disc)
        assert disc["status"] == "disconnected"
    finally:
        c.close()

    print("\nOK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
