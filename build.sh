#!/usr/bin/env bash
# Compila Lindo.exe e Lindo-<versione>.msi (funziona su Linux e nella CI).
# Requisiti: .NET SDK 8 (Microsoft, con il supporto WindowsDesktop) e wixl (pacchetto "wixl" / msitools).
set -euo pipefail
cd "$(dirname "$0")"
VERSION="${VERSION:-1.1.0}"

dotnet test tests/Lindo.Core.Tests -c Release
dotnet build src/Lindo -c Release -p:Version="$VERSION"
dotnet build src/Lindo.Monitor -c Release -p:Version="$VERSION"

rm -rf dist && mkdir -p dist
cp src/Lindo/bin/Release/net48/Lindo.exe src/Lindo/bin/Release/net48/Lindo.exe.config dist/
cp src/Lindo.Monitor/bin/Release/net48/LindoMonitor.exe dist/
wixl -a x64 -D Version="$VERSION" -D BinDir=dist -D AssetsDir=assets -o "dist/Lindo-$VERSION.msi" installer/Lindo.wxs
ls -la dist

# Versione portatile: Lindo.exe + LindoMonitor.exe in un unico zip
python3 - "$VERSION" <<'PY'
import sys, zipfile
v = sys.argv[1]
with zipfile.ZipFile(f"dist/Lindo-{v}-portatile.zip", "w", zipfile.ZIP_DEFLATED) as z:
    for f in ["Lindo.exe", "Lindo.exe.config", "LindoMonitor.exe"]:
        z.write(f"dist/{f}", f)
PY
ls -la dist
