"""Build a single offline, per-user x64 MSI using Windows' native cabinet/MSI APIs.

Build dependency: 64-bit CPython 3.12 (msilib); no Python is included in the MSI.
"""
from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import uuid
import warnings
import xml.etree.ElementTree as ET

warnings.filterwarnings("ignore", category=DeprecationWarning, module=__name__)
try:
    import msilib
    from msilib import schema
except ImportError:
    raise SystemExit("Run this build script with 64-bit Windows CPython 3.12 (includes msilib).")

ROOT = Path(__file__).resolve().parents[1]
NAMESPACE = uuid.UUID("da8ffde8-0725-4424-b09e-69074a5fbabe")


def guid(name):
    return "{" + str(uuid.uuid5(NAMESPACE, name)).upper() + "}"


def identifier(prefix, path):
    return prefix + hashlib.sha256(str(path).lower().encode()).hexdigest()[:32]


def file_metadata(path):
    api = ctypes.WinDLL("msi")
    version, language = ctypes.create_unicode_buffer(256), ctypes.create_unicode_buffer(256)
    nv, nl = ctypes.c_uint(256), ctypes.c_uint(256)
    code = api.MsiGetFileVersionW(str(path), version, ctypes.byref(nv), language, ctypes.byref(nl))
    if code == 0 and version.value:
        return version.value, language.value or None, None
    if code not in (0, 1006, 1812, 1813):
        raise OSError(code, f"Cannot read file version: {path}")

    class Hash(ctypes.Structure):
        _fields_ = [("size", ctypes.c_uint32), ("parts", ctypes.c_int32 * 4)]

    value = Hash()
    value.size = ctypes.sizeof(value)
    code = api.MsiGetFileHashW(str(path), 0, ctypes.byref(value))
    if code:
        raise OSError(code, f"Cannot hash {path}")
    return None, None, tuple(value.parts)


def ui(db):
    add = lambda table, rows: msilib.add_data(db, table, rows)
    add("TextStyle", [("Normal", "Segoe UI", 9, None, 0), ("Title", "Segoe UI", 15, None, 1)])

    def dialog(name, heading, body, first="Finish", cancel="Finish", attributes=3):
        d = msilib.Dialog(db, name, 50, 50, 370, 240, attributes,
                          "[ProductName] Setup", first, first, cancel)
        d.text("Heading", 20, 15, 330, 28, 3, "{\\Title}" + heading)
        d.text("Body", 20, 55, 330, 120, 3, body)
        d.line("Separator", 15, 198, 340, 1)
        return d

    d = dialog("WelcomeDlg", "Install [ProductName]", "GrimList works offline. The app, its runtime, and the starting library are included.\n\nInstall for your Windows account:\n[INSTALLDIR]\n\nYour armies and settings are stored separately and kept when you uninstall.", "Install", "Cancel")
    d.pushbutton("Install", 190, 210, 75, 18, 3, "&Install", "Cancel").event("EndDialog", "Return")
    d.pushbutton("Cancel", 275, 210, 75, 18, 3, "Cancel", "Install").event("EndDialog", "Exit")
    d = dialog("MaintenanceDlg", "Maintain [ProductName]", "Repair reinstalls the program files. Remove uninstalls the program and its shortcuts.\n\nYour armies and settings are kept in either case. Close GrimList before continuing.", "Repair", "Cancel")
    b = d.pushbutton("Repair", 105, 210, 75, 18, 3, "&Repair", "Remove")
    b.event("[REINSTALL]", "ALL", ordering=1)
    b.event("[REINSTALLMODE]", "amus", ordering=2)
    b.event("EndDialog", "Return", ordering=3)
    b = d.pushbutton("Remove", 190, 210, 75, 18, 3, "&Remove", "Cancel")
    b.event("[REMOVE]", "ALL", ordering=1)
    b.event("EndDialog", "Return", ordering=2)
    d.pushbutton("Cancel", 275, 210, 75, 18, 3, "Cancel", "Repair").event("EndDialog", "Exit")
    d = dialog("ProgressDlg", "Installing [ProductName]", "Please wait while Windows updates the program files.", "Status", "Status", 1)
    d.text("Status", 20, 115, 330, 35, 3, "Preparing...").mapping("ActionText", "Text")
    d.control("Progress", "ProgressBar", 20, 165, 330, 15, 65537, None, None, None, None).mapping("SetProgress", "Progress")
    for name, heading, body in [
        ("ExitDialog", "Setup complete", "Setup completed successfully.\n\nIf you installed GrimList, open it from the Start menu or desktop shortcut. After removal, your saved armies remain in your user-data folder."),
        ("FatalError", "Setup could not finish", "Windows could not complete setup. Close GrimList and try again.\n\nFor a diagnostic log, run:\nmsiexec /i package.msi /L*V setup.log"),
        ("UserExit", "Setup cancelled", "Setup was cancelled. Your saved armies and settings have not been removed."),
    ]:
        d = dialog(name, heading, body)
        d.pushbutton("Finish", 275, 210, 75, 18, 3, "&Finish", "Finish").event("EndDialog", "Return")


