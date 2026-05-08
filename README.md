# GlamourCheck

GlamourCheck is a Dalamud plugin for Final Fantasy XIV gear collection QoL.

The plugin tracks the latest known local collection snapshot for a character across player inventory, equipped gear, armory chest, glamour dresser, armoire, chocobo saddlebag, and retainer inventories. It uses that state to show compact gear icon collection markers, current-instance completion summaries, Duty Finder counters, and seeded Garland loot-table coverage.

## Development Entry Points

- Agent guide: [AGENTS.md](AGENTS.md)
- Documentation index: [docs/README.md](docs/README.md)
- Local reference repositories: [docs/07-reference-repos.md](docs/07-reference-repos.md)
- Ordered task list: [docs/TASKS.md](docs/TASKS.md)
- Roadmap: [docs/ROADMAP.md](docs/ROADMAP.md)

## Current State

The repository builds as `GlamourCheck`. `/glamourcheck` opens the tabbed settings window. Implemented systems include source snapshot sync, gear icon overlays, item-detail/crystallize markers, Garland-only loot lookup and expansion, local seed management, current-instance counters, Duty Finder row/detail counters, and the interactive try-on popup.

## Build Prerequisites

- XIVLauncher, Final Fantasy XIV, and Dalamud installed.
- Dalamud has been run at least once.
- Current .NET SDK compatible with the Dalamud template in this repository.
- `DALAMUD_HOME` set only if the local Dalamud dev path is non-standard.

## Storage Direction

The primary persistence layer is SQLite with in-memory read models for fast collection checks and Duty Finder summaries. JSON export/import can be added later for backup or debugging if it becomes useful.

## Garland Tools Direction

The sibling `../garlandtools-api` repository documents useful Garland Tools JSON endpoints. GlamourCheck uses a direct C# HTTP client instead of embedding the stale Node wrapper.

## Local References

The workspace also contains `../Dalamud`, `../FFXIVClientStructs`, `../Glamaholic`, `../AutoRetainer`, `../Artisan`, and `../ECommons`. These should be used as local references for current APIs, client structs, glamour dresser/armoire behavior, retainer flows, and optional helper patterns.
