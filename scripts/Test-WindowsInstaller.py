"""Inspect the MSI and verify its exact embedded payload without installing it.

Optional native app acceptance runs the files extracted from the MSI, with fresh
temporary user data. This does not substitute for installation lifecycle testing.
"""
import argparse
import ctypes
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import warnings

warnings.filterwarnings("ignore", category=DeprecationWarning, module=__name__)
import msilib

ROOT = Path(__file__).resolve().parents[1]


def read_cabinet(msi, output):
    api = ctypes.WinDLL("msi")
    db, view, record = ctypes.c_uint(), ctypes.c_uint(), ctypes.c_uint()

    def check(code):
        if code:
            raise OSError(code, "Cannot read embedded MSI cabinet")

    try:
        check(api.MsiOpenDatabaseW(str(msi), None, ctypes.byref(db)))
        check(api.MsiDatabaseOpenViewW(db, "SELECT `Data` FROM `_Streams` WHERE `Name` = 'GrimList.cab'", ctypes.byref(view)))
        check(api.MsiViewExecute(view, 0))
        check(api.MsiViewFetch(view, ctypes.byref(record)))
        with output.open("wb") as stream:
            buffer = ctypes.create_string_buffer(1024 * 1024)
            while True:
                size = ctypes.c_uint(len(buffer))
                check(api.MsiRecordReadStream(record, 1, buffer, ctypes.byref(size)))
                if not size.value:
                    break
                stream.write(buffer.raw[:size.value])
    finally:
        for handle in (record, view, db):
            if handle.value:
                api.MsiCloseHandle(handle)


def verify(args, test_root):
    msi = args.msi.resolve()
    report = json.loads(msi.with_suffix(".inventory.json").read_text(encoding="utf-8"))
    evidence = args.evidence.resolve()
    evidence.mkdir(parents=True, exist_ok=True)
    results = {"installer": str(msi), "root": str(test_root), "checks": [], "installationLifecycle": "Not run by this test"}

    def check(name, condition=True):
        if not condition:
            raise AssertionError(name)
        results["checks"].append(name)
        print("PASS " + name, flush=True)
        (evidence / "results.json").write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8")

    check("Installer matches recorded SHA-256", hashlib.sha256(msi.read_bytes()).hexdigest() == report["installerSha256"])
    db = msilib.OpenDatabase(str(msi), msilib.MSIDBOPEN_READONLY)

    def rows(sql):
        view = db.OpenView(sql)
        view.Execute(None)
        result = []
        while (record := view.Fetch()) is not None:
            result.append(tuple(record.GetString(i) for i in range(1, record.GetFieldCount() + 1)))
        view.Close()
        return result

    props = dict(rows("SELECT `Property`, `Value` FROM `Property`"))
    check("Per-user installation without elevation", "ALLUSERS" not in props and db.GetSummaryInformation(0).GetProperty(msilib.PID_WORDCOUNT) == 10)
    check("x64 installer", db.GetSummaryInformation(0).GetProperty(msilib.PID_TEMPLATE) == b"x64;1033")
    check("Product identity and version match inventory", props["ProductCode"] == report["productCode"] and props["ProductVersion"] == report["version"])
    check("One embedded cabinet, no external download sources", rows("SELECT `Cabinet` FROM `Media`") == [("#GrimList.cab",)])
    tables = {r[0] for r in rows("SELECT `Name` FROM `_Tables`")}
    validated = 0
    for table in sorted(tables):
        if table.startswith("_"):
            continue
        view = db.OpenView(f"SELECT * FROM `{table}`")
        view.Execute(None)
        while (record := view.Fetch()) is not None:
            view.Modify(msilib.MSIMODIFY_VALIDATE, record)
            validated += 1
        view.Close()
    check(f"Windows Installer validates all {validated} authored table rows")
    check("No custom executable actions", "CustomAction" not in tables or not rows("SELECT `Action` FROM `CustomAction`"))
    files = rows("SELECT `File`, `FileName` FROM `File`")
    check("Exact payload file count", len(files) == len(report["files"]))
    directory_rows = rows("SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`")
    directories = {r[0]: r[1:] for r in directory_rows}
    check("Program directory is separate from user data", directories["INSTALLDIR"][0] == "UserPrograms" and directories["UserPrograms"] == ("LocalAppDataFolder", "Programs"))
    check("Uninstall only removes empty owned folders", all(r[0] == "" and r[1] not in {"LocalAppDataFolder", "UserPrograms", "DesktopFolder", "ProgramMenuFolder"} for r in rows("SELECT `FileName`, `DirProperty` FROM `RemoveFile`")))
    check("Installer registry writes use only its own HKCU markers", all(r[0] == "1" and r[1].startswith("Software\\GrimList\\Installer\\") for r in rows("SELECT `Root`, `Key` FROM `Registry`")))
    check("Start menu and desktop shortcuts present", {r[0] for r in rows("SELECT `Directory_` FROM `Shortcut`")} == {"SHORTCUTDIR", "DesktopFolder"})
    sequence = {r[0]: int(r[1]) for r in rows("SELECT `Action`, `Sequence` FROM `InstallExecuteSequence`")}
    check("Major upgrades remove old app inside the transaction", sequence["InstallInitialize"] < sequence["RemoveExistingProducts"] < sequence["ProcessComponents"])
    check("Both older-version upgrade and newer-version detection authored", len(rows("SELECT `UpgradeCode` FROM `Upgrade`")) == 2)
    cabinet = test_root / "payload.cab"
    raw, app = test_root / "raw", test_root / "app"
    raw.mkdir()
    app.mkdir()
    read_cabinet(msi, cabinet)
    extracted = subprocess.run(["expand.exe", str(cabinet), "-F:*", str(raw)], capture_output=True, text=True)
    (evidence / "cabinet-extraction.txt").write_text(extracted.stdout + extracted.stderr, encoding="utf-8")
    check("Native Windows cabinet extraction succeeds", extracted.returncode == 0)
    for item in report["files"]:
        relative = Path(item["path"])
        if relative.is_absolute() or ".." in relative.parts:
            raise AssertionError("Unsafe inventory path")
        fid = "F_" + hashlib.sha256(item["path"].lower().encode()).hexdigest()[:32]
        source = raw / fid
        assert source.stat().st_size == item["bytes"], item["path"]
        assert hashlib.sha256(source.read_bytes()).hexdigest() == item["sha256"], item["path"]
        destination = app / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        source.rename(destination)
    check("Every embedded file is byte-identical to the published app")
    check("Cabinet contains no additional files", not any(raw.iterdir()))
    check("No user databases or development scripts packaged", not any(p.suffix.lower() in {".db", ".sqlite", ".ps1", ".py", ".cs", ".pdb"} for p in app.rglob("*")))
    if args.run_app_acceptance:
        subprocess.run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(ROOT / "scripts/Test-StandaloneWindows.ps1"),
                        "-PublishedDirectory", str(app), "-EvidenceDirectory", str(evidence / "app"), "-UsePublishedDirectory"], check=True)
        check("MSI-extracted app passes native offline first-run/save/reopen acceptance")
    print(f"Evidence: {evidence}\nTemporary extracted payload will be removed.", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--msi", required=True, type=Path)
    parser.add_argument("--evidence", required=True, type=Path)
    parser.add_argument("--run-app-acceptance", action="store_true")
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="GrimList-MSI-Payload-") as temporary:
        verify(args, Path(temporary).resolve())
