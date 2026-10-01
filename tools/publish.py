#!/usr/bin/env python3
"""Publish a new Stembridge Valley release to GitHub.

  publish.py v0.1.2 [--notes "what changed"]

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

    run([DOTNET, "publish", "-c", "Release", "-o", str(out / "launcher")], cwd=REPO / "src/Launcher", stdout=subprocess.DEVNULL)
    launcher = out / "Play-Stembridge-Valley.exe"  # GitHub turns spaces in asset names into dots
    shutil.move(str(out / "launcher" / "Play Stembridge Valley.exe"), launcher)
    shutil.rmtree(out / "launcher")

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

    sums = "\n".join(f"{hashlib.sha256(p.read_bytes()).hexdigest()}  {p.name}" for p in
                     [out / "mods.zip", launcher, server_zip]) + "\n"
    (out / "SHA256SUMS.txt").write_text(sums)

    if "--dry-run" in sys.argv:
        print("\nDry run: built", ", ".join(p.name for p in out.iterdir()))
        return
    assets = [out / "mods.zip", out / "pack.json", launcher, server_zip, out / "SHA256SUMS.txt"]
    run(["gh", "release", "create", version, *map(str, assets), "--repo", OWNER_REPO,
         "--title", f"Stembridge Valley {version}", "--notes", notes, "--latest"])
    print(f"\nPublished {version}. Players get it automatically next time they launch.")


if __name__ == "__main__":
    main()
