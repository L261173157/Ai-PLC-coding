#!/usr/bin/env python3
"""Offline verification of the G1/G3/G9 fixes against the Fake backend in ReadOnly mode.

G1: tia_cpu_system_clock_memory READ (all params omitted) must be ALLOWED in ReadOnly;
    a WRITE (any param set) must still be DENIED in ReadOnly.
G3: tia_tag_list must honor a `tagtable:NAME` scope segment (Default -> tags, NoSuch -> empty).
G9: tia_project_compile must be DENIED in ReadOnly (returns a failed CompileResult, ACCESS code).

Usage:
    python brands/tia/mcp/tests/verify_fixes.py <path-to-server-dll>
"""
import sys

from mcp_client import Client


def main() -> int:
    dll = sys.argv[1]
    dev = "session:s-fake/project:Demo/device:PLC_1"
    plc = dev + "/plc:program"
    failures = []

    c = Client(dll)
    try:
        c.initialize()
        c.call("tia_connect", {"mode": "headless"})

        # ---- G1: read allowed in ReadOnly, write denied ----
        rd = c.call("tia_cpu_system_clock_memory", {"devicePath": dev})
        rd_status = rd.get("status") if isinstance(rd, dict) else None
        print(f"[G1 read ] status={rd_status!r}  msg={(rd or {}).get('message')!r}")
        if rd_status != "Applied":
            failures.append(f"G1 read not Applied (got {rd_status!r}): {rd}")

        wr = c.call("tia_cpu_system_clock_memory",
                    {"devicePath": dev, "enableSystemMemory": True})
        wr_status = wr.get("status") if isinstance(wr, dict) else None
        print(f"[G1 write] status={wr_status!r}  msg={(wr or {}).get('message')!r}")
        if wr_status != "Denied":
            failures.append(f"G1 write not Denied in ReadOnly (got {wr_status!r}): {wr}")

        # ---- G9: compile denied in ReadOnly -> failed CompileResult with ACCESS code ----
        comp = c.call("tia_project_compile", {"scopePath": plc, "mode": "Software"})
        ok9 = (isinstance(comp, dict) and comp.get("success") is False
               and any((d.get("code") == "ACCESS") for d in comp.get("diagnostics", [])))
        print(f"[G9 comp ] success={comp.get('success')!r}  diag0={(comp.get('diagnostics') or [{}])[0]}")
        if not ok9:
            failures.append(f"G9 compile not denied as failed CompileResult: {comp}")

        # ---- G3: tagtable filter ----
        good = c.call("tia_tag_list", {"path": plc + "/tagtable:Default"})
        bad = c.call("tia_tag_list", {"path": plc + "/tagtable:NoSuch"})
        gtot = good.get("total") if isinstance(good, dict) else None
        btot = bad.get("total") if isinstance(bad, dict) else None
        print(f"[G3 tags ] Default.total={gtot!r}  NoSuch.total={btot!r}")
        if not (isinstance(gtot, int) and gtot > 0):
            failures.append(f"G3 Default table not non-empty (got total={gtot!r}): {good}")
        if btot != 0:
            failures.append(f"G3 NoSuch table not empty (got total={btot!r}): {bad}")
    finally:
        c.close()

    print("\n" + ("ALL CHECKS PASSED" if not failures else "FAILURES:"))
    for f in failures:
        print("  - " + f)
    return 0 if not failures else 1


if __name__ == "__main__":
    sys.exit(main())
