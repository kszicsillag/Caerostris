# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Cærostris is a Blazor WebAssembly PWA Spotify client. It is a proof-of-concept exploring .NET languages/tooling for a client-side web app, not a production product.

**The project currently does not build as-is.** It targets `net6.0` (a 2021-era preview1 SDK) while the devcontainer intentionally runs a current .NET SDK — this repo is staged for a stack upgrade (net6-preview → current .NET), not day-to-day development on the existing code. Don't try to `dotnet build`/`dotnet restore` and treat failures as bugs to fix; they're expected until the upgrade happens.

## Multi-repo structure

`Caerostris.csproj`/`Caerostris.sln` reference sibling projects by relative path that live in **separate git repos**, not in this one:

- `../SpotifyService/Caerostris.Services.Spotify.csproj` — Razor Class Library wrapping the Spotify Web API for Blazor WASM (upstream: `tresoneur/SpotifyService`). This is where `SpotifyService`/`Spotify.Auth`/`Spotify.Playback` etc. (injected throughout this repo) actually live.
- `../CaerostrisServer/Caerostris.Server.csproj` — ASP.NET Core host for Caerostris (upstream: `tresoneur/CaerostrisServer`). In turn depends on `SpotifyAuthServer` (upstream: `tresoneur/SpotifyAuthServer`), which handles the Spotify OAuth Authorization Code flow server-side (`AuthServerApiBase` in `Program.cs` points at a deployed instance of this).

The devcontainer (`.devcontainer/devcontainer.json`) bind-mounts all three sibling repos as `/workspaces/{CaerostrisServer,SpotifyService,SpotifyAuthServer}`, so a single container can develop all four together — this only works if they're checked out as true siblings of this repo on the host. `caerostris.code-workspace` (tracked in this repo, paths relative to it) is the corresponding multi-root VS Code workspace; copy it one directory up (sibling to all four repos) before opening it, since VS Code won't let you save a workspace file inside one of its own member folders.

## Architecture

- **DI/service registration** (`Program.cs`): the Spotify client library is wired up via `.AddSpotify(new() { AuthServerApiBase, ClientId, PermissionScopes, ... })`, plus `.AddBlazoredModal()`. `host.Services.InitializeSpotify()` runs before `host.RunAsync()`.
- **Auth flow**: `Shared/Auth/AuthDaemon.razor` (mounted once, in `MainLayout`) subscribes to `Spotify.Auth.AuthStateChanged` and pops `AuthModal` (via Blazored.Modal) whenever the user isn't authenticated. `Pages/Callback/Callback.razor` (route `/callback`) is the OAuth redirect target and calls `Spotify.Auth.ContinueAuthOnCallback(...)`. `MainLayout` gates rendering the page body on `authAquired`, with an explicit exception for the callback route itself.
- **Layout** (`Shared/Layout/MainLayout.razor`): two cascading values (a sidebar `ContextMenu` and a `RightClickContextMenu`) wrap a `PlaybackContextProvider`, which wraps the actual page body plus the persistent chrome — `NavMenu`, `UserPlaylistsList`, `PlaybackBar`, `AuthDaemon`, `BlazoredModal`. Most pages/components reach the current playback context through this provider rather than fetching it themselves.
- **Styles**: no Blazor CSS isolation. Each `Pages/X` or `Shared/X` folder that has styles keeps them in its own `Styles/*.scss`, and every one of those files is explicitly `@import`-ed into the top-level `Styles/Site.scss`. `sasscompiler.json` (`AspNetCore.SassCompiler`, a cross-platform dart-sass build task run as an MSBuild step) compiles that single entry point to `wwwroot/css/site.g.min.css` — adding a new styled component means adding its `.scss` to `Site.scss`, not just dropping the file in a folder.
- **Localization**: `Resources/Text.resx` (+ `Text.hu.resx` for Hungarian) via `IStringLocalizer<Text>`, conventionally injected as `L` and used as `L["KeyName"]`.
- **PWA**: `wwwroot/service-worker.js` (dev) / `service-worker.published.js` (publish), `wwwroot/manifest.json`. Meant to be viewed on desktop browsers, screens ≥1600px wide.

## LLM tooling available in this devcontainer

- `csharp-ls` (Roslyn-based C# language server) is installed globally via `post-create.sh` on every container create (global dotnet tools live under `$HOME`, which doesn't survive a rebuild).
- `.mcp.json` declares two MCP servers: `microsoft-learn` (remote docs search — useful for authoritative .NET/Blazor migration guidance) and `playwright` (`@playwright/mcp`, for driving/screenshotting the app in a real browser once it builds again). Node ≥20 is required for the latter; the base image doesn't ship Node at all, so `ghcr.io/devcontainers/features/node:1` (`lts`) is in `devcontainer.json` for that reason alone — it isn't needed by anything .NET-related.
