---
name: run-caerostris
description: Build, run, and drive Cærostris (the Blazor WASM Spotify client) via CaerostrisServer. Use when asked to start Caerostris, run the app, take a screenshot of it, verify a change boots, or check the browser console for errors.
---

Cærostris is a Blazor WebAssembly PWA, served in dev by `CaerostrisServer`
(ASP.NET Core standalone WASM host). Drive it headless with the Playwright
REPL at `.claude/skills/run-caerostris/driver.mjs` — no display server
needed, plain `chromium.launch()`.

All paths below are relative to `/workspaces/Caerostris/` (this repo).
Building requires three sibling checkouts (`../SpotifyService`,
`../CaerostrisServer`, `../SpotifyAuthServer` — see this repo's
`CLAUDE.md`/`README.md`); the devcontainer bind-mounts them.

## Prerequisites

```bash
sudo apt-get update && sudo apt-get install -y tmux   # not preinstalled; needed for the agent-path REPL below
```

```bash
cd .claude/skills/run-caerostris
npm install                                # playwright-core, pinned in package.json/package-lock.json
npx playwright install chromium-headless-shell   # ~115MB; playwright-core's default headless launch target isn't pre-cached
```

## Build

From the repo root (builds all four sibling repos via project references):

```bash
cd /workspaces/Caerostris && dotnet build
```

Expect a handful of pre-existing warnings (nullable refs in
`SpotifyService`, an `AssemblyVersion` format warning, a Blazor
`BL0007` param warning) — 0 errors is the bar.

## Run (agent path)

`CaerostrisServer/Properties/launchSettings.json` has a bug:
`dotnetRunMessages` is a JSON string (`"true"`) where a boolean is
expected, so `dotnet run` **silently fails to apply the launch
profile** and falls back to `ASPNETCORE_ENVIRONMENT=Production`.
Production mode serves a bare `404` for `GET /` instead of the app
(triggers `UseHsts`/`UseExceptionHandler` instead of
`UseBlazorFrameworkFiles`/`MapFallbackToFile`). You must set
`ASPNETCORE_ENVIRONMENT=Development` explicitly:

```bash
tmux new-session -d -s caerostris-server -x 200 -y 50
tmux send-keys -t caerostris-server \
  'cd /workspaces/CaerostrisServer && ASPNETCORE_ENVIRONMENT=Development dotnet run --project Caerostris.Server.csproj --urls "http://localhost:5285"' Enter
timeout 30 bash -c 'until curl -sf http://localhost:5285/ >/dev/null; do sleep 1; done' && echo "SERVER UP"
```

Then launch the driver (from `/workspaces/Caerostris`) in its own tmux
session:

```bash
tmux new-session -d -s caerostris-driver -x 200 -y 50
tmux send-keys -t caerostris-driver 'node .claude/skills/run-caerostris/driver.mjs' Enter
timeout 10 bash -c 'until tmux capture-pane -t caerostris-driver -p | tail -1 | grep -q "driver>"; do sleep 0.3; done'

tmux send-keys -t caerostris-driver 'launch' Enter
timeout 15 bash -c 'until tmux capture-pane -t caerostris-driver -p | tail -1 | grep -qE "launched\.|ERROR"; do sleep 0.5; done'

tmux send-keys -t caerostris-driver 'nav http://localhost:5285/' Enter
sleep 1
tmux send-keys -t caerostris-driver 'wait text=Spotify authorization needed' Enter
timeout 25 bash -c 'until tmux capture-pane -t caerostris-driver -p | tail -1 | grep -qE "found:|TIMEOUT:"; do sleep 0.5; done'

tmux send-keys -t caerostris-driver 'ss home' Enter
tmux send-keys -t caerostris-driver 'console --errors' Enter
sleep 1
tmux capture-pane -t caerostris-driver -p
```

Screenshots land in `/tmp/caerostris-shots/` (override: `SCREENSHOT_DIR`).

Stop cleanly: `tmux send-keys -t caerostris-driver 'quit' Enter`, then
`Ctrl-C` in the server session (or `tmux send-keys -t caerostris-server C-c`),
then `tmux kill-session -t caerostris-driver` / `-t caerostris-server`.

### Driver commands

