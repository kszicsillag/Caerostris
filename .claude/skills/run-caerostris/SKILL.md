---
name: run-caerostris
description: Build, run, and drive Cærostris (the Blazor WASM Spotify client) via CaerostrisServer. Use when asked to start Caerostris, run the app, take a screenshot of it, verify a change boots, or check the browser console for errors.
---

Cærostris is a Blazor WebAssembly PWA, served in dev by `CaerostrisServer`
(ASP.NET Core standalone WASM host). Drive it headless with the Playwright
command server at `.claude/skills/run-caerostris/driver.cs` — a .NET 10
*file-based app* (`Microsoft.Playwright` + a minimal ASP.NET Core API via
the `#:sdk Microsoft.NET.Sdk.Web` directive, `dotnet run --file driver.cs`,
no `.csproj`), not a Node script. No display server needed, plain headless
Chromium. Commands are sent over plain HTTP (`curl`), so the driver just
needs to run as an ordinary background process — no tmux/pty required.

All paths below are relative to `/workspaces/Caerostris/` (this repo).
Building requires three sibling checkouts (`../SpotifyService`,
`../CaerostrisServer`, `../SpotifyAuthServer` — see this repo's
`CLAUDE.md`/`README.md`); the devcontainer bind-mounts them.

## Prerequisites

```bash
cd /workspaces/Caerostris
dotnet run --file .claude/skills/run-caerostris/driver.cs -- install
```

Downloads the Chromium browser binaries (~290MB — the .NET Playwright
binding's `install chromium` target pulls both the full browser and the
headless-shell variant; this pins its own browser revision, separate
from whatever `@playwright/mcp`'s Node install already cached). One-time
per container; `dotnet run` also restores the `Microsoft.Playwright`
NuGet package into `~/.nuget/packages` on first invocation.

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
`ASPNETCORE_ENVIRONMENT=Development` explicitly. Run it as a plain
background process (the shell's `&`, or your tool's background-job
support — no pty/multiplexer needed) and poll readiness with `curl`:

```bash
cd /workspaces/CaerostrisServer
ASPNETCORE_ENVIRONMENT=Development dotnet run --project Caerostris.Server.csproj --urls "http://localhost:5285" > /tmp/caerostris-server.log 2>&1 &
disown
timeout 30 bash -c 'until curl -sf http://localhost:5285/ >/dev/null; do sleep 1; done' && echo "SERVER UP"
```

Then start the driver (from `/workspaces/Caerostris`) the same way —
**must use `dotnet run --file`, not bare `dotnet run <path>.cs`** (see
Gotchas: this repo's own `.csproj` in the cwd hijacks that form):

```bash
dotnet run --file .claude/skills/run-caerostris/driver.cs > /tmp/caerostris-driver.log 2>&1 &
disown
timeout 20 bash -c 'until curl -sf http://127.0.0.1:5299/health >/dev/null; do sleep 0.5; done' && echo "DRIVER UP"

curl -s -X POST http://127.0.0.1:5299/cmd -d 'launch'
curl -s -X POST http://127.0.0.1:5299/cmd -d 'nav http://localhost:5285/'
curl -s -X POST http://127.0.0.1:5299/cmd -d 'wait text=Spotify authorization needed'

curl -s -X POST http://127.0.0.1:5299/cmd -d 'ss home'
curl -s -X POST http://127.0.0.1:5299/cmd -d 'console --errors'
```

Each `curl` call is synchronous and its response body is the command's
output (equivalent to what the old REPL printed) — no polling needed
between steps other than the initial readiness checks above. Override the
driver's port with `DRIVER_PORT` (default `5299`) if it's taken.

Screenshots land in `/tmp/caerostris-shots/` (override: `SCREENSHOT_DIR`).

Stop cleanly: `curl -s -X POST http://127.0.0.1:5299/cmd -d 'quit'` (closes
the browser), then `curl -s -X POST http://127.0.0.1:5299/cmd -d 'shutdown'`
(stops the driver process itself), then `pkill -f "Caerostris.Server"` for
the server.

### Driver commands

Send as the body of `POST /cmd`, e.g. `curl -X POST .../cmd -d 'nav <url>'`.

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
| `quit` | close the browser (driver process keeps running) |
| `shutdown` | stop the driver's HTTP server (exits the process) |

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
- **`dotnet run driver.cs` (no `--file`) silently does the wrong
  thing** when run from `/workspaces/Caerostris`: because
  `Caerostris.csproj` exists in the cwd, the CLI treats `driver.cs` as
  a *command-line argument to that project* instead of a file-based
  app — it launches the Blazor dev server itself (and crashes on a
  missing HTTPS dev cert) rather than the driver. Always pass
  `--file` explicitly here; this ambiguity is specific to running the
  driver from inside a directory that already has its own `.csproj`.
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
- **The .NET Playwright binding still shells out to a bundled Node.js
  process internally** (`Microsoft.Playwright` is a thin RPC client
  over the same Node-based Playwright core `@playwright/mcp` uses) —
  it downloads its own private Node/driver binaries on first
  `Playwright.CreateAsync()`, invisible to this skill but real disk/
  network use. This driver removes the *hand-written, repo-committed*
  JS (no more `driver.mjs`/`package.json`/`node_modules`), not every
  Node process on the machine.
- **`quit` vs `shutdown` are different lifecycles**: `quit` only closes
  the browser (the driver's HTTP server keeps listening, so you can
  `launch` again without restarting `dotnet run`). `shutdown` stops the
  server process itself — after that, poll `GET /health` (expect it to
  fail, then start failing-then-succeeding once you relaunch) rather than
  assuming the port is immediately free; give the process a moment to
  exit before starting a new one on the same `DRIVER_PORT`.

## Troubleshooting

- **`curl http://localhost:5285/` → `404 Not Found`, no redirect:**
  you're in Production mode. Confirm via `/tmp/caerostris-server.log`:
  it should say `Hosting environment: Development`, not `Production`. Set
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
- **`curl .../health` never comes up / driver log shows an ASP.NET
  Kestrel/HTTPS-cert crash:** you ran `dotnet run <path>/driver.cs`
  without `--file` from `/workspaces/Caerostris` — see the `--file`
  Gotcha above. `pkill -f "driver.cs"`, retry with `dotnet run --file
  .claude/skills/run-caerostris/driver.cs`.
- **Driver returns a browser-executable-not-found error from `launch`:**
  run the `dotnet run --file .../driver.cs -- install` step in
  Prerequisites.
- **`Address already in use` on the driver's port (`:5299` default):**
  a previous driver process is still up — `pkill -f "driver.cs"`, confirm
  `curl -sf http://127.0.0.1:5299/health` fails, then relaunch (or set
  `DRIVER_PORT` to something else).
