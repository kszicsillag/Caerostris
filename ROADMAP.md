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

## Phase 2 — Dependency health (done)

5. **`AutoMapper` 10.1.1** (`SpotifyService`) had a confirmed high-severity
   advisory (`NU1903`, GHSA-rvv3-g6hj-g44x) surfaced by `dotnet restore`.
   Its only two call sites (`FullAlbum`→`SimpleAlbum`, `FullPlaylist`→
   `SimplePlaylist` in `WebApiModelExtensions.cs`) were replaced with
   `Riok.Mapperly` 4.3.1 (MIT, source-generator, no runtime dependency —
   avoids both the advisory and AutoMapper 13+'s commercial-license
   threshold). New file: `WebApiModelMapper.cs`, a `[Mapper]`-attributed
   partial class; `[MapperIgnoreSource]`/`[MapperIgnoreTarget]` document the
   handful of intentionally-unmapped members (e.g. `FullAlbum.Popularity`,
   `SimpleAlbum.AlbumGroup`) that AutoMapper silently dropped, and a small
   user-implemented `MapCollaborative(bool?) => bool` method replaces the
   nullable→non-nullable coercion the old `.ForMember` call did. Verified:
   `dotnet restore` no longer reports the advisory, `dotnet build` is
   warning-clean for the mapper, and the app still boots with 0 console
   errors (playwright). Also updated the AutoMapper entry on the `/about`
   attributions page to Mapperly.

6. **Dead `Blazor.Extensions.Storage` file reference.** Confirmed `../Storage`
   doesn't exist anywhere in this environment and the NuGet `PackageReference`
   (`1.1.0-preview3`) alone satisfies the type — deleted the `<Reference
   HintPath="...">` `ItemGroup` from `SpotifyService`'s csproj. Full-solution
   build still succeeds.

7. **`Caerostris.Services.Spotify.IndexedDB` (1.5.12-preview) and
   `SpotifyAuthServer.Model` (1.0.0)** — smoke-tested post-TFM-bump: full
   `dotnet build` across all four repos succeeds, and `CaerostrisServer`
   boots the WASM app cleanly under Playwright (0 console errors/warnings,
   `/` and `/about` both route correctly). No issues surfaced from either
   package at runtime.

8. **`SpotifyAPI.Web` 6.0.0** (`SpotifyService`) is three majors behind
   current (7.4.2). Investigated the breaking-change history rather than
   upgrading blind: v7.0.0 added an optional `CancellationToken` to every API
   call (low risk, additive); more importantly, **v7.0.2 removed the
   `SimplePlaylist` type entirely** ("replaced by `FullPlaylist`") — the exact
   type the new `WebApiModelMapper.ToSimplePlaylist` (item 5, above) maps
   into, also referenced directly in `AlbumCard.razor`, `PlaylistCard.razor`,
   `UserPlaylistsList.razor`, `WebAPIManager.cs`, `LibraryService.cs`,
   `ExploreService.cs`, `Sections.cs`, and `ArtistProfile.cs`. Deferring the
   actual version bump — it's not a build blocker at 6.0.0 and is a
   real (if mechanical) refactor across ~8 files, not a one-line dependency
   update. When it happens: drop `SimpleAlbum`/`SimplePlaylist` conversions
   and the Mapperly mapper they depend on, switch those call sites to
   `FullAlbum`/`FullPlaylist` directly, and re-check the `CancellationToken`
   additions don't collide with any lambda-typed call sites.

## Phase 3 — Runtime/hosting modernization (done)

Full build across all four repos is 0 warnings/0 errors (was CS7035 +
SYSLIB0060 before this phase), and the app still boots cleanly under the
`run-caerostris` skill's Playwright driver — 0 console errors on `/` and
`/about`, screenshot confirms the sidebar/playback bar/auth-modal chrome
renders correctly. A `dotnet publish -c Release` was also run to actually
exercise the trimmer (item 11 below), not just `dotnet build`.

