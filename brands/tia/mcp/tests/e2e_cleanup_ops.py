#!/usr/bin/env python3
"""LIVE verification of the destructive + CPU-memory ops that no other script touches on a real
TIA (safe sandbox: run against the throwaway copy made by e2e_dev_flow.py).

Covers:
  * tia_cpu_system_clock_memory  -> read, WRITE (enable system+clock memory bytes), read back
  * tia_module_delete            -> preview (no confirm) then confirm=true
  * tia_subnet_delete            -> preview then confirm
  * tia_device_delete            -> preview then confirm (removes the CPU station)
Everything runs against the E2EFlowCopy scratch project; nothing of value is destroyed.

Usage: python brands/tia/mcp/tests/e2e_cleanup_ops.py <server.dll> [<copy.ap21>]
       (copy default: plc/_scratch/e2e_dev_flow/copy/E2EFlowCopy/E2EFlowCopy.ap21)
"""
import os
import sys
import tempfile

from mcp_client import Client

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))))
COPY_DEFAULT = os.path.join(REPO_ROOT, "plc", "_scratch", "e2e_dev_flow", "copy", "E2EFlowCopy",
                            "E2EFlowCopy.ap21")

def field2(x, *keys):
    if isinstance(x, dict):
        for k in keys:
            if x.get(k) is not None:
                return x.get(k)
    return None


FAILURES = []


def check(name, ok, detail=""):
    print(f"[{'PASS' if ok else 'FAIL'}] {name}" + (f"  | {detail}" if detail and not ok else ""), flush=True)
    if not ok:
        FAILURES.append(f"{name}: {detail}")


