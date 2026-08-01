# Sandbox Design: Agentic Claude in a Dev Container
 
## Purpose and scope
 
This document describes the sandbox design for running an agentic, Opus-class
Claude Code workflow inside a dev container. The goal is a setup where Claude can
work productively on code — editing files, running builds and tests, driving a
headless browser — without being able to exfiltrate secrets or reach arbitrary
network destinations if a prompt injection or a malicious dependency tries to
steer it.
 
The design targets a specific environment and makes deliberate trade-offs for it.
It is an *interactive* configuration: Claude's own tools are gated by permission
prompts rather than run unattended in bypass mode, and a network boundary sits
underneath as a backstop for the things permissions cannot see.
 
## Environment and assumptions
 
- **Host:** WSL2 with a Linux-native Docker install (not the Docker Desktop WSL
  integration). All containers and any alternative runtime share the single
  Microsoft-supplied WSL2 kernel.
- **Stack:** .NET (`dotnet`), some npm usage, and Playwright driving headless
  browsers. Browser tests currently target only the local app on `localhost`;
  external targets are considered a possible future need.
- **Scale:** one WSL2 distro, two dev containers.
- **Trust posture:** repositories are trusted, but the design adds defense in
  depth against prompt injection and compromised dependencies. It is not intended
  to safely run genuinely hostile code.
## Threat model
 
The primary threat is **exfiltration driven by prompt injection or a malicious
dependency**: text in a repo, a web page, or a build artifact convinces Claude
(or code it runs) to send secrets or sensitive data to an attacker-controlled
destination, or to take a destructive action.
 
The design defends against this with four independent layers, so that defeating
one does not defeat the whole:
 
1. The container isolates the filesystem and processes from the host.
2. Permission rules gate Claude's own tools interactively.
3. A network boundary allows only approved egress destinations.
4. Secrets are simply not present in the container.
Explicitly **out of scope**: defending against a determined attacker running
fully hostile code with the intent to escape the container itself. For that,
use an ephemeral VM per task. The dev container is a strong fence, not a vault.
 
## Architecture overview
 
Four layers, from outermost to innermost:
 
| Layer | Mechanism | What it contains |
|---|---|---|
| Container | Docker (filesystem + process namespaces) | Blast radius; host stays clean |
| Egress | Proxy-caged Docker network topology | Where any subprocess (incl. the browser) can connect |
| Permissions | `.claude/settings.json` allow/ask/deny | What Claude's own tools may do, interactively |
| Secrets | Not mounted | Nothing sensitive to read or leak |
 
The permission layer and the egress layer are complementary, not redundant:
permission rules are enforced by Claude Code and apply to Claude's tools (Bash,
Read, Edit, WebFetch, MCP); the egress boundary is enforced by the network and
applies to *every* process, including ones that open their own sockets and never
touch a Claude tool at all (a build script, a headless browser).
 
## Layer 1: The container as the trust boundary
 
The container provides filesystem and process isolation and is the primary
boundary. Because the blast radius is the container rather than the host, it is
reasonable to let Claude act inside it with relatively little friction. Everything
below is layered on top of this boundary; none of it weakens it.
 
A consequence recorded here because it drove several later decisions: **the
container is already the sandbox.** Adding a second, nested OS-level sandbox inside
it is only worthwhile if it does not degrade the container — a condition that does
not hold in this environment (see Rejected alternatives).
 
## Layer 2: Interactive permission gating
 
Claude's own tools are governed by `.claude/settings.json`. The mode is chosen so
that file work and routine dev commands are silent, while anything that reaches
the network or is destructive stops for a decision.
 
