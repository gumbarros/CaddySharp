#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet build src/CaddySharp -c Release --runtime linux-x64
dotnet build src/CaddySharp.Sample -c Release
cp src/CaddySharp/obj/Release/net10.0/linux-x64/dnne/bin/CaddySharpNE.so src/CaddySharp/bin/Release/net10.0/linux-x64/CaddySharpNE.so
mkdir -p bin
dotnet pack src/CaddySharp -c Release --no-build --runtime linux-x64 -o bin/packages
go_bin="${GO_BIN:-$(command -v go || true)}"
if [[ -z "$go_bin" && -x "$HOME/.local/lib/go/bin/go" ]]; then go_bin="$HOME/.local/lib/go/bin/go"; fi
if [[ -z "$go_bin" ]]; then echo "Go 1.26+ not found; set GO_BIN" >&2; exit 1; fi
"$go_bin" build -o bin/caddysharp ./cmd/caddy
"$go_bin" build -o bin/caddysharp-load ./cmd/load
