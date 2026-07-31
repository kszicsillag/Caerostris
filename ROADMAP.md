# .NET 10 Migration Roadmap

Cærostris (and its two sibling repos it builds against, `SpotifyService` and
`CaerostrisServer`) targets `net6.0`/`netcoreapp3.1` from 2021. The devcontainer
already runs the .NET 10 SDK on purpose, staged for this migration (see
`CLAUDE.md`). This roadmap lists what's actually blocking a real `dotnet build`
today, verified by running restore/build in the devcontainer against all four
repos (`Caerostris`, `SpotifyService`, `CaerostrisServer`, `SpotifyAuthServer`),
not just inferred from reading project files.

## Phase 1 — Get a build running at all (done)

`dotnet build` now succeeds with 0 errors across all four repos, and the app
boots cleanly in a browser (verified with the `playwright` MCP server) with no
console errors. Two environment-level fixes were needed beyond the four items
below, folded into `.devcontainer/post-create.sh` so they survive a rebuild:
the base image's `python3.12-minimal` lacks the stdlib modules (`json`,
`shlex`, ...) Emscripten's `emcc.py` shells out to, and the `wasm-tools`
workload wasn't installed (needed to link SkiaSharp's native library —
`LiveChartsCore.SkiaSharpView.Blazor`'s dependency — into the WASM bundle).
Bumping the TFM to `net10.0` also forced a bump of the various
`Microsoft.AspNetCore.Components.*`/`Microsoft.Extensions.Localization`
package references still pinned at `5.0.3`/`6.0.0-preview.*`/`5.0.0`, which
isn't called out as its own item below but was a direct (and blocking)
consequence of item 1. Runtime testing also surfaced a pre-existing,
unrelated-to-this-migration DI bug (`Program.cs` registered the auto-generated
`Text` resx class, whose generated constructor is `internal`, as its own
scoped DI service — nothing actually depended on that registration since
`IStringLocalizer<Text>` resolves independently of it) and two stale
`wwwroot/index.html` references (DevExpress's CSS, the dropped
`Components.Web.Extensions` package's `headManager.js`) that only 404'd once
the app could actually run — both fixed alongside items 3–4.

These are ordered by the sequence in which `dotnet build` actually fails today.

1. **Bump `TargetFramework` to `net10.0`** in all four csproj files
   (`Caerostris.csproj`, `../SpotifyService/Caerostris.Services.Spotify.csproj`,
   `../CaerostrisServer/Caerostris.Server.csproj` currently `net6.0`;
   `../SpotifyAuthServer/SpotifyAuthServer/SpotifyAuthServer.csproj` currently
   `netcoreapp3.1`). Confirmed today's SDK emits `NETSDK1138` ("net6.0 is
   out of support") rather than a hard error, so the repo restores as-is, but
   an EOL TFM shouldn't be the migration target.

2. **Replace `BuildWebCompiler` (1.12.405), the SCSS build step.** This is a
   real, verified hard blocker, not a hypothetical one: `dotnet build` fails
   with `An error occurred trying to start process 'cmd.exe'` — the package
   literally shells out to `cmd.exe` and cannot run on Linux/macOS at all,
   regardless of target framework. It compiles `Styles/Site.scss` →
   `wwwroot/css/site.g.css` (see `compilerconfig.json`). Replace with a
   cross-platform Sass build step — e.g. `dotnet-sass`, the
   `AspNetCore.SassCompiler` NuGet package, or a plain `sass`/Node CLI step —
   before anything else in this repo can build on the devcontainer's Linux host.

3. **Remove `DevExpress.Blazor`, replace the three charts with LiveCharts2**
   (`LiveChartsCore.SkiaSharpView.Blazor`, MIT). This was assessed earlier in
   this conversation: DevExpress is used *only* for `DxChart` in
   `Shared/Data/Graphs/{TrackLengthGraph,AddedAtGraph,AudioFeaturesGraph}.razor`
   (bar/line series with aggregation, dual value axes, and — the one feature
   worth preserving carefully — fully interactive `RenderFragment` tooltips
   containing a real `ActionText` click handler, not just an HTML string).
   Confirmed the pinned `20.1.9` no longer exists on nuget.org; restore silently
   resolves `25.1.3` instead, whose package only ships a `lib/net8.0` asset —
   incompatible with `net6.0`, so `@using DevExpress.Blazor` in
   `Shared/Data/Graphs/_Imports.razor` fails with `CS0246` even after restore
   succeeds. Bumping the TFM alone doesn't fix this: v25 is five majors ahead
   of v20 with a different API surface, so it's a full rewrite of those three
   files either way, and DevExpress remains a commercial/license-gated
   dependency after the rewrite. LiveCharts2 avoids re-introducing that
   license constraint. Also remove `DxExtendStartupHost` from this csproj and
   `CaerostrisServer.csproj`, and the DevExpress feed/license steps in
   `README.md` and `.devcontainer/post-create.sh`.

4. **Drop `Microsoft.AspNetCore.Components.Web.Extensions` (5.0.0-preview9).**
   `Shared/Info/TabTitle.razor` only uses it for the experimental `<Title>`
   component. That's been superseded by the built-in `<PageTitle>` component
   in `Microsoft.AspNetCore.Components.Web` since .NET 6 — a one-line swap that
   removes a five-year-old preview package entirely.

## Phase 2 — Dependency health

5. **`AutoMapper` 10.1.1** (`SpotifyService`) has a confirmed high-severity
   advisory (`NU1903`, GHSA-rvv3-g6hj-g44x) surfaced by `dotnet restore` today.
   Needs a bump — but AutoMapper 13+ moved to a commercial license above a
   revenue threshold, the same shape of problem as DevExpress. Decide: pay,
   pin the last free major, or replace with a source-generator alternative
   (e.g. Mapperly, MIT) while touching this file anyway.

6. **Dead `Blazor.Extensions.Storage` file reference.**
   `SpotifyService`'s csproj has both a NuGet `PackageReference` (`1.1.0-preview3`)
   *and* a `<Reference HintPath="..\Storage\src\...\bin\Release\netstandard2.0\...">`
   pointing at a fourth sibling checkout (`../Storage`) that isn't part of the
   documented 3-repo devcontainer setup and doesn't exist anywhere in this
   environment. It doesn't currently break the build (the PackageReference
   apparently satisfies the type), but it's a landmine — confirm the
   `HintPath` `Reference` can just be deleted, or track down whether it was
   pointing at local patches that never got published to the NuGet package.

