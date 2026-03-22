#!/usr/bin/env bash
set -e

# read version from manifest.json
VERSION=$(python -c "import json; print(json.load(open('timberbot/manifest.json'))['Version'])")
echo "building timberbot v${VERSION}"

# build release DLL
cd timberbot
dotnet build -c Release
cd ..

# package
rm -rf dist
mkdir dist
cp timberbot/bin/Release/netstandard2.1/Timberbot.dll dist/
cp timberbot/manifest.json dist/
cp timberbot/thumbnail.png dist/
cd dist
powershell.exe -Command "Compress-Archive -Path 'Timberbot.dll','manifest.json','thumbnail.png' -DestinationPath 'Timberbot-v${VERSION}.zip' -Force"
cd ..

echo "packaged: dist/Timberbot-v${VERSION}.zip"

# tag and release (if --release flag passed)
if [ "$1" = "--release" ]; then
    git tag "v${VERSION}"
    git push origin "v${VERSION}"
    gh release create "v${VERSION}" "dist/Timberbot-v${VERSION}.zip" \
        --repo abix-/TimberbornMods \
        --title "Timberbot v${VERSION}" \
        --notes "HTTP API for AI agents to read and control Timberborn."
    echo "released: v${VERSION}"
fi
