# SSH Manager — Right-Sidebar Design

**Date:** 2026-07-18
**Status:** Approved design, not yet implemented
**Branch target:** feature branch off `perf-fixes`/`main`

## Goal

A remote-SSH manager living in the right sidebar (beside the agent panel):
browse hosts, connect in a terminal tab, run background port-forwards, and
manage per-host env/secrets. Distinguishes Velo from stock Windows terminals,
which have no first-class SSH surface.

## Locked decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Scope | Full manager, phased | Ship the launcher (Phase 1) alone; layer the rest |
| Secrets | Windows Credential Manager (WinRT `PasswordVault`) | Encrypted at rest, per-user, no P/Invoke |
| Port-forwards | Standalone headless `ssh -N -L` procs | Survive tab close; match the toggle UI |
| `~/.ssh/config` | Read-only merge | Never rewrite the user's hand-tuned file |

## Architecture

An SSH connection **reuses the existing tab spawn path**. Connecting = opening a
terminal tab whose `LaunchCmd` is `ssh <args> <host>`, run through Windows
OpenSSH `ssh.exe` in the PTY Velo already drives. No SSH library, no new PTY
path. New code is confined to discovery, storage, tunnels, and the sidebar UI —
all C# under `csharp/Velo.App/`, following the existing `ShellProfiles` /
`SessionState` patterns (records + static discovery + JSON load/save).

## Components (new files under `csharp/Velo.App/`)

### `SshConfig.cs` — read-only config parser
- Parses `~/.ssh/config` into `SshHost(Alias, HostName, User, Port, ProxyJump, IdentityFile)`.
- Handles `Include` directives, arbitrary indentation, case-insensitive keywords.
- Skips pure-wildcard `Host *` / glob-only patterns (not launchable aliases).
- Cached after first parse, like `ShellProfiles.All()`.
- Never writes.

### `SshStore.cs` — Velo's own extras
- `%APPDATA%\velo\ssh.json`.
- `VeloSshHost(Alias, HostName?, User?, Port?, ProxyJump?, Forwards[], Env{}, SecretRefs{})`.
- `Forward(LocalPort, RemoteHost, RemotePort, Enabled)`.
- `Load()` / `Save()` mirror `SessionState` (best-effort, corrupt file → empty).

### `SshManager.cs` — merge + argv + connect
- Merges `SshConfig` ∪ `SshStore` by alias into display VMs (`SshHostVM`).
  Store fields override/augment config fields for the same alias.
- Builds ssh argv: `ssh [-J <jump>] [-p <port>] [<user>@]<host>`.
- `Connect(alias)` → reuse `NewTab` with the built `LaunchCmd` and an ssh icon.

### `CredentialStore.cs` — secrets
- Thin wrapper over WinRT `Windows.Security.Credentials.PasswordVault`.
- Key format `velo/<alias>/<name>`; `ssh.json` stores only `cred:velo/<alias>/<name>` refs.
- `Write(key, value)` / `Read(key)` / `Delete(key)`.
- Vault unavailable → fall back to in-memory session secret + warn.

### `TunnelManager.cs` — background port-forwards
- Owns headless `ssh -N -L <local>:<rhost>:<rport> <host>` processes keyed by `(alias, forward)`.
- `Start` / `Stop`; status derived from process lifetime + stderr: `starting | up | failed`.
- Plain `Process` (no PTY); capture stderr for the failure tooltip.
- **No auto-retry in v1.**

### UI — right sidebar SSH section
- New collapsible **SSH** section beside the agent panel.
- Host tree; each host expands to a panel: **Connect** button, forward toggles
  (with live status dot), masked env/secret rows, **Edit**.
- **Add host** form (alias, host, user, port, jump).

## Data flow

1. **Discover** — `SshConfig.Parse()` ∪ `SshStore.Load()` → merged `SshHostVM` list.
2. **Connect** — build argv → `NewTab(launchCmd, sshIcon)` (existing path).
3. **Forward on** — `TunnelManager.Start(alias, fwd)` spawns `ssh -N -L…`; toggle
   goes green on `up`, red + stderr tooltip on non-zero exit.
4. **Secret set** — `PasswordVault.Add`; `cred:` ref written to `ssh.json`.
5. **Env** — applied to the **local** spawned process environment (correct for
   `PGPASS` against a forwarded DB port). Sending env to the remote is **out of
   scope v1** (needs server `AcceptEnv`).
6. **Persist** — host/forward/env-ref edits → `SshStore.Save()`.

## Error handling

- Missing `ssh.exe` → Connect disabled + "enable OpenSSH Client" hint.
- Malformed `~/.ssh/config` lines → skipped, never crash.
- Tunnel failure → red status + stderr tooltip; no retry (v1).
- `PasswordVault` unavailable → in-memory session secret + warning.

## Persistence

- `ssh.json` holds hosts, forwards, and env/secret refs.
- Tunnels are **not** live-restored (they die with the app, like shells). The
  enabled/disabled flag of each forward is stored; auto-relaunching enabled
  tunnels on startup is a later-phase `ponytail:` follow-up.

## Phasing

1. **Launcher** — config parse + read-only merge + connect-in-tab. Usable alone.
2. **Managed hosts** — add/edit/delete in `ssh.json`, merged with config.
3. **Forwards** — toggles + `TunnelManager`.
4. **Secrets** — env + Credential Manager.

## Testing

- **`SshConfig` parser** — unit tests over sample configs: `Include`, glob
  patterns, indentation variants, missing fields, case-insensitive keywords.
- **`SshManager` argv builder** — unit tests: alias → argv for jump / port /
  user permutations.
- **Tunnels + vault** — verified manually on the Windows host, consistent with
  the rest of the app (Windows-only runtime surface).

## Out of scope (v1)

- Sending env vars to the remote host.
- Tunnel auto-retry / auto-restore on launch.
- Writing back to `~/.ssh/config`.
- SSH key generation / management (defer to ssh-agent / OpenSSH).
