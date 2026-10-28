using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Oxide.Core;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("TeleportSystem 传送系统", "Custom", "4.0.1")]
    [Description("传送点管理+防卡死+载具同步+地形加载等待 (v4.0.0: 适配最新Rust, StringView/NetworkableId/estimatedSpeed全反射兼容, 断线状态清理)")]
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
            MigrateLegacyConfig();
            LoadConfigData();
            LoadData();
            permission.RegisterPermission(PERM_ADMIN, this);
        }

        // 插件名从纯中文改为"英文 中文"后，自动沿用旧配置，避免配置被重置
        // 数据文件按类名保存（DataFileSystem 用的 Name），改名不受影响
        private void MigrateLegacyConfig()
        {
            try
            {
                string dir = Interface.Oxide.ConfigDirectory;
                if (string.IsNullOrEmpty(dir)) return;

                string oldPath = System.IO.Path.Combine(dir, "传送系统.json");
                string newPath = System.IO.Path.Combine(dir, "TeleportSystem 传送系统.json");
                if (System.IO.File.Exists(oldPath) && !System.IO.File.Exists(newPath))
                    System.IO.File.Copy(oldPath, newPath);
            }
            catch { }
        }

        private void OnServerInitialized()
        {
            Puts("TeleportSystem 传送系统 v4.0.1 已加载！(适配最新Rust)");
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

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;

            ulong userId = player.userID;

            // 清掉仍在等待的传送定时器，避免断线后定时器访问已销毁的玩家实体
            if (activeTeleports.TryGetValue(userId, out var teleportTimer))
            {
                teleportTimer?.Destroy();
                activeTeleports.Remove(userId);
            }

            pendingAddPositions.Remove(userId);
            _pendingDeathNotice.Remove(userId);
            _stuckCooldown.Remove(userId);
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

        #region 控制台命令

        [ConsoleCommand("tp.close")]
        private void CmdClose(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            CloseUI(player);
        }

        [ConsoleCommand("tp.stuckbtn")]
        private void CmdStuckBtn(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            CloseUI(player);
            TryUnstuck(player);
        }

        [ConsoleCommand("tp.go")]
        private void CmdGo(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;

            string arg0 = ArgToStr(arg, 0);
            if (arg0 == null) return;
            if (!int.TryParse(arg0, out int index)) return;

            var points = GetPlayerPoints(player.userID);
            if (index < 0 || index >= points.Count) return;

            var point = points[index];
            CloseUI(player);
            BeginTeleport(player, point.GetPosition(), point.Name);
        }

        [ConsoleCommand("tp.delete")]
        private void CmdDelete(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;

            string arg0 = ArgToStr(arg, 0);
            if (arg0 == null) return;
            if (!int.TryParse(arg0, out int index)) return;

            var points = GetPlayerPoints(player.userID);
            if (index < 0 || index >= points.Count) return;

            var point = points[index];
            points.RemoveAt(index);
            SaveData();

            SendMessage(player, $"已删除传送点: {point.Name}");
            ShowTeleportMenu(player);
        }

        [ConsoleCommand("tp.back")]
        private void CmdBack(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            CloseUI(player);
            TeleportToLastDeath(player);
        }

        // v3.6.0 新增：删除单条死亡记录 tp.deathdel <序号>
        [ConsoleCommand("tp.deathdel")]
        private void CmdDeathDelete(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            string arg0 = ArgToStr(arg, 0);
            if (arg0 == null)
            {
                SendMessage(player, "用法: tp.deathdel <序号>  (序号从0开始,0=最近一次)");
                return;
            }
            if (!int.TryParse(arg0, out int index)) return;

            var deaths = GetPlayerDeathLocations(player.userID);
            if (index < 0 || index >= deaths.Count)
            {
                SendMessage(player, $"死亡记录序号不存在 (当前有 {deaths.Count} 条)");
                return;
            }
            string removedName = deaths[index].GetDisplayName();
            deaths.RemoveAt(index);
            SaveData();
            SendMessage(player, $"已删除死亡记录: {removedName}");

            // 如果是死亡点UI打开状态，刷新UI
            CuiHelper.DestroyUi(player, "DeathList_Main");
            CuiHelper.DestroyUi(player, "TeleportMenu_Main");
            ShowTeleportMenu(player);
        }

        // v3.6.0 升级：死亡记录改为UI面板，每条都有传送+删除+返回按钮
        [ConsoleCommand("tp.deathlist")]
        private void CmdDeathList(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            CloseUI(player);
            ShowDeathListUI(player);
        }

        // 死亡列表 UI 面板（显示最多 5 条，各带传送/删除按钮）
        private void ShowDeathListUI(BasePlayer player)
        {
            var deaths = GetPlayerDeathLocations(player.userID);
            if (deaths.Count == 0)
            {
                SendMessage(player, "暂无死亡记录");
                return;
            }

            var container = new CuiElementContainer();

            // 主面板
            container.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.88" },
                RectTransform = { AnchorMin = "0.30 0.22", AnchorMax = "0.70 0.78" },
                CursorEnabled = true
            }, "Overlay", "DeathList_Main");

            // 外框：红色
            container.Add(new CuiPanel
            {
                Image = { Color = "0.40 0.10 0.18 0.8" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, "DeathList_Main", "DeathListOuter");

            container.Add(new CuiPanel
            {
                Image = { Color = "0.08 0.04 0.06 0.98" },
                RectTransform = { AnchorMin = "0.006 0.006", AnchorMax = "0.994 0.994" }
            }, "DeathListOuter", "DeathListInner");

            // 标题栏
            container.Add(new CuiPanel
            {
                Image = { Color = "0.20 0.06 0.10 1" },
                RectTransform = { AnchorMin = "0 0.93", AnchorMax = "1 1" }
            }, "DeathListInner", "DeathListTitleBg");

            container.Add(new CuiLabel
            {
                Text = { Text = "💀 死亡记录列表  DEATHS", FontSize = 15, Align = TextAnchor.MiddleCenter, Color = "1.00 0.68 0.72 1", FadeIn = 0.2f },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0.86 1" }
            }, "DeathListTitleBg", "DeathListTitle");

            // 返回主菜单按钮
            container.Add(new CuiPanel
            {
                Image = { Color = "0.35 0.20 0.05 1" },
                RectTransform = { AnchorMin = "0.88 0.18", AnchorMax = "0.985 0.82" }
            }, "DeathListTitleBg", "BackBtnBg");
            container.Add(new CuiButton
            {
                Button = { Command = "tp.deathlist.back", Color = "0.18 0.10 0.02 1" },
                RectTransform = { AnchorMin = "0.05 0.10", AnchorMax = "0.95 0.90" },
                Text = { Text = "◀ 返回", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1.00 0.82 0.35 1" }
            }, "BackBtnBg", "BackBtn");

            // 内容区
            container.Add(new CuiPanel
            {
                Image = { Color = "0.05 0.025 0.035 1" },
                RectTransform = { AnchorMin = "0.012 0.012", AnchorMax = "0.988 0.915" }
            }, "DeathListInner", "DeathListContent");

            float y = 0.97f;
            int showCount = Math.Min(deaths.Count, 5);
            for (int i = 0; i < showCount; i++)
            {
                var d = deaths[i];
                y -= 0.185f;

                // 外框（越新的记录边框越亮）
                float brightness = 1.0f - i * 0.12f;
                float r = Math.Max(0.32f, 0.50f * brightness);
                float g = Math.Max(0.08f, 0.12f * brightness);
                float b = Math.Max(0.10f, 0.18f * brightness);
                container.Add(new CuiPanel
                {
                    Image = { Color = $"{r:F2} {g:F2} {b:F2} 0.95" },
                    RectTransform = { AnchorMin = $"0.025 {y:F3}", AnchorMax = $"0.975 {y + 0.175f:F3}" }
                }, "DeathListContent", $"DL_Item_{i}");
                container.Add(new CuiPanel
                {
                    Image = { Color = "0.14 0.05 0.08 1" },
                    RectTransform = { AnchorMin = "0.01 0.06", AnchorMax = "0.99 0.94" }
                }, $"DL_Item_{i}", $"DL_Card_{i}");

                // 编号 + 标题
                string rankColor = i == 0 ? "1.00 0.70 0.75 1" : "0.85 0.60 0.68 1";
                string rankTag = i == 0 ? "最近" : $"#{i + 1}";
                container.Add(new CuiLabel
                {
                    Text = { Text = $"💀 [{rankTag}] {d.GetDisplayName()}", FontSize = 12, Align = TextAnchor.MiddleLeft, Color = rankColor },
                    RectTransform = { AnchorMin = "0.03 0.53", AnchorMax = "0.58 0.96" }
                }, $"DL_Card_{i}", $"DL_Title_{i}");

                // 坐标胶囊
                container.Add(new CuiPanel
                {
                    Image = { Color = "0.22 0.08 0.12 1" },
                    RectTransform = { AnchorMin = "0.03 0.08", AnchorMax = "0.44 0.48" }
                }, $"DL_Card_{i}", $"DL_CoordBg_{i}");
                container.Add(new CuiLabel
                {
                    Text = { Text = $"X:{d.X:F0} Y:{d.Y:F0} Z:{d.Z:F0}", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "0.95 0.72 0.74 1" },
                    RectTransform = { AnchorMin = "0.04 0.05", AnchorMax = "0.96 0.95" }
                }, $"DL_CoordBg_{i}", $"DL_Coord_{i}");

                // [传送] 按钮：绿色
                container.Add(new CuiPanel
                {
                    Image = { Color = "0.30 0.65 0.35 1" },
                    RectTransform = { AnchorMin = "0.50 0.13", AnchorMax = "0.71 0.89" }
                }, $"DL_Card_{i}", $"DL_GoBg_{i}");
                container.Add(new CuiButton
                {
                    Button = { Command = $"tp.deathgoto {i}", Color = "0.12 0.32 0.16 1" },
                    RectTransform = { AnchorMin = "0.06 0.15", AnchorMax = "0.94 0.85" },
                    Text = { Text = "🌀 传送", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "0.72 0.95 0.72 1" }
                }, $"DL_GoBg_{i}", $"DL_Go_{i}");

                // [删除] 按钮：红色
                container.Add(new CuiPanel
                {
                    Image = { Color = "0.70 0.25 0.25 1" },
                    RectTransform = { AnchorMin = "0.74 0.13", AnchorMax = "0.97 0.89" }
                }, $"DL_Card_{i}", $"DL_DelBg_{i}");
                container.Add(new CuiButton
                {
                    Button = { Command = $"tp.deathdel {i}", Color = "0.32 0.10 0.10 1" },
                    RectTransform = { AnchorMin = "0.06 0.15", AnchorMax = "0.94 0.85" },
                    Text = { Text = "✕ 删除", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1.00 0.72 0.72 1" }
                }, $"DL_DelBg_{i}", $"DL_Del_{i}");
            }

            // 不足5条的显示提示
            if (showCount < 5)
            {
                float yEmpty = y - 0.14f;
                container.Add(new CuiLabel
                {
                    Text = { Text = $"共 {deaths.Count} 条死亡记录 (最多显示5条)", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "0.50 0.50 0.58 1" },
                    RectTransform = { AnchorMin = $"0.10 {yEmpty:F3}", AnchorMax = $"0.90 {yEmpty + 0.08f:F3}" }
                }, "DeathListContent", "DL_Footer");
            }

            CuiHelper.AddUi(player, container);
        }

        // 从死亡列表传送到指定序号死亡点
        [ConsoleCommand("tp.deathgoto")]
        private void CmdDeathGoto(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            string arg0 = ArgToStr(arg, 0);
            if (arg0 == null) return;
            if (!int.TryParse(arg0, out int index)) return;

            var deaths = GetPlayerDeathLocations(player.userID);
            if (index < 0 || index >= deaths.Count) return;

            var d = deaths[index];
            CuiHelper.DestroyUi(player, "DeathList_Main");
            BeginTeleport(player, d.GetPosition(), $"死亡点 #{index + 1}");
        }

        // 死亡列表"返回"按钮 -> 重新打开主传送菜单
        [ConsoleCommand("tp.deathlist.back")]
        private void CmdDeathListBack(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            CuiHelper.DestroyUi(player, "DeathList_Main");
            ShowTeleportMenu(player);
        }

        [ConsoleCommand("tp.add")]
        private void CmdAdd(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;

            pendingAddPositions[player.userID] = player.transform.position;

            CuiHelper.DestroyUi(player, "TeleportMenu_Main");
            CuiHelper.DestroyUi(player, "AddPointWindow_Main");

            SendMessage(player, "请按回车键打开聊天框");
            SendMessage(player, "输入传送点名称后按回车创建");
            SendMessage(player, "输入 /cancel 取消创建");
        }

        [ConsoleCommand("tp.create")]
        private void CmdCreate(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;

            if (arg.Args == null || arg.Args.Length == 0) return;
            string name = ArgsJoin(arg).Trim();

            CloseUI(player);
            CreateTeleportPoint(player, name);
            ShowTeleportMenu(player);
        }

        [ConsoleCommand("tp.wipe")]
        private void CmdWipe(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;

            var points = GetPlayerPoints(player.userID);
            int count = points.Count;
            if (count == 0)
            {
                SendMessage(player, "你没有传送点");
                return;
            }

            points.Clear();
            SaveData();
            SendMessage(player, $"已清除 {count} 个传送点");
            CloseUI(player);
        }

        // v3.7.2 紧急修复命令：对准一辆载具(摩托车/汽车/马/船等)执行 /tp.fixvehicle
        // 作用：强制把载具所有 seat.occupied=false 并踢下所有伪乘客
        // 解决之前的 ForceRemountPlayer 手动 AssignSeat 导致"载具永远无法驾驶/坐下无反应"问题
        [ChatCommand("fixvehicle")]
        private void ChatCmdFixVehicle(BasePlayer player, string command, string[] args)
        {
            DoFixVehicle(player);
        }
        [ConsoleCommand("tp.fixvehicle")]
        private void CmdFixVehicle(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            DoFixVehicle(player);
        }
        private void DoFixVehicle(BasePlayer player)
        {
            if (player == null) return;

            // 对准准星前方5m内的实体做 Raycast（玩家eyes方向）
            BaseEntity hitVeh = null;
            try
            {
                RaycastHit hit;
                var eyesXform = player.eyes != null ? player.eyes.transform : player.transform;
                var origin = eyesXform.position;
                var dir = eyesXform.forward;
                if (Physics.Raycast(origin, dir, out hit, 5f, -1, QueryTriggerInteraction.Ignore))
                {
                    var go = hit.collider?.gameObject;
                    if (go != null)
                    {
                        hitVeh = go.GetComponentInParent<BaseEntity>();
                    }
                }
            }
            catch { }
            // 兜底：如果Raycast没打中，看玩家身体接触的父/邻近实体
            if (hitVeh == null || !IsVehicleOrMount(hitVeh))
            {
                try
                {
                    // 检查玩家当前mountedEntity（即使没在马上也可能是卡在座位状态）
                    var curMt = GetPlayerMountedEntity(player);
                    if (curMt != null && IsVehicleOrMount(curMt)) hitVeh = curMt;
                }
                catch { }
            }

            if (hitVeh == null || !IsVehicleOrMount(hitVeh))
            {
                SendMessage(player, "❌ 请面朝载具(摩托车/汽车/马/船)按此命令，准星对准车身5米内");
                return;
            }

            int seatsReset = 0;
            int playersEjected = 0;
            string vn = hitVeh.ShortPrefabName ?? hitVeh.GetType().Name;

            // ------- 1) BaseVehicle.mountPoints 扫描：occupied=false -------
            try
            {
                var bv = hitVeh as BaseVehicle;
                if (bv != null)
                {
                    var mountPointsField = bv.GetType().GetField("mountPoints",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (mountPointsField != null)
                    {
                        var mpList = mountPointsField.GetValue(bv) as System.Collections.IEnumerable;
                        if (mpList != null)
                        {
                            foreach (var mpObj in mpList)
                            {
                                if (mpObj == null) continue;
                                try
                                {
                                    // occupied=false
                                    var occProp = mpObj.GetType().GetProperty("occupied",
                                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    if (occProp != null)
                                    {
                                        bool wasOcc = (bool)occProp.GetValue(mpObj, null);
                                        if (wasOcc) seatsReset++;
                                        occProp.SetValue(mpObj, false, null);
                                    }
                                    var occField = mpObj.GetType().GetField("occupied",
                                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    if (occField != null && occField.FieldType == typeof(bool))
                                    {
                                        bool wasOcc = (bool)occField.GetValue(mpObj);
                                        if (wasOcc) seatsReset++;
                                        occField.SetValue(mpObj, false);
                                    }

                                    // seat.mountable 也要确认能交互
                                    var mountableProp = mpObj.GetType().GetProperty("mountable",
                                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    if (mountableProp != null && mountableProp.PropertyType == typeof(bool))
                                        mountableProp.SetValue(mpObj, true, null);

                                    // 如果当前座椅上"绑着"玩家（_player/player/AssignedPlayer字段属性），调用Dismount踢下
                                    string[] seatPlayerNames = { "_player", "player", "AssignedPlayer", "mountedPlayer" };
                                    BasePlayer seatPlayer = null;
                                    foreach (var spn in seatPlayerNames)
                                    {
                                        try
                                        {
                                            var sp = mpObj.GetType().GetProperty(spn,
                                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                            if (sp != null && typeof(BasePlayer).IsAssignableFrom(sp.PropertyType))
                                            { seatPlayer = sp.GetValue(mpObj, null) as BasePlayer; break; }
                                            var sf = mpObj.GetType().GetField(spn,
                                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                            if (sf != null && typeof(BasePlayer).IsAssignableFrom(sf.FieldType))
                                            { seatPlayer = sf.GetValue(mpObj) as BasePlayer; break; }
                                        }
                                        catch { }
                                    }
                                    if (seatPlayer != null && !seatPlayer.IsDestroyed)
                                    {
                                        try { seatPlayer.EnsureDismounted(); } catch { }
                                        try { seatPlayer.SetParent(null, true); } catch { }
                                        playersEjected++;
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { PrintError($"fixvehicle mountPoints重置异常: {ex.Message}"); }

            // ------- 2) Horse：rider/hasRider 属性 -------
            try
            {
                bool modifiedHorse = false;
                string[] riderFlags = { "hasRider", "IsRidden", "isRidden", "mounted" };
                foreach (var rf in riderFlags)
                {
                    try
                    {
                        var hp = hitVeh.GetType().GetProperty(rf,
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (hp != null && hp.PropertyType == typeof(bool))
                        { if ((bool)hp.GetValue(hitVeh, null)) seatsReset++; hp.SetValue(hitVeh, false, null); modifiedHorse = true; }
                        var hf = hitVeh.GetType().GetField(rf,
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (hf != null && hf.FieldType == typeof(bool))
                        { if ((bool)hf.GetValue(hitVeh)) seatsReset++; hf.SetValue(hitVeh, false); modifiedHorse = true; }
                    }
                    catch { }
                }
                string[] riderRefs = { "rider", "Rider", "currentRider", "_rider" };
                foreach (var rr in riderRefs)
                {
                    try
                    {
                        var rp = hitVeh.GetType().GetProperty(rr,
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (rp != null && typeof(BasePlayer).IsAssignableFrom(rp.PropertyType))
                        {
                            var r = rp.GetValue(hitVeh, null) as BasePlayer;
                            if (r != null) { try { r.EnsureDismounted(); } catch { } try { r.SetParent(null, true); } catch { } playersEjected++; rp.SetValue(hitVeh, null, null); }
                        }
                        var rf = hitVeh.GetType().GetField(rr,
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (rf != null && typeof(BasePlayer).IsAssignableFrom(rf.FieldType))
                        {
                            var r = rf.GetValue(hitVeh) as BasePlayer;
                            if (r != null) { try { r.EnsureDismounted(); } catch { } try { r.SetParent(null, true); } catch { } playersEjected++; rf.SetValue(hitVeh, null); }
                        }
                    }
                    catch { }
                }
                if (modifiedHorse) seatsReset++;
            }
            catch (Exception ex) { PrintError($"fixvehicle 马状态重置异常: {ex.Message}"); }

            // ------- 3) 通用：BaseEntity.SetParent(null, true) 所有子节点的player -------
            try
            {
                var children = hitVeh.GetComponentsInChildren<BasePlayer>(true);
                if (children != null)
                {
                    foreach (var cp in children)
                    {
                        if (cp == null || cp.IsDestroyed) continue;
                        try { cp.EnsureDismounted(); } catch { }
                        try { cp.SetParent(null, true); } catch { }
                        playersEjected++;
                    }
                }
            }
            catch { }

            // ------- 4) 通用：再检查 RB / SetSleep / WakeUp 解决"无法启动/轮子动不了" -------
            try
            {
                var rbv = hitVeh.GetComponent<Rigidbody>();
                if (rbv != null)
                {
                    rbv.WakeUp();
                    rbv.velocity = Vector3.zero;
                    rbv.angularVelocity = Vector3.zero;
                    try { rbv.isKinematic = false; } catch { }
                    try { rbv.useGravity = true; } catch { }
                }
            }
            catch { }

            SendMessage(player, $"✅ 载具 [{vn}] 状态已修复：");
            SendMessage(player, $"   🔧 座椅占用状态重置: {seatsReset}");
            SendMessage(player, $"   👤 踢下伪乘客/清理挂载: {playersEjected}");
            SendMessage(player, $"   现在请面朝载具按 E 即可正常驾驶 / 坐下");
            Puts($"[FIX-VEHICLE] {player.displayName} 修复了载具 {vn} seatsReset={seatsReset} ejected={playersEjected}");
        }

        [ConsoleCommand("tp.admin.bring")]
        private void CmdAdminBring(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;

            if (!HasAdminPerm(player))
            {
                arg.ReplyWith("[传送系统] 你没有权限使用此命令");
                return;
            }

            if (arg.Args == null || arg.Args.Length == 0)
            {
                arg.ReplyWith("用法: tp.admin.bring <玩家名>");
                return;
            }

            string targetName = ArgsJoin(arg);
            var target = FindPlayerByName(targetName);
            if (target == null)
            {
                arg.ReplyWith($"[传送系统] 找不到玩家: {targetName}");
                return;
            }

            if (!CompatIsConnected(target))
            {
                arg.ReplyWith($"[传送系统] 玩家 {targetName} 不在线");
                return;
            }

            if (CompatIsDead(target))
            {
                arg.ReplyWith($"[传送系统] 玩家 {targetName} 已死亡");
                return;
            }

            try
            {
                // v3.7.1 载具同步 + 安全传送：管理员bring也处理载具
                BaseEntity mounted = GetPlayerMountedEntity(target);
                bool targetHasMount = mounted != null && IsVehicleOrMount(mounted);
                Quaternion mountRot = Quaternion.identity;
                try { if (targetHasMount) mountRot = mounted.transform.rotation; } catch { }

                ForceDismountPlayer(target);
                target.SetParent(null, true);

                Vector3 safeTargetPos = FindSafePosition(player.transform.position);
                if (IsPositionOverlappingConstruction(safeTargetPos))
                    safeTargetPos = FindNearbySafePosition(player.transform.position, 6f, 1.0f);

                // 传送载具（如果有）
                if (targetHasMount && !CompatIsDestroyed(mounted))
                {
                    try
                    {
                        Vector3 vp = FindSafePositionForVehicle(safeTargetPos, mounted);
                        // 清载具RB速度
                        try
                        {
                            var rbv = mounted.GetComponent<Rigidbody>();
                            if (rbv != null)
                            {
                                rbv.velocity = Vector3.zero;
                                rbv.angularVelocity = Vector3.zero;
                                rbv.WakeUp();
                            }
                        }
                        catch { }
                        var mTel = mounted.GetType().GetMethods(
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            .FirstOrDefault(m => m.Name == "Teleport" && m.GetParameters().Length >= 1
                                && m.GetParameters()[0].ParameterType == typeof(Vector3));
                        if (mTel != null) mTel.Invoke(mounted, new object[] { vp });
                        else mounted.transform.position = vp;
                        try { mounted.transform.rotation = mountRot; } catch { }
                        try
                        {
                            var rb = mounted.GetComponent<Rigidbody>();
                            if (rb != null) { rb.WakeUp(); rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
                        }
                        catch { }
                    }
                    catch (Exception vx) { PrintError($"bring载具传送失败: {vx.Message}"); targetHasMount = false; }
                }

                // v3.7.1 改用 SafeTeleportPlayer (含无敌+清CC+四轮复查)
                SafeTeleportPlayer(target, safeTargetPos, $"管理员拉人至 {player.displayName}");

                if (targetHasMount && !CompatIsDestroyed(mounted))
                {
                    ulong tid = target.userID;
                    BaseEntity mref = mounted;
                    timer.Once(0.65f, () =>
                    {
                        var tp = BasePlayer.FindByID(tid);
                        if (tp != null && !CompatIsDestroyed(mref))
                        {
                            try { ForceRemountPlayer(tp, mref); } catch { }
                        }
                    });
                }

                arg.ReplyWith($"[传送系统] 已将 {target.displayName} 传送到你身边");
                SendMessage(target, $"管理员将你传送到了 {player.displayName} 身边 (安全模式)");
            }
            catch (Exception ex)
            {
                arg.ReplyWith($"[传送系统] 传送失败: {ex.Message}");
            }
        }

        [ConsoleCommand("tp.admin.to")]
        private void CmdAdminTo(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;

            if (!HasAdminPerm(player))
            {
                arg.ReplyWith("[传送系统] 你没有权限使用此命令");
                return;
            }

            if (arg.Args == null || arg.Args.Length == 0)
            {
                arg.ReplyWith("用法: tp.admin.to <玩家名>");
                return;
            }

            string targetName = ArgsJoin(arg);
            var target = FindPlayerByName(targetName);
            if (target == null)
            {
                arg.ReplyWith($"[传送系统] 找不到玩家: {targetName}");
                return;
            }

            if (!CompatIsConnected(target))
            {
                arg.ReplyWith($"[传送系统] 玩家 {targetName} 不在线");
                return;
            }

            if (CompatIsDead(player))
            {
                arg.ReplyWith("[传送系统] 你已死亡，无法传送");
                return;
            }

            Vector3 targetSafePos = FindSafePosition(target.transform.position);
            if (IsPositionOverlappingConstruction(targetSafePos))
                targetSafePos = FindNearbySafePosition(target.transform.position, 6f, 1.0f);
            BeginTeleport(player, targetSafePos, $"管理员传送至 {target.displayName}");
        }

        [ConsoleCommand("tp.reload")]
        private void CmdReload(ConsoleSystem.Arg arg)
        {
            LoadConfigData();
            LoadData();
            arg.ReplyWith("[传送系统] 配置和数据已重载");
        }

        [ConsoleCommand("tp.stuck")]
        private void CmdConsoleStuck(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
            {
                arg.ReplyWith("用法: tp.stuck（游戏内使用）或 tp.stuck <玩家名>（管理员）");
                return;
            }
            TryUnstuck(player);
        }

        [ConsoleCommand("tp.stuck.admin")]
        private void CmdAdminStuck(ConsoleSystem.Arg arg)
        {
            if (!HasAdminPerm(arg.Player()))
            {
                arg.ReplyWith("[传送系统] 你没有权限使用此命令");
                return;
            }
            if (arg.Args == null || arg.Args.Length == 0)
            {
                arg.ReplyWith("用法: tp.stuck.admin <玩家名>");
                return;
            }
            string targetName = ArgsJoin(arg);
            var target = FindPlayerByName(targetName);
            if (target == null)
            {
                arg.ReplyWith($"[传送系统] 找不到玩家: {targetName}");
                return;
            }
            if (!CompatIsConnected(target))
            {
                arg.ReplyWith($"[传送系统] 玩家 {targetName} 不在线");
                return;
            }
            // 管理员强制脱困，跳过冷却
            _stuckCooldown.Remove(target.userID);
            TryUnstuck(target);
            arg.ReplyWith($"[传送系统] 已为 {target.displayName} 执行脱困");
        }

        [ConsoleCommand("tp.listall")]
        private void CmdListAll(ConsoleSystem.Arg arg)
        {
            if (!HasAdminPerm(arg.Player()))
            {
                arg.ReplyWith("[传送系统] 你没有权限使用此命令");
                return;
            }

            var players = BasePlayer.activePlayerList;
            if (players == null || players.Count == 0)
            {
                arg.ReplyWith("[传送系统] 当前没有在线玩家");
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[传送系统] 在线玩家传送点列表:");
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p != null && CompatIsConnected(p))
                {
                    int pointCount = GetPlayerPoints(p.userID).Count;
                    int deathCount = GetPlayerDeathLocations(p.userID).Count;
                    sb.AppendLine($"  {p.displayName}: {pointCount} 传送点, {deathCount} 死亡地点");
                }
            }
            arg.ReplyWith(sb.ToString());
        }

        #endregion

        #region UI 辅助

        private void CloseUI(BasePlayer player)
        {
            if (player == null) return;
            CuiHelper.DestroyUi(player, "TeleportMenu_Main");
        }

        #endregion

        #region 帮助和列表

        private void ShowHelp(BasePlayer player)
        {
            if (player == null) return;

            SendMessage(player, "========== 传送系统帮助 ==========");
            SendMessage(player, "/tp - 打开传送菜单");
            SendMessage(player, "/tp help - 显示此帮助");
            SendMessage(player, "/tp list - 列出传送点");
            SendMessage(player, "/tpname <名称> - 创建传送点");
            SendMessage(player, "/tp go <名称> - 传送到指定点");
            SendMessage(player, "/tpd <名称> - 删除传送点");
            SendMessage(player, "/tp back - 返回最近死亡点");
            SendMessage(player, "/tp death - 列出死亡地点");
            SendMessage(player, "/stuck - 卡墙/卡地基时自动脱困 (冷却30秒)");
            SendMessage(player, "/help5 - 查看传送系统命令");
            SendMessage(player, "========================================");
        }

        [ChatCommand("help5")]
        private void CmdHelp5(BasePlayer player, string command, string[] args)
        {
            ShowHelp(player);
        }

        private void ListTeleportPoints(BasePlayer player)
        {
            if (player == null) return;
            var points = GetPlayerPoints(player.userID);

            if (points.Count == 0)
            {
                SendMessage(player, "你还没有传送点");
                SendMessage(player, "使用 /tpname <名称> 创建");
                return;
            }

            SendMessage(player, $"传送点 ({points.Count}/{config.Settings.MaxTeleportPoints}):");
            foreach (var point in points)
            {
                string coordStr = config.Settings.ShowCoordinates ? $" (X:{point.X:F0} Y:{point.Y:F0} Z:{point.Z:F0})" : "";
                SendMessage(player, $"  • {point.Name}{coordStr}");
            }
        }

        private void ListDeathLocations(BasePlayer player)
        {
            if (player == null) return;
            var deaths = GetPlayerDeathLocations(player.userID);

            if (deaths.Count == 0)
            {
                SendMessage(player, "暂无死亡地点记录");
                return;
            }

            SendMessage(player, $"死亡地点记录 ({deaths.Count}):");
            for (int i = 0; i < deaths.Count; i++)
            {
                var death = deaths[i];
                string coordStr = config.Settings.ShowCoordinates ? $" [X:{death.X:F0} Z:{death.Z:F0}]" : "";
                SendMessage(player, $"  {i + 1}. {death.GetDisplayName()}{coordStr}");
            }
            SendMessage(player, "传送回最近死亡点: /tp back");
        }

        #endregion

        #region 传送点管理

        private void CreateTeleportPoint(BasePlayer player, string name)
        {
            CreateTeleportPoint(player, name, player.transform.position);
        }

        private void CreateTeleportPoint(BasePlayer player, string name, Vector3 position)
        {
            if (player == null || string.IsNullOrEmpty(name)) return;

            if (name.Length > 20)
            {
                SendMessage(player, "传送点名称不能超过20个字符！");
                return;
            }

            var points = GetPlayerPoints(player.userID);

            if (points.Count >= config.Settings.MaxTeleportPoints)
            {
                SendMessage(player, $"已达到传送点上限 ({config.Settings.MaxTeleportPoints})！");
                return;
            }

            if (points.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                SendMessage(player, $"传送点 \"{name}\" 已存在！");
                return;
            }

            points.Add(new TeleportPoint(name, position));
            SaveData();

            SendMessage(player, $"已创建传送点: {name}");
            if (config.Settings.ShowCoordinates)
            {
                SendMessage(player, $"位置: X={position.x:F0} Y={position.y:F0} Z={position.z:F0}");
            }
        }

        private void DeleteTeleportPoint(BasePlayer player, string name)
        {
            if (player == null || string.IsNullOrEmpty(name)) return;

            var points = GetPlayerPoints(player.userID);
            var point = points.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (point == null)
            {
                SendMessage(player, $"找不到传送点: {name}");
                return;
            }

            points.Remove(point);
            SaveData();
            SendMessage(player, $"已删除传送点: {point.Name}");
        }

        private void TeleportToPointByName(BasePlayer player, string name)
        {
            if (player == null || string.IsNullOrEmpty(name)) return;

            var points = GetPlayerPoints(player.userID);
            var point = points.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (point == null)
            {
                SendMessage(player, $"找不到传送点: {name}");
                return;
            }

            BeginTeleport(player, point.GetPosition(), point.Name);
        }

        private void TeleportToLastDeath(BasePlayer player)
        {
            if (player == null) return;

            var deaths = GetPlayerDeathLocations(player.userID);
            if (deaths.Count == 0)
            {
                SendMessage(player, "没有死亡地点记录");
                return;
            }

            BeginTeleport(player, deaths[0].GetPosition(), deaths[0].GetDisplayName());
        }

        #endregion

        #region 载具同步传送 (v3.7.0 新增)

        // 通过反射获取玩家当前骑乘的载具实体（兼容 BaseVehicle / Horse / 其他可骑乘实体）
        private BaseEntity GetPlayerMountedEntity(BasePlayer player)
        {
            if (player == null) return null;
            try
            {
                // 方式1: BasePlayer.mountedObject (字段)
                var fi = typeof(BasePlayer).GetField("mountedObject",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (fi != null)
                {
                    var val = fi.GetValue(player) as BaseEntity;
                    if (val != null && !CompatIsDestroyed(val)) return val;
                }

                // 方式2: BasePlayer.currentMountedVehicle / MountedVehicle (属性)
                string[] propNames = { "currentMountedVehicle", "MountedVehicle", "MountedEntity", "mountedEntity" };
                foreach (var pn in propNames)
                {
                    var pi = typeof(BasePlayer).GetProperty(pn,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (pi != null && typeof(BaseEntity).IsAssignableFrom(pi.PropertyType))
                    {
                        var val = pi.GetValue(player, null) as BaseEntity;
                        if (val != null && !CompatIsDestroyed(val)) return val;
                    }
                }

                // 方式3: GetMounted() / GetMountedEntity() / GetVehicle() 方法
                string[] methodNames = { "GetMounted", "GetMountedEntity", "get_mounted", "GetVehicle", "GetCurrentVehicle" };
                foreach (var mn in methodNames)
                {
                    var mi = typeof(BasePlayer).GetMethod(mn,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, Type.EmptyTypes, null);
                    if (mi != null && typeof(BaseEntity).IsAssignableFrom(mi.ReturnType))
                    {
                        var val = mi.Invoke(player, null) as BaseEntity;
                        if (val != null && !CompatIsDestroyed(val)) return val;
                    }
                }

                // 方式4: IsMounted 为 true 时，从 parent hierarchy / GetParentEntity 反推载具
                bool isMounted = false;
                var isMountedProp = typeof(BasePlayer).GetProperty("IsMounted",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (isMountedProp != null) { try { isMounted = (bool)isMountedProp.GetValue(player, null); } catch { } }
                if (!isMounted)
                {
                    var isMountedField = typeof(BasePlayer).GetField("IsMounted",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (isMountedField != null) { try { isMounted = (bool)isMountedField.GetValue(player); } catch { } }
                }
                // 另外看 currentSeat / seat 字段/属性是否存在 → 座椅有值说明一定在载具上
                if (!isMounted)
                {
                    string[] seatNames = { "currentSeat", "CurrentSeat", "seat", "MountSeat" };
                    foreach (var sn in seatNames)
                    {
                        try
                        {
                            var piSeat = typeof(BasePlayer).GetProperty(sn,
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (piSeat != null && piSeat.GetValue(player, null) != null) { isMounted = true; break; }
                            var fiSeat = typeof(BasePlayer).GetField(sn,
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (fiSeat != null && fiSeat.GetValue(player) != null) { isMounted = true; break; }
                        }
                        catch { }
                    }
                }

                if (isMounted)
                {
                    // A) GetParentEntity 方法
                    try
                    {
                        var gpe = typeof(BaseEntity).GetMethods(
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            .FirstOrDefault(m => m.Name == "GetParentEntity" && m.GetParameters().Length == 0);
                        if (gpe != null && typeof(BaseEntity).IsAssignableFrom(gpe.ReturnType))
                        {
                            var cur = player as BaseEntity;
                            int guard = 0;
                            while (cur != null && guard < 10)
                            {
                                guard++;
                                var parent = gpe.Invoke(cur, null) as BaseEntity;
                                if (parent == null || CompatIsDestroyed(parent)) break;
                                if (IsVehicleOrMount(parent)) return parent;
                                cur = parent;
                            }
                        }
                    }
                    catch { }

                    // B) 扫描 transform.parent 层级（最暴力，兼容所有情况）
                    var p = player.transform;
                    int guard2 = 0;
                    BaseEntity lastVehicle = null;
                    while (p != null && guard2 < 15)
                    {
                        guard2++;
                        var be = p.GetComponent<BaseEntity>();
                        if (be != null && be != player && !CompatIsDestroyed(be))
                        {
                            if (IsVehicleOrMount(be)) return be;
                            // 只要是实体就先记下来，如果上层没找到再看这个
                            if (lastVehicle == null) lastVehicle = be;
                        }
                        p = p.parent;
                    }
                    if (lastVehicle != null) return lastVehicle;
                }

                // 方式5 (v3.7.1): 就算 IsMounted=false, 也向上扫一级 parent (防止状态未同步的情况)
                {
                    try
                    {
                        var gp = typeof(BaseEntity).GetMethods(
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            .FirstOrDefault(m => m.Name == "GetParentEntity" && m.GetParameters().Length == 0);
                        if (gp != null)
                        {
                            var pr = gp.Invoke(player, null) as BaseEntity;
                            if (pr != null && !CompatIsDestroyed(pr) && IsVehicleOrMount(pr)) return pr;
                        }
                    }
                    catch { }
                    if (player.transform.parent != null)
                    {
                        var be = player.transform.parent.GetComponent<BaseEntity>();
                        if (be != null && !CompatIsDestroyed(be) && IsVehicleOrMount(be)) return be;
                    }
                }
            }
            catch { }
            return null;
        }

        // 判断实体是否是"载具"类型（需要一起传送的坐骑/交通工具）
        private bool IsVehicleOrMount(BaseEntity entity)
        {
            if (entity == null) return false;
            string typeName = entity.GetType().Name;
            string shortName = (entity.ShortPrefabName ?? "").ToLower();

            // BaseVehicle 及其子类
            if (entity is BaseVehicle) return true;
            if (typeName.Contains("Vehicle") || typeName.Contains("Horse") || typeName.Contains("Ridable"))
                return true;
            if (typeName == "ModularCar" || typeName == "RHIB" || typeName == "Rowboat" ||
                typeName == "CargoShip" || typeName == "Chinook" || typeName == "AttackHelicopter" ||
                typeName == "PatrolHelicopter" || typeName == "MiniCopter" || typeName == "ScrapTransportHelicopter" ||
                typeName == "HotAirBalloon" || typeName == "Kayak" || typeName == "Submarine" ||
                typeName == "Snowmobile" || typeName == "Tugboat" || typeName == "Barge" ||
                typeName == "Motorbike" || typeName == "Bicycle" || typeName == "Sled")
                return true;

            // 马 (Horse / RidableHorse)
            if (shortName.Contains("horse") || shortName.Contains("ridable")) return true;
            // 各类载具短名
            if (shortName.Contains("motorbike") || shortName.Contains("bike") || shortName.Contains("bicycle") ||
                shortName.Contains("car") || shortName.Contains("modular") || shortName.Contains("truck") ||
                shortName.Contains("boat") || shortName.Contains("rhib") || shortName.Contains("rowboat") ||
                shortName.Contains("kayak") || shortName.Contains("submarine") || shortName.Contains("tugboat") ||
                shortName.Contains("barge") || shortName.Contains("heli") || shortName.Contains("copter") ||
                shortName.Contains("balloon") || shortName.Contains("snowmobile") || shortName.Contains("sled") ||
                shortName.Contains("scraptransport") || shortName.Contains("minicopter"))
                return true;

            return false;
        }

        // 让玩家下马（v3.7.2 仅保留官方安全API，不碰任何 SetMounted 等可能改坏 seat.occupied 的内部方法）
        private void ForceDismountPlayer(BasePlayer player)
        {
            if (player == null) return;
            try { player.EnsureDismounted(); } catch { }
            try { player.SetParent(null, true); } catch { }
        }

        // 让玩家重新骑乘载具（v3.7.2：只尝试官方公开 Mount API，失败直接放弃，绝不手动 AssignSeat/SetParent
        // 避免改乱 seat.occupied 状态导致载具永久无法驾驶）
        private void ForceRemountPlayer(BasePlayer player, BaseEntity vehicle)
        {
            if (player == null || vehicle == null || CompatIsDestroyed(vehicle)) return;

            try
            {
                // 仅尝试官方 Mount 方法（按可能性排序），成功了就 return，失败不报错
                string[] vehicleMountMethods = {
                    "MountPlayer", "Mount", "DoMount", "AddMountedPlayer",
                    "TryMount", "SetRider", "MountRider", "SitPlayer", "EnterVehicle"
                };
                foreach (var mm in vehicleMountMethods)
                {
                    // (BasePlayer) 签名
                    var mi = vehicle.GetType().GetMethod(mm,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, new Type[] { typeof(BasePlayer) }, null);
                    if (mi != null)
                    {
                        try { mi.Invoke(vehicle, new object[] { player }); return; } catch { }
                    }
                    // (BasePlayer, bool) 签名
                    mi = vehicle.GetType().GetMethod(mm,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, new Type[] { typeof(BasePlayer), typeof(bool) }, null);
                    if (mi != null)
                    {
                        try { mi.Invoke(vehicle, new object[] { player, true }); return; } catch { }
                    }
                }
                // 马的 TryMount 也带 BasePlayer 参数，上面已经覆盖了

                // v3.7.2 关键：所有公开方法都失败 → 直接放弃自动上马
                // 绝不再手动 AssignSeat / mountPoints.Assign / SetParent，这些会永久把 seat.occupied 锁死
            }
            catch { }
        }

        // 为载具寻找附近的安全落地位置（比玩家稍大的范围）
        private Vector3 FindSafePositionForVehicle(Vector3 targetPos, BaseEntity vehicle)
        {
            // 先用玩家的安全位置算法
            Vector3 safe = FindSafePosition(targetPos);
            // v3.7.1 修复：载具偏移从0.8m增加到2.5m，避免玩家与载具重叠产生挤压伤害
            return safe + new Vector3(2.5f, 0f, 1.8f);
        }

        #endregion

        #region 传送安全辅助 (v3.7.1 新增：解决建筑附近传送重伤/死亡)

        // 保存/恢复 CharacterController 状态 + 清零速度
        private class TeleportPhysicsState
        {
            public bool CcEnabled;
            public CharacterController CC;
            public Rigidbody RB;
        }

        private TeleportPhysicsState PrepPlayerPhysicsForTeleport(BasePlayer player)
        {
            var state = new TeleportPhysicsState();
            if (player == null) return state;
            try
            {
                state.CC = player.GetComponent<CharacterController>();
                if (state.CC != null)
                {
                    state.CcEnabled = state.CC.enabled;
                    state.CC.enabled = false;
                }
            }
            catch { }
            try
            {
                state.RB = player.GetComponent<Rigidbody>();
                if (state.RB != null)
                {
                    state.RB.velocity = Vector3.zero;
                    state.RB.angularVelocity = Vector3.zero;
                    // v3.7.2 去掉 RB.Sleep()：部分版本Sleep会导致玩家-载具绑定状态异常
                    // 传送后再在 Restore 中再清一次速度即可
                }
            }
            catch { }
            // 清除玩家 estimatedSpeed 同步字段（避免延迟传送检查误判）
            try
            {
                var fi = typeof(BasePlayer).GetField("estimatedSpeed",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (fi != null) fi.SetValue(player, 0f);
            }
            catch { }
            // 调用客户端位置同步重置（有的版本叫 ResetPosition / ResetNetworkPosition）
            try
            {
                var mi = typeof(BasePlayer).GetMethod("ResetNetworkPosition",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                if (mi != null) mi.Invoke(player, null);
            }
            catch { }
            return state;
        }

        private void RestorePlayerPhysicsAfterTeleport(BasePlayer player, TeleportPhysicsState state)
        {
            if (player == null || state == null) return;
            try
            {
                if (state.RB != null)
                {
                    state.RB.velocity = Vector3.zero;
                    state.RB.angularVelocity = Vector3.zero;
                }
            }
            catch { }
            try
            {
                if (state.CC != null)
                {
                    state.CC.enabled = state.CcEnabled;
                }
            }
            catch { }
        }

        // 记录开启无敌的玩家集合，传送完成后自动关闭（且OnEntityTakeDamage里会拦截这部分玩家的所有伤害）
        private readonly HashSet<ulong> _tpInvinciblePlayers = new HashSet<ulong>();

        // v3.7.2：只改 BasePlayer.invincible 单一字段/属性 → 绝不碰 godmode（GodMode会禁用载具驾驶）
        // 伤害拦截兜底交给 OnEntityTakeDamage Hook，100%防止挤压/载具碰撞导致传送即死
        private void SetPlayerInvincibleSafe(BasePlayer player, bool invincible, float seconds = -1f)
        {
            if (player == null) return;
            ulong pid = player.userID;
            try
            {
                // 只尝试一种方式：invincible 属性 → 再 invincible 字段 → 找不到就放弃
                bool applied = false;
                try
                {
                    var pi = typeof(BasePlayer).GetProperty("invincible",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (pi != null && pi.PropertyType == typeof(bool))
                    {
                        pi.SetValue(player, invincible, null);
                        applied = true;
                    }
                }
                catch { }
                if (!applied)
                {
                    try
                    {
                        var fi = typeof(BasePlayer).GetField("invincible",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (fi != null && fi.FieldType == typeof(bool)) fi.SetValue(player, invincible);
                    }
                    catch { }
                }

                // 开启无敌时立即治疗20HP，抵消可能已经发生的前几帧挤压伤害
                if (invincible)
                {
                    try
                    {
                        var hpProp = typeof(BaseCombatEntity).GetProperty("health",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (hpProp != null)
                        {
                            float curHp = (float)hpProp.GetValue(player, null);
                            if (curHp > 0f && curHp < player.MaxHealth())
                            {
                                var healMi = typeof(BasePlayer).GetMethod("Heal",
                                    new Type[] { typeof(float) });
                                if (healMi != null) healMi.Invoke(player, new object[] { 25f });
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            // 登记/取消登记（OnEntityTakeDamage Hook会根据这个集合拦截伤害）
            if (invincible)
            {
                lock (_tpInvinciblePlayers) _tpInvinciblePlayers.Add(pid);
                if (seconds > 0f)
                {
                    timer.Once(seconds, () =>
                    {
                        lock (_tpInvinciblePlayers) _tpInvinciblePlayers.Remove(pid);
                        var p = BasePlayer.FindByID(pid);
                        if (p != null) SetPlayerInvincibleSafe(p, false);
                    });
                }
            }
            else
            {
                lock (_tpInvinciblePlayers) _tpInvinciblePlayers.Remove(pid);
            }
        }

        // v3.7.2 紧急防护：传送无敌期内，拦截所有对玩家的伤害（100%避免传送即死）
        // 必须优先于其他插件执行，所以取 void 返回值 + CancelHookDamage
        private object OnEntityTakeDamage(BasePlayer victim, HitInfo info)
        {
            if (victim == null || info == null) return null;
            try
            {
                ulong vid;
                try { vid = victim.userID; } catch { return null; }
                bool inInvincible;
                lock (_tpInvinciblePlayers) inInvincible = _tpInvinciblePlayers.Contains(vid);
                if (inInvincible)
                {
                    // 登记过的传送无敌期玩家 → 彻底清空伤害
                    return CancelHookDamage(info);
                }
            }
            catch { }
            return null;
        }

        // 反射清空 HitInfo 所有伤害/状态位，确保传送无敌期内100%免伤
        private object CancelHookDamage(HitInfo info)
        {
            if (info == null) return null;
            try
            {
                // 1) damageAmount 字段/属性清零
                var fi = info.GetType().GetField("damageAmount",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (fi != null && fi.FieldType == typeof(float)) fi.SetValue(info, 0f);
                var pi = info.GetType().GetProperty("damageAmount",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pi != null && pi.PropertyType == typeof(float)) pi.SetValue(info, 0f, null);

                // 2) damage 字段/属性清零
                var fi2 = info.GetType().GetField("damage",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (fi2 != null && fi2.FieldType == typeof(float)) fi2.SetValue(info, 0f);
                var pi2 = info.GetType().GetProperty("damage",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pi2 != null && pi2.PropertyType == typeof(float)) pi2.SetValue(info, 0f, null);

                // 3) 状态位：DoNotGather=true, CanGather=false, IgnoreProtection=true
                string[] boolFlags = { "DoNotGather", "IgnoreProtection" };
                foreach (var bf in boolFlags)
                {
                    try
                    {
                        var f = info.GetType().GetField(bf,
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (f != null && f.FieldType == typeof(bool)) f.SetValue(info, true);
                        var p = info.GetType().GetProperty(bf,
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (p != null && p.PropertyType == typeof(bool)) p.SetValue(info, true, null);
                    }
                    catch { }
                }
                try
                {
                    var cn = info.GetType().GetField("CanGather",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (cn != null && cn.FieldType == typeof(bool)) cn.SetValue(info, false);
                    var cp = info.GetType().GetProperty("CanGather",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (cp != null && cp.PropertyType == typeof(bool)) cp.SetValue(info, false, null);
                }
                catch { }

                // 4) Carbon: CancelProcessing=true
                try
                {
                    var cp = info.GetType().GetProperty("CancelProcessing",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (cp != null) cp.SetValue(info, true, null);
                    var cf = info.GetType().GetField("CancelProcessing",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (cf != null) cf.SetValue(info, true);
                }
                catch { }
            }
            catch { }
            return null;
        }

        // 强推位置：如果检测到卡进建筑，先抬升再螺旋向外搜索，直到完全安全
        private Vector3 ForceFindSafeEscape(Vector3 pos, string debugTag)
        {
            // 1) 快速抬升尝试
            for (float lift = 1f; lift <= 8f; lift += 0.5f)
            {
                Vector3 lifted = pos + Vector3.up * lift;
                if (!IsPositionOverlappingConstruction(lifted) && IsPositionSafeForPlayer(lifted))
                {
                    return lifted;
                }
            }
            // 2) 螺旋向外+抬升（最可靠）
            for (float radius = 1.5f; radius <= 12f; radius += 1.5f)
            {
                Vector3 ring = FindNearbySafePosition(pos, radius, radius * 0.25f);
                if (!IsPositionOverlappingConstruction(ring) && IsPositionSafeForPlayer(ring))
                {
                    return ring;
                }
                for (float lift = 0.5f; lift <= 6f; lift += 0.5f)
                {
                    Vector3 lifted = ring + Vector3.up * lift;
                    if (!IsPositionOverlappingConstruction(lifted) && IsPositionSafeForPlayer(lifted))
                    {
                        return lifted;
                    }
                }
            }
            // 3) 终极兜底
            return pos + Vector3.up * 10f;
        }

        // 四轮复查 + 必要时继续推
        private void SchedulePositionRevalidations(BasePlayer player, Vector3 intendedPos)
        {
            if (player == null) return;
            ulong pid = player.userID;

            // 四轮时间点：0.1s / 0.3s / 0.6s / 1.0s
            // 每轮检查：是否与建筑重叠 → 重叠就推并继续下一轮
            float[] timePoints = { 0.1f, 0.3f, 0.6f, 1.0f };
            for (int i = 0; i < timePoints.Length; i++)
            {
                int round = i + 1;
                float tp = timePoints[i];
                timer.Once(tp, () =>
                {
                    try
                    {
                        var p = BasePlayer.FindByID(pid);
                        if (p == null || !CompatIsConnected(p)) return;

                        Vector3 cp = p.transform.position;
                        bool stuck = IsPositionOverlappingConstruction(cp) || !IsPositionSafeForPlayer(cp);
                        if (!stuck) return; // 安全就跳过

                        // 不安全 → 推到安全位置
                        Vector3 safe = ForceFindSafeEscape(cp, $"R{round}");
                        PrintError($"[TP-REVALIDATE R{round}] 检测到挤压风险 {cp:F1}->{safe:F1}，立即推离");
                        var state = PrepPlayerPhysicsForTeleport(p);
                        p.Teleport(safe);
                        RestorePlayerPhysicsAfterTeleport(p, state);
                        SendMessage(p, $"已修正位置(第{round}次复查)，防止挤压伤害");
                    }
                    catch (Exception e)
                    {
                        PrintError($"[TP-REVALIDATE R{round}] 异常: {e.Message}");
                    }
                });
            }
        }

        // ===== 地形加载检测：远距离传送时目标地形可能还没加载完 =====
        // 检测方法：用 TerrainMeta.HeightMap.GetHeight + 物理射线检测 是否有地面碰撞体
        private bool IsTerrainLoadedAt(Vector3 pos)
        {
            try
            {
                // 方法1：TerrainMeta.HeightMap 返回合理高度（不是0或NaN）
                var terrain = TerrainMeta.HeightMap;
                if (terrain != null)
                {
                    float h = terrain.GetHeight(pos);
                    if (!float.IsNaN(h) && !float.IsInfinity(h) && h > -500f && h < 5000f)
                    {
                        // 高度合理，但还要验证物理碰撞体存在
                        if (Physics.Raycast(pos + Vector3.up * 100f, Vector3.down, out var hit, 200f, AllGroundMask))
                        {
                            return true; // 地形+碰撞体都OK
                        }
                        // Terrain高度OK但没碰撞体——可能刚加载，再试一次
                        if (Physics.Raycast(pos + Vector3.up * 50f, Vector3.down, out hit, 60f, AllGroundMask))
                            return true;
                    }
                }
                // 方法2：直接从上方射线检测物理碰撞体（覆盖建筑/地形/资源）
                if (Physics.Raycast(pos + Vector3.up * 200f, Vector3.down, out var hit2, 400f, AllGroundMask))
                    return true;
            }
            catch { }
            return false;
        }

        // 等待地形加载：轮询最多 waitSeconds 秒，每0.3秒检查一次
        private void WaitForTerrainAndDoTeleport(BasePlayer player, Vector3 targetPos, string targetName, float waitSeconds = 5f)
        {
            if (player == null || !CompatIsConnected(player)) return;

            SendMessage(player, "⏳ 正在等待目标区域地形加载...");
            Puts($"[TP-TERRAIN] {player.displayName} 等待地形加载 pos={targetPos}");

            float checkInterval = 0.3f;
            int maxChecks = Mathf.CeilToInt(waitSeconds / checkInterval);
            int checks = 0;

            var waitTimer = timer.Repeat(checkInterval, 0, () =>
            {
                checks++;
                if (player == null || !CompatIsConnected(player))
                {
                    CancelTerrainWait(player);
                    return;
                }
                if (CompatIsDead(player))
                {
                    CancelTerrainWait(player);
                    SendMessage(player, "传送已取消（你已死亡）");
                    return;
                }

                bool terrainReady = IsTerrainLoadedAt(targetPos);

                if (terrainReady || checks >= maxChecks)
                {
                    CancelTerrainWait(player);

                    if (terrainReady)
                    {
                        SendMessage(player, $"✅ 地形已加载（等待{checks * checkInterval:F1}s），正在传送...");
                        Puts($"[TP-TERRAIN] {player.displayName} 地形加载完成({checks * checkInterval:F1}s) 传送执行");
                    }
                    else
                    {
                        SendMessage(player, "⚠️ 地形加载超时，尝试强制传送（可能有风险）");
                        Puts($"[TP-TERRAIN] {player.displayName} 地形加载超时({waitSeconds}s) 强制传送");
                    }

                    if (player != null && CompatIsConnected(player) && !CompatIsDead(player))
                    {
                        DoTeleport(player, targetPos, targetName);
                    }
                }
            });

            _terrainWaitTimers[player.userID] = waitTimer;
        }

        private Dictionary<ulong, Timer> _terrainWaitTimers = new Dictionary<ulong, Timer>();

        private void CancelTerrainWait(BasePlayer player)
        {
            if (player == null) return;
            try
            {
                if (_terrainWaitTimers.TryGetValue(player.userID, out var t))
                {
                    t?.Destroy();
                    _terrainWaitTimers.Remove(player.userID);
                }
            }
            catch { }
        }

        // 统一的"安全传送玩家"入口：禁用CC → 清速度 → 无敌2.5s+伤害Hook拦截 → Teleport → 恢复CC → 四轮复查
        private void SafeTeleportPlayer(BasePlayer player, Vector3 safePos, string targetName)
        {
            if (player == null || !CompatIsConnected(player)) return;

            try
            {
                // ① 物理准备（禁用CC + 清零速度）
                var phys = PrepPlayerPhysicsForTeleport(player);

                // ② 开启 2.5s 无敌期（1.5s不够，载具+建筑双重挤压可能持续>2s）
                // 防护双层：invincible 属性/字段 + OnEntityTakeDamage Hook 直接清空所有伤害
                SetPlayerInvincibleSafe(player, true, 2.5f);

                // ③ 最终位置安全保障（如果目标本身就不安全，重推一次）
                if (IsPositionOverlappingConstruction(safePos) || !IsPositionSafeForPlayer(safePos))
                {
                    safePos = ForceFindSafeEscape(safePos, "PRE-TP");
                }

                // ④ 执行传送
                player.Teleport(safePos);

                // ⑤ 恢复物理
                RestorePlayerPhysicsAfterTeleport(player, phys);

                // ⑥ 四轮位置复查 + 异常修正
                SchedulePositionRevalidations(player, safePos);

                SendMessage(player, $"已传送到: {targetName}  (安全模式已激活)");
            }
            catch (Exception ex)
            {
                PrintError($"SafeTeleport 失败: {ex.Message}");
                SendMessage(player, "传送失败，请重试！");
            }
        }

        #endregion

        #region 传送执行

        private void BeginTeleport(BasePlayer player, Vector3 targetPos, string targetName)
        {
            if (player == null) return;

            if (CompatIsDead(player))
            {
                SendMessage(player, "死亡状态下无法传送！");
                return;
            }

            if (IsTeleporting(player))
            {
                SendMessage(player, "传送正在进行中，请稍候...");
                return;
            }

            if (config.Settings.RequireStandingStill && GetEstimatedSpeed(player) > config.Settings.StandingStillThreshold)
            {
                SendMessage(player, "传送需要保持静止状态！");
                return;
            }

            Vector3 safePos = FindSafePosition(targetPos);

            // ===== 地形加载检查：远距离(>200m)传送时，目标地形可能还没加载 =====
            float dist = Vector3.Distance(player.transform.position, targetPos);
            if (dist > 200f && !IsTerrainLoadedAt(safePos))
            {
                SendMessage(player, $"📡 检测到远距离传送({dist:F0}m)，目标地形加载中...");
                WaitForTerrainAndDoTeleport(player, safePos, targetName, 5f);
                return;
            }
            // 如果地形已加载但距离远，给一个短暂延迟让客户端预加载
            if (dist > 500f)
            {
                SendMessage(player, $"📡 远距离传送({dist:F0}m)，预加载中...");
                var preloadTimer = timer.Once(0.5f, () =>
                {
                    if (player == null || !CompatIsConnected(player) || CompatIsDead(player)) return;
                    DoTeleport(player, safePos, targetName);
                });
                _terrainWaitTimers[player.userID] = preloadTimer;
                return;
            }

            if (config.Settings.TeleportDelay <= 0)
            {
                DoTeleport(player, safePos, targetName);
                return;
            }

            SendMessage(player, $"将在 {config.Settings.TeleportDelay} 秒后传送到: {targetName}");
            SendMessage(player, "请保持静止，移动会取消传送");

            float checkInterval = 0.5f;
            int totalChecks = Mathf.CeilToInt(config.Settings.TeleportDelay / checkInterval);
            int checksDone = 0;

            var checkTimer = timer.Repeat(checkInterval, 0, () =>
            {
                if (player == null || !CompatIsConnected(player))
                {
                    CancelTeleport(player);
                    return;
                }
                if (CompatIsDead(player))
                {
                    CancelTeleport(player);
                    SendMessage(player, "传送已取消（你已死亡）");
                    return;
                }
                if (config.Settings.RequireStandingStill && GetEstimatedSpeed(player) > config.Settings.StandingStillThreshold)
                {
                    CancelTeleport(player);
                    SendMessage(player, "传送已取消（你移动了）");
                    return;
                }

                checksDone++;
                if (checksDone >= totalChecks)
                {
                    if (player != null && CompatIsConnected(player) && !CompatIsDead(player))
                    {
                        DoTeleport(player, safePos, targetName);
                    }
                    CancelTeleport(player);
                }
            });

            activeTeleports[player.userID] = checkTimer;
        }

        private void DoTeleport(BasePlayer player, Vector3 position, string targetName)
        {
            if (player == null || !CompatIsConnected(player)) return;

            try
            {
                // ===== v3.7.1 载具同步：步骤1 检测并保存载具引用（强化后的检测） =====
                BaseEntity mountedEntity = GetPlayerMountedEntity(player);
                bool hasMounted = mountedEntity != null && IsVehicleOrMount(mountedEntity);
                Quaternion mountedRot = Quaternion.identity;
                if (hasMounted)
                {
                    try { mountedRot = mountedEntity.transform.rotation; } catch { }
                    string vn = mountedEntity.ShortPrefabName ?? mountedEntity.GetType().Name;
                    SendMessage(player, $"检测到载具: {vn}，将同步传送...");
                    Puts($"[TP-MOUNT] {player.displayName} 骑乘 {vn} -> {targetName}");
                }

                // 强制下马（载具和玩家完全解耦，避免传送中途物理绑定出问题）
                ForceDismountPlayer(player);
                player.SetParent(null, true);

                // 位置合法性：NaN/无穷大兜底
                if (float.IsNaN(position.x) || float.IsNaN(position.y) || float.IsNaN(position.z) ||
                    float.IsInfinity(position.x) || float.IsInfinity(position.y) || float.IsInfinity(position.z))
                {
                    position = player.transform.position;
                }

                // 目标位置一级安全保障
                if (IsPositionOverlappingConstruction(position) || !IsPositionSafeForPlayer(position))
                {
                    position = FindNearbySafePosition(position, 6f, 1.0f);
                }

                // ===== v3.7.2 载具传送：三重安全保障 =====
                //  步骤A: 先把载具推到玩家上方200m高空完全分离 → 防止下马瞬间碰撞叠死玩家
                //  步骤B: 下一帧再传载具到目标附近+WakeUp+清速度
                //  步骤C: 玩家传送时无敌期2.5s + 伤害Hook拦截 = 100%不死
                if (hasMounted && !CompatIsDestroyed(mountedEntity))
                {
                    try
                    {
                        Vector3 playerPos = player.transform.position;
                        Vector3 highAltitude = playerPos + new Vector3(0f, 200f, 0f);

                        // ---------- 步骤A：先强制移到高空200m隔离 ----------
                        try
                        {
                            var rbHigh = mountedEntity.GetComponent<Rigidbody>();
                            if (rbHigh != null)
                            {
                                rbHigh.velocity = Vector3.zero;
                                rbHigh.angularVelocity = Vector3.zero;
                                rbHigh.WakeUp();
                            }
                        }
                        catch { }
                        try
                        {
                            var mTelH = mountedEntity.GetType().GetMethods(
                                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                .FirstOrDefault(m => m.Name == "Teleport" && m.GetParameters().Length >= 1
                                    && m.GetParameters()[0].ParameterType == typeof(Vector3));
                            if (mTelH != null) mTelH.Invoke(mountedEntity, new object[] { highAltitude });
                            else mountedEntity.transform.position = highAltitude;
                        }
                        catch { }

                        // ---------- 计算最终目标位置 ----------
                        Vector3 vehiclePos = FindSafePositionForVehicle(position, mountedEntity);

                        // ---------- 载具物理准备：RB清零 + 唤醒 ----------
                        try
                        {
                            var rbv = mountedEntity.GetComponent<Rigidbody>();
                            if (rbv != null)
                            {
                                rbv.velocity = Vector3.zero;
                                rbv.angularVelocity = Vector3.zero;
                                rbv.WakeUp();
                            }
                        }
                        catch { }

                        // ---------- 步骤B：高空→正式传送到目标附近 ----------
                        bool vehicleMoved = false;
                        try
                        {
                            var mTel = mountedEntity.GetType().GetMethods(
                                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                .FirstOrDefault(m => m.Name == "Teleport" && m.GetParameters().Length >= 1
                                    && m.GetParameters()[0].ParameterType == typeof(Vector3));
                            if (mTel != null)
                            {
                                mTel.Invoke(mountedEntity, new object[] { vehiclePos });
                                vehicleMoved = true;
                            }
                        }
                        catch { }

                        if (!vehicleMoved)
                        {
                            try { mountedEntity.transform.position = vehiclePos; vehicleMoved = true; }
                            catch { }
                        }

                        // 同步旋转 + 再次清刚体
                        if (vehicleMoved)
                        {
                            try { mountedEntity.transform.rotation = mountedRot; } catch { }
                            try
                            {
                                var rbv2 = mountedEntity.GetComponent<Rigidbody>();
                                if (rbv2 != null)
                                {
                                    rbv2.velocity = Vector3.zero;
                                    rbv2.angularVelocity = Vector3.zero;
                                    rbv2.WakeUp();
                                }
                            }
                            catch { }
                        }
                    }
                    catch (Exception vex)
                    {
                        PrintError($"[TP-MOUNT] 载具传送失败: {vex.Message}");
                        SendMessage(player, "载具传送失败，仅传送玩家（载具可能留在原地）");
                        hasMounted = false;
                    }
                }

                // ===== 传送玩家：全部走 SafeTeleportPlayer 安全链路 =====
                // (内部做了 PrepCC/清速度 → 无敌2.5s+伤害Hook拦截 → 再Teleport → 四轮复查)
                SafeTeleportPlayer(player, position, targetName);

                // ===== v3.7.2 载具：延迟0.75s后尝试自动重新骑乘 =====
                //  (自动上车只尝试公开Mount API，失败就提示玩家手动按E，绝不再AssignSeat改坏seat状态)
                if (hasMounted && !CompatIsDestroyed(mountedEntity))
                {
                    ulong playerId = player.userID;
                    BaseEntity vehicleRef = mountedEntity;
                    timer.Once(0.75f, () =>
                    {
                        try
                        {
                            var p = BasePlayer.FindByID(playerId);
                            if (p == null || !CompatIsConnected(p) || CompatIsDead(p)) return;
                            if (vehicleRef == null || CompatIsDestroyed(vehicleRef)) return;

                            // 载具位置稳定（再次清速度，防止载具在地上"滑"）
                            try
                            {
                                var rbv = vehicleRef.GetComponent<Rigidbody>();
                                if (rbv != null)
                                {
                                    rbv.velocity = Vector3.zero;
                                    rbv.angularVelocity = Vector3.zero;
                                    rbv.WakeUp();
                                }
                            }
                            catch { }

                            ForceRemountPlayer(p, vehicleRef);

                            // 再0.35s检查是否真的在马上
                            timer.Once(0.35f, () =>
                            {
                                try
                                {
                                    var pp = BasePlayer.FindByID(playerId);
                                    if (pp == null) return;
                                    var curMt = GetPlayerMountedEntity(pp);
                                    if (curMt == null || !ReferenceEquals(curMt, vehicleRef))
                                    {
                                        SendMessage(pp, "🚗 载具已传送到你身边，请面朝载具按E驾驶");
                                    }
                                    else
                                    {
                                        SendMessage(pp, "✓ 载具同步传送成功！已自动回到驾驶位");
                                    }
                                }
                                catch { }
                            });
                        }
                        catch (Exception rex)
                        {
                            PrintError($"[TP-MOUNT] 重新骑乘失败: {rex.Message}");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                PrintError($"传送失败: {ex.Message}");
                SendMessage(player, "传送失败，请重试！");
            }
        }

        #endregion

        #region 安全位置

        // 兼容不同版本的 WaterLevel.GetWaterLevel (签名可能变化)
        private static float GetWaterLevelSafe(Vector3 pos)
        {
            try { return WaterLevel.GetWaterLevel(pos, true); }
            catch { }
            try
            {
                var mi = typeof(WaterLevel).GetMethod("GetWaterLevel",
                    BindingFlags.Public | BindingFlags.Static);
                if (mi != null)
                {
                    var pars = mi.GetParameters();
                    if (pars.Length == 1 && pars[0].ParameterType == typeof(Vector3))
                        return (float)mi.Invoke(null, new object[] { pos });
                    if (pars.Length == 2)
                        return (float)mi.Invoke(null, new object[] { pos, true });
                }
            }
            catch { }
            return 0f;
        }

        private static readonly int ConstructionMask = LayerMask.GetMask("Construction", "Building");
        private static readonly int TerrainMask = LayerMask.GetMask("Terrain", "World");
        private static readonly int AllGroundMask = LayerMask.GetMask("Terrain", "World", "Construction", "Building");

        private bool IsPositionOverlappingConstruction(Vector3 pos)
        {
            try
            {
                Vector3 center = pos + Vector3.up * 0.8f;
                float radius = 0.35f;
                if (Physics.CheckSphere(center, radius, ConstructionMask))
                    return true;

                Vector3 p1 = pos + Vector3.up * 0.1f;
                Vector3 p2 = pos + Vector3.up * 1.6f;
                if (Physics.CheckCapsule(p1, p2, 0.35f, ConstructionMask))
                    return true;
            }
            catch { }
            return false;
        }

        private bool IsPositionSafeForPlayer(Vector3 pos)
        {
            try
            {
                Vector3 center = pos + Vector3.up * 0.9f;
                float radius = 0.4f;
                if (Physics.CheckSphere(center, radius, ConstructionMask))
                    return false;

                Vector3 p1 = pos + Vector3.up * 0.1f;
                Vector3 p2 = pos + Vector3.up * 1.8f;
                if (Physics.CheckCapsule(p1, p2, 0.4f, ConstructionMask))
                    return false;
            }
            catch { }
            return true;
        }

        private Vector3 FindNearbySafePosition(Vector3 centerPos, float searchRadius, float stepSize)
        {
            Vector3 bestPos = centerPos;
            bool foundSafe = false;
            float bestHeight = float.MaxValue;

            for (float r = 0f; r <= searchRadius; r += stepSize)
            {
                int ringCount = r < 0.01f ? 1 : Math.Max(8, Mathf.CeilToInt(2f * Mathf.PI * r / stepSize));
                for (int i = 0; i < ringCount; i++)
                {
                    float angle = (i / (float)ringCount) * 2f * Mathf.PI;
                    Vector3 testOffset = new Vector3(Mathf.Cos(angle) * r, 0f, Mathf.Sin(angle) * r);
                    Vector3 testPos = centerPos + testOffset;
                    Vector3 ground = GetGroundPosition(testPos);

                    if (!IsPositionOverlappingConstruction(ground) && IsPositionSafeForPlayer(ground))
                    {
                        float heightPenalty = Mathf.Abs(ground.y - centerPos.y);
                        if (heightPenalty < bestHeight)
                        {
                            bestHeight = heightPenalty;
                            bestPos = ground;
                            foundSafe = true;
                        }
                    }
                }
                if (foundSafe && r > 0.5f) break;
            }

            if (!foundSafe)
            {
                for (float lift = 0.5f; lift <= 5f; lift += 0.5f)
                {
                    Vector3 liftedPos = centerPos + Vector3.up * lift;
                    if (!IsPositionOverlappingConstruction(liftedPos) && IsPositionSafeForPlayer(liftedPos))
                        return liftedPos;
                }
                return centerPos + Vector3.up * 3f;
            }

            return bestPos;
        }

        private Vector3 FindSafePosition(Vector3 targetPosition)
        {
            Vector3 groundPos = GetGroundPosition(targetPosition);

            if (!IsPositionOverlappingConstruction(groundPos) && GetWaterLevelSafe(groundPos) <= groundPos.y + 0.5f)
            {
                return groundPos;
            }

            Vector3 safePos = FindNearbySafePosition(targetPosition, 4f, 0.6f);
            if (GetWaterLevelSafe(safePos) <= safePos.y + 0.5f &&
                !IsPositionOverlappingConstruction(safePos))
            {
                return safePos;
            }

            for (int i = 1; i <= 4; i++)
            {
                float radius = i * 2.5f;
                Vector3 ringPos = FindNearbySafePosition(targetPosition, radius, 1.0f);
                if (GetWaterLevelSafe(ringPos) <= ringPos.y + 0.5f &&
                    !IsPositionOverlappingConstruction(ringPos) &&
                    IsPositionSafeForPlayer(ringPos))
                {
                    return ringPos;
                }
            }

            return targetPosition + Vector3.up * 3f;
        }

        private Vector3 GetGroundPosition(Vector3 position)
        {
            RaycastHit hit;
            if (Physics.Raycast(position + Vector3.up * 50f, Vector3.down, out hit, 100f, AllGroundMask))
            {
                if (hit.collider != null)
                {
                    string layerName = LayerMask.LayerToName(hit.collider.gameObject.layer);
                    if (layerName == "Construction" || layerName == "Building")
                    {
                        return hit.point + Vector3.up * 0.8f;
                    }
                }
                return hit.point + Vector3.up * 0.1f;
            }

            if (Physics.Raycast(position + Vector3.up * 50f, Vector3.down, out hit, 100f, ConstructionMask))
            {
                return hit.point + Vector3.up * 0.8f;
            }

            var terrain = TerrainMeta.HeightMap;
            if (terrain != null)
            {
                float terrainHeight = terrain.GetHeight(position);
                return new Vector3(position.x, terrainHeight + 0.5f, position.z);
            }

            return position + Vector3.up * 2f;
        }

        #endregion

        #region 防卡死脱困

        private const float STUCK_COOLDOWN = 30f; // 冷却30秒

        private bool IsStuckOnCooldown(ulong playerId)
        {
            if (!_stuckCooldown.TryGetValue(playerId, out float expire)) return false;
            if (Time.realtimeSinceStartup < expire) return true;
            _stuckCooldown.Remove(playerId);
            return false;
        }

        private void SetStuckCooldown(ulong playerId)
        {
            _stuckCooldown[playerId] = Time.realtimeSinceStartup + STUCK_COOLDOWN;
        }

        private float GetStuckCooldownRemaining(ulong playerId)
        {
            if (!_stuckCooldown.TryGetValue(playerId, out float expire)) return 0f;
            float remaining = expire - Time.realtimeSinceStartup;
            return remaining > 0f ? remaining : 0f;
        }

        private bool IsPlayerStuck(Vector3 pos)
        {
            return IsPositionOverlappingConstruction(pos) || !IsPositionSafeForPlayer(pos);
        }

        private Vector3 FindEscapePosition(Vector3 currentPos)
        {
            // 策略1：螺旋搜索附近安全点（小范围精细搜索）
            Vector3 safe = FindNearbySafePosition(currentPos, 5f, 0.5f);
            if (safe != currentPos && !IsPlayerStuck(safe))
                return safe;

            // 策略2：扩大搜索范围
            for (int radius = 6; radius <= 15; radius += 3)
            {
                Vector3 ringSafe = FindNearbySafePosition(currentPos, radius, 1.2f);
                if (!IsPlayerStuck(ringSafe))
                    return ringSafe;
            }

            // 策略3：向上抬升脱离建筑
            for (float lift = 1f; lift <= 10f; lift += 1f)
            {
                Vector3 lifted = currentPos + Vector3.up * lift;
                Vector3 grounded = GetGroundPosition(lifted);
                if (!IsPlayerStuck(grounded))
                    return grounded;
            }

            // 策略4：向上+水平偏移
            for (float lift = 3f; lift <= 8f; lift += 1f)
            {
                for (int angle = 0; angle < 360; angle += 30)
                {
                    float rad = angle * Mathf.Deg2Rad;
                    Vector3 offset = new Vector3(Mathf.Cos(rad) * 3f, lift, Mathf.Sin(rad) * 3f);
                    Vector3 candidate = GetGroundPosition(currentPos + offset);
                    if (!IsPlayerStuck(candidate))
                        return candidate;
                }
            }

            // 兜底：向上抬升5m
            return currentPos + Vector3.up * 5f;
        }

        private void TryUnstuck(BasePlayer player)
        {
            if (player == null || !CompatIsConnected(player)) return;
            if (CompatIsDead(player))
            {
                SendMessage(player, "死亡状态下无法使用脱困功能！");
                return;
            }

            ulong pid = player.userID;
            if (IsStuckOnCooldown(pid))
            {
                float remaining = GetStuckCooldownRemaining(pid);
                SendMessage(player, $"防卡死冷却中，请等待 {Mathf.CeilToInt(remaining)} 秒");
                return;
            }

            Vector3 currentPos = player.transform.position;
            bool isStuck = IsPlayerStuck(currentPos);

            if (!isStuck)
            {
                // 当前位置没卡住，但玩家可能感觉自己卡住了（视觉/物理抖动）
                // 仍然提供一次小幅脱困
                SendMessage(player, "检测到当前位置未卡在建筑中，尝试微调位置...");
            }
            else
            {
                SendMessage(player, "检测到卡在建筑中，正在脱困...");
            }

            try
            {
                Vector3 escapePos = FindEscapePosition(currentPos);

                // v3.7.1 载具同步 + 安全传送：如果玩家骑乘载具一起脱困
                BaseEntity mounted = GetPlayerMountedEntity(player);
                bool hasMount = mounted != null && IsVehicleOrMount(mounted);
                Quaternion mountRot = Quaternion.identity;
                try { if (hasMount) mountRot = mounted.transform.rotation; } catch { }

                ForceDismountPlayer(player);
                player.SetParent(null, true);

                // 传送载具
                if (hasMount && !CompatIsDestroyed(mounted))
                {
                    try
                    {
                        Vector3 vp = FindSafePositionForVehicle(escapePos, mounted);
                        try
                        {
                            var rbv = mounted.GetComponent<Rigidbody>();
                            if (rbv != null)
                            {
                                rbv.velocity = Vector3.zero;
                                rbv.angularVelocity = Vector3.zero;
                                rbv.WakeUp();
                            }
                        }
                        catch { }
                        var mTel = mounted.GetType().GetMethods(
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            .FirstOrDefault(m => m.Name == "Teleport" && m.GetParameters().Length >= 1
                                && m.GetParameters()[0].ParameterType == typeof(Vector3));
                        if (mTel != null) mTel.Invoke(mounted, new object[] { vp });
                        else mounted.transform.position = vp;
                        try { mounted.transform.rotation = mountRot; } catch { }
                        try
                        {
                            var rb = mounted.GetComponent<Rigidbody>();
                            if (rb != null) { rb.WakeUp(); rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
                        }
                        catch { }
                    }
                    catch (Exception sx) { PrintError($"脱困载具传送失败: {sx.Message}"); hasMount = false; }
                }

                // v3.7.1 改用 SafeTeleportPlayer (含无敌+清CC+四轮复查，防止脱困过程中挤压伤害)
                SafeTeleportPlayer(player, escapePos, "防卡死脱困");

                if (hasMount && !CompatIsDestroyed(mounted))
                {
                    ulong pid2 = pid;
                    BaseEntity mref = mounted;
                    timer.Once(0.65f, () =>
                    {
                        var pp = BasePlayer.FindByID(pid2);
                        if (pp != null && !CompatIsDestroyed(mref))
                        {
                            try { ForceRemountPlayer(pp, mref); } catch { }
                        }
                    });
                }

                SetStuckCooldown(pid);

                // 0.3秒后复查
                timer.Once(0.3f, () =>
                {
                    if (player == null || !CompatIsConnected(player)) return;
                    try
                    {
                        Vector3 afterPos = player.transform.position;
                        if (IsPlayerStuck(afterPos))
                        {
                            // 仍然卡住，尝试更大范围搜索
                            Vector3 retryPos = FindEscapePosition(afterPos);
                            player.Teleport(retryPos);
                            SendMessage(player, "已执行二次脱困修正");
                        }
                        else
                        {
                            SendMessage(player, "脱困成功！如仍卡住请稍后再次使用");
                        }
                    }
                    catch { }
                });
            }
            catch (Exception ex)
            {
                PrintError($"防卡死失败: {ex.Message}");
                SendMessage(player, "脱困失败，请尝试重新连接服务器");
            }
        }

        #endregion

        #region 管理员功能

        private bool HasAdminPerm(BasePlayer player)
        {
            if (player == null) return false;
            if (player.IsAdmin || player.IsDeveloper) return true;
            return permission.UserHasPermission(player.UserIDString, PERM_ADMIN);
        }

        private void AdminListPlayers(BasePlayer player)
        {
            if (player == null) return;

            var players = BasePlayer.activePlayerList;
            if (players == null || players.Count == 0)
            {
                SendMessage(player, "当前没有在线玩家");
                return;
            }

            SendMessage(player, $"在线玩家 ({players.Count}):");
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p != null && CompatIsConnected(p))
                {
                    SendMessage(player, $"  {p.displayName} (ID: {p.userID}) - X:{p.transform.position.x:F0} Z:{p.transform.position.z:F0}");
                }
            }
            SendMessage(player, "使用 /tp admin <玩家名> 传送至该玩家");
        }

        private void AdminTeleportToPlayer(BasePlayer player, string targetName)
        {
            if (player == null || string.IsNullOrEmpty(targetName)) return;

            var target = FindPlayerByName(targetName);
            if (target == null)
            {
                SendMessage(player, $"找不到玩家: {targetName}");
                return;
            }

            if (!CompatIsConnected(target))
            {
                SendMessage(player, $"玩家 {targetName} 不在线");
                return;
            }

            BeginTeleport(player, target.transform.position, $"管理员传送至 {target.displayName}");
        }

        private void AdminWipePlayerPoints(BasePlayer player, string targetName)
        {
            if (player == null || string.IsNullOrEmpty(targetName)) return;

            var target = FindPlayerByName(targetName);
            if (target == null)
            {
                SendMessage(player, $"找不到玩家: {targetName}");
                return;
            }

            if (!storedData.PlayerPoints.ContainsKey(target.userID) || storedData.PlayerPoints[target.userID].Count == 0)
            {
                SendMessage(player, $"玩家 {targetName} 没有传送点");
                return;
            }

            int count = storedData.PlayerPoints[target.userID].Count;
            storedData.PlayerPoints[target.userID].Clear();
            SaveData();
            SendMessage(player, $"已清除 {targetName} 的 {count} 个传送点");
        }

        private BasePlayer FindPlayerByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            if (ulong.TryParse(name, out ulong userId))
            {
                var player = BasePlayer.FindByID(userId);
                if (player != null) return player;
            }

            var players = BasePlayer.activePlayerList;
            if (players == null) return null;

            foreach (var p in players)
            {
                if (p != null && CompatIsConnected(p) && p.displayName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return p;
            }

            foreach (var p in players)
            {
                if (p != null && CompatIsConnected(p) && p.displayName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    return p;
            }

            return null;
        }

        #endregion

        #region 消息

        private void SendMessage(BasePlayer player, string message)
        {
            if (player == null) return;
            SendReply(player, $"<color={config.Settings.ChatPrefixColor}>{config.Settings.ChatPrefix}</color> {message}");
        }

        #endregion
    }
}