```json
{
  "permissions": {
    "defaultMode": "acceptEdits",
    "allow": [
      "Bash(git *)",
      "Bash(dotnet build *)", "Bash(dotnet test *)", "Bash(dotnet run *)",
      "Bash(dotnet restore *)", "Bash(dotnet add *)", "Bash(dotnet format *)",
      "Bash(npm *)", "Bash(npx *)",
      "Bash(ls *)", "Bash(cat *)", "Bash(grep *)", "Bash(rg *)",
      "Bash(find *)", "Bash(sed *)", "Bash(mkdir *)", "Bash(cp *)", "Bash(mv *)"
    ],
    "ask": [
      "WebFetch", "WebSearch",
      "Bash(git push *)", "Bash(npm publish *)",
      "Bash(dotnet nuget push *)",
      "Bash(dotnet tool install *)", "Bash(dotnet tool update *)"
    ],
    "deny": [
      "Read(./.env)", "Read(./.env.*)", "Read(.env)", "Read(./secrets/**)",
      "Read(~/.ssh/**)", "Read(~/.aws/**)", "Read(**/*.pem)",
      "Bash(curl *)", "Bash(wget *)", "Bash(nc *)", "Bash(ncat *)",
      "Bash(ssh *)", "Bash(scp *)",
      "Bash(rm -rf *)"
    ]
  }
}
```
 
### Design principles for the three buckets
 
Precedence is **deny -> ask -> allow**, first match wins; a matching `ask`
overrides a broader `allow`, and `deny` cannot carry exceptions.
 
- **`acceptEdits`** makes file reads/writes inside the workspace silent while
  leaving unmatched shell commands to prompt. That is the "inside is quiet"
  behavior without opening up shell execution wholesale.
- **`allow`** lists the everyday commands that should never interrupt. Package
  managers stay here even though they touch registries; the network boundary
  (Layer 3), not a prompt, is what constrains where they can reach.
- **`ask`** is for judgment calls — operations that are *sometimes* correct and
  benefit from a human decision each time: outbound web access, publishing,
  pushing, installing tools.
- **`deny`** is for operations where the approval click is itself the failure.
  Reading a credential into context is denied because a "yes" is what causes the
  leak; `rm -rf` is denied because there is no good approval story for it; the raw
  network binaries (`curl`, `wget`, ...) are denied so that all sanctioned network
  access is funnelled through the one auditable path rather than a leaky side door.
The deciding test for a new rule: *imagine clicking "approve" at the end of a long
day.* If "yes" is sometimes right, it is `ask`; if "yes" is always a mistake, it
is `deny`.
 
### Environment runners
 
Some commands are not single tools but launchers for arbitrary code, and the
permission layer only sees the launcher, not the inner command. These are handled
with care:
 
- **`tmux`** is the worst case: it is both an arbitrary-exec channel
  (`new-session`, `send-keys`, `run-shell`) and a client/server tool whose server
  runs work outside the boundary that wrapped the launching call. It is kept out
  of `allow` entirely; the exec verbs may additionally be denied. Long-running
  processes are managed by the test runner instead (see Layer 5), not by a
  persistent multiplexer.
- **`pwsh`**, **`dotnet run`**, and **`dotnet build`** likewise execute arbitrary
  project code (including MSBuild `<Exec>` tasks). They remain in `allow` for
  usability, but with the explicit understanding that the network boundary — not
  the permission layer — is what actually contains what that code does. This is
  the core reason Layer 3 exists.
Permission changes hot-reload into a running session, so tightening a rule takes
effect on the next tool call without a restart; `/status` confirms which settings
sources loaded.
 
## Layer 3: Egress control (proxy-caged topology)
 
The permission layer cannot see network calls made by subprocesses — a headless
browser's `page.goto`, a build script's socket, a compiled test binary. Those are
governed here, at the container network layer, using nothing but Docker network
topology plus a filtering forward proxy. Critically, this approach requires **no
`NET_ADMIN`, no `ipset`, and no hand-written firewall** — so it does not depend on
the WSL2 kernel's netfilter features, which are the source of the friction that
ruled out the alternative (see Rejected alternatives).
 
### Topology
 
Two Docker networks:
 
- `internal_net` with `internal: true` — no route to the outside world.
- `egress` — a normal bridge with outbound access.
The dev container attaches to `internal_net` **only**, so it has no path to the
internet. A Squid proxy sidecar attaches to **both** networks, making it the sole
bridge between the caged agent and the internet. The default-deny property falls
out of the topology; it is not expressed as rules anyone maintains.
 
