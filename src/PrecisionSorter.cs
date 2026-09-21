using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Oxide.Core;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("PrecisionSorter", "gringatestudios", "0.2.0")]
    [Description("Per-box item and category sorting with a searchable picker UI.")]
    public class PrecisionSorter : RustPlugin
    {
        private const string UsePerm     = "precisionsorter.use";
        private const string NearbyPerm  = "precisionsorter.nearby";
        private const string DumpAllPerm = "precisionsorter.dumpall";
        private const string LootAllPerm = "precisionsorter.lootall";
        private const string ArrangePerm = "precisionsorter.arrange";
        private const string AdminPerm   = "precisionsorter.admin";
        private const string PanelId     = "precisionsorter.panel";
        private const string DataFile    = "PrecisionSorter/BoxFilters";

        private PluginConfig config;
        private SorterData data;

        private readonly List<ItemDefinition> catalog = new List<ItemDefinition>();
        private readonly Dictionary<string, ItemDefinition> byShortname = new Dictionary<string, ItemDefinition>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ulong, BaseEntity> openBox = new Dictionary<ulong, BaseEntity>();
        private readonly HashSet<ulong> busy = new HashSet<ulong>();

        private bool dataDirty;
        private bool saveScheduled;

        protected override void LoadDefaultConfig() => Config.WriteObject(PluginConfig.Default(), true);

        private void Init()
        {
            LoadConfig();
            RegisterPermissions();
            LoadData();
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
            if (dataDirty)
            {
                SaveData();
            }
        }

        private void Unload()
        {
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
                openBox.Remove(player.userID);
                busy.Remove(player.userID);
            }
        }

        private void OnLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (player == null || entity == null)
            {
                return;
            }

            if (!permission.UserHasPermission(player.UserIDString, UsePerm))
            {
                return;
            }

            if (!config.AllowedContainers.Contains(entity.ShortPrefabName))
            {
                return;
            }

            if (!(entity is IItemContainerEntity))
            {
                return;
            }

            openBox[player.userID] = entity;
            ShowPanel(player, entity);
        }

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
        }

        private void RegisterPermissions()
        {
            permission.RegisterPermission(UsePerm, this);
            permission.RegisterPermission(NearbyPerm, this);
            permission.RegisterPermission(DumpAllPerm, this);
            permission.RegisterPermission(LootAllPerm, this);
            permission.RegisterPermission(ArrangePerm, this);
            permission.RegisterPermission(AdminPerm, this);
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
                if (def == null || string.IsNullOrEmpty(def.shortname))
                {
                    continue;
                }

                if (def.category == ItemCategory.All || def.category == ItemCategory.Favourite || def.category == ItemCategory.Search)
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
            data = Interface.Oxide.DataFileSystem.ReadObject<SorterData>(DataFile);
            if (data == null)
            {
                data = new SorterData();
            }

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

        // Stable box identity: owner, prefab and exact position survive restarts, net IDs do not.
        // Two decimals keeps neighbours distinct and is immune to locale decimal separators.
        private static string BuildKey(ulong ownerId, BaseEntity entity)
        {
            var position = entity.transform.position;
            return string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2:F2},{3:F2},{4:F2}",
                ownerId, entity.ShortPrefabName, position.x, position.y, position.z);
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

        // Skin never matters: matching uses shortname and category only.
        // Exclude wins over everything, so "Weapon except rockets" is expressible.
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

        private bool TryGetOpenBox(BasePlayer player, out BaseEntity entity, out ItemContainer container)
        {
            entity = null;
            container = null;

            if (!openBox.TryGetValue(player.userID, out entity) || entity == null || entity.IsDestroyed)
            {
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

        // Snapshot first, then move: never iterate a live list while it mutates.
        private int SortThis(BasePlayer player, BaseEntity entity, ItemContainer target)
        {
            var filter = GetFilter(player.userID, entity);
            if (filter == null)
            {
                return 0;
            }

            var sources = new List<ItemContainer> { player.inventory.containerMain };
            if (config.IncludeHotbar)
            {
                sources.Add(player.inventory.containerBelt);
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
                    if (item?.info == null || !Accepts(filter, item.info))
                    {
                        continue;
                    }

                    if (item.MoveToContainer(target))
                    {
                        moved++;
                    }
                }
            }

            return moved;
        }

        private int Arrange(BasePlayer player, BaseEntity entity, ItemContainer target)
        {
            var list = target?.itemList;
            if (list == null || list.Count < 2)
            {
                return 0;
            }

            var filter = GetFilter(player.userID, entity);
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

        private bool Guard(BasePlayer player, string action)
        {
            if (busy.Contains(player.userID))
            {
                SendReply(player, "A sort is already running.");
                return false;
            }

            busy.Add(player.userID);
            return true;
        }

        [ChatCommand("ps")]
        private void CmdPs(BasePlayer player, string command, string[] args)
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
                SendReply(player, "/ps this | arrange | add <item> | cat <category> | exclude <item> | mode <whitelist|blacklist> | clear | show | find <text>");
                return;
            }

            BaseEntity entity;
            ItemContainer container;

            switch (args[0].ToLowerInvariant())
            {
                case "this":
                    if (!TryGetOpenBox(player, out entity, out container))
                    {
                        SendReply(player, "Open an allowed container first.");
                        return;
                    }

                    if (!Guard(player, "this"))
                    {
                        return;
                    }

                    try
                    {
                        SendReply(player, $"Moved {SortThis(player, entity, container)} items.");
                    }
                    finally
                    {
                        busy.Remove(player.userID);
                    }

                    break;

                case "arrange":
                    if (!TryGetOpenBox(player, out entity, out container))
                    {
                        SendReply(player, "Open an allowed container first.");
                        return;
                    }

                    SendReply(player, $"Arranged {Arrange(player, entity, container)} items.");
                    break;

                case "add":
                case "cat":
                case "exclude":
                    if (args.Length < 2)
                    {
                        SendReply(player, $"Usage: /ps {args[0]} <value>");
                        return;
                    }

                    EditFilter(player, args[0], args[1]);
                    break;

                case "mode":
                    SetMode(player, args.Length > 1 ? args[1] : null);
                    break;

                case "clear":
                    ClearFilter(player);
                    break;

                case "show":
                    ShowFilter(player);
                    break;

                case "find":
                    FindItems(player, args.Length > 1 ? args[1] : null);
                    break;

                default:
                    SendReply(player, "Unknown subcommand.");
                    break;
            }
        }

        private void EditFilter(BasePlayer player, string kind, string value)
        {
            BaseEntity entity;
            ItemContainer container;

            if (!TryGetOpenBox(player, out entity, out container))
            {
                SendReply(player, "Open an allowed container first.");
                return;
            }

            var filter = EnsureFilter(player.userID, entity);
            value = value.ToLowerInvariant();

            if (kind == "cat")
            {
                ItemCategory category;
                if (!Enum.TryParse(value, true, out category))
                {
                    SendReply(player, $"Unknown category: {value}");
                    return;
                }

                filter.Categories.Add(category.ToString());
            }
            else if (kind == "add")
            {
                if (!byShortname.ContainsKey(value))
                {
                    SendReply(player, $"Unknown item: {value}");
                    return;
                }

                filter.Items.Add(value);
            }
            else
            {
                if (!byShortname.ContainsKey(value))
                {
                    SendReply(player, $"Unknown item: {value}");
                    return;
                }

                filter.Exclude.Add(value);
            }

            filter.Rebuild();
            MarkDirty();
            ShowFilter(player);
        }

        private void SetMode(BasePlayer player, string mode)
        {
            BaseEntity entity;
            ItemContainer container;

            if (!TryGetOpenBox(player, out entity, out container))
            {
                SendReply(player, "Open an allowed container first.");
                return;
            }

            if (mode != null && mode.Equals("blacklist", StringComparison.OrdinalIgnoreCase))
            {
                EnsureFilter(player.userID, entity).Mode = FilterMode.Blacklist;
            }
            else if (mode != null && mode.Equals("whitelist", StringComparison.OrdinalIgnoreCase))
            {
                EnsureFilter(player.userID, entity).Mode = FilterMode.Whitelist;
            }
            else
            {
                SendReply(player, "Usage: /ps mode <whitelist|blacklist>");
                return;
            }

            MarkDirty();
            ShowFilter(player);
        }

        private void ClearFilter(BasePlayer player)
        {
            BaseEntity entity;
            ItemContainer container;

            if (!TryGetOpenBox(player, out entity, out container))
            {
                SendReply(player, "Open an allowed container first.");
                return;
            }

            data.Boxes.Remove(BuildKey(player.userID, entity));
            MarkDirty();
            SendReply(player, "Filter cleared.");
        }

        private void ShowFilter(BasePlayer player)
        {
            BaseEntity entity;
            ItemContainer container;

            if (!TryGetOpenBox(player, out entity, out container))
            {
                SendReply(player, "Open an allowed container first.");
                return;
            }

            var filter = GetFilter(player.userID, entity);
            if (filter == null)
            {
                SendReply(player, "No filter on this container.");
                return;
            }

            SendReply(player, $"Mode: {filter.Mode} | Items: {string.Join(", ", filter.Items)} | Categories: {string.Join(", ", filter.Categories)} | Exclude: {string.Join(", ", filter.Exclude)}");
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

        // Diagnostic: proves the matcher against the live item DB without needing a client.
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
        }

        private ItemDefinition Find(string shortname)
        {
            ItemDefinition def;
            return byShortname.TryGetValue(shortname, out def) ? def : null;
        }

        private void ShowPanel(BasePlayer player, BaseEntity entity)
        {
            CuiHelper.DestroyUi(player, PanelId);

            var filter = GetFilter(player.userID, entity);
            var summary = filter == null
                ? "No filter set"
                : $"{filter.Mode} | {filter.Items.Count} items | {filter.Categories.Count} categories";

            var cui = new CuiElementContainer();
            cui.Add(new CuiPanel
            {
                Image = { Color = "0.1 0.1 0.1 0.9" },
                RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-220 -140", OffsetMax = "220 140" },
                CursorEnabled = true
            }, "Overlay", PanelId);

            cui.Add(new CuiLabel
            {
                Text = { Text = $"PrecisionSorter - {entity.ShortPrefabName}", FontSize = 14, Align = TextAnchor.MiddleCenter },
                RectTransform = { AnchorMin = "0 0.88", AnchorMax = "1 0.98" }
            }, PanelId);

            cui.Add(new CuiLabel
            {
                Text = { Text = summary, FontSize = 11, Align = TextAnchor.MiddleCenter },
                RectTransform = { AnchorMin = "0 0.78", AnchorMax = "1 0.88" }
            }, PanelId);

            cui.Add(new CuiButton
            {
                Button = { Command = "ps.this", Color = "0.2 0.5 0.2 0.9" },
                Text = { Text = "This", FontSize = 12, Align = TextAnchor.MiddleCenter },
                RectTransform = { AnchorMin = "0.08 0.62", AnchorMax = "0.48 0.74" }
            }, PanelId);

            cui.Add(new CuiButton
            {
                Button = { Command = "ps.arrange", Color = "0.2 0.4 0.6 0.9" },
                Text = { Text = "Arrange", FontSize = 12, Align = TextAnchor.MiddleCenter },
                RectTransform = { AnchorMin = "0.52 0.62", AnchorMax = "0.92 0.74" }
            }, PanelId);

            cui.Add(new CuiButton
            {
                Button = { Command = "ps.close", Color = "0.6 0.2 0.2 0.9" },
                Text = { Text = "Close", FontSize = 12, Align = TextAnchor.MiddleCenter },
                RectTransform = { AnchorMin = "0.4 0.02", AnchorMax = "0.6 0.12" }
            }, PanelId);

            CuiHelper.AddUi(player, cui);
        }

        [ConsoleCommand("ps.close")]
        private void CmdClose(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
            {
                return;
            }

            CuiHelper.DestroyUi(player, PanelId);
        }

        [ConsoleCommand("ps.this")]
        private void CmdThis(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
            {
                return;
            }

            BaseEntity entity;
            ItemContainer container;

            if (!TryGetOpenBox(player, out entity, out container) || !Guard(player, "this"))
            {
                return;
            }

            try
            {
                SendReply(player, $"Moved {SortThis(player, entity, container)} items.");
            }
            finally
            {
                busy.Remove(player.userID);
            }
        }

        [ConsoleCommand("ps.arrange")]
        private void CmdArrange(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
            {
                return;
            }

            BaseEntity entity;
            ItemContainer container;

            if (!TryGetOpenBox(player, out entity, out container))
            {
                return;
            }

            SendReply(player, $"Arranged {Arrange(player, entity, container)} items.");
        }

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

            // Category names are stored as text so game enum reordering cannot corrupt saved filters.
            public void Rebuild()
            {
                ParsedCategories = new HashSet<ItemCategory>();

                foreach (var name in Categories)
                {
                    ItemCategory parsed;
                    if (Enum.TryParse(name, true, out parsed))
                    {
                        ParsedCategories.Add(parsed);
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
            public List<string> AllowedContainers { get; set; } = new List<string>
            {
                "woodbox_deployed", "box.wooden.large", "small_stash_deployed",
                "fridge.deployed", "coffinstorage", "campfire", "furnace", "furnace.large"
            };

            public float NearbyRadius { get; set; } = 30f;
            public bool RequireBuildingPrivilege { get; set; } = true;
            public bool RespectNoEscape { get; set; } = true;
            public bool IncludeHotbar { get; set; } = false;
            public int SortsPerMinute { get; set; } = 20;

            public static PluginConfig Default() => new PluginConfig();
        }
    }
}