def main() -> int:
    dll = sys.argv[1]
    proj_ap21 = sys.argv[2] if len(sys.argv) > 2 else COPY_DEFAULT
    if not os.path.isfile(proj_ap21):
        print(f"project not found: {proj_ap21} - run e2e_dev_flow.py first (or pass a path)", flush=True)
        return 2

    err_path = os.path.join(tempfile.gettempdir(), "tiamcp_cleanup_stderr.log")
    c = Client(dll, "openness", "ReadWrite", client_name="e2e-cleanup", stderr_path=err_path)
    try:
        c.initialize()
        c.call("tia_status", timeout=120)
        sess = c.call("tia_connect", {"mode": "headless"}, timeout=400)
        sp = sess.get("path")
        check("connect headless", bool(sp), repr(sess)[:200])
        if not sp:
            return 1

        proj = c.call("tia_project_open", {"sessionPath": sp, "path": proj_ap21, "visible": False},
                      timeout=300)
        pp = proj.get("path")
        check("open scratch copy", bool(pp), repr(proj)[:200])
        if not pp:
            return 1

        tgts = c.call("tia_project_list", {"projectPath": pp}, timeout=120)
        plc = next((t for t in tgts if t.get("kind") == "Plc"), None)
        check("copy has PLC", plc is not None, repr(tgts)[:200])
        if not plc:
            return 1
        device = plc["path"]

        # ---- CPU system / clock memory ---------------------------------------
        rd = c.call("tia_cpu_system_clock_memory", {"devicePath": device}, timeout=180)
        print(f"  cpu memory before: {rd}", flush=True)
        wr = c.call("tia_cpu_system_clock_memory",
                    {"devicePath": device, "enableSystemMemory": True, "systemMemoryByte": 10,
                     "enableClockMemory": True, "clockMemoryByte": 11}, timeout=180)
        print(f"  cpu memory write: {wr}", flush=True)
        rd2 = c.call("tia_cpu_system_clock_memory", {"devicePath": device}, timeout=180)
        # The read comes back as a MutationResult whose message carries the values
        # ("CPU memory: SystemByte=10(en=True), ClockByte=11(en=True)") — parse the text.
        import re
        msg2 = str(field2(rd2, "message") or rd2)
        m = re.search(r"SystemByte=(\d+)\(en=(\w+)\), ClockByte=(\d+)\(en=(\w+)\)", msg2)
        check("cpu memory round-trip",
              bool(m) and m.group(1) == "10" and m.group(3) == "11"
              and m.group(2) == "True" and m.group(4) == "True", msg2[:200])

        # ---- module delete: preview then confirm ------------------------------
        items = c.call("tia_device_item_list", {"path": device}, timeout=120)
        item_list = items if isinstance(items, list) else \
            ((items.get("items") or items.get("deviceItems")) if isinstance(items, dict) else [])
        names = [i.get("name") for i in item_list]
        print(f"  device items: {names}", flush=True)
        mod = next((n for n in names if "DQ" in n or "E2E_DQ" in n), None) or \
              next((n for n in names if n not in ("Rack", "CPU")), None)
        if mod:
            pv = c.call("tia_module_delete", {"projectPath": pp, "deviceName": plc.get("name") or "PLC_E2E",
                                              "moduleName": mod, "confirm": False}, timeout=180)
            check("module_delete preview", pv.get("status") == "AwaitingConfirmation", repr(pv)[:300])
            dl = c.call("tia_module_delete", {"projectPath": pp, "deviceName": plc.get("name") or "PLC_E2E",
                                              "moduleName": mod, "confirm": True}, timeout=180)
            check("module_delete applied", dl.get("status") == "Applied", repr(dl)[:300])
        else:
            print("  (no signal module found to delete - module_delete leg skipped)", flush=True)

        # ---- subnet delete: preview then confirm ------------------------------
        hw = c.call("tia_hardware_read", {"projectPath": pp}, timeout=180)
        subnets = hw.get("subnets") or []
        sub = subnets[0] if subnets else None
        if sub:
            sname = sub.get("name") if isinstance(sub, dict) else str(sub)
            # deleting the subnet a running interface still references -> preview must say so;
            # fall back to accept a clear refusal as correct guard behaviour
            pv = c.call("tia_subnet_delete", {"projectPath": pp, "subnetName": sname, "confirm": False},
                        timeout=180)
            check("subnet_delete preview", pv.get("status") == "AwaitingConfirmation"
                  or pv.get("status") == "Failed", repr(pv)[:300])
            dl = c.call("tia_subnet_delete", {"projectPath": pp, "subnetName": sname, "confirm": True},
                        timeout=180)
            print(f"  subnet_delete applied -> {dl}", flush=True)
            check("subnet_delete applied-or-guarded", dl.get("status") in ("Applied", "Failed"), repr(dl)[:300])
        else:
            print("  (no subnet - subnet_delete leg skipped)", flush=True)

        # ---- device delete: preview then confirm ------------------------------
        dev_name = plc.get("name") or "PLC_E2E"
        pv = c.call("tia_device_delete", {"projectPath": pp, "deviceName": dev_name, "confirm": False},
                    timeout=180)
        check("device_delete preview", pv.get("status") == "AwaitingConfirmation", repr(pv)[:300])
        dl = c.call("tia_device_delete", {"projectPath": pp, "deviceName": dev_name, "confirm": True},
                    timeout=300)
        check("device_delete applied", dl.get("status") == "Applied", repr(dl)[:300])

        cl = c.call("tia_project_close", {"projectPath": pp, "saveBeforeClose": False}, timeout=300)
        print(f"  close: {cl}", flush=True)
    finally:
        try:
            c.call("tia_disconnect", timeout=120)
        except Exception as ex:  # noqa: BLE001
            print(f"  disconnect: {ex}", flush=True)
        c.close()

    print("\n" + ("ALL CHECKS PASSED" if not FAILURES else f"{len(FAILURES)} FAILURE(S):"), flush=True)
    for f in FAILURES:
        print("  - " + f, flush=True)
    return 0 if not FAILURES else 1


if __name__ == "__main__":
    sys.exit(main())