```yaml
services:
  devcontainer:
    networks: [internal_net]          # no direct egress
    environment:                      # container-wide, so every process is proxied
      HTTP_PROXY: http://proxy:3128
      HTTPS_PROXY: http://proxy:3128
      http_proxy: http://proxy:3128
      https_proxy: http://proxy:3128
      NO_PROXY: localhost,127.0.0.1,::1
      no_proxy: localhost,127.0.0.1,::1
  proxy:
    image: ubuntu/squid:<pinned-tag>
    networks: [internal_net, egress]  # reachable by agent AND can reach internet
    volumes:
      - ./squid/squid.conf:/etc/squid/squid.conf:ro
      - ./squid/allowed_domains.txt:/etc/squid/allowed_domains.txt:ro
networks:
  internal_net: { internal: true }
  egress: { driver: bridge }
```
 
Proxy env is set container-wide (in compose `environment:`, not only VS Code's
`remoteEnv`) so that commands Claude runs via bash inherit it, not just processes
VS Code launches. `NO_PROXY` exempts loopback, which is what keeps the current
localhost Playwright tests untouched.
 
### Allowlist
 
Squid enforces a default-deny allowlist, filtering HTTPS on the hostname in the
CONNECT request — so domain filtering works **without TLS interception**. The
implemented list (`.devcontainer/squid/allowed_domains.txt`) is broader than the
first draft of this doc, for two reasons found while implementing it:

```
.anthropic.com  .claude.ai  .claude.com     # CRITICAL — see below
.nuget.org
.npmjs.org  .npmjs.com
.github.com  .githubusercontent.com
learn.microsoft.com                        # microsoft-learn MCP server (.mcp.json)
vscode.download.prss.microsoft.com         # VS Code Server + Marketplace bootstrap —
update.code.visualstudio.com               # re-fetched every rebuild, since
marketplace.visualstudio.com               # ~/.vscode-server isn't in a
download.visualstudio.microsoft.com        # persisted named volume
.vsassets.io  .vscode-cdn.net
```

(`.gallerycdn.vsassets.io` was dropped from the first draft — it's a subdomain
of `.vsassets.io`, already covered by that wildcard entry; listing both is a
redundant/confusing entry, not an additional protection.)

- **Claude Code's own allowlist is bigger than `.anthropic.com`.** The official
  list (`code.claude.com/docs/en/network-config`) includes `claude.ai` (sign-in),
  `claude.com`/`platform.claude.com` (auth), `mcp-proxy.anthropic.com`, and
  `code.claude.com` (doc lookups), all covered by the `.anthropic.com`/`.claude.ai`/
  `.claude.com` wildcards above. `DISABLE_AUTOUPDATER=1` and
  `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1` (set in `docker-compose.yml`) trim
  this further by cutting the auto-updater and Datadog telemetry hosts entirely,
  rather than allowlisting them.
- **VS Code itself needs to bootstrap through the cage.** Attach and port
  forwarding go host→container over the Docker bridge and don't touch the egress
  boundary at all — but the *first* thing VS Code does on a fresh/rebuilt container
  is download the VS Code Server binary and Marketplace extensions (here,
  `ms-dotnettools.csdevkit`) **from inside the container** over HTTPS, which is
  exactly the traffic the cage governs. Miss these domains and the failure mode
  isn't an error, it's a container that never finishes attaching.

The `.anthropic.com`/`.claude.ai`/`.claude.com` entries are load-bearing: once the
agent is on an internal-only network, Claude Code and the VS Code extension can
reach the Anthropic API *only* through the proxy. Omitting them takes Claude
offline.

**A live incident confirmed a sharper version of that risk.** Claude Code reads
`HTTP_PROXY`/`HTTPS_PROXY` from `.claude/settings.json`'s `env` block as well as
the process environment — and unlike `devcontainer.json`, settings.json changes
apply to the *running* session immediately, no rebuild required. Adding the proxy
vars there while the compose stack (and the `proxy` hostname) didn't exist yet
broke the session's own API connectivity on the spot. **Sequencing matters: add
the proxy vars to `.claude/settings.json` only after the compose stack is up and
`proxy` actually resolves — never as part of the same edit that introduces the
compose file.**

