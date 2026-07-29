#!/usr/bin/env bash
set -euo pipefail

# Idempotent: this runs on every container create, not just the first.

# Fresh named volumes are created root-owned; fix that before anything tries
# to write into them as the non-root "vscode" user.
sudo mkdir -p /commandhistory
sudo touch /commandhistory/.bash_history
sudo chown -R vscode:vscode \
  /home/vscode/.claude \
  /home/vscode/.config/gh \
  /home/vscode/.nuget/packages \
  /commandhistory

dotnet --info

cat <<'EOF'

Caerostris devcontainer ready.

This repo alone cannot be restored/built yet:
  - Caerostris.csproj and Caerostris.sln reference sibling projects
    ../SpotifyService/Caerostris.Services.Spotify.csproj and
    ../CaerostrisServer/Caerostris.Server.csproj, which are separate repos
    not present in this workspace.
  - DevExpress.Blazor is a licensed package; it needs a NuGet source added
    via `dotnet nuget add source` (or a NuGet.Config) pointing at your
    DevExpress feed. Do not commit that source/credential.

See README.md for the one-time steps.
EOF
