# RiotPrefill

[![License: MIT](https://img.shields.io/badge/License-MIT-blue?style=for-the-badge)](LICENSE)
[![Discord](https://dcbadge.vercel.app/api/server/BKnBS4u?style=for-the-badge)](https://discord.com/invite/BKnBS4u)

Riot Games prefill daemon for [LANCache](https://lancache.net/) — a companion to [**LANCache Manager**](https://github.com/regix1/lancache-manager), which coordinates the prefill providers.

It pre-downloads Riot Games content (League of Legends and Valorant) **through your lancache** so the cache is warm before installing — the real install then comes from your LAN at full speed. No data is written to disk; bytes are streamed through the cache and discarded.

## How it works

Riot prefill is **fully anonymous** — no account login required. Riot's manifest and CDN endpoints are publicly accessible over plain HTTP:

1. **Select titles** — choose from League of Legends and Valorant.
2. **Resolve** — release metadata is fetched from `clientconfig.rpg.riotgames.com`; manifests (RMAN/FlatBuffers format) and CDN URLs are resolved from `*.dyn.riotcdn.net`.
3. **Prefill** — each title's content is fetched through the lancache and discarded, warming the cache.

## Requirements

- A running [LANCache](https://lancache.net/) with the **`riot`** cache-domain group enabled (from [uklans/cache-domains](https://github.com/uklans/cache-domains)).
- No account login needed — Riot CDN content is publicly accessible.
- Docker, or the [.NET 8 SDK](https://dotnet.microsoft.com/) to build from source.

## Running it

RiotPrefill runs as a **daemon** driven by [**LANCache Manager**](https://github.com/regix1/lancache-manager) — set it up there alongside the other prefill providers.

To build and run standalone:

```bash
dotnet build RiotPrefill/RiotPrefill.csproj -c Release
dotnet run  --project RiotPrefill/RiotPrefill.csproj -c Release
```

## Support & License

Questions or issues? [Open an issue](https://github.com/regix1/riot-prefill-daemon/issues), or find the LANCache community on the [LanCache.NET Discord](https://discord.com/invite/BKnBS4u).

Licensed under the MIT License (see [LICENSE](LICENSE)); a fork of the lancache-prefill tools by Tim Pilius ([@tpill90](https://github.com/tpill90)).

If these tools have been useful, you can support the original author on [ko-fi](https://ko-fi.com/Y8Y5DWGZN) or support this fork via [buy me a coffee](https://www.buymeacoffee.com/regix).
