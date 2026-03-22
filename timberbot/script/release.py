"""Build, package, and optionally release Timberbot.

Usage:
    python release.py            build + package ZIP
    python release.py --release  build + package + tag + GitHub release
"""
import json
import os
import shutil
import subprocess
import sys
import zipfile

ROOT = os.path.dirname(os.path.abspath(__file__))
SRC_DIR = os.path.join(ROOT, "timberbot", "src")
DIST_DIR = os.path.join(ROOT, "dist")
MANIFEST = os.path.join(SRC_DIR, "manifest.json")
DLL_PATH = os.path.join(SRC_DIR, "bin", "Release", "netstandard2.1", "Timberbot.dll")
SCRIPT = os.path.join(ROOT, "timberbot", "script", "timberbot.py")


def run(cmd, **kwargs):
    print(f"  > {cmd}")
    subprocess.check_call(cmd, shell=True, **kwargs)


def main():
    release = "--release" in sys.argv

    with open(MANIFEST) as f:
        version = json.load(f)["Version"]

    print(f"building timberbot v{version}")

    # build
    run("dotnet build -c Release", cwd=SRC_DIR)

    # package
    if os.path.exists(DIST_DIR):
        shutil.rmtree(DIST_DIR)
    os.makedirs(DIST_DIR)

    # mod zip (DLL + manifest + thumbnail + python client)
    zip_name = f"Timberbot-v{version}.zip"
    zip_path = os.path.join(DIST_DIR, zip_name)
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
        zf.write(DLL_PATH, "Timberbot.dll")
        zf.write(MANIFEST, "manifest.json")
        thumb = os.path.join(SRC_DIR, "thumbnail.png")
        if os.path.exists(thumb):
            zf.write(thumb, "thumbnail.png")
        zf.write(SCRIPT, "timberbot.py")

    print(f"packaged: dist/{zip_name}")

    # release
    if release:
        tag = f"v{version}"
        run(f"git tag {tag}")
        run(f"git push origin {tag}")
        run(
            f'gh release create {tag} "{zip_path}"'
            f" --repo abix-/TimberbornMods"
            f' --title "Timberbot {tag}"'
            f' --notes "HTTP API for AI agents to read and control Timberborn."'
        )
        print(f"released: {tag}")


if __name__ == "__main__":
    main()
