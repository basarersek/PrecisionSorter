using System;
using System.Collections.Generic;
using Oxide.Core;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("PrecisionSorter", "gringatestudios", "0.1.0")]
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

        private PluginConfig config;
        private readonly List<ItemDefinition> catalog = new List<ItemDefinition>();

        protected override void LoadDefaultConfig() => Config.WriteObject(PluginConfig.Default(), true);

        private void Init()
        {
            LoadConfig();
            RegisterPermissions();
        }

        // ItemManager.itemList is empty during Init, so the catalog waits for server init.
        private void OnServerInitialized()
        {
            BuildCatalog();
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
            }

            Puts($"Catalog cached: {catalog.Count} items.");
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

            if (entity as IItemContainerEntity == null)
            {
                return;
            }

            ShowPanel(player, entity);
        }

        private void ShowPanel(BasePlayer player, BaseEntity entity)
        {
            CuiHelper.DestroyUi(player, PanelId);

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
                RectTransform = { AnchorMin = "0 0.9", AnchorMax = "1 1" }
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

            SendReply(player, $"PrecisionSorter {Version} - {catalog.Count} items cached.");
        }

        private void OnNewSave(string filename)
        {
            Puts("New wipe detected - box filters must be cleared.");
        }

        private void Unload()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player != null)
                {
                    CuiHelper.DestroyUi(player, PanelId);
                }
            }
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
