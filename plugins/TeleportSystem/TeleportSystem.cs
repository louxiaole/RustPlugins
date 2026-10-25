using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Oxide.Core;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("传送系统", "Custom", "3.9.0")]
    [Description("传送点管理+防卡死+载具同步+地形加载等待 (v3.9.0: 适配最新Rust, StringView/NetworkableId/estimatedSpeed全反射兼容)")]
    class TeleportSystem : RustPlugin
    {
        #region Carbon / Oxide 双框架兼容层

        private static readonly Dictionary<Type, Dictionary<string, MemberInfo>> _compatCache =
            new Dictionary<Type, Dictionary<string, MemberInfo>>();

        private static TMember GetCompatMember<TMember>(Type type, string name, BindingFlags flags)
            where TMember : MemberInfo
        {
            if (!_compatCache.TryGetValue(type, out var map))
            {
                map = new Dictionary<string, MemberInfo>();
                _compatCache[type] = map;
            }
            if (map.TryGetValue(name, out var cached)) return cached as TMember;

            MemberInfo result = type.GetProperty(name, flags) as MemberInfo
                             ?? type.GetMethod(name, flags, null, Type.EmptyTypes, null);
            map[name] = result;
            return result as TMember;
        }

        private static bool CompatIsConnected(BasePlayer player)
        {
            if (player == null) return false;
            // 全部走反射，避免属性/方法调用歧义导致的编译期错误
            var prop = GetCompatMember<PropertyInfo>(typeof(BasePlayer), "IsConnected",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null) { try { return (bool)prop.GetValue(player, null); } catch { } }
            var meth = GetCompatMember<MethodInfo>(typeof(BasePlayer), "IsConnected",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (meth != null) { try { return (bool)meth.Invoke(player, null); } catch { } }
            // 备用：查找 connection 字段/activeConnection 属性判断
            var fconn = typeof(BasePlayer).GetProperty("Connection", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (fconn != null) { try { return fconn.GetValue(player, null) != null; } catch { } }
            var fact = typeof(BasePlayer).GetField("connection", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (fact != null) { try { return fact.GetValue(player) != null; } catch { } }
            return true;
        }

        private static bool CompatIsDestroyed(BaseEntity entity)
        {
            if (entity == null) return true;
            // 全部走反射
            var prop = GetCompatMember<PropertyInfo>(typeof(BaseNetworkable), "IsDestroyed",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null) { try { return (bool)prop.GetValue(entity, null); } catch { } }
            var meth = GetCompatMember<MethodInfo>(typeof(BaseNetworkable), "IsDestroyed",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (meth != null) { try { return (bool)meth.Invoke(entity, null); } catch { } }
            // 备用：反射检测 NetworkableId.Value / IsNil
            try
            {
                if (entity.net == null) return true;
                var netId = entity.net.ID;
                // IsNil 属性
                var idProp = netId.GetType().GetProperty("IsNil",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (idProp != null) return (bool)idProp.GetValue(netId, null);
                // Value 字段/属性 等于 0/空
                var idValue = netId.GetType().GetProperty("Value",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (idValue != null)
                {
                    var v = idValue.GetValue(netId, null);
                    if (v is ulong ul) return ul == 0;
                    if (v is uint ui) return ui == 0;
                    if (v is long l) return l == 0;
                    if (v is int i) return i == 0;
                }
                var idField = netId.GetType().GetField("Value",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (idField != null)
                {
                    var v = idField.GetValue(netId);
                    if (v is ulong ul) return ul == 0;
                    if (v is uint ui) return ui == 0;
                }
            }
            catch { /* ignore */ }
            return false;
        }

        // IsDead: 全部走反射 + 回退检查 health
        private static bool CompatIsDead(BasePlayer player)
        {
            if (player == null) return true;
            var meth = GetCompatMember<MethodInfo>(typeof(BaseCombatEntity), "IsDead",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (meth != null) { try { return (bool)meth.Invoke(player, null); } catch { } }
            var prop = GetCompatMember<PropertyInfo>(typeof(BaseCombatEntity), "IsDead",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null) { try { return (bool)prop.GetValue(player, null); } catch { } }
            // 备用：取 IsAlive 反射后取反（注意也是全反射访问，避免属性/方法歧义）
            var methA = GetCompatMember<MethodInfo>(typeof(BaseCombatEntity), "IsAlive",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (methA != null) { try { return !(bool)methA.Invoke(player, null); } catch { } }
            var propA = GetCompatMember<PropertyInfo>(typeof(BaseCombatEntity), "IsAlive",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (propA != null) { try { return !(bool)propA.GetValue(player, null); } catch { } }
            // 终极备用：health 字段
            var hpProp = typeof(BaseCombatEntity).GetProperty("health", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (hpProp != null) { try { return (float)hpProp.GetValue(player, null) <= 0f; } catch { } }
            return false;
        }

        // HitInfo: InitiatorPlayer 不同版本名字不同 (InitiatorPlayer / Initiator)
        private static BasePlayer GetHitInfoInitiatorPlayer(HitInfo info)
        {
            if (info == null) return null;
            try
            {
                // 优先用 InitiatorPlayer (老版本)
                var prop = typeof(HitInfo).GetProperty("InitiatorPlayer",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null) return prop.GetValue(info, null) as BasePlayer;

                var field = typeof(HitInfo).GetField("InitiatorPlayer",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null) return field.GetValue(info) as BasePlayer;

                // 新版本: 用 Initiator 再转 BasePlayer
                var val = GetHitInfoInitiator(info);
                if (val is BasePlayer bp) return bp;
                if (val is BaseEntity be) return be.ToPlayer();
            }
            catch { /* ignore */ }
            try { return info.InitiatorPlayer; } catch { /* ignore */ }
            return null;
        }

        // 兼容: HitInfo.Initiator (属性or字段)
        private static object GetHitInfoInitiator(HitInfo info)
        {
            if (info == null) return null;
            try
            {
                var prop = typeof(HitInfo).GetProperty("Initiator",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null) return prop.GetValue(info, null);

                var field = typeof(HitInfo).GetField("Initiator",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null) return field.GetValue(info);
            }
            catch { /* ignore */ }
            try { return info.Initiator; } catch { /* ignore */ }
            return null;
        }

        // 兼容 StringView (最新Rust ConsoleSystem.Arg.Args 是 StringView[])
        private static string ArgToStr(ConsoleSystem.Arg arg, int index)
        {
            if (arg == null || arg.Args == null || index < 0 || index >= arg.Args.Length) return null;
            try { return arg.Args[index].ToString(); }
            catch { return null; }
        }

        // 将所有参数拼接为字符串 (兼容 StringView[])
        private static string ArgsJoin(ConsoleSystem.Arg arg, string sep = " ")
        {
            if (arg == null || arg.Args == null || arg.Args.Length == 0) return "";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < arg.Args.Length; i++)
            {
                if (i > 0) sb.Append(sep);
                try { sb.Append(arg.Args[i].ToString()); }
                catch { }
            }
            return sb.ToString();
        }

        // 兼容不同版本的 player.estimatedSpeed (字段or属性)
        private static float GetEstimatedSpeed(BasePlayer player)
        {
            if (player == null) return 0f;
            try
            {
                var field = typeof(BasePlayer).GetField("estimatedSpeed",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null && field.FieldType == typeof(float))
                    return (float)field.GetValue(player);
                var prop = typeof(BasePlayer).GetProperty("estimatedSpeed",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null && prop.PropertyType == typeof(float))
                    return (float)prop.GetValue(player, null);
            }
            catch { }
            try { return player.estimatedSpeed; } catch { return 0f; }
        }

        #endregion

        private const string PERM_ADMIN = "teleportsystem.admin";

        private Configuration config;

        private class Configuration
        {
            public PluginSettings Settings = new PluginSettings();
        }

        private class PluginSettings
        {
            public string ChatPrefix = "[传送系统]";
            public string ChatPrefixColor = "#00FF00";
            public int MaxTeleportPoints = 20;
            public float TeleportDelay = 3.0f;
            public bool RequireStandingStill = true;
            public float StandingStillThreshold = 0.5f;
            public bool ShowCoordinates = true;
            public bool RecordDeathLocations = true;
            public int MaxDeathLocations = 5;
        }

        private StoredData storedData;

        private class StoredData
        {
            public Dictionary<ulong, List<TeleportPoint>> PlayerPoints = new Dictionary<ulong, List<TeleportPoint>>();
            public Dictionary<ulong, List<DeathLocation>> DeathLocations = new Dictionary<ulong, List<DeathLocation>>();
        }

        private class TeleportPoint
        {
            public string Name;
            public float X;
            public float Y;
            public float Z;

            public TeleportPoint() { }

            public TeleportPoint(string name, Vector3 position)
            {
                Name = name;
                X = position.x;
                Y = position.y;
                Z = position.z;
            }

            public Vector3 GetPosition()
            {
                return new Vector3(X, Y, Z);
            }
        }

        private class DeathLocation
        {
            public float X;
            public float Y;
            public float Z;
            public string DeathDate;
            public string DeathTime;
            public string KillerName;

            public DeathLocation() { }

            public DeathLocation(Vector3 position, string killerName = null)
            {
                X = position.x;
                Y = position.y;
                Z = position.z;
                DateTime now = DateTime.Now;
                DeathDate = now.ToString("MM-dd");
                DeathTime = now.ToString("HH:mm");
                KillerName = killerName;
            }

            public Vector3 GetPosition()
            {
                return new Vector3(X, Y, Z);
            }

            public string GetDisplayName()
            {
                if (!string.IsNullOrEmpty(KillerName))
                    return $"死亡地点 ({DeathDate} {DeathTime}) 被 {KillerName} 击杀";
                return $"死亡地点 ({DeathDate} {DeathTime})";
            }
        }

        private Dictionary<ulong, Timer> activeTeleports = new Dictionary<ulong, Timer>();
        private Dictionary<ulong, Vector3> pendingAddPositions = new Dictionary<ulong, Vector3>();
        private readonly HashSet<ulong> _pendingDeathNotice = new HashSet<ulong>(); // 复活时提示死亡地点
        private readonly Dictionary<ulong, float> _stuckCooldown = new Dictionary<ulong, float>(); // 防卡死冷却

        private void Init()
        {
            LoadConfigData();
            LoadData();
            permission.RegisterPermission(PERM_ADMIN, this);
        }

        private void OnServerInitialized()
        {
            Puts("传送系统插件 v3.9.0 已加载！(适配最新Rust)");
            Puts("命令: /tp 打开传送菜单 | /stuck 防卡死脱困 | /fixvehicle 修复载具无法驾驶");
            Puts("载具同步：传送时自动同步身下的摩托/马/汽车等载具");
            Puts("安全模式：禁用CC+清速度+2.5s无敌期+伤害Hook拦截+4轮复查，防止建筑附近/载具传送即死");
            Puts("载具修复：绝不再手动AssignSeat改坏seat.occupied；若仍无法驾驶请对准载具按 /fixvehicle");
        }

        void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;

            timer.Once(2f, () =>
            {
                if (player == null || !CompatIsConnected(player)) return;
                SendMessage(player, "========== 传送系统 ==========");
                SendMessage(player, "/tp - 打开传送菜单");
                SendMessage(player, "/tpname <名称> - 创建传送点");
                SendMessage(player, "/tp go <名称> - 传送到指定点");
                SendMessage(player, "/tpd <名称> - 删除传送点");
                SendMessage(player, "/tp back - 返回最近死亡点");
                SendMessage(player, "/tp death - 列出死亡地点");
                SendMessage(player, "/stuck - 卡墙/卡地基时自动脱困");
                SendMessage(player, "/tp help - 查看帮助");
                SendMessage(player, "/help5 - 查看传送系统命令");
                SendMessage(player, "==============================");
            });
        }

        private void Unload()
        {
            SaveData();
            foreach (var timer in activeTeleports.Values)
                timer?.Destroy();
            activeTeleports.Clear();
        }

        #region 配置管理

        protected override void LoadDefaultConfig()
        {
            Config.Clear();
            Config.WriteObject(GetDefaultConfig(), true);
        }

        private Configuration GetDefaultConfig()
        {
            return new Configuration
            {
                Settings = new PluginSettings()
            };
        }

        private void LoadConfigData()
        {
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null)
                {
                    LoadDefaultConfig();
                    config = Config.ReadObject<Configuration>();
                }
            }
            catch (Exception ex)
            {
                PrintError($"加载配置失败: {ex.Message}");
                LoadDefaultConfig();
                config = Config.ReadObject<Configuration>();
            }
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(config, true);
        }

        #endregion

        #region 数据管理

        private void LoadData()
        {
            try
            {
                storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
                if (storedData == null)
                {
                    storedData = new StoredData();
                    SaveData();
                }
            }
            catch (Exception ex)
            {
                PrintError($"加载数据失败: {ex.Message}");
                storedData = new StoredData();
            }
        }

        private void SaveData()
        {
            try
            {
                Interface.Oxide.DataFileSystem.WriteObject(Name, storedData);
            }
            catch (Exception ex)
            {
                PrintError($"保存数据失败: {ex.Message}");
            }
        }

        private List<TeleportPoint> GetPlayerPoints(ulong playerId)
        {
            if (!storedData.PlayerPoints.ContainsKey(playerId))
            {
                storedData.PlayerPoints[playerId] = new List<TeleportPoint>();
            }
            return storedData.PlayerPoints[playerId];
        }

        private List<DeathLocation> GetPlayerDeathLocations(ulong playerId)
        {
            if (!storedData.DeathLocations.ContainsKey(playerId))
            {
                storedData.DeathLocations[playerId] = new List<DeathLocation>();
            }
            return storedData.DeathLocations[playerId];
        }

        #endregion

        #region 传送管理

        private void CancelTeleport(BasePlayer player)
        {
            if (player == null) return;
            if (activeTeleports.TryGetValue(player.userID, out var t))
            {
                t?.Destroy();
                activeTeleports.Remove(player.userID);
            }
        }

        private bool IsTeleporting(BasePlayer player)
        {
            return player != null && activeTeleports.ContainsKey(player.userID);
        }

        #endregion

        #region 聊天命令

        private object OnPlayerChat(BasePlayer player, string message, ConVar.Chat.ChatChannel channel)
        {
            if (player == null || string.IsNullOrEmpty(message)) return null;

            if (pendingAddPositions.ContainsKey(player.userID))
            {
                if (message.Equals("/cancel", StringComparison.OrdinalIgnoreCase))
                {
                    pendingAddPositions.Remove(player.userID);
                    SendMessage(player, "已取消创建传送点");
                    ShowTeleportMenu(player);
                    return true;
                }

                if (message.StartsWith("/"))
                {
                    return null;
                }

                Vector3 pos = pendingAddPositions[player.userID];
                pendingAddPositions.Remove(player.userID);

                CreateTeleportPoint(player, message.Trim());
                ShowTeleportMenu(player);
                return true;
            }

            return null;
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (!config.Settings.RecordDeathLocations) return;
            var player = entity as BasePlayer;
            if (player == null) return;

            Vector3 deathPos = player.transform.position;
            string killerName = null;

            // v3.5.0 修复：兼容不同版本的 InitiatorPlayer/Initiator API
            BasePlayer initiator = GetHitInfoInitiatorPlayer(info);
            if (initiator != null && initiator != player && !string.IsNullOrEmpty(initiator.displayName))
            {
                killerName = initiator.displayName;
            }
            // 备用：NPC/动物击杀，从 info 中提取名字
            if (string.IsNullOrEmpty(killerName) && info != null)
            {
                var initObj = GetHitInfoInitiator(info);
                var initEntity = initObj as BaseEntity;
                if (initEntity != null && !(initEntity is BasePlayer))
                {
                    var shortname = initEntity.ShortPrefabName;
                    if (!string.IsNullOrEmpty(shortname)) killerName = shortname;
                }
            }

            var deathLocation = new DeathLocation(deathPos, killerName);
            var deathLocations = GetPlayerDeathLocations(player.userID);
            deathLocations.Insert(0, deathLocation);
            while (deathLocations.Count > config.Settings.MaxDeathLocations)
            {
                deathLocations.RemoveAt(deathLocations.Count - 1);
            }
            SaveData();

            // v3.5.0 修复：玩家已死亡时 SendReply 可能无法收到
            // 改为标记，等玩家复活(OnPlayerRespawned)时再提示
            _pendingDeathNotice.Add(player.userID);
        }

        // v3.5.0 修复：复活时提示死亡地点（保证玩家能收到消息）
        private void OnPlayerRespawned(BasePlayer player)
        {
            if (player == null || !_pendingDeathNotice.Contains(player.userID)) return;
            _pendingDeathNotice.Remove(player.userID);

            timer.Once(1.2f, () =>
            {
                if (player == null || !CompatIsConnected(player) || CompatIsDead(player)) return;
                var deaths = GetPlayerDeathLocations(player.userID);
                if (deaths.Count == 0) return;
                var latest = deaths[0];
                SendMessage(player, $"已记录死亡地点: {latest.GetDisplayName()}");
                SendMessage(player, "输入 /tp back 可返回最近死亡点");
            });
        }

        [ChatCommand("tp")]
        private void CmdTp(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            if (args == null || args.Length == 0)
            {
                ShowTeleportMenu(player);
                return;
            }

            string subCmd = args[0].ToLower();

            if (subCmd == "help")
            {
                ShowHelp(player);
                return;
            }

            if (subCmd == "list")
            {
                ListTeleportPoints(player);
                return;
            }

            if (subCmd == "go" && args.Length >= 2)
            {
                string pointName = string.Join(" ", args.Skip(1));
                TeleportToPointByName(player, pointName);
                return;
            }

            if (subCmd == "delete" && args.Length >= 2)
            {
                string pointName = string.Join(" ", args.Skip(1));
                DeleteTeleportPoint(player, pointName);
                return;
            }

            if (subCmd == "death")
            {
                ListDeathLocations(player);
                return;
            }

            if (subCmd == "back")
            {
                TeleportToLastDeath(player);
                return;
            }

            if (subCmd == "admin")
            {
                if (!HasAdminPerm(player))
                {
                    SendMessage(player, "你没有权限使用此命令");
                    return;
                }
                if (args.Length >= 2)
                {
                    string targetName = string.Join(" ", args.Skip(1));
                    AdminTeleportToPlayer(player, targetName);
                }
                else
                {
                    AdminListPlayers(player);
                }
                return;
            }

            if (subCmd == "wipe" && args.Length >= 2)
            {
                if (!HasAdminPerm(player))
                {
                    SendMessage(player, "你没有权限使用此命令");
                    return;
                }
                string targetName = string.Join(" ", args.Skip(1));
                AdminWipePlayerPoints(player, targetName);
                return;
            }

            ShowHelp(player);
        }

        [ChatCommand("tpname")]
        private void CmdTpName(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (args == null || args.Length == 0)
            {
                SendMessage(player, "用法: /tpname <传送点名称>");
                return;
            }
            string pointName = string.Join(" ", args).Trim();
            CreateTeleportPoint(player, pointName);
        }

        [ChatCommand("tpc")]
        private void CmdTpCreate(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (args == null || args.Length == 0)
            {
                SendMessage(player, "用法: /tpc <传送点名称>");
                return;
            }
            string pointName = string.Join(" ", args).Trim();
            CreateTeleportPoint(player, pointName);
        }

        [ChatCommand("tpd")]
        private void CmdTpDelete(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (args == null || args.Length == 0)
            {
                SendMessage(player, "用法: /tpd <传送点名称>");
                return;
            }
            string pointName = string.Join(" ", args).Trim();
            DeleteTeleportPoint(player, pointName);
        }

        [ChatCommand("stuck")]
        private void CmdStuck(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            TryUnstuck(player);
        }

        [ChatCommand("unstuck")]
        private void CmdUnstuck(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            TryUnstuck(player);
        }

        // v3.6.0 UI美化：卡片双层边框+渐变、按钮配色优化
        // 修复：死亡点"返回"→"传送"，每条记录新增"删除"按钮
        private void ShowTeleportMenu(BasePlayer player)
        {
            CloseUI(player);

            var points = GetPlayerPoints(player.userID);
            var deaths = GetPlayerDeathLocations(player.userID);

            var container = new CuiElementContainer();

            // 主面板 - 加深色半透明磨砂遮罩
            container.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.88" },
                RectTransform = { AnchorMin = "0.28 0.15", AnchorMax = "0.72 0.85" },
                CursorEnabled = true
            }, "Overlay", "TeleportMenu_Main");

            // 外发光描边（紫色渐变）
            container.Add(new CuiPanel
            {
                Image = { Color = "0.22 0.10 0.40 0.65" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, "TeleportMenu_Main", "OuterGlow");

            // 主面板内框 - 深色底
            container.Add(new CuiPanel
            {
                Image = { Color = "0.07 0.07 0.11 0.98" },
                RectTransform = { AnchorMin = "0.006 0.006", AnchorMax = "0.994 0.994" }
            }, "OuterGlow", "MainInner");

            // ===== 标题栏 =====
            container.Add(new CuiPanel
            {
                Image = { Color = "0.12 0.07 0.20 1" },
                RectTransform = { AnchorMin = "0 0.935", AnchorMax = "1 1" }
            }, "MainInner", "TitleBar");
            // 标题栏上沿金边
            container.Add(new CuiPanel
            {
                Image = { Color = "0.80 0.60 0.20 0.9" },
                RectTransform = { AnchorMin = "0 0.97", AnchorMax = "1 1" }
            }, "TitleBar", "TitleTopAccent");

            container.Add(new CuiLabel
            {
                Text = { Text = "★ TELEPORT 传送系统 ★", FontSize = 15, Align = TextAnchor.MiddleCenter, Color = "0.92 0.80 0.45 1", FadeIn = 0.2f },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0.90 1" }
            }, "TitleBar", "TitleText");

            // 关闭按钮 - 深红立体
            container.Add(new CuiPanel
            {
                Image = { Color = "0.55 0.15 0.15 1" },
                RectTransform = { AnchorMin = "0.915 0.18", AnchorMax = "0.985 0.82" }
            }, "TitleBar", "CloseBtnBg");
            container.Add(new CuiButton
            {
                Button = { Command = "tp.close", Color = "0.35 0.10 0.10 1" },
                RectTransform = { AnchorMin = "0.06 0.12", AnchorMax = "0.94 0.88" },
                Text = { Text = "✕", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "0.95 0.60 0.60 1" }
            }, "CloseBtnBg", "CloseBtn");

            // ===== 内容区 =====
            container.Add(new CuiPanel
            {
                Image = { Color = "0.04 0.04 0.06 1" },
                RectTransform = { AnchorMin = "0.012 0.012", AnchorMax = "0.988 0.92" }
            }, "MainInner", "ContentPanel");

            float y = 0.97f;
            int count = 0;

            if (points.Count == 0)
            {
                container.Add(new CuiLabel
                {
                    Text = { Text = "—  暂无传送点  —\n点击下方按钮创建", FontSize = 13, Align = TextAnchor.MiddleCenter, Color = "0.55 0.55 0.65 1" },
                    RectTransform = { AnchorMin = "0.10 0.55", AnchorMax = "0.90 0.75" }
                }, "ContentPanel", "EmptyText");
                y -= 0.20f;
            }
            else
            {
                foreach (var point in points)
                {
                    if (count >= 5) break;
                    y -= 0.175f;

                    // 外框：紫色渐变
                    container.Add(new CuiPanel
                    {
                        Image = { Color = "0.32 0.20 0.55 0.9" },
                        RectTransform = { AnchorMin = $"0.025 {y:F3}", AnchorMax = $"0.975 {y + 0.165f:F3}" }
                    }, "ContentPanel", $"Point_{count}");
                    // 内层（卡片主体）
                    container.Add(new CuiPanel
                    {
                        Image = { Color = "0.11 0.08 0.19 1" },
                        RectTransform = { AnchorMin = "0.01 0.06", AnchorMax = "0.99 0.94" }
                    }, $"Point_{count}", $"PointCard_{count}");

                    container.Add(new CuiLabel
                    {
                        Text = { Text = $"📌  {point.Name}", FontSize = 14, Align = TextAnchor.MiddleLeft, Color = "0.85 0.78 1.00 1" },
                        RectTransform = { AnchorMin = "0.035 0.52", AnchorMax = "0.54 0.96" }
                    }, $"PointCard_{count}", $"Name_{count}");

                    if (config.Settings.ShowCoordinates)
                    {
                        // 坐标胶囊 - 蓝绿色
                        container.Add(new CuiPanel
                        {
                            Image = { Color = "0.06 0.18 0.28 1" },
                            RectTransform = { AnchorMin = "0.035 0.08", AnchorMax = "0.40 0.48" }
                        }, $"PointCard_{count}", $"CoordBg_{count}");
                        container.Add(new CuiLabel
                        {
                            Text = { Text = $"X:{point.X:F0}  Y:{point.Y:F0}  Z:{point.Z:F0}", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "0.55 0.82 0.95 1" },
                            RectTransform = { AnchorMin = "0.04 0.05", AnchorMax = "0.96 0.95" }
                        }, $"CoordBg_{count}", $"Coord_{count}");
                    }

                    // [传送] 按钮：绿色渐变
                    container.Add(new CuiPanel
                    {
                        Image = { Color = "0.30 0.65 0.35 1" },
                        RectTransform = { AnchorMin = "0.56 0.12", AnchorMax = "0.76 0.90" }
                    }, $"PointCard_{count}", $"GoBg_{count}");
                    container.Add(new CuiButton
                    {
                        Button = { Command = $"tp.go {count}", Color = "0.12 0.32 0.16 1" },
                        RectTransform = { AnchorMin = "0.06 0.15", AnchorMax = "0.94 0.85" },
                        Text = { Text = "🌀 传送", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "0.70 0.95 0.70 1" }
                    }, $"GoBg_{count}", $"Go_{count}");

                    // [删除] 按钮：红色渐变
                    container.Add(new CuiPanel
                    {
                        Image = { Color = "0.70 0.25 0.25 1" },
                        RectTransform = { AnchorMin = "0.78 0.12", AnchorMax = "0.97 0.90" }
                    }, $"PointCard_{count}", $"DelBg_{count}");
                    container.Add(new CuiButton
                    {
                        Button = { Command = $"tp.delete {count}", Color = "0.32 0.10 0.10 1" },
                        RectTransform = { AnchorMin = "0.06 0.15", AnchorMax = "0.94 0.85" },
                        Text = { Text = "✕ 删除", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1.00 0.72 0.72 1" }
                    }, $"DelBg_{count}", $"Del_{count}");

                    count++;
                }
            }

            // ===== 死亡点（最近 1 条，带删除+传送+更多） =====
            if (deaths.Count > 0)
            {
                y -= 0.13f;
                // 外框：红色渐变
                container.Add(new CuiPanel
                {
                    Image = { Color = "0.50 0.14 0.22 0.95" },
                    RectTransform = { AnchorMin = $"0.025 {y:F3}", AnchorMax = $"0.975 {y + 0.150f:F3}" }
                }, "ContentPanel", "DeathPanel");
                // 内层卡片
                container.Add(new CuiPanel
                {
                    Image = { Color = "0.17 0.06 0.10 1" },
                    RectTransform = { AnchorMin = "0.01 0.06", AnchorMax = "0.99 0.94" }
                }, "DeathPanel", "DeathCard");

                container.Add(new CuiLabel
                {
                    Text = { Text = $"💀 最近死亡 · {deaths[0].GetDisplayName()}", FontSize = 13, Align = TextAnchor.MiddleLeft, Color = "1.00 0.70 0.75 1" },
                    RectTransform = { AnchorMin = "0.035 0.50", AnchorMax = "0.52 0.96" }
                }, "DeathCard", "DeathName");

                // 死亡坐标胶囊
                if (config.Settings.ShowCoordinates)
                {
                    container.Add(new CuiPanel
                    {
                        Image = { Color = "0.22 0.08 0.12 1" },
                        RectTransform = { AnchorMin = "0.035 0.08", AnchorMax = "0.38 0.48" }
                    }, "DeathCard", "DeathCoordBg");
                    container.Add(new CuiLabel
                    {
                        Text = { Text = $"X:{deaths[0].X:F0} Y:{deaths[0].Y:F0} Z:{deaths[0].Z:F0}", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "0.95 0.70 0.72 1" },
                        RectTransform = { AnchorMin = "0.04 0.05", AnchorMax = "0.96 0.95" }
                    }, "DeathCoordBg", "DeathCoord");
                }

                // [传送]按钮(原"返回")：红偏绿色
                container.Add(new CuiPanel
                {
                    Image = { Color = "0.30 0.65 0.35 1" },
                    RectTransform = { AnchorMin = "0.50 0.12", AnchorMax = "0.675 0.90" }
                }, "DeathCard", "DeathGoBg");
                container.Add(new CuiButton
                {
                    Button = { Command = "tp.back", Color = "0.12 0.32 0.16 1" },
                    RectTransform = { AnchorMin = "0.06 0.15", AnchorMax = "0.94 0.85" },
                    Text = { Text = "🌀 传送", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "0.72 0.95 0.72 1" }
                }, "DeathGoBg", "DeathGo");

                // [删除]按钮(新增!)：删除这条死亡记录
                container.Add(new CuiPanel
                {
                    Image = { Color = "0.70 0.25 0.25 1" },
                    RectTransform = { AnchorMin = "0.690 0.12", AnchorMax = "0.835 0.90" }
                }, "DeathCard", "DeathDelBg");
                container.Add(new CuiButton
                {
                    Button = { Command = "tp.deathdel 0", Color = "0.32 0.10 0.10 1" },
                    RectTransform = { AnchorMin = "0.06 0.15", AnchorMax = "0.94 0.85" },
                    Text = { Text = "✕ 删除", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1.00 0.72 0.72 1" }
                }, "DeathDelBg", "DeathDelBtn");

                // [更多]按钮：蓝色
                container.Add(new CuiPanel
                {
                    Image = { Color = "0.25 0.40 0.75 1" },
                    RectTransform = { AnchorMin = "0.850 0.12", AnchorMax = "0.97 0.90" }
                }, "DeathCard", "DeathMoreBg");
                container.Add(new CuiButton
                {
                    Button = { Command = "tp.deathlist", Color = "0.08 0.16 0.35 1" },
                    RectTransform = { AnchorMin = "0.06 0.15", AnchorMax = "0.94 0.85" },
                    Text = { Text = "⋮ 更多", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "0.72 0.85 1.00 1" }
                }, "DeathMoreBg", "DeathMore");
            }

            // ===== 底部按钮区：创建传送点 + 防卡死脱困 =====
            // 左：创建传送点（金色）
            container.Add(new CuiPanel
            {
                Image = { Color = "0.85 0.60 0.15 1" },
                RectTransform = { AnchorMin = "0.025 0.012", AnchorMax = "0.485 0.092" }
            }, "ContentPanel", "AddButtonPanel");
            container.Add(new CuiButton
            {
                Button = { Command = "tp.add", Color = "0.25 0.17 0.04 1" },
                RectTransform = { AnchorMin = "0.02 0.10", AnchorMax = "0.98 0.90" },
                Text = { Text = "＋  创建传送点", FontSize = 13, Align = TextAnchor.MiddleCenter, Color = "1.00 0.88 0.42 1" }
            }, "AddButtonPanel", "AddButton");

            // 右：防卡死脱困（橙红色醒目）
            container.Add(new CuiPanel
            {
                Image = { Color = "0.75 0.30 0.10 1" },
                RectTransform = { AnchorMin = "0.515 0.012", AnchorMax = "0.975 0.092" }
            }, "ContentPanel", "StuckButtonPanel");
            container.Add(new CuiButton
            {
                Button = { Command = "tp.stuckbtn", Color = "0.20 0.08 0.03 1" },
                RectTransform = { AnchorMin = "0.02 0.10", AnchorMax = "0.98 0.90" },
                Text = { Text = "🔧 防卡死脱困", FontSize = 13, Align = TextAnchor.MiddleCenter, Color = "1.00 0.80 0.45 1" }
            }, "StuckButtonPanel", "StuckButton");

            CuiHelper.AddUi(player, container);
        }

        #endregion
