#!/usr/bin/env python3
"""Build a Junimo Hollow mod pack: Mods/ zip + pack.json (version, url, sha256).

Usage: build_pack.py <version> <out_dir> [--url-base URL]
  Without --url-base the pack.json points at the local zip (for tests).
"""
import hashlib, json, os, shutil, subprocess, sys, zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
GAME = Path("/mnt/c/Program Files (x86)/Steam/steamapps/common/Stardew Valley")
DOTNET = Path.home() / ".dotnet/dotnet"
MOD_SRC = REPO / "src/StembridgeValley"
EXTRA = REPO / "pack/Mods"  # any other mods we add later live here
SMAPI_BUNDLED = ["SaveBackup"]  # SMAPI's own save-backup mod: cheap insurance


def main():
    version, out = sys.argv[1], Path(sys.argv[2])
    url_base = sys.argv[sys.argv.index("--url-base") + 1] if "--url-base" in sys.argv else None
    out.mkdir(parents=True, exist_ok=True)

    subprocess.run([str(DOTNET), "build", "-c", "Release", f"-p:Version={version.lstrip('v')}"], cwd=MOD_SRC, check=True,
                   stdout=subprocess.DEVNULL)
    build = MOD_SRC / "bin/Release/net6.0"

    stage = out / "stage"
    shutil.rmtree(stage, ignore_errors=True)
    mod_dir = stage / "StembridgeValley"
    mod_dir.mkdir(parents=True)
    for name in ["StembridgeValley.dll", "StembridgeValley.pdb"]:
        shutil.copy2(build / name, mod_dir / name)
    manifest = json.loads((MOD_SRC / "manifest.json").read_text())
    manifest["Version"] = version.lstrip("v")
    (mod_dir / "manifest.json").write_text(json.dumps(manifest, indent=2))
    config = REPO / "pack/config.json"
    if config.exists():
        shutil.copy2(config, mod_dir / "config.json")
    for name in SMAPI_BUNDLED:
        src = GAME / "Mods" / name
        if src.is_dir():
            shutil.copytree(src, stage / name)
    if EXTRA.is_dir():
        for d in EXTRA.iterdir():
            if d.is_dir():
                shutil.copytree(d, stage / d.name)

    zip_path = out / "mods.zip"
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for f in sorted(stage.rglob("*")):
            if f.is_file() and not f.name.startswith("__folder_managed_by_vortex"):
                info = zipfile.ZipInfo(str(f.relative_to(stage)).replace(os.sep, "/"), date_time=(2026, 1, 1, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                z.writestr(info, f.read_bytes())
    shutil.rmtree(stage)

    digest = hashlib.sha256(zip_path.read_bytes()).hexdigest()
    url = f"{url_base.rstrip('/')}/mods.zip" if url_base else win_path(zip_path)
    info = {"version": version, "url": url, "sha256": digest, "size": zip_path.stat().st_size}
    (out / "pack.json").write_text(json.dumps(info, indent=2))
    print(json.dumps(info, indent=2))


def win_path(p: Path) -> str:
    s = str(p.resolve())
    if s.startswith("/mnt/"):
        return s[5].upper() + ":\\" + s[7:].replace("/", "\\")
    raise SystemExit("Local test packs must live under /mnt/<drive>/ so Windows can read them")


if __name__ == "__main__":
    main()