def build(args):
    if sys.maxsize <= 2**32 or not msilib.Win64:
        raise SystemExit("The installer builder requires 64-bit Windows Python.")
    version = args.version or ET.parse(ROOT / "Directory.Build.props").findtext(".//Version")
    if not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise SystemExit("MSI version must have three numeric parts.")
    major, minor, patch = map(int, version.split("."))
    if major > 255 or minor > 255 or patch > 65535:
        raise SystemExit("Version exceeds Windows Installer version limits.")
    source = (args.source or ROOT / "artifacts/builds/windows" / version).resolve()
    for required in ["GrimList.exe", "GrimList.Application.dll", "runtime/host/fxr"]:
        if not (source / required).exists():
            raise SystemExit(f"Missing standalone payload: {source / required}")
    payload = sorted(p for p in source.rglob("*") if p.is_file() and p.suffix.lower() != ".pdb")
    forbidden = {".local", "artifacts", "tools", "scripts", "tests", "reports", "obj", "bin", ".git"}
    for path in payload:
        relative = path.relative_to(source)
        if path.is_symlink() or forbidden.intersection(s.lower() for s in relative.parts) or path.suffix.lower() in {".db", ".sqlite", ".sqlite3", ".py", ".ps1", ".cs", ".apk"}:
            raise SystemExit(f"Development/user material cannot be packaged: {relative}")
    identity = "installer-test" if args.test_identity else "production"
    name = "GrimList Installer Test" if args.test_identity else "GrimList"
    code = guid(f"{identity}/product/{version}")
    upgrade = guid(f"{identity}/upgrade")
    output = args.output or ROOT / "artifacts/installers/windows" / version / f"{name.replace(' ', '-')}-{version}-win-x64.msi"
    output = output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    db = msilib.init_database(str(output), schema, name, code, version, "GrimList")
    add = lambda table, rows: msilib.add_data(db, table, rows)
    summary = db.GetSummaryInformation(20)
    summary.SetProperty(msilib.PID_TEMPLATE, "x64;1033")
    summary.SetProperty(msilib.PID_WORDCOUNT, 10)  # compressed, no elevation
    summary.SetProperty(msilib.PID_PAGECOUNT, 500)
    summary.SetProperty(msilib.PID_APPNAME, "GrimList installer builder")
    summary.Persist()
    del summary
    add("Property", [("UpgradeCode", upgrade), ("DefaultUIFont", "Normal"), ("INSTALLLEVEL", "1"),
        ("ARPPRODUCTICON", "AppIcon"), ("ARPNOMODIFY", "1"), ("MSIRESTARTMANAGERCONTROL", "Disable"),
        ("SecureCustomProperties", "GRIMLIST_OLDVERSIONS;GRIMLIST_NEWER"),
        ("ARPCOMMENTS", "Offline army builder. Uninstall keeps armies and settings.")])
    # ALLUSERS is deliberately absent: installation belongs to the current user.
    add("Directory", [("TARGETDIR", None, "SourceDir"), ("LocalAppDataFolder", "TARGETDIR", "."),
        ("UserPrograms", "LocalAppDataFolder", "Programs"), ("INSTALLDIR", "UserPrograms", "GRIMLIST|" + name),
        ("ProgramMenuFolder", "TARGETDIR", "."), ("SHORTCUTDIR", "ProgramMenuFolder", "GRIMLIST|" + name),
        ("DesktopFolder", "TARGETDIR", ".")])
    directories = {Path("."): "INSTALLDIR"}
    parents = sorted({p.parent.relative_to(source) for p in payload}, key=lambda p: (len(p.parts), str(p)))
    for parent in parents:
        for i in range(1, len(parent.parts) + 1):
            relative = Path(*parent.parts[:i])
            if relative not in directories:
                did = identifier("D_", relative.as_posix())
                short = f"D{len(directories):07d}"
                add("Directory", [(did, directories[relative.parent], short + "|" + relative.name)])
                directories[relative] = did
    add("Feature", [("Main", None, name, "Application, offline data, and private .NET runtime", 1, 1, "INSTALLDIR", 0)])
    cab = msilib.CAB("GrimList.cab")
    inventory = []
    for index, path in enumerate(payload, 1):
        relative = path.relative_to(source)
        rel = relative.as_posix()
        fid, cid, rid = (identifier(p, rel) for p in ("F_", "C_", "R_"))
        directory = directories[relative.parent]
        seq, _ = cab.append(str(path), path.name, fid)
        file_version, language, file_hash = file_metadata(path)
        # Registry keypaths make components in a user profile belong to that user.
        add("Component", [(cid, guid(f"{identity}/component/{rel.lower()}"), directory, 260, None, rid)])
        add("Registry", [(rid, 1, f"Software\\GrimList\\Installer\\{identity}\\Components", cid, "1", cid)])
        short = f"F{index:07d}" + (path.suffix[:4] if path.suffix else "")
        add("File", [(fid, cid, short + "|" + path.name, path.stat().st_size, file_version, language, 512, seq)])
        if file_hash:
            add("MsiFileHash", [(fid, 0, *file_hash)])
        add("FeatureComponents", [("Main", cid)])
        if rel == "GrimList.exe":
            main_component = cid
            for sid, folder in [("StartMenuShortcut", "SHORTCUTDIR"), ("DesktopShortcut", "DesktopFolder")]:
                add("Shortcut", [(sid, folder, "GRIMLIST|" + name, cid, f"[#{fid}]", None, "Offline army builder", None, "AppIcon", 0, 1, "INSTALLDIR")])
        inventory.append({"path": rel, "bytes": path.stat().st_size, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
    # Only remove empty program/shortcut directories. The user-data folder is never owned.
    for directory in [*directories.values(), "SHORTCUTDIR"]:
        add("RemoveFile", [("Remove_" + directory, main_component, None, directory, 2)])
    add("Icon", [("AppIcon", msilib.Binary(str(ROOT / "Icon/AppIcon.ico")))])
    add("Upgrade", [(upgrade, None, version, None, 1, None, "GRIMLIST_OLDVERSIONS"),
                    (upgrade, version, None, None, 2, None, "GRIMLIST_NEWER")])
    # VersionNT64 reports 603 on Windows 10/11; read the actual major version.
    add("RegLocator", [("WindowsMajor", 2, "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion", "CurrentMajorVersionNumber", 18)])
    add("AppSearch", [("WINDOWSMAJOR", "WindowsMajor")])
    add("LaunchCondition", [('Installed OR (VersionNT64 AND WINDOWSMAJOR = "#10")', "GrimList requires 64-bit Windows 10 or later."),
        ("Installed OR NOT GRIMLIST_NEWER", "A newer version of GrimList is already installed. Uninstall it before installing an older version. Your armies will be kept.")])
    execute = [("FindRelatedProducts", None, 25), ("AppSearch", None, 50), ("LaunchConditions", None, 100),
        ("CostInitialize", None, 800), ("FileCost", None, 900), ("CostFinalize", None, 1000),
        ("MigrateFeatureStates", None, 1200), ("InstallValidate", None, 1400), ("InstallInitialize", None, 1500),
        ("RemoveExistingProducts", "GRIMLIST_OLDVERSIONS", 1510), ("ProcessComponents", None, 1600),
        ("UnpublishFeatures", None, 1800), ("RemoveRegistryValues", None, 2600), ("RemoveShortcuts", None, 3200),
        ("RemoveFiles", None, 3500), ("RemoveFolders", None, 3600), ("CreateFolders", None, 3700),
        ("InstallFiles", None, 4000), ("CreateShortcuts", None, 4500), ("WriteRegistryValues", None, 5000),
        ("RegisterUser", None, 6000), ("RegisterProduct", None, 6100), ("PublishFeatures", None, 6300),
        ("PublishProduct", None, 6400), ("InstallFinalize", None, 6600)]
    add("InstallExecuteSequence", execute)
    add("InstallUISequence", [(a, c, n) for a, c, n in execute if n <= 1200] + [
        ("WelcomeDlg", "NOT Installed", 1230), ("MaintenanceDlg", "Installed", 1240),
        ("ProgressDlg", None, 1280), ("ExecuteAction", None, 1300),
        ("ExitDialog", None, -1), ("UserExit", None, -2), ("FatalError", None, -3)])
    add("AdminExecuteSequence", [(a, None, n) for a, n in [("CostInitialize", 800), ("FileCost", 900),
        ("CostFinalize", 1000), ("InstallValidate", 1400), ("InstallInitialize", 1500),
        ("InstallAdminPackage", 3900), ("InstallFiles", 4000), ("InstallFinalize", 6600)]])
    add("AdminUISequence", [("CostInitialize", None, 800), ("FileCost", None, 900),
        ("CostFinalize", None, 1000), ("ExecuteAction", None, 1300)])
    ui(db)
    db.Commit()
    print(f"Compressing {len(payload)} files ({sum(p['bytes'] for p in inventory):,} bytes)...", flush=True)
    cab.commit(db)
    db.Commit()
    del db
    report = {"product": name, "version": version, "productCode": code, "upgradeCode": upgrade,
              "testIdentity": args.test_identity, "source": str(source), "installer": str(output),
              "installerBytes": output.stat().st_size, "installerSha256": hashlib.sha256(output.read_bytes()).hexdigest(),
              "files": inventory}
    output.with_suffix(".inventory.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({k: v for k, v in report.items() if k != "files"}, indent=2), flush=True)
    # Only normal production output participates in automatic release retention.
    # Custom/test destinations must never prune the production installer history.
    default_output = ROOT / "artifacts/installers/windows" / version / f"GrimList-{version}-win-x64.msi"
    if not args.test_identity and output == default_output.resolve():
        subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(ROOT / "scripts/Complete-Build.ps1"),
                        "-Kind", "windows-installer", "-Version", version, "-Artifact", str(output)], check=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, help="Published Windows directory (includes private runtime)")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--version", help="Defaults to Directory.Build.props")
    parser.add_argument("--test-identity", action="store_true", help="Isolated product/upgrade IDs for lifecycle tests")
    build(parser.parse_args())