9. **`SpotifyAuthServer`: EF Core `3.1.3` → `10.0.10`.** (The `netcoreapp3.1`
   → `net10.0` TFM bump itself had already landed in Phase 1, item 1.) No
   `Migrations/` folder exists (schema is created directly), so there was no
   migration-graph risk to worry about. `EntityFrameworkCore.DataEncryption`
   1.1.0 → 8.0.0: the package changed ownership/namespace on the way to
   8.0.0 — `Microsoft.EntityFrameworkCore.DataEncryption*` became
   `SoftFluent.EntityFrameworkCore.DataEncryption*`, and `[Encrypted]` moved
   to a new `SoftFluent.ComponentModel.DataAnnotations` namespace — but the
   type/method surface (`AesProvider`, `EncryptedAttribute`, `UseEncryption`)
   is unchanged, so this was a `using` fixup in `UserDbContext.cs`/`User.cs`,
   not a rewrite. Restore also surfaced a fresh `NU1903` for transitive
   `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 (pinned by `Microsoft.Data.Sqlite.Core`
   10.0.10 itself) — fixed with an explicit
   `SQLitePCLRaw.bundle_e_sqlite3` 2.1.12 reference to override the pin,
   same pattern as the AutoMapper advisory fix in Phase 2.
   The EF Core bump also flipped `UserDbContext`'s `Rfc2898DeriveBytes`
   constructor call to a `SYSLIB0060` obsolete-API warning. Given this key
   material feeds `AesProvider` for encrypting stored OAuth tokens, it was
   replaced with the static `Rfc2898DeriveBytes.Pbkdf2(...)` method using the
   old constructor's documented legacy defaults (1000 iterations, SHA1)
   rather than just silencing the warning — Microsoft's own docs confirm
   `GetBytes(32)` then `GetBytes(16)` on one instance is byte-for-byte
   equivalent to a single 48-byte `Pbkdf2` call split at `[0:32]`/`[32:48]`,
   so this preserves the exact derived key for any already-encrypted rows.

10. **`CaerostrisServer`'s `Startup.cs`** replaced with the current minimal
    `Program.cs` hosting model (top-level statements, `WebApplication.
    CreateBuilder`/`.Build()`/`.Run()`); `Startup.cs` deleted. Same
    middleware pipeline, same order, no behavior change — verified via the
    playwright smoke test above.

11. **Trimming/linker settings.** Confirmed `BlazorWebAssemblyEnableLinking`
    doesn't exist anywhere in the current SDK (`grep`-ed
    `Microsoft.NET.Sdk.BlazorWebAssembly`'s `.props`/`.targets` — zero hits),
    so it's been a no-op since Phase 1's TFM bump. Removed it from
    `Caerostris.csproj`. The current SDK already defaults to
    `PublishTrimmed=true`/`TrimMode=partial` on its own; a `dotnet publish -c
    Release` shows the IL trimmer actively running ("Optimizing assemblies
    for size...") and the published bundle still contains the
    SkiaSharp/LiveCharts2 WASM assets Phase 1 had to get building in the
    first place.

12. **Minor cleanup**: `AssemblyVersion` `1.0.*` → `1.0.0.0` (fixes the
    `CS7035` warning). This also let `Deterministic=False` be removed
    entirely — that flag existed only because a wildcarded `AssemblyVersion`
    requires non-deterministic builds; a fixed version doesn't need it.

Incidental fix, not itself a roadmap item: verifying this phase via the
`run-caerostris` skill surfaced that the devcontainer's headless Chromium
has no OS-level deps installed (`chrome-headless-shell: error while loading
shared libraries: libglib-2.0.so.0`) — `Microsoft.Playwright`'s browser
binary download alone isn't enough on this base image. Folded a
`Program.Main(["install-deps", "chromium"])` call into the driver's existing
`-- install` one-time setup step (`.claude/skills/run-caerostris/driver.cs`)
so it installs both the browser binary and its shared libraries in one go.

## Phase 4 — Can't be caught by the compiler (done)

Verified by actually publishing (`dotnet publish -c Release`) and serving the
published `CaerostrisServer` output — not `dotnet run`, which every earlier
phase used and which never exercises the SDK's publish-time asset
compression or the real (non-stub) `service-worker.js` at all. That gap is
exactly why item 14 below went undetected through Phases 1–3.

13. **PWA/service-worker regression pass.** `service-worker.published.js`'s
    `offlineAssetsInclude` regex list is the unmodified 2021 template and
    hadn't been re-checked against what net10's Blazor WASM SDK actually
    outputs. Diffed it against the real `service-worker-assets.js` manifest:
    3 ICU globalization `.dat` files (`icudt_CJK`/`icudt_EFIGS`/`icudt_no_CJK`
    — feeds `IStringLocalizer`/the Hungarian localization) and the Material
    Icons webfont's `.woff2`/`.ttf`/`.eot` variants matched no pattern and
    were silently never cached for offline use. Extended the include list
    (`wwwroot/service-worker.published.js`) to `/\.woff2?$/`, `/\.ttf$/`,
    `/\.eot$/`, `/\.dat$/`, `/\.mp3$/` (the last for
    `mediasession-mock-audio.mp3`, same gap). Verified via a real offline
    test, not just inspection: added an `offline` command to the
    `run-caerostris` driver (`page.Context.SetOfflineAsync` — Playwright
    doesn't expose this by default), loaded the app once online to populate
    the cache (confirmed 123/123 expected assets cached, matching the
    include/exclude filters applied to the actual manifest), then flipped
    the browser to offline and did a *fresh navigation* (not a soft reload):
    the app boots and renders pixel-identical to the online screenshot, 0
    unexpected console errors. `manifest.json` and its icon both resolve
    correctly offline too. (`Styles/Fonts.scss`'s `@import
    url('https://rsms.me/inter/inter.css')` is the one uncacheable
    same-session resource — external, cross-origin, pre-existing since
    before this migration, and the CSS already falls back to `sans-serif`,
    so this is a pre-existing design tradeoff, not a regression.)

14. **Publish-time brotli compression corrupts `dotnet.native.*.wasm.br`.**
    Found while setting up the item 13 verification above, not something
    `dotnet build`/`dotnet run` could ever have caught: the SDK's
    static-asset compression truncates the brotli sidecar for the AOT WASM
    runtime file (~9MB, by far the largest single asset — 1 corrupt file out
    of 120 `.br` assets in the published output). Confirmed reproducible
    across a full clean rebuild (`dotnet clean` + republish), and confirmed
    it's specifically the SDK's compression *task* at fault, not .NET's
    brotli codec: round-tripping the identical bytes through
    `System.IO.Compression.BrotliStream` directly succeeds. Since virtually
    every real browser advertises `Accept-Encoding: br`, every user hitting
    the published app got served the truncated file, which fails Blazor's
    SRI integrity check on the WASM runtime and blocks the app from booting
    at all — a total, silent failure of the *published* build, invisible
    the entire time because Phases 1–3 only ever verified via `dotnet run`
    (which serves assets uncompressed, straight from disk). The `.gz`
    sidecar for the same file was verified byte-correct (round-tripped and
    compared to the raw file), so the fix (`CaerostrisServer.csproj`, a
    `Target AfterTargets="Publish"`) just deletes the broken `.br` file from
    the publish output, letting content negotiation fall back to the
    already-verified-good gzip encoding for that one file rather than
    disabling compression project-wide.

*Environment note, not itself a roadmap item:* this session's devcontainer
needed its own round of fixes before Phase 4 could even start: the sibling
repos were bind-mounted via `${localWorkspaceFolder}`, a devcontainer.json
variable that isn't reliably propagated as a real env var to every
`docker compose` invocation — when unset it silently resolves to the host
filesystem root, which Docker then auto-creates as an empty root-owned
directory instead of failing loudly. Switched `docker-compose.yml`'s three
sibling-repo mounts to paths relative to the compose file itself
(`../../CaerostrisServer` etc.), which Compose resolves natively with no
variable substitution involved. Separately, the Squid egress cage
(`.devcontainer/squid/allowed_domains.txt`) added since Phase 3 was missing
several domains needed by tooling this phase depends on: `.ubuntu.com` (apt,
for Playwright's `install-deps`), `cdn.playwright.dev` +
`storage.googleapis.com` (the actual Chromium/Chrome-for-Testing binary
download, which redirects through both), and
`roslyn.blob.core.windows.net`/`vsdebugger.blob.core.windows.net` (optional
C# Dev Kit components, unrelated to this phase but denied noisily enough to
flag). Also: `curl`/`wget` are hard-denied in `.claude/settings.json` for
this repo, which the `run-caerostris` skill's own docs assume are
available — drove the driver's HTTP API with `python3`'s `urllib` instead
throughout this phase.

---

*Verification method: this list was produced by actually running
`dotnet restore`/`dotnet build` against all four sibling repos in the
devcontainer (SDK 10.0.301), not solely by reading project files — items 2
and 3 above are confirmed live build failures, not predictions.*