The allowlist is grown from evidence, not guesswork: Squid logs allow/deny to
stdout, so a blocked `dotnet restore` or plugin call names the missing domain in
`docker compose logs proxy`. Add it, `docker compose restart proxy`, done. Private
feeds (`pkgs.dev.azure.com`, `nuget.pkg.github.com`) and runtime .NET SDK/workload
domains are added the same way if needed.
 
## Layer 4: Secrets
 
Host credentials are **not bind-mounted** into the container — no `~/.ssh`,
`~/.aws`, or `.env`. The container boundary then does the work that an OS-sandbox
read-denylist would otherwise do: there is nothing sensitive present for a
subprocess to read. The permission-layer `Read` denies remain as a second lock,
keeping any repo-local secrets out of Claude's own tools and context. Between the
two, both leak paths — Claude's tools and arbitrary subprocesses — are covered
without an OS sandbox.
 
## Layer 5: Playwright and headless browsers
 
A headless browser is the sharpest case of subprocess egress: `page.goto` bypasses
the permission layer entirely and is governed only by Layer 3. The handling:
 
- **Browsers are installed at build time**, in the Dockerfile
  (`playwright install --with-deps`), not at runtime. Build-time networking runs
  through the Docker daemon with full access, avoiding the runtime proxy's DNS
  behavior that breaks Playwright's downloader.
- **Long-running processes are the runner's job, not tmux's.** Playwright's
  `webServer` config starts and stops the app under test around the run; results
  come from the HTML report / trace, not from watching a terminal pane. This is
  what removes any need for a persistent multiplexer.
- **Today (localhost only):** browser traffic is loopback, exempt via `NO_PROXY`,
  and never touches the egress boundary. Nothing external is required.
- **Future (external targets):** point the browser at the proxy
  (`chromium.launch({ proxy: { server: 'http://proxy:3128' } })`) so a
  proxy-aware Chromium delegates DNS over CONNECT, and add the target domains to
  the allowlist. Two lines, no rearchitecture — this is the reason the proxy
  approach was chosen over alternatives for a workload that may grow into external
  browsing.
## Rejected alternatives
 
### OS-level sandbox (bubblewrap / `/sandbox`) nested in the container
 
Rejected. Making bubblewrap function inside the container is not a package
problem; the container's confinement blocks the unprivileged user namespaces
bubblewrap needs. Enabling it requires loosening the container with
`--security-opt seccomp=unconfined` and/or `--cap-add SYS_ADMIN` — weakening the
*primary* boundary to stand up a nested one that is itself deliberately weakened
(`enableWeakerNestedSandbox`). That is a net downgrade. In a container, the OS
sandbox and the container are alternatives, not a stack; here the container wins.
 
### In-container iptables/ipset firewall (the reference `init-firewall.sh`)
 
Rejected for this environment. Its friction is kernel-level, and on WSL2 the
kernel is a shared, Microsoft-controlled component:
 
- The iptables backend (nftables vs legacy) must match what the WSL2 kernel
  supports, and Docker-on-WSL2 commonly needs the legacy backend.
- `ipset` needs kernel modules that are not guaranteed present in the WSL2 kernel.
- WSL2 kernels have shipped netfilter regressions (e.g. a missing `raw` table)
  that break this outright.
- The allowlist is expressed in domains but enforced on IPs resolved at init, so
  CDN IP rotation (NuGet, GitHub) causes sporadic, hard-to-debug failures
  mid-session.
The maintenance cost is not a steady chore but an unpredictable tail of breakage
tied to `wsl --update` and CDN churn — precisely the two categories the proxy
approach does not have (it never touches the kernel netfilter stack and matches on
hostname, not a frozen IP set).
 
### Swapping the container runtime to fix the above
 
Does not help the egress problem. Podman, containerd, and Docker all ride the same
single WSL2 kernel, so the netfilter friction is unchanged — rootless Podman on
WSL2 reproduces the exact "iptables table does not exist" failure. Rootless
runtimes make an in-container firewall *harder*, since they cannot program
iptables and route through userspace (pasta/slirp4netns).
 