7. **`Caerostris.Services.Spotify.IndexedDB` (1.5.12-preview) and
   `SpotifyAuthServer.Model` (1.0.0)** — both preview/frozen-1.0 packages,
   presumably from the same author. They resolve fine today; just worth a
   compile/runtime smoke-test pass once the TFM bump lands, since "resolves"
   isn't the same as "was ever tested against .NET 10."

8. **`SpotifyAPI.Web` 6.0.0** (`SpotifyService`) is several majors behind
   current. Not a build blocker by itself, but the wrapper code should be
   checked against whatever version is targeted, since this library has had
   breaking renames across majors.

## Phase 3 — Runtime/hosting modernization

9. **`SpotifyAuthServer`: `netcoreapp3.1` → `net10.0`, EF Core `3.1.3` → current.**
   No `Migrations/` folder exists in this repo (schema is created directly, not
   via migration snapshots), which removes the usual "migration graph doesn't
   apply across major EF versions" risk. Still need to check
   `EntityFrameworkCore.DataEncryption` (1.1.0) against a modern EF Core.

10. **`CaerostrisServer`'s `Startup.cs`** uses the classic .NET 5/6
    `IHostingStartup` + `UseBlazorFrameworkFiles()` + `UseEndpoints(...)`
    pattern for hosting the standalone WASM app. Confirm this still works
    unchanged under net10 (likely does, standalone-hosted WASM apps are still
    supported), or take the opportunity to move to the current minimal
    `Program.cs` hosting model.

11. **Trimming/linker settings.** `BlazorWebAssemblyEnableLinking=false` in
    `Caerostris.csproj` is Mono-linker-era config; WASM trimming/AOT controls
    have changed across net6→net8→net10. Revisit once building — leaving the
    stale flag may just be ignored, or may leave payload size/AOT options on
    the table that current defaults would give for free.

12. **Minor cleanup noticed in passing**: `Deterministic=False` and
    `AssemblyVersion=1.0.*` in `Caerostris.csproj` (the latter throws a
    `CS7035` warning today, "does not conform to major.minor.build.revision").
    Not blockers, cheap to fix while everything else is being touched.

## Phase 4 — Can't be caught by the compiler

13. **PWA/service-worker regression pass.** `wwwroot/service-worker.js` /
    `service-worker.published.js` and Blazor WASM's boot/caching pipeline have
    changed across net6→net8→net10. Once the app actually boots, manually
    verify offline caching and `manifest.json` behavior in a real browser
    (the `playwright` MCP server in this devcontainer is set up for exactly
    this) — this class of regression won't show up in `dotnet build`.

---

*Verification method: this list was produced by actually running
`dotnet restore`/`dotnet build` against all four sibling repos in the
devcontainer (SDK 10.0.301), not solely by reading project files — items 2
and 3 above are confirmed live build failures, not predictions.*
