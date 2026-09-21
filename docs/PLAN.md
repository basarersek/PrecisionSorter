# PrecisionSorter - Project Plan

Rust game plugin (C# / Oxide). Per-box item and category sorting with a searchable picker UI.
Target: 500-1000 player servers, zero item loss, zero duplication.

## 1. Goal

Give players a reliable way to bind a storage box to an exact set of items and categories, then move loot in one click.
Improve on AbsolutSorter in three areas: filter precision, UI quality, and server safety.

## 2. What AbsolutSorter does and where it falls short

AbsolutSorter binds each box to accepted items/categories through an in-inventory panel, then offers
This / Nearby / Arrange / Dump All / Loot All actions. It works, but:

- Filter UI is small and bottom-right, no search. Finding one item in 400+ items is slow.
- Category-only selection is coarse. "Weapon" pulls in everything; you cannot say "rockets only, no ammo".
- No rate limiting or move validation story for large populations.

PrecisionSorter keeps the same muscle memory (same action names, same open-box flow) and fixes all three.

## 3. Stack

- Language: C# (.NET Framework compatible, compiled at runtime by Oxide).
- Framework: Oxide/uMod first. Carbon port starts only after Oxide is shipping-ready.
  Carbon mirrors Oxide's folder layout and API, so the port is mostly a re-test, not a rewrite.
- Dev server: local Windows Rust Dedicated Server (SteamCMD app 258550) with Oxide installed.
- Editor: Rider 2026.2.1 (present) or VS Code.

## 4. Verified Rust API (read from the installed Assembly-CSharp.dll)

Categories (`ItemCategory` enum, exact names):
`Weapon, Construction, Items, Resources, Attire, Tool, Medical, Food, Ammunition, Traps, Misc, Component, Electrical, Fun`
plus internal `All, Common, Search, Favourite` which the UI must hide.

Note the real names differ from AbsolutSorter's display labels: `Ammunition` (not "Ammo"), `Misc` (not "Miscellaneous"), `Component` (not "Components"). We show friendly labels, map to these values.

Item identity:
- `ItemDefinition.shortname` (string) - skin-agnostic key. All skins of one item share it.
- `ItemDefinition.itemid` (int), `.displayName` (Phrase), `.category` (ItemCategory), `.stackable` (int).
- `Item.info` (ItemDefinition), `.skin` (ulong), `.amount` (int), `.parent` (ItemContainer), `.uid` (ItemId).
- Item catalog source: `ItemManager.itemList` (List<ItemDefinition>). Cached once, never rescanned.

Containers and moves:
- `ItemContainer.itemList` (List<Item>), `.capacity` (int), `.Insert(Item)` -> bool, `CanAccept`, `IsFull`, `Take`.
- `Item.MoveToContainer(ItemContainer, int targetPosition = -1, bool allowStack = true, ...)` -> bool.
- `Item.SplitItem(int)` -> Item. `Item.RemoveFromContainer()`.
- Boxes, fridges, coffins: `StorageContainer`. Furnaces and campfires are ovens.
  Use the shared `IItemContainerEntity` interface to cover both; read `.inventory`.

## 5. Box identity and persistence (the reliability core)

A box must remember its filter across server restarts. Net IDs change every restart, so they are only a session cache.

Key: `ownerId | prefabShortname | x,y,z` with 2-decimal position rounding (about 1 cm).
Deployable collision stops two boxes from sharing a coordinate, so adjacent boxes never collide like coarse rounding would.
The key survives restarts and reconnects. It only breaks if a box is picked up and redeployed, which is acceptable and detectable.

Data file: `oxide/data/PrecisionSorter/BoxFilters.json`, grouped by owner for fast load and save.
Each record stores the key, mode, categories, items, and a `wipeStamp`.
On `OnNewSave` the wipe stamp rotates and all records are dropped, so a new map starts clean.

Saves are batched: mark dirty, write on a timer and on `OnServerSave` / shutdown. Never write per move.

## 6. Filter model

Each box holds one filter:
- Mode: `Whitelist` (accept only listed) or `Blacklist` (accept all except listed).
- Categories: set of `ItemCategory` values.
- Items: set of item shortnames.
- Match rule: item is accepted if its shortname is listed, or its `category` is listed. Skins never matter.
- An item-level entry always wins over a category entry, so "Weapon except rockets" is expressible.

This gives exactly the requested behavior: select `Assault Rifle` -> every skin qualifies. Select `LR300` -> only LR300.
Select `Ammunition` then remove rockets -> rockets out.

## 7. Actions (same names as AbsolutSorter)

- This: move accepted items from the player inventory into the open box.
- Nearby: for each accepted inventory item, route it to the nearest authorized box within radius whose filter accepts it. Spreads across the base.
- Arrange: reorder box contents so accepted items come first (by name), the rest after (by name).
- Dump All: move the whole inventory into the box, ignoring the filter.
- Loot All: move the box contents into the player inventory.

Gating: `RequireBuildingPrivilege` for This/Nearby/Arrange. `RespectNoEscape` blocks all actions while raid blocked,
using the NoEscape API when that plugin is loaded (detected via `Interface.Oxide`, optional soft dependency).

## 8. UI

Replace the cramped corner panel with a proper overlay:
- Header with box name and mode toggle.
- Search field at the top (CUI input field) that filters the item grid live by display name.
- Category row: one button per real category, with friendly labels and item counts.
- Item grid: icon + name, click toggles membership. Selected items listed separately for quick review.
- Action bar: This / Nearby / Arrange / Dump All / Loot All, each permission-gated and dimmed when unavailable.
- Apply and Clear.

Cursor handling is explicit (`CursorEnabled`, keyboard capture for the search field) so looting never soft-locks the player.
Icons: prefer the game's own item sprites; fall back to a bundled icon sheet if sprite access is unreliable, decided in P1.

## 9. Performance for 500-1000 players

- Item catalog built once at load. No per-open or per-tick DB scans.
- Work happens only inside `OnLootEntity`. No global entity iteration.
- Nearby uses a single sphere query (`Vis.Entities` / server query) within radius, filtered to containers.
- Per-player cooldown from `SortsPerMinute`. Excess calls are dropped with a message, not queued.
- Move loops iterate a snapshot copy of the item list, so mutations during the pass are safe.
- Data writes batched and off the hot path.
- No per-player timers when idle; the panel is event-driven.

## 10. Item integrity (no dupes, no vanish)

- Every transfer goes through `MoveToContainer` / `Insert`, which are the engine's atomic stack-merge paths. No manual item creation or deletion.
- `SplitItem` is used only when a partial stack must move; the remainder stays owned by the source.
- `MoveToContainer` return value is checked. On false the item stays where it is; nothing is dropped.
- Per-player `sorting` guard flag prevents hook re-entrancy while a sort runs (`OnItemAddedToContainer` and `CanMoveItem` from other plugins).
- Snapshot-before-move: capture `itemList` to an array first, then move. Never iterate the live list while moving.
- Optional audit log (config) records moved counts per player per action for admin review.

## 11. Permissions

- `precisisionsorter.use` - base panel and This sort.
- `precisisionsorter.nearby` - Nearby action.
- `precisisionsorter.arrange` - Arrange action.
- `precisisionsorter.dumpall` - Dump All action.
- `precisisionsorter.lootall` - Loot All action.
- `precisisionsorter.admin` - debug commands, audit log access.

Server owner assigns these to groups. Nothing is free unless the owner grants it.

## 12. Config (owner-facing)

Every option has a sane default, so a fresh install works untouched. The file is rewritten on load,
so options added in a later version appear without wiping the owner's file.

- `AllowedContainers` - prefab shortnames that may carry a filter.
- `ChatCommands` - chat aliases, default `ps`. Registered at load, so owners pick their own name.
- `NearbyRadius`, `RequireBuildingPrivilege`, `RespectNoEscape`, `IncludeHotbar`, `SortsPerMinute`.
- `LogActions` - writes a buffered audit file to `oxide/logs/PrecisionSorter/<date>.txt`.
- `EnableHarness` - exposes the test harness. Default false, keep false on a live server.
- `Ui` - anchor, panel offsets for both panels, and the eight palette colours.

Known trap: Json.NET appends to a pre-filled collection instead of replacing it, so list defaults must
stay empty in the class and be supplied by `PluginConfig.Default()`. Otherwise every load duplicates
each entry.

## 13. Roadmap

- P1 (toolchain proof): DONE. Plugin loads, catalog caches 1259 items, permissions register.
- P2 (core): DONE. Filter storage and box identity, This + Arrange, data save/load, wipe reset.
- P3 (full actions): DONE. Nearby, Dump All, Loot All, building privilege, NoEscape gating, cooldown.
- P4 (UI): DONE. Main panel, search field, category row, paged item grid, mode toggle, cursor and keyboard handling.
- P5 (config + harden): DONE for config. Audit log, population load test and live-client edge cases remain.
- P6 (Carbon port): re-test on Carbon after Oxide ships.

## 14. Testing phases

- T0 compile and boot - automated. Plugin loads, catalog builds, no exceptions. PASSING.
- T1 matcher truth table - automated. `ps.selftest` asserts the matcher and reports item and category drift
  against the live DB. PASSING: 5 assertions plus 2 drift checks.
- T2 item conservation - automated. `ps.harness run <n>` spawns real boxes, creates real items and runs
  This, Dump and Arrange repeatedly, asserting item count, total stack amount, reference uniqueness and
  capacity. PASSING: 300 iterations, 0 conservation failures, 0 duplicates, 0 capacity violations.
- T3 persistence - automated. `ps.harness save` writes a filter, the server restarts, then
  `ps.harness check` reads it back. PASSING.
- T4 gating matrix - needs a client for the chat path. Permission, privilege, raid block and cooldown refusals.
- T5 single client - manual. Panel scope, search field, cursor and keyboard, icon rendering, each action.
- T6 concurrency - manual, two clients. Simultaneous sorts on one box, moves during a sort, adjacent boxes.
- T7 load - timing a Nearby sort with many boxes, then a soak.
- T8 update regression - re-run T0 to T3 after every Facepunch and Oxide update.

100% is not attainable: Facepunch can change item data every month, and dupe or UI faults only appear
with a real client. T2 and T3 are the mechanical guard; T5 and T6 are yours.

## 15. Risks and open questions

- CUI input fields behave differently across client resolutions; needs a real client test.
- `IItemContainerEntity` and `MoveToContainer` signatures are confirmed by compile on the dev server, not yet by reflection (PS 5.1 cannot reflect default-interface types).
- NoEscape API surface varies by version; the soft-dependency wrapper isolates that.
- Stashes and small boxes share the same prefab family; verify the allowed-list defaults on a live wipe.

## 15. Dev workflow

- Dev server: `C:\RustServer\server` (SteamCMD + Oxide). Launch with `tools\start_server.bat`.
- Deploy: run `tools\deploy.ps1`. Oxide hot-reloads the plugin file, no restart needed.
- Verify load: watch `Server.log` for the plugin line, then run `oxide.plugins` or `/ps` in game.
- Repo: `C:\RustPlugins\PrecisionSorter`, branch `main`, private remote.

Verified on this machine: server boots to "Server startup complete", plugin loads, catalog caches 1259 items, no exceptions.

Gotcha: `+server.level "Procedural Map"` must reach the process as one quoted argument.
Windows PowerShell 5.1 `Start-Process -ArgumentList <array>` drops the quotes, splits the value, and the server
boots with no map and crashes in `NetworkVisibilityGrid`. Pass one pre-quoted argument string, or use the batch file.

Gotcha: `+server.queryport` defaults to `server.port + 1`, which collides with `rcon.port 28016`.
The query port answers with the Steam A2S protocol, so an RCON client sees a broken HTTP handshake.
Keep them apart: game 28015, RCON 28016, query 28017, app 28018.

RCON protocol (read from `Facepunch.Rcon.dll`): the password is the WebSocket request path, and frames are JSON.
Connect to `ws://127.0.0.1:28016/<password>`, send `{"Identifier":1,"Message":"<command>","Name":"dev"}`.
`tools\rcon.ps1` does this. The first frame back can be a server broadcast, not the command reply.

P2 verified on this machine: `ps.selftest` proves the matcher against the live DB
(whitelist Weapon minus rockets accepts 110 of 1259 items; rifle.ak True; ammo.rocket.basic False; ammo.rifle False).