Runtime choice is worth making on a *different* axis: rootless Podman gives a
stronger container boundary (container-root maps to an unprivileged host UID), and
the proxy-caged topology ports to it directly. So the runtime decision is
independent of the egress design and can be made later for boundary strength, not
to rescue a firewall.
 
## Verification
 
After bringing the stack up, confirm the four properties from inside the container:
 
1. `curl -I https://example.com` -> **blocked** (Squid 403). Default-deny works.
2. `curl -I https://api.nuget.org/v3/index.json` -> **succeeds**. Allowlist works.
3. `dotnet restore` -> **succeeds** (proxy-aware, DNS delegated over CONNECT).
4. Localhost Playwright test -> **succeeds**, unchanged (NO_PROXY exemption).
5. VS Code finishes "Reopen in Container" — Explorer populates and the C# Dev
   Kit extension installs — without hanging on a stuck spinner. That hang, not
   an explicit error, is what a missing VS Code Server/Marketplace domain looks
   like from the outside.
Also confirm Claude itself can reach the API (`claude --debug`, check `/status`
for the active proxy row) and that `gh auth status` / the NuGet cache still show
prior state (proves named volumes carried over rather than starting fresh). It
will fail immediately if `.anthropic.com` is missing from the allowlist. Re-run
a non-allowlisted egress test periodically to catch any accidental fail-open.
 
## Operations and maintenance
 
- **Pin the proxy image** to a specific tag; `latest` drifting inside a security
  boundary is an avoidable surprise. (Canonical doesn't publish a numbered
  `_stable` tag for `ubuntu/squid`, only `_edge`/`_beta` channels — pin one of
  those by digest after the first pull rather than trusting the tag alone.)
- **Grow the allowlist from proxy logs**, not speculation.
- **`sudo` resets the environment by default**, which silently drops
  `HTTP_PROXY`/`HTTPS_PROXY` for any `sudo`-run command even though the rest of
  `post-create.sh` (running as the non-root user) inherits them fine. Fixed with
  an `/etc/apt/apt.conf.d/` proxy entry for `apt-get` specifically, rather than
  relying on sudoers' `env_keep`.
- **`.claude/settings.json`'s `env` block applies live, with no rebuild gate** —
  unlike every other change in this design. Add the proxy vars there only once
  the compose stack is actually up (see the Allowlist section above); doing it
  earlier takes Claude's own session offline immediately.
- **Permission changes hot-reload**; egress/allowlist changes need a
  `docker compose restart proxy`.
- **Two dev containers, one distro:** the proxy and allowlist can be shared as a
  base with per-project deltas; there is no kernel state to reconstruct across the
  `wsl --shutdown` cycles that WSL2 instability sometimes forces.
## Residual risks
 
- **Hostname-only filtering / domain fronting.** With no TLS interception, a
  client could open a CONNECT to an allowlisted host but send a different SNI/Host
  inside the tunnel. Acceptable under the trusted-repo posture; the escalation
  path is Squid `ssl_bump` with a managed CA if the threat model hardens.
- **DNS-based exfiltration.** A theoretical channel independent of the proxy;
  closable by constraining the container's DNS if ever required.
- **Trusted-repository assumption.** The whole design assumes repositories are not
  actively hostile. Genuinely untrusted code belongs in an ephemeral VM, not this
  dev container.
- **Substrate instability (orthogonal).** WSL2 host<->VM (vsock) instability can
  disrupt the environment, but it is independent of this sandbox design and is not
  mitigated or caused by any layer here. It is an operational concern for the host,
  addressed separately.
## Summary
 
The design leans on the container as the real boundary, gates Claude's own tools
interactively with allow/ask/deny rules, and fences all subprocess egress with a
proxy-caged network topology that needs no kernel privileges and therefore sits
comfortably on WSL2. Secrets are kept out of the container rather than merely
hidden from one tool. Every layer is chosen to hold without weakening the layer
beneath it — which is precisely why the nested OS sandbox and the in-container
firewall were rejected, and why the topology-based proxy was chosen instead.