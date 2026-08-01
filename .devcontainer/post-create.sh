#!/usr/bin/env bash
set -euo pipefail

# Idempotent: this runs on every container create, not just the first.

# Fresh named volumes are created root-owned; fix that before anything tries
# to write into them as the non-root "vscode" user.
sudo mkdir -p /commandhistory
sudo touch /commandhistory/.bash_history
sudo mkdir -p /home/vscode/.config/git
sudo touch /home/vscode/.config/git/config
sudo chown -R vscode:vscode \
  /home/vscode/.claude \
  /home/vscode/.config/gh \
  /home/vscode/.config/git \
  /home/vscode/.nuget \
  /commandhistory

dotnet --info

# sudo resets the environment by default, which would otherwise silently drop
# HTTP_PROXY/HTTPS_PROXY (set container-wide in docker-compose.yml) for just
# this one apt-get call - every other command below runs as vscode, not
# sudo, and inherits the proxy vars directly. An apt.conf.d entry sidesteps
# the sudo env-reset instead of depending on sudoers' env_keep list.
sudo tee /etc/apt/apt.conf.d/01proxy > /dev/null <<'EOF'
Acquire::http::Proxy "http://proxy:3128";
Acquire::https::Proxy "http://proxy:3128";
EOF

# The base image only ships python3.12-minimal (no stdlib modules like json/
# shlex). Emscripten's emcc.py shells out to python3 and needs the real
# stdlib - without it, the wasm-tools workload below fails native builds.
sudo apt-get update -qq && sudo apt-get install -y -qq python3

# Blazor WASM build tools: required to link SkiaSharp's native library
# (libSkiaSharp.a, used by LiveCharts2) into the WASM bundle. Without this,
# `dotnet build` fails with LVC0001.
dotnet workload install wasm-tools 2>&1 | tail -20

# csharp-ls: open-source Roslyn-based C# language server. Global tools install
# under $HOME, which is wiped on rebuild (only /workspaces and named volumes
# survive) - reinstall every time rather than assuming it's still there.
dotnet tool install --global csharp-ls 2>&1 | tail -5 || dotnet tool update --global csharp-ls 2>&1 | tail -5

cat <<'EOF'

Caerostris devcontainer ready.

This repo alone cannot be restored/built yet:
  - Caerostris.csproj and Caerostris.sln reference sibling projects
    ../SpotifyService/Caerostris.Services.Spotify.csproj and
    ../CaerostrisServer/Caerostris.Server.csproj, which are separate repos
    not present in this workspace.

See README.md for the one-time steps.
EOF
