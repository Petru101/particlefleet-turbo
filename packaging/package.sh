#!/usr/bin/env bash
# Build the plugin and assemble dist/ParticleFleetTurbo-<version>.zip (BepInEx x86 + config preset + plugin).
set -euo pipefail
cd "$(dirname "$0")/.."

BEPINEX_ZIP=vendor/BepInEx_win_x86_5.4.23.5.zip
VERSION=$(grep -oP 'BepInPlugin\("[^"]+", "[^"]+", "\K[0-9.]+' src/ParticleFleetTurbo/Plugin.cs)
NAME=ParticleFleetTurbo-$VERSION
STAGE=dist/$NAME

[ -f "$BEPINEX_ZIP" ] || { echo "missing $BEPINEX_ZIP (download it from github.com/BepInEx/BepInEx/releases)"; exit 1; }
dotnet build src/ParticleFleetTurbo -c Release --nologo -v q

rm -rf "$STAGE" "dist/$NAME.zip"
mkdir -p "$STAGE"
unzip -q "$BEPINEX_ZIP" -d "$STAGE"
mkdir -p "$STAGE/BepInEx/plugins" "$STAGE/BepInEx/config"
cp src/ParticleFleetTurbo/bin/Release/ParticleFleetTurbo.dll "$STAGE/BepInEx/plugins/"
cp packaging/BepInEx.cfg "$STAGE/BepInEx/config/"
cp packaging/THIRD-PARTY-NOTICES.txt "$STAGE/"
cp packaging/INSTALL.txt "$STAGE/"
cp LICENSE "$STAGE/LICENSE.txt"
(cd "$STAGE" && zip -qr9 "../$NAME.zip" .)
echo "dist/$NAME.zip"