| command | what it does |
|---|---|
| `launch` | launch headless Chromium |
| `nav <url>` | navigate |
| `wait <selector-or-text=...>` | wait up to 20s for an element/text (see Gotchas — cold boot is slow) |
| `ss [name]` | screenshot → `/tmp/caerostris-shots/<name>.png` |
| `click <css-sel>` | click via DOM (`el.click()`, not coordinates) |
| `click-text <text>` | click the button/link/anchor containing this text |
| `type <text>` / `press <key>` | keyboard input |
| `eval <js>` | evaluate in the page, prints JSON |
| `text [css-sel]` | print `innerText` (whole body if no selector) |
| `console [--errors]` | dump captured console/page-error messages |
| `quit` | close the browser |

## Run (human path)

```bash
cd /workspaces/CaerostrisServer
ASPNETCORE_ENVIRONMENT=Development dotnet run --project Caerostris.Server.csproj --urls "http://localhost:5285"
```

Open `http://localhost:5285/` in a real browser at ≥1600px wide (this
app is desktop-only, per its own README). `Ctrl-C` to stop.

## Gotchas

- **`launchSettings.json`'s `dotnetRunMessages` bug forces Production
  mode**, which 404s instead of serving the app — see Run (agent path)
  above. This is a pre-existing bug in the file itself; the fix here is
  the env var, not editing the file.
- **Blazor WASM cold boot is slow (10-15s+):** the browser has to
  download and JIT the whole .NET runtime on first `nav`. A `ss` taken
  right after `nav` just screenshots the "Loading…" spinner. Always
  `wait` for real content first — `wait text=Spotify authorization
  needed` is the reliable ready-marker for a fresh, unauthenticated
  session (the driver's default `wait` timeout is already bumped to
  20s for this reason).
- **Nearly every route renders a blank page body**, by design:
  `MainLayout` (see this repo's `CLAUDE.md`) gates all page content
  behind Spotify OAuth except the `/callback` route. Navigating to
  `/about`, `/library`, etc. without real Spotify credentials
  configured just shows the sidebar + the same "Spotify authorization
  needed" modal — that's expected, not a regression. Confirm routing
  worked via `eval location.pathname` and the sidebar's active-item
  highlight, not by expecting page content.
- **`playwright-core`'s default `chromium.launch()` target
  (`chromium_headless_shell`) isn't the same cached browser
  `@playwright/mcp` downloads** (a full `chromium` build under
  `~/.cache/ms-playwright/b/browser@<hash>`) — you need the separate
  `npx playwright install chromium-headless-shell` in Prerequisites
  even if the MCP server's browser is already cached.
- **After `quit`, wait for the shell prompt before relaunching** —
  sending the next `node driver.mjs` command too quickly after `quit`
  can race the exiting process and get swallowed. Poll for `driver> `
  the same way the launch step does; don't chain them with a bare
  `sleep 1`.

## Troubleshooting

- **`curl http://localhost:5285/` → `404 Not Found`, no redirect:**
  you're in Production mode. Confirm with `tmux capture-pane`: the log
  should say `Hosting environment: Development`, not `Production`. Set
  `ASPNETCORE_ENVIRONMENT=Development` explicitly (see Gotchas).
  Kestrel binds fine either way — this is a routing/environment issue,
  not a port issue.
  - Relatedly, the launch log always prints `An error was encountered
    when reading 'Properties/launchSettings.json'` — that's the same
    known bug, harmless once you're setting the env var yourself.
- **`Address already in use` on `:5285`:** a previous server is still
  up. `dotnet run`'s child process outlives the parent PID, so `kill
  $(cat pidfile)` doesn't work — use
  `pkill -9 -f "Caerostris.Server"` instead, then re-check
  `curl -sf http://localhost:5285/` fails before relaunching.
- **`Executable doesn't exist at .../chromium_headless_shell-.../chrome-headless-shell`:**
  run the `npx playwright install chromium-headless-shell` step in
  Prerequisites.
- **`npx playwright install` prints a "you are running without first
  installing your project's dependencies" warning box:** harmless —
  `npx` resolves a standalone `playwright` CLI regardless of the local
  `playwright-core` dependency. Exit code is still `0` and the browser
  does get installed/verified; check
  `ls ~/.cache/ms-playwright/ | grep headless` if in doubt.
