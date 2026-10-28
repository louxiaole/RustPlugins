using System;
using System.Collections.Generic;
using System.Text;
using Oxide.Core;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("KillFeed 击杀提示", "Custom", "3.4.1")]
    [Description("显示玩家击杀提示，包括击杀者、被击杀者、武器等信息 (适配最新Rust, 区分NPC与玩家)")]
    class KillFeed : RustPlugin
    {
        #region 常量

        private const string PERM_ADMIN = "killfeed.admin";

        #endregion

        #region 配置数据

        private ConfigData configData;

        private class ConfigData
        {
            public bool Enabled { get; set; } = true;
            public bool ShowPlayerKills { get; set; } = true;
            public bool ShowNPCKills { get; set; } = true;
            public bool ShowAnimalKills { get; set; } = true;
            public bool ShowSuicide { get; set; } = true;
            public bool ShowToAllPlayers { get; set; } = true;
            public bool ShowToKiller { get; set; } = true;
            public bool ShowToVictim { get; set; } = true;
            public bool ShowWeaponName { get; set; } = true;
            public bool ShowDistance { get; set; } = true;
            public bool ShowInChat { get; set; } = true;
            public bool ShowInHud { get; set; } = false;
            public string ChatPrefix { get; set; } = "[击杀提示]";
            public string ChatPrefixColor { get; set; } = "#FF0000";
            public float HudDuration { get; set; } = 3f;
            public Dictionary<string, string> WeaponNames { get; set; } = new Dictionary<string, string>();
        }

        protected override void LoadDefaultConfig()
        {
            Config.Clear();
            Config.WriteObject(GetDefaultConfig(), true);
        }

        private ConfigData GetDefaultConfig()
        {
            return new ConfigData
            {
                Enabled = true,
                ShowPlayerKills = true,
                ShowNPCKills = true,
                ShowAnimalKills = true,
                ShowSuicide = true,
                ShowToAllPlayers = true,
                ShowToKiller = true,
                ShowToVictim = true,
                ShowWeaponName = true,
                ShowDistance = true,
                ShowInChat = true,
                ShowInHud = false,
                ChatPrefix = "[击杀提示]",
                ChatPrefixColor = "#FF0000",
                HudDuration = 3f,
                WeaponNames = new Dictionary<string, string>
                {
                    ["rifle.ak"] = "AK47",
                    ["rifle.bolt"] = "狙击枪",
                    ["rifle.lr300"] = "LR300",
                    ["rifle.m39"] = "M39",
                    ["rifle.semiauto"] = "半自动步枪",
                    ["pistol.m92"] = "M92手枪",
                    ["pistol.revolver"] = "左轮手枪",
                    ["pistol.eoka"] = "Eoka手枪",
                    ["pistol.semiauto"] = "半自动手枪",
                    ["pistol.python"] = "蟒蛇手枪",
                    ["smg.thompson"] = "汤普森",
                    ["smg.mp5"] = "MP5",
                    ["smg.ump45"] = "UMP45",
                    ["smg.mac10"] = "MAC-10",
                    ["smg.vector"] = "Vector",
                    ["lmg.m249"] = "M249",
                    ["lmg.m300"] = "M300",
                    ["crossbow"] = "十字弩",
                    ["bow.hunting"] = "狩猎弓",
                    ["bow.compound"] = "复合弓",
                    ["shotgun.pump"] = "泵动霰弹枪",
                    ["shotgun.spas12"] = "SPAS-12",
                    ["shotgun.waterpipe"] = "水管霰弹枪",
                    ["shotgun.double"] = "双管霰弹枪",
                    ["shotgun.single"] = "单发霰弹枪",
                    ["rock"] = "石头",
                    ["torch"] = "火把",
                    ["knife.bone"] = "骨刀",
                    ["knife.combat"] = "战斗刀",
                    ["knife.primitive"] = "简易刀",
                    ["mace"] = "狼牙棒",
                    ["spear.stone"] = "石矛",
                    ["spear.wooden"] = "木矛",
                    ["spear.bone"] = "骨矛",
                    ["sword"] = "剑",
                    ["longsword"] = "长剑",
                    ["salvaged.sword"] = "废料剑",
                    ["hammer"] = "锤子",
                    ["hatchet"] = "斧头",
                    ["pickaxe"] = "镐子",
                    ["stonehatchet"] = "石斧",
                    ["stonepickaxe"] = "石镐",
                    ["nailgun"] = "钉枪",
                    ["explosive.timed"] = "定时炸弹",
                    ["grenade.f1"] = "F1手雷",
                    ["explosive.satchel"] = "炸药包",
                    ["explosive.beancan"] = "豆罐炸弹",
                    ["explosive.charge"] = "爆破炸药",
                    ["rocket.basic"] = "基础火箭弹",
                    ["rocket.hv"] = "高速火箭弹",
                    ["rocket.smoke"] = "烟雾火箭弹",
                    ["c4"] = "C4炸药",
                    ["landmine"] = "地雷",
                    ["flame.thrower"] = "火焰喷射器",
                    ["flamethrower"] = "火焰喷射器",
                    ["waterpipe.shotgun"] = "水管霰弹枪"
                }
            };
        }

        private void LoadConfigData()
        {
            try
            {
                configData = Config.ReadObject<ConfigData>();
                if (configData == null)
                {
                    LoadDefaultConfig();
                    configData = Config.ReadObject<ConfigData>();
                }
            }
            catch (Exception ex)
            {
                PrintError($"加载配置失败: {ex.Message}");
                LoadDefaultConfig();
                configData = Config.ReadObject<ConfigData>();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(configData, true);

        #endregion

        #region 初始化

        private static readonly Dictionary<string, string> AnimalNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["bear"] = "熊",
            ["polarbear"] = "北极熊",
            ["polar_bear"] = "北极熊",
            ["wolf"] = "狼",
            ["wolfblack"] = "黑狼",
            ["boar"] = "野猪",
            ["chicken"] = "鸡",
            ["stag"] = "鹿",
            ["deer"] = "鹿",
            ["horse"] = "马",
            ["rabbit"] = "兔子",
            ["pig"] = "猪",
            ["sheep"] = "羊",
            ["goat"] = "山羊",
            ["cow"] = "牛",
            ["dolphin"] = "海豚",
            ["shark"] = "鲨鱼",
            ["alligator"] = "鳄鱼",
            ["snake"] = "蛇",
            ["hawk"] = "鹰",
            ["vulture"] = "秃鹫",
            ["tiger"] = "老虎",
            ["lion"] = "狮子",
            ["elephant"] = "大象",
            ["rhino"] = "犀牛"
        };

        private readonly Dictionary<string, bool> _animalCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _animalNameCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private void Init()
        {
            MigrateLegacyConfig();
            LoadConfigData();
            permission.RegisterPermission(PERM_ADMIN, this);
        }

        // 插件名从纯中文改为"英文 中文"后，自动沿用旧配置，避免配置被重置
        private void MigrateLegacyConfig()
        {
            try
            {
                string dir = Interface.Oxide.ConfigDirectory;
                if (string.IsNullOrEmpty(dir)) return;

                string oldPath = System.IO.Path.Combine(dir, "击杀提示.json");
                string newPath = System.IO.Path.Combine(dir, "KillFeed 击杀提示.json");
                if (System.IO.File.Exists(oldPath) && !System.IO.File.Exists(newPath))
                    System.IO.File.Copy(oldPath, newPath);
            }
            catch { }
        }

        private void OnServerInitialized()
        {
            if (configData.Enabled)
            {
                Puts("========================================");
                Puts("KillFeed 击杀提示 插件 v3.4.1 已加载！(适配最新Rust)");
                Puts($"显示模式: {(configData.ShowInChat ? "聊天" : "")}{(configData.ShowInChat && configData.ShowInHud ? "+HUD" : (configData.ShowInHud ? "HUD" : ""))}");
                Puts("可用命令: /killfeed");
                Puts("========================================");
            }
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null || player.IsNpc) return;
            timer.Once(4f, () =>
            {
                if (player == null || !player.IsConnected) return;
                SendReply(player, "<color=#FF0000>[击杀提示]</color> 命令列表:");
                SendReply(player, "  /killfeed info - 查看当前设置");
                SendReply(player, "  /killfeed help - 查看帮助");
                SendReply(player, "  /help4 - 查看击杀提示命令");
            });
        }

        private void Unload()
        {
            _animalCache.Clear();
            _animalNameCache.Clear();
        }

        #endregion

        #region 击杀处理

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (!configData.Enabled || entity == null || info == null) return;

            if (IsHarvesting(entity, info))
            {
                return;
            }

            var victim = entity as BasePlayer;
            if (victim != null)
            {
                // 最新Rust: 科学家等 NPCPlayer 也继承自 BasePlayer，需要先按 NPC 处理
                if (victim.IsNpc)
                {
                    if (configData.ShowNPCKills)
                        HandleNpcPlayerDeath(victim, info);
                }
                else
                {
                    HandlePlayerDeath(victim, info);
                }
                return;
            }

            if (configData.ShowAnimalKills && IsAnimal(entity))
            {
                HandleAnimalDeath(entity, info);
                return;
            }

            if (configData.ShowNPCKills && entity is BaseNpc)
            {
                HandleNPCDeath(entity as BaseNpc, info);
            }
        }

        private void HandlePlayerDeath(BasePlayer victim, HitInfo info)
        {
            if (victim == null || info == null) return;

            var attacker = GetInitiatorPlayer(info);

            if (attacker == null || attacker == victim)
            {
                if (configData.ShowSuicide)
                {
                    var sb = new StringBuilder(128);
                    sb.Append("<color=#FF6B6B>");
                    sb.Append(victim.displayName);
                    sb.Append("</color> 自杀了");
                    SendKillMessage(sb.ToString(), victim, null);
                }
                return;
            }

            if (configData.ShowPlayerKills && attacker is BasePlayer)
            {
                string killerName = attacker.IsNpc ? GetNpcPlayerName(attacker) : attacker.displayName;
                string weaponName = configData.ShowWeaponName ? GetWeaponName(info) : null;
                float distance = configData.ShowDistance ? CalculateDistance(victim, info) : 0f;
                string message = BuildKillMessage(killerName, victim.displayName, weaponName, distance);
                SendKillMessage(message, attacker, victim);
            }
        }

        private void HandleNpcPlayerDeath(BasePlayer npc, HitInfo info)
        {
            if (npc == null || info == null) return;

            var attacker = GetInitiatorPlayer(info);
            if (attacker == null || attacker == npc || attacker.IsNpc) return;

            string npcName = GetNpcPlayerName(npc);
            string weaponName = configData.ShowWeaponName ? GetWeaponName(info) : null;
            float distance = configData.ShowDistance ? CalculateDistance(npc, info) : 0f;
            string message = BuildKillMessage(attacker.displayName, npcName, weaponName, distance);
            SendKillMessage(message, attacker, null);
        }

        private void HandleAnimalDeath(BaseCombatEntity animal, HitInfo info)
        {
            if (animal == null || info == null) return;

            var attacker = GetInitiatorPlayer(info);
            if (attacker == null || !(attacker is BasePlayer)) return;

            string animalName = GetAnimalName(animal);
            string weaponName = configData.ShowWeaponName ? GetWeaponName(info) : null;
            float distance = configData.ShowDistance ? CalculateDistance(animal, info) : 0f;
            string message = BuildKillMessage(attacker.displayName, animalName, weaponName, distance);
            SendKillMessage(message, attacker, null);
        }

        private void HandleNPCDeath(BaseNpc npc, HitInfo info)
        {
            if (npc == null || info == null) return;

            var attacker = GetInitiatorPlayer(info);
            if (attacker == null || !(attacker is BasePlayer)) return;

            string npcName = GetNPCName(npc);
            string weaponName = configData.ShowWeaponName ? GetWeaponName(info) : null;
            float distance = configData.ShowDistance ? CalculateDistance(npc, info) : 0f;
            string message = BuildKillMessage(attacker.displayName, npcName, weaponName, distance);
            SendKillMessage(message, attacker, null);
        }

        private string BuildKillMessage(string killerName, string victimName, string weaponName, float distance)
        {
            var sb = new StringBuilder(256);
            sb.Append("<color=#4ECDC4>");
            sb.Append(killerName);
            sb.Append("</color> 击杀了 <color=#FF6B6B>");
            sb.Append(victimName);
            sb.Append("</color>");

            if (!string.IsNullOrEmpty(weaponName))
            {
                sb.Append(" [<color=#FFE66D>");
                sb.Append(weaponName);
                sb.Append("</color>]");
            }

            if (distance > 0)
            {
                sb.Append(" <color=#95E1D3>(");
                sb.Append(distance.ToString("F1"));
                sb.Append("米)</color>");
            }

            return sb.ToString();
        }

        private void SendKillMessage(string message, BasePlayer killer, BasePlayer victim)
        {
            if (string.IsNullOrEmpty(message)) return;

            var prefixBuilder = new StringBuilder(128);
            prefixBuilder.Append("<color=");
            prefixBuilder.Append(configData.ChatPrefixColor);
            prefixBuilder.Append(">");
            prefixBuilder.Append(configData.ChatPrefix);
            prefixBuilder.Append("</color> ");
            prefixBuilder.Append(message);
            string fullMessage = prefixBuilder.ToString();

            if (configData.ShowInChat)
            {
                if (configData.ShowToAllPlayers)
                {
                    var players = BasePlayer.activePlayerList;
                    if (players != null && players.Count > 0)
                    {
                        for (int i = 0; i < players.Count; i++)
                        {
                            var player = players[i];
                            if (player != null && player.IsConnected && !CompatIsDead(player))
                            {
                                SendReply(player, fullMessage);
                            }
                        }
                    }
                }
                else
                {
                    if (configData.ShowToKiller && killer != null && killer.IsConnected && !CompatIsDead(killer))
                    {
                        SendReply(killer, fullMessage);
                    }

                    if (configData.ShowToVictim && victim != null && victim.IsConnected && !CompatIsDead(victim))
                    {
                        SendReply(victim, fullMessage);
                    }
                }
            }

            if (configData.ShowInHud)
            {
                SendHudMessage(fullMessage);
            }
        }

        private void SendHudMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return;

            var players = BasePlayer.activePlayerList;
            if (players == null || players.Count == 0) return;

            float duration = Mathf.Clamp(configData.HudDuration, 1f, 10f);

            for (int i = 0; i < players.Count; i++)
            {
                var player = players[i];
                if (player != null && player.IsConnected && !CompatIsDead(player))
                {
                    SendReply(player, message);
                }
            }
        }

        #endregion

        #region 辅助方法

        // 兼容不同版本的 HitInfo.InitiatorPlayer (新版可能改名或用 Initiator)
        private static BasePlayer GetInitiatorPlayer(HitInfo info)
        {
            if (info == null) return null;
            try
            {
                var prop = typeof(HitInfo).GetProperty("InitiatorPlayer",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (prop != null) return prop.GetValue(info, null) as BasePlayer;
                var field = typeof(HitInfo).GetField("InitiatorPlayer",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (field != null) return field.GetValue(info) as BasePlayer;
            }
            catch { }
            // 新版: 用 Initiator 再转 BasePlayer
            try
            {
                var prop = typeof(HitInfo).GetProperty("Initiator",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (prop != null)
                {
                    var val = prop.GetValue(info, null);
                    if (val is BasePlayer bp) return bp;
                    if (val is BaseEntity be) return be.ToPlayer();
                }
                var field = typeof(HitInfo).GetField("Initiator",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (field != null)
                {
                    var val = field.GetValue(info);
                    if (val is BasePlayer bp) return bp;
                    if (val is BaseEntity be) return be.ToPlayer();
                }
            }
            catch { }
            try { return info.InitiatorPlayer; } catch { }
            return null;
        }

        // 兼容不同版本的 IsDead (方法 vs 属性)
        private static bool CompatIsDead(BaseCombatEntity entity)
        {
            if (entity == null) return true;
            try { return entity.IsDead(); }
            catch { }
            try
            {
                var meth = typeof(BaseCombatEntity).GetMethod("IsDead",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                if (meth != null) return (bool)meth.Invoke(entity, null);
                var prop = typeof(BaseCombatEntity).GetProperty("IsDead",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (prop != null) return (bool)prop.GetValue(entity, null);
            }
            catch { }
            try { return entity.health <= 0f; } catch { return false; }
        }

        private bool IsHarvesting(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null) return false;

            var corpse = entity.GetComponent<LootableCorpse>();
            if (corpse != null)
            {
                return true;
            }

            if (entity.health <= 0 && CompatIsDead(entity))
            {
                if (GetInitiatorPlayer(info) != null)
                {
                    var weapon = info.Weapon;
                    if (weapon != null)
                    {
                        var item = weapon.GetItem();
                        if (item?.info != null)
                        {
                            string shortname = item.info.shortname?.ToLower() ?? "";

                            if (shortname.Contains("knife") ||
                                shortname.Contains("hatchet") ||
                                shortname.Contains("pickaxe") ||
                                shortname.Contains("bone") ||
                                shortname == "rock")
                            {
                                if (info.damageTypes != null)
                                {
                                    float totalDamage = info.damageTypes.Total();
                                    if (totalDamage < 30f)
                                    {
                                        return true;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (IsAnimal(entity))
            {
                if (entity.health <= 0)
                {
                    var weapon = info.Weapon;
                    if (weapon != null)
                    {
                        var item = weapon.GetItem();
                        if (item?.info != null)
                        {
                            string shortname = item.info.shortname?.ToLower() ?? "";
                            if (shortname.Contains("knife") ||
                                shortname.Contains("hatchet") ||
                                shortname.Contains("bone"))
                            {
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        private string GetWeaponName(HitInfo info)
        {
            if (info?.Weapon == null) return null;

            var item = info.Weapon.GetItem();
            if (item?.info == null) return null;

            string weaponShortname = item.info.shortname;
            if (string.IsNullOrEmpty(weaponShortname)) return null;

            if (configData.WeaponNames != null && configData.WeaponNames.TryGetValue(weaponShortname, out string weaponName))
            {
                return weaponName;
            }

            return weaponShortname;
        }

        private bool IsAnimal(BaseCombatEntity entity)
        {
            if (entity == null) return false;

            string prefabName = entity.ShortPrefabName;
            if (string.IsNullOrEmpty(prefabName)) return false;

            if (_animalCache.TryGetValue(prefabName, out bool cached))
            {
                return cached;
            }

            bool isAnimal = false;
            string lowerName = prefabName.ToLower();

            if (lowerName.Contains("polarbear") || (lowerName.Contains("polar") && lowerName.Contains("bear")))
            {
                isAnimal = true;
            }
            else
            {
                foreach (var key in AnimalNameMap.Keys)
                {
                    if (lowerName.Contains(key))
                    {
                        isAnimal = true;
                        break;
                    }
                }
            }

            _animalCache[prefabName] = isAnimal;
            return isAnimal;
        }

        private string GetAnimalName(BaseCombatEntity animal)
        {
            if (animal == null) return "未知动物";

            string prefabName = animal.ShortPrefabName;
            if (string.IsNullOrEmpty(prefabName)) return "动物";

            if (_animalNameCache.TryGetValue(prefabName, out string cachedName))
            {
                return cachedName;
            }

            string animalName = prefabName.ToLower();
            string result = "动物";

            if (animalName.Contains("polarbear") || (animalName.Contains("polar") && animalName.Contains("bear")))
            {
                result = "北极熊";
            }
            else
            {
                foreach (var kvp in AnimalNameMap)
                {
                    if (animalName.Contains(kvp.Key))
                    {
                        result = kvp.Value;
                        break;
                    }
                }
            }

            _animalNameCache[prefabName] = result;
            return result;
        }

        private string GetNPCName(BaseNpc npc)
        {
            if (npc == null) return "未知";

            string npcName = npc.ShortPrefabName.ToLower();
            if (npcName.Contains("scientist")) return "科学家";
            if (npcName.Contains("scavenger")) return "拾荒者";
            if (npcName.Contains("brute")) return "蛮力者";
            if (npcName.Contains("bandit")) return "土匪";
            if (npcName.Contains("raider")) return "袭击者";
            if (npcName.Contains("wolf")) return "野狼";
            if (npcName.Contains("bear")) return "熊";
            if (npcName.Contains("boar")) return "野猪";
            return "NPC";
        }

        // NPCPlayer(科学家/拾荒者等) 继承自 BasePlayer，单独命名避免显示英文预制体名
        private string GetNpcPlayerName(BasePlayer npc)
        {
            if (npc == null) return "NPC";

            // 插件生成的AI虚拟玩家会设置自定义中文名，优先使用
            if (!string.IsNullOrEmpty(npc.displayName)
                && npc.displayName.IndexOf("scientist", StringComparison.OrdinalIgnoreCase) < 0
                && npc.displayName != "NPC")
                return npc.displayName;

            string prefab = (npc.ShortPrefabName ?? string.Empty).ToLower();
            if (prefab.Contains("scientist")) return "科学家";
            if (prefab.Contains("scavenger")) return "拾荒者";
            if (prefab.Contains("brute")) return "蛮力者";
            if (prefab.Contains("bandit")) return "土匪";
            if (prefab.Contains("raider")) return "袭击者";
            if (prefab.Contains("tunnel")) return "隧道居民";
            if (!string.IsNullOrEmpty(npc.displayName)) return npc.displayName;
            return "NPC";
        }

        private float CalculateDistance(BaseCombatEntity victim, HitInfo info)
        {
            if (victim == null || info == null || victim.transform == null) return 0f;

            var attacker = GetInitiatorPlayer(info);
            if (attacker == null || attacker.transform == null) return 0f;

            return Vector3.Distance(victim.transform.position, attacker.transform.position);
        }

        #endregion

        #region 命令

        [ChatCommand("killfeed")]
        private void KillFeedCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            if (args == null || args.Length == 0)
            {
                ShowHelp(player);
                return;
            }

            string subCommand = args[0].ToLower();

            switch (subCommand)
            {
                case "toggle":
                    if (!HasAdminPerm(player))
                    {
                        SendReply(player, "你没有权限使用此命令！");
                        return;
                    }
                    configData.Enabled = !configData.Enabled;
                    SaveConfig();
                    SendReply(player, $"击杀提示已{(configData.Enabled ? "启用" : "禁用")}");
                    break;

                case "reload":
                    if (!HasAdminPerm(player))
                    {
                        SendReply(player, "你没有权限使用此命令！");
                        return;
                    }
                    LoadConfigData();
                    SendReply(player, "配置已重新加载");
                    break;

                case "info":
                    ShowInfo(player);
                    break;

                case "chat":
                    if (!HasAdminPerm(player))
                    {
                        SendReply(player, "你没有权限使用此命令！");
                        return;
                    }
                    configData.ShowInChat = !configData.ShowInChat;
                    SaveConfig();
                    SendReply(player, $"聊天显示已{(configData.ShowInChat ? "启用" : "禁用")}");
                    break;

                case "hud":
                    if (!HasAdminPerm(player))
                    {
                        SendReply(player, "你没有权限使用此命令！");
                        return;
                    }
                    configData.ShowInHud = !configData.ShowInHud;
                    SaveConfig();
                    SendReply(player, $"HUD显示已{(configData.ShowInHud ? "启用" : "禁用")}");
                    break;

                case "help":
                    ShowHelp(player);
                    break;

                default:
                    ShowHelp(player);
                    break;
            }
        }

        private void ShowHelp(BasePlayer player)
        {
            var lines = new[]
            {
                "========================================",
                "击杀提示插件命令帮助",
                "========================================",
                "/killfeed toggle - 启用/禁用插件 (管理员)",
                "/killfeed chat - 切换聊天显示 (管理员)",
                "/killfeed hud - 切换HUD显示 (管理员)",
                "/killfeed reload - 重新加载配置 (管理员)",
                "/killfeed info - 显示当前设置",
                "/killfeed help - 显示此帮助",
                "/help4 - 查看击杀提示命令",
                "========================================"
            };
            SendReply(player, string.Join("\n", lines));
        }

        [ChatCommand("help4")]
        private void CmdHelp4(BasePlayer player, string command, string[] args)
        {
            ShowHelp(player);
        }

        private void ShowInfo(BasePlayer player)
        {
            var lines = new[]
            {
                "========================================",
                "击杀提示插件当前设置",
                "========================================",
                $"插件状态: {(configData.Enabled ? "启用" : "禁用")}",
                $"显示玩家击杀: {(configData.ShowPlayerKills ? "是" : "否")}",
                $"显示NPC击杀: {(configData.ShowNPCKills ? "是" : "否")}",
                $"显示动物击杀: {(configData.ShowAnimalKills ? "是" : "否")}",
                $"显示自杀: {(configData.ShowSuicide ? "是" : "否")}",
                $"聊天显示: {(configData.ShowInChat ? "是" : "否")}",
                $"HUD显示: {(configData.ShowInHud ? "是" : "否")}",
                $"向所有玩家显示: {(configData.ShowToAllPlayers ? "是" : "否")}",
                $"显示武器名称: {(configData.ShowWeaponName ? "是" : "否")}",
                $"显示击杀距离: {(configData.ShowDistance ? "是" : "否")}",
                "========================================"
            };
            SendReply(player, string.Join("\n", lines));
        }

        private bool HasAdminPerm(BasePlayer player)
        {
            if (player == null) return false;
            if (player.IsAdmin || player.IsDeveloper) return true;
            return permission.UserHasPermission(player.UserIDString, PERM_ADMIN);
        }

        #endregion

        #region 控制台命令

        [ConsoleCommand("killfeed.reload")]
        private void CcmdReload(ConsoleSystem.Arg arg)
        {
            LoadConfigData();
            arg.ReplyWith("[击杀提示] 配置已重载");
        }

        [ConsoleCommand("killfeed.toggle")]
        private void CcmdToggle(ConsoleSystem.Arg arg)
        {
            configData.Enabled = !configData.Enabled;
            SaveConfig();
            arg.ReplyWith($"[击杀提示] 已{(configData.Enabled ? "启用" : "禁用")}");
        }

        [ConsoleCommand("killfeed.status")]
        private void CcmdStatus(ConsoleSystem.Arg arg)
        {
            arg.ReplyWith($"[击杀提示] 状态 - 启用: {(configData.Enabled ? "是" : "否")} | 聊天: {(configData.ShowInChat ? "是" : "否")} | HUD: {(configData.ShowInHud ? "是" : "否")}");
        }

        #endregion
    }
}
