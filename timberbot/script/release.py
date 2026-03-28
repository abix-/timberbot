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

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(SCRIPT_DIR))
SRC_DIR = os.path.join(ROOT, "timberbot", "src")
DIST_DIR = os.path.join(ROOT, "dist")
MOD_DIR = os.path.join(os.environ["USERPROFILE"], "Documents", "Timberborn", "Mods", "Timberbot")
MANIFEST = os.path.join(SRC_DIR, "manifest.json")
DLL_PATH = os.path.join(SRC_DIR, "bin", "Release", "netstandard2.1", "Timberbot.dll")
SCRIPT = os.path.join(SCRIPT_DIR, "timberbot.py")
SKILL = os.path.join(ROOT, "skill", "timberbot.md")


def run(cmd, **kwargs):
    print(f"  > {cmd}")
    subprocess.check_call(cmd, shell=True, **kwargs)

PRESERVE_MOD_FILES = {"settings.json", "workshop_data.json", "memory", "autoload.json"}


def clean_mod_dir():
    if not os.path.isdir(MOD_DIR):
        return

    for name in os.listdir(MOD_DIR):
        if name in PRESERVE_MOD_FILES:
            continue

        path = os.path.join(MOD_DIR, name)
        if os.path.isdir(path):
            shutil.rmtree(path)
        else:
            os.remove(path)



def main():
    release = "--release" in sys.argv

    with open(MANIFEST) as f:
        version = json.load(f)["Version"]

    print(f"building Timberbot API v{version}")

    # clean deployed mod folder but preserve Workshop linkage and local settings
    clean_mod_dir()

    # build
    run("dotnet build -c Release", cwd=SRC_DIR)

    # package
    if os.path.exists(DIST_DIR):
        shutil.rmtree(DIST_DIR)
    os.makedirs(DIST_DIR)

    # mod zip (DLL + manifest + thumbnail + python client + Claude skill + docs)
    zip_name = f"TimberbotAPI-v{version}.zip"
    zip_path = os.path.join(DIST_DIR, zip_name)
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
        zf.write(DLL_PATH, "Timberbot.dll")
        zf.write(MANIFEST, "manifest.json")
        thumb = os.path.join(SRC_DIR, "thumbnail.png")
        if os.path.exists(thumb):
            zf.write(thumb, "thumbnail.png")
        zf.write(SCRIPT, "timberbot.py")
        zf.write(SKILL, "skill/timberbot.md")
        # include settings.json with debug disabled
        release_settings = json.dumps({
            "debugEndpointEnabled": False,
            "httpPort": 8085
        }, indent=2)
        zf.writestr("settings.json", release_settings)
        # include docs
        docs_dir = os.path.join(ROOT, "docs")
        for doc in os.listdir(docs_dir):
            if doc.endswith((".md", ".txt")):
                zf.write(os.path.join(docs_dir, doc), f"docs/{doc}")

    print(f"packaged: dist/{zip_name}")

    # release
    if release:
        tag = f"v{version}"
        run(f"git tag {tag}")
        run(f"git push origin {tag}")
        run(
            f'gh release create {tag} "{zip_path}"'
            f" --repo abix-/TimberbornMods"
            f' --title "Timberbot API {tag}"'
            f' --notes "HTTP API for AI agents to read and control Timberborn."'
        )
        print(f"released: {tag}")


if __name__ == "__main__":
    main()
