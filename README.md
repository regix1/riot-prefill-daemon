# riot-prefill-daemon

A long-running **daemon** wrapper around [tpill90/riot-lancache-prefill](https://github.com/tpill90/riot-lancache-prefill).
It prefills a [LanCache](https://lancache.net/) with **Riot Games CDN content** (League of Legends + Valorant) so
subsequent client downloads are served from your local cache.

This image is designed to be driven by **[lancache-manager](https://github.com/regix1/lancache-manager)** over a
Unix-domain-socket (or TCP) IPC channel, exactly like the sibling `steam-prefill-daemon`, `epic-prefill-daemon`,
and `battlenet-prefill-daemon` images. lancache-manager spawns the container, connects to the socket, and issues
commands (select apps, prefill, clear cache, …) while receiving real-time progress events.

## Riot is anonymous — no account login

Unlike Steam (user / password / 2FA) and Epic (OAuth), **Riot prefill is fully anonymous**. Riot's manifest and CDN
endpoints are publicly accessible over plain HTTP (only requiring a `User-Agent: RiotNetwork/1.0.0` header), so:

- There is **no account login**, no credentials, and no OAuth flow.
- The daemon reports itself as ready/logged-in **immediately on connect**.
- `get-owned-games` returns the **fixed product list** (League of Legends, Valorant), not a personal library.
- The only security layer is the optional **socket HMAC handshake** (`PREFILL_SOCKET_SECRET`) — that secures the
  IPC transport, *not* any Riot account.

## Container image

```
ghcr.io/regix1/riot-prefill-daemon:latest
```

Multi-arch (`linux/amd64` + `linux/arm64`), built on native per-arch runners by `.github/workflows/docker-build.yml`.

The binary runs as **PID 1** (no `entrypoint.sh`). It listens on a Unix Domain Socket by default
(`/responses/daemon.sock`) and only switches to TCP when `PREFILL_TCP_PORT` is set (useful for Windows Docker Desktop
bind mounts). There is no `EXPOSE` and no docker-compose — lancache-manager launches and wires the container
programmatically.

### Volumes

| Path | Purpose |
|---|---|
| `/responses` | Daemon socket + responses (shared with lancache-manager) |
| `/commands` | Reserved for command exchange |
| `/app/Config` | Persisted user state (`selectedAppsToPrefill.json`) |
| `/app/.cache` | Downloaded manifest indexes / metadata (safe to delete) |

### Environment variables

| Variable | Default | Purpose |
|---|---|---|
| `PREFILL_RESPONSES_DIR` | `/responses` | Directory holding the socket + responses |
| `PREFILL_SOCKET_PATH` | `<responsesDir>/daemon.sock` | Unix socket path |
| `PREFILL_TCP_PORT` | *(unset)* | If set (>0), listen on TCP instead of a Unix socket |
| `PREFILL_SOCKET_SECRET` | *(unset)* | Shared HMAC secret; when set the first command must be `auth` |
| `LANCACHE_IP` | *(unset)* | Override LanCache detection (bypass poisoned-DNS resolution) |

Hidden CLI flags (passed as container args): `--verbose` / `--debug`, `--no-download`, `--nocache`.

## Command surface (socket IPC)

Length-prefixed JSON (`[4-byte LE Int32 length][UTF-8 JSON]`). Request → single response with the same `id`;
progress is pushed as unsolicited `progress` events.

| Command | Params | Notes |
|---|---|---|
| `auth` | `secret` | Socket handshake (only when `PREFILL_SOCKET_SECRET` is set) |
| `status` | — | `{ isLoggedIn:true, isInitialized:true }` (anonymous = always ready) |
| `get-owned-games` | — | Fixed product list `[{ appId:"league_of_legends", name:"League of Legends" }, { appId:"valorant", name:"Valorant" }]` |
| `get-selected-apps` | — | Product IDs from `selectedAppsToPrefill.json` |
| `set-selected-apps` | `appIds` (JSON array string) | Persists the selection |
| `get-selected-apps-status` | — | Selected apps + prefill status |
| `check-cache-status` | `appIds` (JSON array string) | Per-product up-to-date status |
| `prefill` | `all`, `force`, `products` (JSON array string) | Starts a prefill; progress via events |
| `cancel-prefill` | — | Cancels an in-flight prefill |
| `clear-cache` | — | Deletes the cache dir |
| `get-cache-info` | — | Cache size / file count |
| `shutdown` | — | Cleans up and exits |

Progress events use a `state` machine: `preparing` → `downloading` → `app_completed` / `already_cached` →
`completed` (or `error`).

## Supported products

| App ID | Display name | Notes |
|---|---|---|
| `league_of_legends` | League of Legends | Windows / NA patchline |
| `valorant` | Valorant | Windows / NA patchline |

Content is fetched from `*.dyn.riotcdn.net`. Release discovery uses `clientconfig.rpg.riotgames.com`; manifests are
RMAN files encoded as FlatBuffers.

## Building

```bash
git clone --recurse-submodules https://github.com/regix1/riot-prefill-daemon
cd riot-prefill-daemon
dotnet build RiotPrefill.sln
```

The `LancachePrefill.Common` submodule (`regix1/lancache-prefill-common`) provides the shared LanCache resolver,
`TempDirUtils`, the bundled `CliFx.dll`, and progress helpers.

### FlatSharp pre-generated code

RMAN manifests are parsed with [FlatSharp](https://github.com/jamescourtney/FlatSharp). The FlatSharp-generated C#
is **pre-generated and committed** to `RiotPrefill/ReleaseManifestFile/Generated/`, and `FlatSharp.Compiler` is
intentionally **not** a build dependency. This removes the `flatc` codegen step at build time — FlatSharp's bundled
`flatc` is x86-64-only and breaks native arm64 builds.

To regenerate after editing a `.fbs` schema: temporarily re-add the `FlatSharp.Compiler` PackageReference and
`FlatSharpSchema` items, build on an x64 machine, then recommit the updated `.cs` files.

## Architecture

The daemon is a fork of the upstream console tool with a small `RiotPrefill/Api/` layer added:

- `Program.cs` — daemon entrypoint (env parsing, UDS vs TCP, runs `DaemonMode`). No interactive prompts.
- `Api/SocketServer.cs` — length-prefixed JSON socket server + HMAC handshake.
- `Api/SocketCommandInterface.cs` — command dispatcher + socket progress emitter.
- `Api/RiotPrefillApi.cs` — wraps the upstream `ManifestHandler` / `DownloadHandler` / `CdnRequestManager` in-process.
- `Api/ApiConsoleAdapter.cs` — routes the upstream Spectre `IAnsiConsole` output to structured progress.
- `Handlers/`, `Models/`, `ReleaseManifestFile/` — upstream tool code.

## License

The upstream project ([tpill90/riot-lancache-prefill](https://github.com/tpill90/riot-lancache-prefill)) ships with
no explicit license. This daemon wrapper follows the same convention. Credit to
[@tpill90](https://github.com/tpill90) for the original riot-lancache-prefill tool.
