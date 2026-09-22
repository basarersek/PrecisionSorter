using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Oxide.Core;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("PrecisionSorter", "Basar Ersek", "0.7.0")]
    [Description("Per-box item and category sorting with a searchable picker UI.")]
    public class PrecisionSorter : RustPlugin
    {
        // ------------------------------------------------------------------ identity

        private const string UsePerm     = "precisionsorter.use";
        private const string NearbyPerm  = "precisionsorter.nearby";
        private const string DumpAllPerm = "precisionsorter.dumpall";
        private const string LootAllPerm = "precisionsorter.lootall";
        private const string ArrangePerm = "precisionsorter.arrange";

        private const string PanelId  = "precisionsorter.panel";
        private const string DataFile = "PrecisionSorter/BoxFilters";

        private const string TestBoxPrefab = "assets/prefabs/deployable/woodenbox/woodbox_deployed.prefab";

        // ------------------------------------------------------------------ ui palette

        private string ColorPanel    => config.Ui.Colors.Panel;
        private string ColorHeader   => config.Ui.Colors.Header;
        private string ColorButton   => config.Ui.Colors.Button;
        private string ColorAction   => config.Ui.Colors.Action;
        private string ColorDanger   => config.Ui.Colors.Danger;
        private string ColorMuted    => config.Ui.Colors.Muted;
        private string ColorSelected => config.Ui.Colors.Selected;
        private string ColorText     => config.Ui.Colors.Text;

        private const int GridColumns = 6;
        private const int GridRows    = 4;
        private const int PageSize    = GridColumns * GridRows;
        private const int MaxNameLength = 14;

        // ------------------------------------------------------------------ game data

        // Internal enum members that are not real item groups.
        private static readonly string[] HiddenCategories = { "All", "Common", "Search", "Favourite" };

        // Fixed positions let the harness prove filter persistence across a restart.
        private static readonly Vector3 HarnessSourcePos = new Vector3(0f, 200f, 0f);
        private static readonly Vector3 HarnessTargetPos = new Vector3(4f, 200f, 0f);

        private static readonly string[] HarnessItems =
        {
            "wood", "stone", "metal.fragments", "scrap", "cloth", "lowgrade",
            "rifle.ak", "ammo.rifle", "ammo.rocket.basic", "syringe.medical", "bandage", "sewingkit"
        };

        // NoEscape and Raid Block expose different names across versions; any true blocks.
        private static readonly string[] RaidBlockHooks =
        {
            "API_IsRaidBlocked", "IsRaidBlocked", "API_IsEscapeBlocked", "IsEscapeBlocked"
        };

        private static readonly string[] CategoryNames = Enum.GetNames(typeof(ItemCategory))
            .Where(name => !HiddenCategories.Contains(name))
            .ToArray();

        // ------------------------------------------------------------------ state

        private PluginConfig config;
        private SorterData data;

        private readonly List<ItemDefinition> catalog = new List<ItemDefinition>();
        private readonly Dictionary<string, ItemDefinition> byShortname = new Dictionary<string, ItemDefinition>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ulong, Session> sessions = new Dictionary<ulong, Session>();
        private readonly List<string> auditBuffer = new List<string>();

        private bool dataDirty;
        private bool saveScheduled;
        private bool auditScheduled;

        // Per-player UI and rate-limit state, dropped when the player leaves.
        private class Session
        {
            public BaseEntity Box;
            public string Search;
            public string BrowseCategory;
            public int Page;
            public bool Busy;
            public string CachedQuery;
            public string CachedBrowse;
            public List<ItemDefinition> CachedItems;
            public readonly List<float> SortTimes = new List<float>();
        }

        // A box the nearby sort may use, with its filter and distance resolved once.
        private class SortTarget
        {
            public ItemContainer Container;
            public BoxFilter Filter;
            public float Distance;
        }

        // ------------------------------------------------------------------ lifecycle

        protected override void LoadDefaultConfig() => Config.WriteObject(PluginConfig.Default(), true);

        private void Init()
        {
            LoadConfig();
            RegisterPermissions();
            RegisterChatCommands();
            LoadData();
        }

        // Aliases come from the config, so owners can pick the command name they want.
        private void RegisterChatCommands()
        {
            var aliases = config.ChatCommands
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Select(alias => alias.Trim().ToLowerInvariant())
                .Distinct();

            foreach (var alias in aliases)
            {
                cmd.AddChatCommand(alias, this, CmdChat);
            }
        }

        // ItemManager.itemList is empty during Init, so the catalog waits for server init.
        private void OnServerInitialized() => BuildCatalog();

        private void OnNewSave(string filename)
        {
            data.Boxes.Clear();
            SaveData();
            Puts("Wipe detected: box filters cleared.");
        }

        private void OnServerSave()
        {
            FlushAudit();

            if (dataDirty)
            {
                SaveData();
            }
        }

        private void Unload()
        {
            FlushAudit();

            if (dataDirty)
            {
                SaveData();
            }

            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player != null)
                {
                    CuiHelper.DestroyUi(player, PanelId);
                }
            }
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player != null)
            {
                sessions.Remove(player.userID);
            }
        }

        private void OnLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (player == null || entity == null || !IsSortableContainer(entity))
            {
                return;
            }

            if (!permission.UserHasPermission(player.UserIDString, UsePerm))
            {
                return;
            }

            var session = GetSession(player);
            session.Box = entity;
            session.Search = null;
            session.BrowseCategory = null;
            session.Page = 0;
            ShowUi(player);
        }

        private void OnLootEntityEnd(BasePlayer player, BaseCombatEntity entity)
        {
            if (player == null)
            {
                return;
            }

            CuiHelper.DestroyUi(player, PanelId);
            sessions.Remove(player.userID);
        }

        // ------------------------------------------------------------------ setup

        private void LoadConfig()
        {
            try
            {
                config = Config.ReadObject<PluginConfig>();
            }
            catch
            {
                PrintWarning("Config unreadable, using defaults.");
                config = null;
            }

            if (config == null)
            {
                config = PluginConfig.Default();
            }

            var defaults = PluginConfig.Default();

            if (config.AllowedContainers == null)
            {
                config.AllowedContainers = defaults.AllowedContainers;
            }

            if (config.ChatCommands == null)
            {
                config.ChatCommands = defaults.ChatCommands;
            }

            if (config.Ui == null)
            {
                config.Ui = defaults.Ui;
            }

            if (config.Ui.Colors == null)
            {
                config.Ui.Colors = defaults.Ui.Colors;
            }

            // Writes back so options added in later versions appear in an existing file.
            Config.WriteObject(config, true);
        }

        private void RegisterPermissions()
        {
            permission.RegisterPermission(UsePerm, this);
            permission.RegisterPermission(NearbyPerm, this);
            permission.RegisterPermission(DumpAllPerm, this);
            permission.RegisterPermission(LootAllPerm, this);
            permission.RegisterPermission(ArrangePerm, this);
        }

        // Caches every real item once so no per-open scan of the item DB is needed.
        private void BuildCatalog()
        {
            catalog.Clear();
            byShortname.Clear();

            if (ItemManager.itemList == null)
            {
                PrintWarning("Item list not ready; catalog empty.");
                return;
            }

            foreach (var def in ItemManager.itemList)
            {
                if (def == null || string.IsNullOrEmpty(def.shortname) || HiddenCategories.Contains(def.category.ToString()))
                {
                    continue;
                }

                catalog.Add(def);
                byShortname[def.shortname] = def;
            }

            Puts($"Catalog cached: {catalog.Count} items.");
        }

        private void LoadData()
        {
            data = Interface.Oxide.DataFileSystem.ReadObject<SorterData>(DataFile) ?? new SorterData();

            if (data.Boxes == null)
            {
                data.Boxes = new Dictionary<string, BoxFilter>();
            }

            foreach (var filter in data.Boxes.Values)
            {
                filter.Rebuild();
            }
        }

        private void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(DataFile, data);
            dataDirty = false;
        }

        // Batches writes so a busy server never saves per item move.
        private void MarkDirty()
        {
            dataDirty = true;

            if (saveScheduled)
            {
                return;
            }

            saveScheduled = true;
            timer.Once(30f, () =>
            {
                saveScheduled = false;
                if (dataDirty)
                {
                    SaveData();
                }
            });
        }

        // ------------------------------------------------------------------ audit log

        // Buffered so file IO never happens on the game thread during a sort.
        private void Audit(BasePlayer player, string message)
        {
            if (!config.LogActions)
            {
                return;
            }

            auditBuffer.Add(string.Format(CultureInfo.InvariantCulture, "[{0:yyyy-MM-dd HH:mm:ss}] {1} {2}: {3}",
                DateTime.UtcNow, player.userID, player.displayName, message));

            if (auditScheduled)
            {
                return;
            }

            auditScheduled = true;
            timer.Once(60f, FlushAudit);
        }

        private void FlushAudit()
        {
            auditScheduled = false;

            if (auditBuffer.Count == 0)
            {
                return;
            }

            try
            {
                var directory = Path.Combine(Interface.Oxide.LogDirectory, "PrecisionSorter");
                Directory.CreateDirectory(directory);

                var file = Path.Combine(directory, DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".txt");
                File.AppendAllLines(file, auditBuffer);
            }
            catch (Exception ex)
            {
                PrintWarning($"Audit write failed: {ex.Message}");
            }

            auditBuffer.Clear();
        }

        // ------------------------------------------------------------------ box identity

        // Owner, prefab and position survive restarts; net IDs do not. Invariant culture keeps keys stable.
        private static string BuildKey(ulong ownerId, string prefab, Vector3 position)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2:F2},{3:F2},{4:F2}",
                ownerId, prefab, position.x, position.y, position.z);
        }

        private static string BuildKey(ulong ownerId, BaseEntity entity)
        {
            return BuildKey(ownerId, entity.ShortPrefabName, entity.transform.position);
        }

        // The filter belongs to the box, so teammates share one configuration.
        private static ulong FilterOwner(BaseEntity entity, BasePlayer player)
        {
            if (entity.OwnerID != 0)
            {
                return entity.OwnerID;
            }

            return player != null ? player.userID : 0ul;
        }

        private BoxFilter GetFilter(ulong ownerId, BaseEntity entity)
        {
            BoxFilter filter;
            return data.Boxes.TryGetValue(BuildKey(ownerId, entity), out filter) ? filter : null;
        }

        private BoxFilter EnsureFilter(ulong ownerId, BaseEntity entity)
        {
            var key = BuildKey(ownerId, entity);
            BoxFilter filter;

            if (!data.Boxes.TryGetValue(key, out filter))
            {
                filter = new BoxFilter();
                data.Boxes[key] = filter;
            }

            return filter;
        }

        // Skin never matters; exclude wins, so "Weapon except rockets" is expressible.
        private static bool Accepts(BoxFilter filter, ItemDefinition def)
        {
            if (filter == null || def == null)
            {
                return false;
            }

            if (filter.Exclude.Contains(def.shortname))
            {
                return false;
            }

            bool listed = filter.Items.Contains(def.shortname) || filter.ParsedCategories.Contains(def.category);
            return filter.Mode == FilterMode.Whitelist ? listed : !listed;
        }

        // ------------------------------------------------------------------ access rules

        private static bool IsSortableContainer(BaseEntity entity)
        {
            return entity is IItemContainerEntity;
        }

        private static bool CanReach(BasePlayer player, BaseEntity box)
        {
            if (box.OwnerID == player.userID)
            {
                return true;
            }

            var privilege = box.GetBuildingPrivilege();
            return privilege != null && privilege.IsAuthed(player);
        }

        private bool RaidBlocked(BasePlayer player)
        {
            if (!config.RespectNoEscape)
            {
                return false;
            }

            foreach (var hook in RaidBlockHooks)
            {
                var result = Interface.CallHook(hook, player);
                if (result is bool && (bool)result)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool CooldownExceeded(Session session, int perMinute, float now)
        {
            if (perMinute <= 0)
            {
                return false;
            }

            session.SortTimes.RemoveAll(time => now - time > 60f);
            return session.SortTimes.Count >= perMinute;
        }

        // Pure check: never mutates the session, so callers can decide when to charge.
        private bool CanOperate(BasePlayer player, BaseEntity box, Session session, string perm, out string reason)
        {
            reason = null;

            if (!permission.UserHasPermission(player.UserIDString, UsePerm))
            {
                reason = "You do not have permission to use the sorter.";
                return false;
            }

            if (!string.IsNullOrEmpty(perm) && !permission.UserHasPermission(player.UserIDString, perm))
            {
                reason = "You do not have permission for that action.";
                return false;
            }

            if (RaidBlocked(player))
            {
                reason = "You cannot sort while raid blocked.";
                return false;
            }

            if (config.RequireBuildingPrivilege && !CanReach(player, box))
            {
                reason = "You must be authorized on this base.";
                return false;
            }

            if (CooldownExceeded(session, config.SortsPerMinute, Time.realtimeSinceStartup))
            {
                reason = "Sorting too fast, wait a moment.";
                return false;
            }

            return true;
        }

        // ------------------------------------------------------------------ core operations

        // Snapshot first, then move: never iterate a live list while it mutates.
        private static int MoveMatching(BoxFilter filter, ItemContainer target, List<ItemContainer> sources)
        {
            int moved = 0;

            foreach (var source in sources)
            {
                if (source?.itemList == null)
                {
                    continue;
                }

                foreach (var item in source.itemList.ToList())
                {
                    if (item?.info != null && Accepts(filter, item.info) && item.MoveToContainer(target))
                    {
                        moved++;
                    }
                }
            }

            return moved;
        }

        private static int MoveEverything(ItemContainer target, List<ItemContainer> sources)
        {
            int moved = 0;

            foreach (var source in sources)
            {
                if (source?.itemList == null)
                {
                    continue;
                }

                foreach (var item in source.itemList.ToList())
                {
                    if (item?.info != null && item.MoveToContainer(target))
                    {
                        moved++;
                    }
                }
            }

            return moved;
        }

        private static int LootAll(ItemContainer source, ItemContainer destination)
        {
            if (source?.itemList == null)
            {
                return 0;
            }

            int moved = 0;

            foreach (var item in source.itemList.ToList())
            {
                if (item?.info != null && item.MoveToContainer(destination))
                {
                    moved++;
                }
            }

            return moved;
        }

        private static int Arrange(BoxFilter filter, ItemContainer target)
        {
            var list = target?.itemList;
            if (list == null || list.Count < 2)
            {
                return 0;
            }

            var ordered = list
                .OrderBy(item => Accepts(filter, item.info) ? 0 : 1)
                .ThenBy(item => item.info.displayName.english, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (int i = 0; i < ordered.Count; i++)
            {
                ordered[i].position = i;
            }

            target.MarkDirty();
            return ordered.Count;
        }

        // Resolves reachable boxes once, nearest first, so distance is the natural tie-break.
        private List<SortTarget> NearbyTargets(BasePlayer player)
        {
            var targets = new List<SortTarget>();
            var candidates = new List<BaseEntity>();

            Vis.Entities(player.transform.position, config.NearbyRadius, candidates);

            foreach (var entity in candidates)
            {
                if (entity == null || entity.IsDestroyed || !IsSortableContainer(entity))
                {
                    continue;
                }

                if (!config.AllowedContainers.Contains(entity.ShortPrefabName) || !CanReach(player, entity))
                {
                    continue;
                }

                var container = ((IItemContainerEntity)entity).inventory;
                if (container == null)
                {
                    continue;
                }

                targets.Add(new SortTarget
                {
                    Container = container,
                    Filter = GetFilter(FilterOwner(entity, player), entity),
                    Distance = Vector3.Distance(player.transform.position, entity.transform.position)
                });
            }

            targets.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            return targets;
        }

        // An explicitly listed item outranks a category; a blacklist box is the least specific.
        private static int Specificity(BoxFilter filter, ItemDefinition def)
        {
            if (!Accepts(filter, def))
            {
                return -1;
            }

            if (filter.Mode != FilterMode.Whitelist)
            {
                return 0;
            }

            if (filter.Items.Contains(def.shortname))
            {
                return 2;
            }

            return filter.ParsedCategories.Contains(def.category) ? 1 : 0;
        }

        // Most specific tier first, nearest box first; a full box falls through.
        private static bool TryRoute(Item item, List<SortTarget> byDistance)
        {
            for (int tier = 2; tier >= 0; tier--)
            {
                foreach (var target in byDistance)
                {
                    if (Specificity(target.Filter, item.info) != tier)
                    {
                        continue;
                    }

                    if (item.MoveToContainer(target.Container))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private int SortNearby(BasePlayer player, List<ItemContainer> sources)
        {
            var targets = NearbyTargets(player);
            if (targets.Count == 0)
            {
                return 0;
            }

            int moved = 0;

            foreach (var source in sources)
            {
                if (source?.itemList == null)
                {
                    continue;
                }

                foreach (var item in source.itemList.ToList())
                {
                    if (item?.info != null && TryRoute(item, targets))
                    {
                        moved++;
                    }
                }
            }

            return moved;
        }

        private List<ItemContainer> SourceContainers(BasePlayer player)
        {
            var sources = new List<ItemContainer> { player.inventory.containerMain };

            if (config.IncludeHotbar)
            {
                sources.Add(player.inventory.containerBelt);
            }

            return sources;
        }

        // ------------------------------------------------------------------ session and ui state

        private Session GetSession(BasePlayer player)
        {
            Session session;
            if (!sessions.TryGetValue(player.userID, out session))
            {
                session = new Session();
                sessions[player.userID] = session;
            }

            return session;
        }

        private bool TryGetOpenBox(BasePlayer player, out Session session, out BaseEntity entity, out ItemContainer container)
        {
            session = GetSession(player);
            entity = session.Box;
            container = null;

            if (entity == null || entity.IsDestroyed)
            {
                session.Box = null;
                return false;
            }

            var owner = entity as IItemContainerEntity;
            if (owner == null)
            {
                return false;
            }

            container = owner.inventory;
            return container != null;
        }

        private void CloseUi(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, PanelId);
            sessions.Remove(player.userID);
        }

        private void ShowUi(BasePlayer player)
        {
            Session session;
            BaseEntity entity;
            ItemContainer container;

            if (!TryGetOpenBox(player, out session, out entity, out container))
            {
                // A destroyed box would otherwise leave the player stuck with a dead panel.
                CuiHelper.DestroyUi(player, PanelId);
                sessions.Remove(player.userID);
                return;
            }

            var filter = EnsureFilter(FilterOwner(entity, player), entity);
            var visible = VisibleItems(session);
            int pageCount = Math.Max(1, (visible.Count + PageSize - 1) / PageSize);
            session.Page = Math.Min(Math.Max(session.Page, 0), pageCount - 1);

            CuiHelper.DestroyUi(player, PanelId);
            CuiHelper.AddUi(player, BuildPanel(entity.ShortPrefabName, filter, visible, session, pageCount));
        }

        // ------------------------------------------------------------------ ui building

        private static string Anchor(float x, float y)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.####} {1:0.####}", x, y);
        }

        private void AddButton(CuiElementContainer cui, string parent, string text, string command, string color, float xMin, float xMax, float yMin, float yMax, int fontSize = 12, Sprite sprite = null)
        {
            cui.Add(new CuiButton
            {
                Button = { Command = command, Color = color, Sprite = sprite != null ? sprite.name : null },
                Text = { Text = text, FontSize = fontSize, Align = TextAnchor.MiddleCenter, Color = ColorText },
                RectTransform = { AnchorMin = Anchor(xMin, yMin), AnchorMax = Anchor(xMax, yMax) }
            }, parent);
        }

        private void AddLabel(CuiElementContainer cui, string parent, string text, float xMin, float xMax, float yMin, float yMax, int fontSize = 12, TextAnchor align = TextAnchor.MiddleCenter)
        {
            cui.Add(new CuiLabel
            {
                Text = { Text = text, FontSize = fontSize, Align = align, Color = ColorText },
                RectTransform = { AnchorMin = Anchor(xMin, yMin), AnchorMax = Anchor(xMax, yMax) }
            }, parent);
        }

        private CuiElementContainer NewPanel(string name, string anchorMin, string anchorMax, string offsetMin, string offsetMax, bool keyboard)
        {
            var cui = new CuiElementContainer();
            cui.Add(new CuiPanel
            {
                Image = { Color = ColorPanel },
                RectTransform =
                {
                    AnchorMin = anchorMin,
                    AnchorMax = anchorMax,
                    OffsetMin = offsetMin,
                    OffsetMax = offsetMax
                },
                CursorEnabled = true,
                KeyboardEnabled = keyboard
            }, "Overlay", name);

            cui.Add(new CuiPanel
            {
                Image = { Color = ColorHeader },
                RectTransform = { AnchorMin = "0 0.9", AnchorMax = "1 1" }
            }, name);

            return cui;
        }

        // Player-free so the harness can render and validate the panel without a client.
        private CuiElementContainer BuildPanel(string prefabName, BoxFilter filter, List<ItemDefinition> visible, Session session, int pageCount)
        {
            var summary = $"{filter.Mode} | {filter.Items.Count} items | {filter.Categories.Count} categories | {filter.Exclude.Count} excluded";
            var cui = NewPanel(PanelId, config.Ui.AnchorMin, config.Ui.AnchorMax, config.Ui.OffsetMin, config.Ui.OffsetMax, true);

            AddLabel(cui, PanelId, $"PrecisionSorter - {prefabName}", 0.02f, 0.98f, 0.945f, 1f, 15);
            AddLabel(cui, PanelId, summary, 0.03f, 0.97f, 0.895f, 0.945f, 10);

            AddActionRow(cui);
            AddSearchRow(cui, session.Search);
            AddCategoryRow(cui, filter, session.BrowseCategory);
            AddCategoryAction(cui, filter, session.BrowseCategory);
            AddItemGrid(cui, filter, visible, session.Page);

            AddLabel(cui, PanelId, $"Page {session.Page + 1} / {pageCount}   ({visible.Count} items)", 0.30f, 0.70f, 0.005f, 0.045f, 10);
            AddButton(cui, PanelId, "< Prev", "ps.ui page " + (session.Page - 1), ColorButton, 0.03f, 0.14f, 0.005f, 0.045f, 10);
            AddButton(cui, PanelId, "Next >", "ps.ui page " + (session.Page + 1), ColorButton, 0.86f, 0.97f, 0.005f, 0.045f, 10);
            AddButton(cui, PanelId, "Mode: " + filter.Mode, "ps.ui mode", ColorButton, 0.15f, 0.29f, 0.005f, 0.045f, 9);
            AddButton(cui, PanelId, "Close", "ps.ui close", ColorDanger, 0.71f, 0.84f, 0.005f, 0.045f, 10);

            return cui;
        }

        private void AddActionRow(CuiElementContainer cui)
        {
            const float gap = 0.008f;
            float width = (0.94f - gap * 4) / 5f;
            string[] labels = { "This", "Nearby", "Arrange", "Dump All", "Loot All" };
            string[] commands = { "ps.run this", "ps.run nearby", "ps.run arrange", "ps.run dumpall", "ps.run lootall" };

            for (int i = 0; i < labels.Length; i++)
            {
                float xMin = 0.03f + i * (width + gap);
                AddButton(cui, PanelId, labels[i], commands[i], i == 0 ? ColorAction : ColorButton, xMin, xMin + width, 0.80f, 0.875f, 11);
            }
        }

        private void AddSearchRow(CuiElementContainer cui, string query)
        {
            cui.Add(new CuiElement
            {
                Parent = PanelId,
                Components =
                {
                    new CuiImageComponent { Color = "0.16 0.16 0.16 1" },
                    new CuiRectTransformComponent { AnchorMin = Anchor(0.03f, 0.715f), AnchorMax = Anchor(0.62f, 0.785f) },
                    new CuiInputFieldComponent
                    {
                        Command = "ps.ui search",
                        Text = query ?? string.Empty,
                        FontSize = 12,
                        Align = TextAnchor.MiddleLeft,
                        Color = ColorText,
                        CharsLimit = 28,
                        NeedsKeyboard = true
                    }
                }
            });

            AddButton(cui, PanelId, "Clear search", "ps.ui search", ColorMuted, 0.635f, 0.80f, 0.715f, 0.785f, 10);
            AddButton(cui, PanelId, "Clear filter", "ps.ui clear", ColorDanger, 0.815f, 0.97f, 0.715f, 0.785f, 10);
        }

        // Category buttons only browse the grid; assigning a whole category is a separate action.
        private void AddCategoryRow(CuiElementContainer cui, BoxFilter filter, string browsing)
        {
            const int perRow = 7;
            const float width = 0.94f / perRow;

            for (int i = 0; i < CategoryNames.Length; i++)
            {
                int row = i / perRow;
                int column = i % perRow;
                float xMin = 0.03f + column * width;
                float yMax = 0.70f - row * 0.075f;
                string name = CategoryNames[i];
                bool active = browsing == name;
                string color = active ? ColorButton : (filter.Categories.Contains(name) ? ColorSelected : ColorMuted);

                AddButton(cui, PanelId, name, "ps.ui browse " + name, color, xMin, xMin + width - 0.008f, yMax - 0.065f, yMax, 9);
            }
        }

        private void AddCategoryAction(CuiElementContainer cui, BoxFilter filter, string browsing)
        {
            if (string.IsNullOrEmpty(browsing))
            {
                AddButton(cui, PanelId, "Showing every category - click one above to narrow the list", "ps.ui browse", ColorMuted, 0.03f, 0.97f, 0.49f, 0.55f, 10);
                return;
            }

            bool assigned = filter.Categories.Contains(browsing);
            string label = (assigned ? "Remove whole category: " : "Accept whole category: ") + browsing;

            AddButton(cui, PanelId, label, "ps.ui accept", assigned ? ColorDanger : ColorAction, 0.03f, 0.62f, 0.49f, 0.55f, 10);
            AddButton(cui, PanelId, "Show all", "ps.ui browse", ColorMuted, 0.64f, 0.97f, 0.49f, 0.55f, 10);
        }

        private void AddItemGrid(CuiElementContainer cui, BoxFilter filter, List<ItemDefinition> visible, int currentPage)
        {
            const float width = 0.94f / GridColumns;
            int start = currentPage * PageSize;

            for (int i = 0; i < PageSize; i++)
            {
                int index = start + i;
                if (index >= visible.Count)
                {
                    return;
                }

                var def = visible[index];
                int row = i / GridColumns;
                int column = i % GridColumns;
                float xMin = 0.03f + column * width;
                float yMax = 0.475f - row * 0.105f;

                AddItemCell(cui, filter, def, xMin, xMin + width - 0.006f, yMax - 0.095f, yMax);
            }
        }

        // Only items with a real sprite get an icon; a bare item id disconnects clients.
        private void AddItemCell(CuiElementContainer cui, BoxFilter filter, ItemDefinition def, float xMin, float xMax, float yMin, float yMax)
        {
            if (config.Ui.ShowIcons && def.iconSprite != null)
            {
                cui.Add(new CuiElement
                {
                    Parent = PanelId,
                    Components =
                    {
                        new CuiImageComponent
                        {
                            Sprite = def.iconSprite.name,
                            Color = "1 1 1 1",
                            Material = "assets/icons/iconmaterial.mat",
                            BlocksRaycast = false
                        },
                        new CuiRectTransformComponent { AnchorMin = Anchor(xMin + 0.012f, yMin + 0.030f), AnchorMax = Anchor(xMax - 0.012f, yMax - 0.006f) }
                    }
                });
            }

            cui.Add(new CuiButton
            {
                Button = { Command = "ps.ui item " + def.shortname, Color = StateOverlay(filter, def) },
                Text = { Text = string.Empty, FontSize = 1 },
                RectTransform = { AnchorMin = Anchor(xMin, yMin), AnchorMax = Anchor(xMax, yMax) }
            }, PanelId);

            cui.Add(new CuiLabel
            {
                Text = { Text = Shorten(def.displayName.english), FontSize = 9, Align = TextAnchor.MiddleCenter, Color = StateColor(filter, def), BlocksRaycast = false },
                RectTransform = { AnchorMin = Anchor(xMin, yMin), AnchorMax = Anchor(xMax, yMin + 0.026f) }
            }, PanelId);
        }

        // Untouched, accepted and excluded each get their own tint.
        private static string StateOverlay(BoxFilter filter, ItemDefinition def)
        {
            if (filter.Exclude.Contains(def.shortname))
            {
                return "0.55 0.20 0.20 0.50";
            }

            return filter.Items.Contains(def.shortname) ? "0.20 0.45 0.25 0.50" : "0 0 0 0.45";
        }

        private static string StateColor(BoxFilter filter, ItemDefinition def)
        {
            if (filter.Exclude.Contains(def.shortname))
            {
                return "0.95 0.55 0.55 1";
            }

            return filter.Items.Contains(def.shortname) ? "0.55 0.90 0.55 1" : "0.88 0.88 0.88 1";
        }

        private static string Shorten(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= MaxNameLength)
            {
                return text;
            }

            return text.Substring(0, MaxNameLength - 1) + ".";
        }

        // Rescanning 1259 items per click is wasted work, so results are cached.
        private List<ItemDefinition> VisibleItems(Session session)
        {
            if (session.CachedItems != null && session.CachedQuery == session.Search && session.CachedBrowse == session.BrowseCategory)
            {
                return session.CachedItems;
            }

            IEnumerable<ItemDefinition> items = catalog;

            if (!string.IsNullOrEmpty(session.BrowseCategory))
            {
                ItemCategory parsed;
                if (Enum.TryParse(session.BrowseCategory, true, out parsed))
                {
                    items = items.Where(def => def.category == parsed);
                }
            }

            if (!string.IsNullOrEmpty(session.Search))
            {
                items = items.Where(def => def.displayName.english.IndexOf(session.Search, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            session.CachedQuery = session.Search;
            session.CachedBrowse = session.BrowseCategory;
            session.CachedItems = items as List<ItemDefinition> ?? items.ToList();

            return session.CachedItems;
        }

        // ------------------------------------------------------------------ actions

        private void Execute(BasePlayer player, string key)
        {
            switch (key)
            {
                case "this":
                    ExecuteAction(player, null, "Moved", DoThis);
                    break;
                case "nearby":
                    ExecuteAction(player, NearbyPerm, "Moved nearby", DoNearby);
                    break;
                case "arrange":
                    ExecuteAction(player, ArrangePerm, "Arranged", DoArrange);
                    break;
                case "dumpall":
                    ExecuteAction(player, DumpAllPerm, "Dumped", DoDumpAll);
                    break;
                case "lootall":
                    ExecuteAction(player, LootAllPerm, "Looted", DoLootAll);
                    break;
            }
        }

        private void ExecuteAction(BasePlayer player, string perm, string label, Func<BasePlayer, BaseEntity, ItemContainer, int> action)
        {
            Session session;
            BaseEntity entity;
            ItemContainer container;

            if (!TryGetOpenBox(player, out session, out entity, out container))
            {
                SendReply(player, "Open an allowed container first.");
                return;
            }

            string reason;
            if (!CanOperate(player, entity, session, perm, out reason))
            {
                SendReply(player, reason);
                return;
            }

            if (session.Busy)
            {
                SendReply(player, "A sort is already running.");
                return;
            }

            session.Busy = true;

            try
            {
                int moved = action(player, entity, container);
                SendReply(player, $"{label}: {moved} items.");
                Audit(player, $"{label} {moved} items in {entity.ShortPrefabName}");
            }
            finally
            {
                session.Busy = false;
                session.SortTimes.Add(Time.realtimeSinceStartup);
            }

            ShowUi(player);
        }

        private int DoThis(BasePlayer player, BaseEntity entity, ItemContainer container)
        {
            return MoveMatching(GetFilter(FilterOwner(entity, player), entity), container, SourceContainers(player));
        }

        private int DoNearby(BasePlayer player, BaseEntity entity, ItemContainer container)
        {
            return SortNearby(player, SourceContainers(player));
        }

        private int DoArrange(BasePlayer player, BaseEntity entity, ItemContainer container)
        {
            return Arrange(GetFilter(FilterOwner(entity, player), entity), container);
        }

        private int DoDumpAll(BasePlayer player, BaseEntity entity, ItemContainer container)
        {
            return MoveEverything(container, SourceContainers(player));
        }

        private int DoLootAll(BasePlayer player, BaseEntity entity, ItemContainer container)
        {
            return LootAll(container, player.inventory.containerMain);
        }

        // ------------------------------------------------------------------ picker editing

        private void ApplySearch(BasePlayer player, string text)
        {
            var session = GetSession(player);
            session.Search = string.IsNullOrEmpty(text) ? null : text;
            session.Page = 0;
            ShowUi(player);
        }

        private void GoToPage(BasePlayer player, string rawPage)
        {
            var session = GetSession(player);
            int target;

            if (rawPage != null && int.TryParse(rawPage, out target))
            {
                session.Page = Math.Max(0, target);
            }

            ShowUi(player);
        }

        // Browsing narrows the grid only; it never changes what the box accepts.
        private void SetBrowseCategory(BasePlayer player, string name)
        {
            var session = GetSession(player);
            session.BrowseCategory = string.IsNullOrEmpty(name) || session.BrowseCategory == name ? null : name;
            session.Page = 0;
            ShowUi(player);
        }

        // Assigns or removes the whole browsed category on the box.
        private void ToggleBrowsedCategory(BasePlayer player)
        {
            Session session;
            BaseEntity entity;
            ItemContainer container;

            if (!TryGetOpenBox(player, out session, out entity, out container))
            {
                return;
            }

            if (string.IsNullOrEmpty(session.BrowseCategory))
            {
                SendReply(player, "Pick a category first.");
                return;
            }

            var filter = EnsureFilter(FilterOwner(entity, player), entity);

            if (!filter.Categories.Remove(session.BrowseCategory))
            {
                filter.Categories.Add(session.BrowseCategory);
            }

            filter.Rebuild();
            MarkDirty();
            ShowUi(player);
        }

        private void ToggleItem(BasePlayer player, string shortname)
        {
            Session session;
            BaseEntity entity;
            ItemContainer container;

            if (shortname == null || !TryGetOpenBox(player, out session, out entity, out container))
            {
                return;
            }

            if (!byShortname.ContainsKey(shortname))
            {
                SendReply(player, $"Unknown item: {shortname}");
                return;
            }

            var filter = EnsureFilter(FilterOwner(entity, player), entity);

            // Cycles accept -> exclude -> untouched, so the picker needs no chat command.
            if (filter.Items.Remove(shortname))
            {
                filter.Exclude.Add(shortname);
            }
            else if (!filter.Exclude.Remove(shortname))
            {
                filter.Items.Add(shortname);
            }

            filter.Rebuild();
            MarkDirty();
            ShowUi(player);
        }

        private void ToggleMode(BasePlayer player)
        {
            Session session;
            BaseEntity entity;
            ItemContainer container;

            if (!TryGetOpenBox(player, out session, out entity, out container))
            {
                return;
            }

            var filter = EnsureFilter(FilterOwner(entity, player), entity);
            filter.Mode = filter.Mode == FilterMode.Whitelist ? FilterMode.Blacklist : FilterMode.Whitelist;
            MarkDirty();
            ShowUi(player);
        }

        private void ClearFilter(BasePlayer player)
        {
            Session session;
            BaseEntity entity;
            ItemContainer container;

            if (!TryGetOpenBox(player, out session, out entity, out container))
            {
                return;
            }

            data.Boxes.Remove(BuildKey(FilterOwner(entity, player), entity));
            MarkDirty();
            SendReply(player, "Filter cleared.");
            ShowUi(player);
        }

        // ------------------------------------------------------------------ command entry points

        // ConsoleSystem.Arg.Args holds StringView values, not strings, in this Oxide build.
        private static string ArgText(ConsoleSystem.Arg arg, int index)
        {
            return arg.Args != null && arg.Args.Length > index ? arg.Args[index].ToString() : null;
        }

        private static string ArgTextFrom(ConsoleSystem.Arg arg, int startIndex)
        {
            if (arg.Args == null || arg.Args.Length <= startIndex)
            {
                return null;
            }

            return string.Join(" ", arg.Args.Skip(startIndex).Select(part => part.ToString())).Trim();
        }

        [ConsoleCommand("ps.run")]
        private void CmdRun(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null)
            {
                Execute(player, (ArgText(arg, 0) ?? string.Empty).ToLowerInvariant());
            }
        }

        [ConsoleCommand("ps.ui")]
        private void CmdUi(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
            {
                return;
            }

            switch ((ArgText(arg, 0) ?? string.Empty).ToLowerInvariant())
            {
                case "browse":
                    SetBrowseCategory(player, ArgText(arg, 1));
                    break;
                case "accept":
                    ToggleBrowsedCategory(player);
                    break;
                case "close":
                    CloseUi(player);
                    break;
                case "search":
                    ApplySearch(player, ArgTextFrom(arg, 1));
                    break;
                case "cat":
                    SetBrowseCategory(player, ArgText(arg, 1));
                    break;
                case "item":
                    ToggleItem(player, ArgText(arg, 1));
                    break;
                case "page":
                    GoToPage(player, ArgText(arg, 1));
                    break;
                case "mode":
                    ToggleMode(player);
                    break;
                case "clear":
                    ClearFilter(player);
                    break;
            }
        }

        private void CmdChat(BasePlayer player, string command, string[] args)
        {
            if (player == null)
            {
                return;
            }

            if (!permission.UserHasPermission(player.UserIDString, UsePerm))
            {
                SendReply(player, "No permission.");
                return;
            }

            if (args == null || args.Length == 0)
            {
                SendReply(player, "/ps this | nearby | arrange | dumpall | lootall | find <text> | exclude <item>");
                return;
            }

            var verb = args[0].ToLowerInvariant();

            if (verb == "find")
            {
                FindItems(player, args.Length > 1 ? args[1] : null);
                return;
            }

            if (verb == "exclude")
            {
                ToggleExclusion(player, args.Length > 1 ? args[1] : null);
                return;
            }

            Execute(player, verb);
        }

        private void FindItems(BasePlayer player, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                SendReply(player, "Usage: /ps find <text>");
                return;
            }

            var matches = catalog
                .Where(def => def.displayName.english.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                .Take(15)
                .Select(def => def.shortname);

            SendReply(player, "Matches: " + string.Join(", ", matches));
        }

        private void ToggleExclusion(BasePlayer player, string shortname)
        {
            Session session;
            BaseEntity entity;
            ItemContainer container;

            if (shortname == null || !TryGetOpenBox(player, out session, out entity, out container))
            {
                return;
            }

            shortname = shortname.ToLowerInvariant();

            if (!byShortname.ContainsKey(shortname))
            {
                SendReply(player, $"Unknown item: {shortname}");
                return;
            }

            var filter = EnsureFilter(FilterOwner(entity, player), entity);

            if (!filter.Exclude.Remove(shortname))
            {
                filter.Exclude.Add(shortname);
            }

            filter.Rebuild();
            MarkDirty();
            SendReply(player, $"Excluded: {string.Join(", ", filter.Exclude)}");
            ShowUi(player);
        }

        // ------------------------------------------------------------------ diagnostics

        // Proves the matcher and the saved data against the live item DB.
        [ConsoleCommand("ps.selftest")]
        private void CmdSelfTest(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && !arg.IsAdmin)
            {
                return;
            }

            if (catalog.Count == 0)
            {
                PrintWarning("Selftest: catalog empty.");
                return;
            }

            var filter = new BoxFilter();
            filter.Categories.Add(ItemCategory.Weapon.ToString());
            filter.Exclude.Add("ammo.rocket.basic");
            filter.Rebuild();

            int accepted = catalog.Count(def => Accepts(filter, def));

            PrintWarning($"Selftest: whitelist Weapon minus rockets accepts {accepted} of {catalog.Count} items.");
            PrintWarning($"Selftest: rifle.ak accepted = {Accepts(filter, Find("rifle.ak"))} (expected True)");
            PrintWarning($"Selftest: ammo.rocket.basic accepted = {Accepts(filter, Find("ammo.rocket.basic"))} (expected False)");
            PrintWarning($"Selftest: ammo.rifle accepted = {Accepts(filter, Find("ammo.rifle"))} (expected False)");

            filter.Mode = FilterMode.Blacklist;
            PrintWarning($"Selftest: blacklist flips rifle.ak to {Accepts(filter, Find("rifle.ak"))} (expected False)");

            var unknownCategories = data.Boxes.Values.SelectMany(box => box.UnknownCategories).Distinct().ToList();
            PrintWarning(unknownCategories.Count == 0
                ? "Selftest: every saved category still exists in the game."
                : "Selftest: saved categories missing from the game: " + string.Join(", ", unknownCategories));

            var deadItems = data.Boxes.Values.SelectMany(box => box.Items).Distinct().Where(shortname => !byShortname.ContainsKey(shortname)).ToList();
            PrintWarning(deadItems.Count == 0
                ? "Selftest: every saved item shortname still exists in the game."
                : "Selftest: saved items missing from the game: " + string.Join(", ", deadItems));

            int withIcons = catalog.Count(def => def.iconSprite != null);
            PrintWarning($"Selftest: {withIcons} of {catalog.Count} items expose an icon sprite.");
        }

        private ItemDefinition Find(string shortname)
        {
            ItemDefinition def;
            return byShortname.TryGetValue(shortname, out def) ? def : null;
        }

        // ------------------------------------------------------------------ test harness

        [ConsoleCommand("ps.harness")]
        private void CmdHarness(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && !arg.IsAdmin)
            {
                return;
            }

            if (!config.EnableHarness)
            {
                PrintWarning("Harness disabled. Set EnableHarness true in the config to run it.");
                return;
            }

            switch ((ArgText(arg, 0) ?? "run").ToLowerInvariant())
            {
                case "run":
                    int iterations = 200;
                    int.TryParse(ArgText(arg, 1), out iterations);
                    RunConservation(Math.Max(1, iterations));
                    break;
                case "save":
                    HarnessSaveFilter();
                    break;
                case "check":
                    HarnessCheckFilter();
                    break;
                case "clear":
                    HarnessClear();
                    break;
                case "ui":
                    DumpUiPreviews();
                    break;
                case "route":
                    RunRouting();
                    break;
                default:
                    PrintWarning("Usage: ps.harness run [n] | save | check | clear | ui | route");
                    break;
            }
        }

        private static string HarnessKey(Vector3 position)
        {
            return BuildKey(0ul, "woodbox_deployed", position);
        }

        private StorageContainer SpawnTestBox(Vector3 position)
        {
            var entity = GameManager.server.CreateEntity(TestBoxPrefab, position);
            if (entity == null)
            {
                return null;
            }

            entity.Spawn();
            return entity as StorageContainer;
        }

        private static void KillTestBox(BaseEntity box)
        {
            if (box != null && !box.IsDestroyed)
            {
                box.Kill();
            }
        }

        private void SeedItems(ItemContainer container)
        {
            for (int i = 0; i < HarnessItems.Length; i++)
            {
                var def = Find(HarnessItems[i]);
                if (def == null)
                {
                    continue;
                }

                var item = ItemManager.CreateByName(HarnessItems[i], Math.Max(1, (def.stackable / (i % 3 + 1)) + i));
                if (item == null)
                {
                    continue;
                }

                if (!item.MoveToContainer(container))
                {
                    item.Remove();
                }
            }
        }

        private static void ClearContainer(ItemContainer container)
        {
            if (container?.itemList == null)
            {
                return;
            }

            foreach (var item in container.itemList.ToList())
            {
                item.Remove();
            }
        }

        private static void Measure(ItemContainer first, ItemContainer second, out long count, out long amount, out List<Item> items)
        {
            count = 0;
            amount = 0;
            items = new List<Item>();

            foreach (var container in new[] { first, second })
            {
                if (container?.itemList == null)
                {
                    continue;
                }

                foreach (var item in container.itemList)
                {
                    if (item == null)
                    {
                        continue;
                    }

                    count++;
                    amount += item.amount;
                    items.Add(item);
                }
            }
        }

        // Proves no action creates, destroys or double-owns an item.
        private void RunConservation(int iterations)
        {
            var source = SpawnTestBox(HarnessSourcePos);
            var target = SpawnTestBox(HarnessTargetPos);

            if (source == null || target == null)
            {
                PrintWarning("Harness: could not spawn test boxes.");
                KillTestBox(source);
                KillTestBox(target);
                return;
            }

            var filter = new BoxFilter();
            filter.Categories.Add(ItemCategory.Weapon.ToString());
            filter.Categories.Add(ItemCategory.Resources.ToString());
            filter.Items.Add("ammo.rifle");
            filter.Rebuild();

            var sources = new List<ItemContainer> { source.inventory };
            int conservationFailures = 0;
            int duplicateFailures = 0;
            int capacityFailures = 0;

            try
            {
                for (int i = 0; i < iterations; i++)
                {
                    SeedItems(source.inventory);

                    long beforeCount, beforeAmount;
                    List<Item> ignored;
                    Measure(source.inventory, target.inventory, out beforeCount, out beforeAmount, out ignored);

                    switch (i % 3)
                    {
                        case 0:
                            MoveMatching(filter, target.inventory, sources);
                            break;
                        case 1:
                            MoveEverything(target.inventory, sources);
                            break;
                        default:
                            Arrange(filter, target.inventory);
                            break;
                    }

                    long afterCount, afterAmount;
                    List<Item> afterItems;
                    Measure(source.inventory, target.inventory, out afterCount, out afterAmount, out afterItems);

                    if (beforeCount != afterCount || beforeAmount != afterAmount)
                    {
                        conservationFailures++;
                        if (conservationFailures <= 3)
                        {
                            PrintWarning($"Harness: conservation broke on iteration {i} ({beforeCount}/{beforeAmount} -> {afterCount}/{afterAmount}).");
                        }
                    }

                    if (afterItems.Distinct().Count() != afterItems.Count)
                    {
                        duplicateFailures++;
                    }

                    if (target.inventory.itemList.Count > target.inventory.capacity)
                    {
                        capacityFailures++;
                    }

                    ClearContainer(source.inventory);
                    ClearContainer(target.inventory);
                }
            }
            finally
            {
                KillTestBox(source);
                KillTestBox(target);
            }

            PrintWarning($"Harness: {iterations} iterations | conservation failures {conservationFailures} | duplicate items {duplicateFailures} | capacity violations {capacityFailures}.");
        }

        // Proves the routing rule: an explicit item beats a category, then the nearest box wins.
        private void RunRouting()
        {
            var itemBox = SpawnTestBox(HarnessSourcePos);
            var nearCategoryBox = SpawnTestBox(HarnessTargetPos);
            var farCategoryBox = SpawnTestBox(HarnessTargetPos + new Vector3(5f, 0f, 0f));
            var source = SpawnTestBox(HarnessSourcePos + new Vector3(-5f, 0f, 0f));

            if (itemBox == null || nearCategoryBox == null || farCategoryBox == null || source == null)
            {
                PrintWarning("Harness: could not spawn routing boxes.");
                KillTestBox(itemBox);
                KillTestBox(nearCategoryBox);
                KillTestBox(farCategoryBox);
                KillTestBox(source);
                return;
            }

            var itemFilter = new BoxFilter();
            itemFilter.Items.Add("rifle.ak");
            itemFilter.Rebuild();

            var categoryFilter = new BoxFilter();
            categoryFilter.Categories.Add(ItemCategory.Weapon.ToString());
            categoryFilter.Rebuild();

            // Distances are synthetic so the test does not depend on world geometry.
            var targets = new List<SortTarget>
            {
                new SortTarget { Container = itemBox.inventory, Filter = itemFilter, Distance = 30f },
                new SortTarget { Container = farCategoryBox.inventory, Filter = categoryFilter, Distance = 20f },
                new SortTarget { Container = nearCategoryBox.inventory, Filter = categoryFilter, Distance = 5f }
            };
            targets.Sort((a, b) => a.Distance.CompareTo(b.Distance));

            try
            {
                var first = ItemManager.CreateByName("rifle.ak", 1);
                bool routed = first != null && TryRoute(first, targets);
                PrintWarning($"Harness: explicit item box wins = {routed && first.parent == itemBox.inventory} (expected True)");

                FillContainer(itemBox.inventory);

                var second = ItemManager.CreateByName("rifle.ak", 1);
                routed = second != null && TryRoute(second, targets);
                PrintWarning($"Harness: full item box falls back to nearest category box = {routed && second.parent == nearCategoryBox.inventory} (expected True)");

                var ammo = ItemManager.CreateByName("ammo.rifle", 1);
                bool refused = ammo != null && !TryRoute(ammo, targets);
                PrintWarning($"Harness: unmatched item is left alone = {refused && ammo.parent == null} (expected True)");

                if (ammo != null && ammo.parent == null)
                {
                    ammo.Remove();
                }
            }
            finally
            {
                KillTestBox(itemBox);
                KillTestBox(nearCategoryBox);
                KillTestBox(farCategoryBox);
                KillTestBox(source);
            }
        }

        private void FillContainer(ItemContainer container)
        {
            var def = Find("wood");
            if (def == null || container == null)
            {
                return;
            }

            for (int i = 0; i <= container.capacity; i++)
            {
                var item = ItemManager.CreateByName("wood", def.stackable);
                if (item == null)
                {
                    return;
                }

                if (!item.MoveToContainer(container))
                {
                    item.Remove();
                    return;
                }
            }
        }

        private void HarnessSaveFilter()
        {
            var box = SpawnTestBox(HarnessSourcePos);
            if (box == null)
            {
                PrintWarning("Harness: could not spawn test box.");
                return;
            }

            var filter = EnsureFilter(0ul, box);
            filter.Items.Add("rifle.ak");
            filter.Items.Add("ammo.rocket.basic");
            filter.Categories.Add(ItemCategory.Medical.ToString());
            filter.Rebuild();
            MarkDirty();
            SaveData();

            PrintWarning($"Harness: saved test filter at key {HarnessKey(HarnessSourcePos)}.");
            KillTestBox(box);
        }

        private void HarnessCheckFilter()
        {
            var box = SpawnTestBox(HarnessSourcePos);
            if (box == null)
            {
                PrintWarning("Harness: could not spawn test box.");
                return;
            }

            var filter = GetFilter(0ul, box);

            if (filter == null)
            {
                PrintWarning($"Harness: NO filter found for key {HarnessKey(HarnessSourcePos)} (persistence failed).");
            }
            else
            {
                PrintWarning($"Harness: filter found for key {HarnessKey(HarnessSourcePos)} | {filter.Mode} | items {string.Join(", ", filter.Items)} | categories {string.Join(", ", filter.Categories)}.");
            }

            KillTestBox(box);
        }

        private void HarnessClear()
        {
            data.Boxes.Remove(HarnessKey(HarnessSourcePos));
            data.Boxes.Remove(HarnessKey(HarnessTargetPos));
            SaveData();
            PrintWarning("Harness: test filters cleared.");
        }

        // Writes the panel as CUI JSON so layout can be checked headlessly.
        private void DumpUiPreviews()
        {
            var filter = new BoxFilter();
            filter.Categories.Add(ItemCategory.Weapon.ToString());
            filter.Categories.Add(ItemCategory.Medical.ToString());
            filter.Items.Add("rifle.ak");
            filter.Exclude.Add("ammo.rocket.basic");
            filter.Rebuild();

            var directory = Path.Combine(Interface.Oxide.DataDirectory, "PrecisionSorter");
            Directory.CreateDirectory(directory);

            foreach (var browse in new[] { null, ItemCategory.Weapon.ToString() })
            {
                var session = new Session { BrowseCategory = browse };
                var visible = VisibleItems(session);
                int pageCount = Math.Max(1, (visible.Count + PageSize - 1) / PageSize);
                string name = browse == null ? "ui-panel-all.json" : "ui-panel-" + browse.ToLowerInvariant() + ".json";

                File.WriteAllText(Path.Combine(directory, name), BuildPanel("woodbox_deployed", filter, visible, session, pageCount).ToJson());
            }

            PrintWarning($"Harness: wrote UI previews to {directory}.");
        }

        // ------------------------------------------------------------------ serialized models

        [JsonConverter(typeof(StringEnumConverter))]
        public enum FilterMode
        {
            Whitelist,
            Blacklist
        }

        public class BoxFilter
        {
            public FilterMode Mode { get; set; } = FilterMode.Whitelist;
            public HashSet<string> Items { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Categories { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Exclude { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            [JsonIgnore]
            public HashSet<ItemCategory> ParsedCategories { get; private set; } = new HashSet<ItemCategory>();

            // Names that no longer exist in the game, surfaced by the selftest after an update.
            [JsonIgnore]
            public List<string> UnknownCategories { get; private set; } = new List<string>();

            // Category names are stored as text so game enum reordering cannot corrupt saved filters.
            public void Rebuild()
            {
                ParsedCategories = new HashSet<ItemCategory>();
                UnknownCategories = new List<string>();

                foreach (var name in Categories)
                {
                    ItemCategory parsed;
                    if (Enum.TryParse(name, true, out parsed))
                    {
                        ParsedCategories.Add(parsed);
                    }
                    else
                    {
                        UnknownCategories.Add(name);
                    }
                }
            }
        }

        public class SorterData
        {
            public Dictionary<string, BoxFilter> Boxes { get; set; } = new Dictionary<string, BoxFilter>();
        }

        public class PluginConfig
        {
            // Collections stay empty here; Json.NET appends to pre-filled lists and duplicates them.
            public List<string> AllowedContainers { get; set; }
            public List<string> ChatCommands { get; set; }

            public float NearbyRadius { get; set; } = 30f;
            public bool RequireBuildingPrivilege { get; set; } = true;
            public bool RespectNoEscape { get; set; } = true;
            public bool IncludeHotbar { get; set; } = false;
            public int SortsPerMinute { get; set; } = 20;
            public bool LogActions { get; set; } = false;
            public bool EnableHarness { get; set; } = false;

            public UiSettings Ui { get; set; } = new UiSettings();

            public static PluginConfig Default()
            {
                return new PluginConfig
                {
                    AllowedContainers = new List<string>
                    {
                        "woodbox_deployed", "box.wooden.large", "small_stash_deployed",
                        "fridge.deployed", "coffinstorage", "campfire", "furnace", "furnace.large"
                    },
                    ChatCommands = new List<string> { "ps" }
                };
            }
        }

        public class UiSettings
        {
            // Off by default. Only items with a real sprite get an icon element; see AddItemCell.
            public bool ShowIcons { get; set; } = false;

            // Defaults keep the panel off the centred vanilla loot window.
            public string AnchorMin { get; set; } = "1 0.5";
            public string AnchorMax { get; set; } = "1 0.5";
            public string OffsetMin { get; set; } = "-680 -330";
            public string OffsetMax { get; set; } = "-20 330";

            public UiColors Colors { get; set; } = new UiColors();
        }

        public class UiColors
        {
            public string Panel { get; set; } = "0.07 0.07 0.07 0.95";
            public string Header { get; set; } = "0.15 0.15 0.15 1";
            public string Button { get; set; } = "0.20 0.35 0.55 0.95";
            public string Action { get; set; } = "0.20 0.45 0.25 0.95";
            public string Danger { get; set; } = "0.55 0.20 0.20 0.95";
            public string Muted { get; set; } = "0.25 0.25 0.25 0.80";
            public string Selected { get; set; } = "0.20 0.45 0.25 0.95";
            public string Text { get; set; } = "0.88 0.88 0.88 1";
        }
    }
}
