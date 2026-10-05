#!/usr/bin/env python3
"""Publish a new Junimo Hollow release to GitHub.

  publish.py v0.1.2 [--notes "what changed"] [--new-launcher] [--dry-run]

Builds the mod pack, the player launcher and the server tool, then creates a GitHub release
with mods.zip, pack.json, the launcher and the server zip. Launchers read
releases/latest/download/pack.json, so players update automatically the next time they play.
"""
import hashlib, json, shutil, subprocess, sys, zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
OWNER_REPO = "Stembridge10/stembridge-valley"
DOTNET = str(Path.home() / ".dotnet/dotnet")


def run(cmd, **kw):
    print("$", " ".join(str(c) for c in cmd), flush=True)
    return subprocess.run(cmd, check=True, **kw)


def main():
    version = sys.argv[1]
    if not version.startswith("v"):
        raise SystemExit("Version must look like v0.1.2")
    notes = sys.argv[sys.argv.index("--notes") + 1] if "--notes" in sys.argv else "Mod pack update."
    out = REPO / "out" / version
    shutil.rmtree(out, ignore_errors=True)
    out.mkdir(parents=True)

    url_base = f"https://github.com/{OWNER_REPO}/releases/download/{version}"
    run([sys.executable, str(REPO / "tools/build_pack.py"), version, str(out), "--url-base", url_base])
    pack = json.loads((out / "pack.json").read_text())
    assert pack["url"].endswith("/mods.zip") and len(pack["sha256"]) == 64

    launcher = out / "Play-Junimo-Hollow.exe"  # GitHub turns spaces in asset names into dots
    pinned_launcher(out, launcher, version)

    run([DOTNET, "build", "-c", "Release", "-r", "win-x64", "--self-contained", "false"], cwd=REPO / "src/Host", stdout=subprocess.DEVNULL)
    host_build = REPO / "src/Host/bin/Release/net6.0/win-x64"
    server_zip = out / "StembridgeValley-Server.zip"
    with zipfile.ZipFile(server_zip, "w", zipfile.ZIP_DEFLATED) as z:
        for f in sorted(host_build.iterdir()):
            if f.is_file() and f.suffix in {".exe", ".dll", ".json"}:
                z.write(f, "StembridgeValley-Server/" + f.name)
        z.write(REPO / "docs/SERVER.md", "StembridgeValley-Server/README.md")
        for name in ["Start Server.cmd", "Stop Server.cmd", "Server Status.cmd", "Allow Through Firewall.cmd"]:
            z.write(REPO / "server" / name, "StembridgeValley-Server/" + name)

    # Linux ARM server (Oracle box): host tool + systemd unit + LZ4 shim source.
    linux_build = out / "host-linux"
    run([DOTNET, "build", "-c", "Release", "-r", "linux-arm64", "--self-contained", "false", "-o", str(linux_build)],
        cwd=REPO / "src/Host", stdout=subprocess.DEVNULL)
    linux_tar = out / "StembridgeValley-Server-linux-arm64.tar.gz"
    import tarfile
    with tarfile.open(linux_tar, "w:gz") as t:
        for f in sorted(linux_build.iterdir()):
            if f.is_file() and f.suffix in {".dll", ".json"}:
                t.add(f, "host/" + f.name)
        for f in sorted((REPO / "server/linux").iterdir()):
            t.add(f, f.name)
    shutil.rmtree(linux_build)

    sums = "\n".join(f"{hashlib.sha256(p.read_bytes()).hexdigest()}  {p.name}" for p in
                     [out / "mods.zip", launcher, server_zip, linux_tar]) + "\n"
    (out / "SHA256SUMS.txt").write_text(sums)

    if "--dry-run" in sys.argv:
        print("\nDry run: built", ", ".join(p.name for p in out.iterdir()))
        return
    assets = [out / "mods.zip", out / "pack.json", launcher, server_zip, linux_tar, out / "SHA256SUMS.txt"]
    run(["gh", "release", "create", version, *map(str, assets), "--repo", OWNER_REPO,
         "--title", f"Junimo Hollow {version}", "--notes", notes, "--latest"])
    print(f"\nPublished {version}. Players get it automatically next time they launch.")


# ---------- launcher pinning ----------
# Windows SmartScreen trusts a download by its exact bytes. Every rebuild makes a new file that starts
# from zero, so we re-ship the SAME launcher exe until the launcher's own code really changes.
PIN = REPO / "tools/launcher-pin.json"           # committed: which exe is current and the code it came from
PIN_CACHE = REPO / "out/launcher-pinned/Play-Junimo-Hollow.exe"   # local copy (exe is too big for git)


def launcher_source_hash():
    h = hashlib.sha256()
    for d in ["src/Launcher", "src/Shared"]:
        for f in sorted((REPO / d).rglob("*")):
            rel = f.relative_to(REPO).as_posix()
            if f.is_file() and "/bin/" not in rel and "/obj/" not in rel:
                h.update(rel.encode() + b"\0" + f.read_bytes() + b"\0")
    return h.hexdigest()


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def pinned_launcher(out, launcher, version):
    src = launcher_source_hash()
    pin = json.loads(PIN.read_text()) if PIN.exists() else None
    if pin and pin["source"] == src and "--new-launcher" not in sys.argv:
        if not PIN_CACHE.exists() or sha256(PIN_CACHE) != pin["exe_sha256"]:
            PIN_CACHE.parent.mkdir(parents=True, exist_ok=True)
            run(["gh", "release", "download", pin["release"], "--repo", OWNER_REPO, "--pattern", launcher.name,
                 "--dir", str(PIN_CACHE.parent), "--clobber"])
        if sha256(PIN_CACHE) != pin["exe_sha256"]:
            raise SystemExit("Pinned launcher doesn't match launcher-pin.json; refusing to ship a different exe.")
        shutil.copy2(PIN_CACHE, launcher)
        print(f"Launcher unchanged: re-using the exe from {pin['release']} (keeps Windows' trust).")
        return
    print("Launcher code changed: building a NEW exe. Windows will treat it as brand new (warning starts over).")
    run([DOTNET, "publish", "-c", "Release", "-o", str(out / "launcher")], cwd=REPO / "src/Launcher", stdout=subprocess.DEVNULL)
    shutil.move(str(out / "launcher" / "Play Junimo Hollow.exe"), launcher)
    shutil.rmtree(out / "launcher")
    if "--dry-run" not in sys.argv:
        PIN_CACHE.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(launcher, PIN_CACHE)
        PIN.write_text(json.dumps({"release": version, "exe_sha256": sha256(launcher), "source": src}, indent=2) + "\n")
        print(f"Pinned the new launcher to {version}. Commit tools/launcher-pin.json.")


if __name__ == "__main__":
    main()
