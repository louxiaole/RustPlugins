using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Oxide.Core;

namespace Oxide.Plugins
{
    [Info("Gather Multiplier", "Custom", "4.6.0")]
    [Description("资源采集倍率及战利品倍率插件 - Carbon/Oxide双框架兼容 - 修复尸体物资二次乘算 (适配最新Rust, 数值溢出保护)")]
    class ResourceGatherMultiplier : RustPlugin
    {
        #region Carbon / Oxide 双框架兼容层

        // 反射缓存：属性or方法，运行时自动检测
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

        // IsConnected: Oxide=属性, Carbon某些版本=方法。用委托缓存避免每次反射。
        private static readonly Lazy<Func<BasePlayer, bool>> _getIsConnected = new Lazy<Func<BasePlayer, bool>>(() =>
        {
            var prop = typeof(BasePlayer).GetProperty("IsConnected", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null)
            {
                var getter = prop.GetGetMethod(true);
                if (getter != null)
                    return (Func<BasePlayer, bool>)Delegate.CreateDelegate(typeof(Func<BasePlayer, bool>), null, getter);
            }
            var meth = typeof(BasePlayer).GetMethod("IsConnected", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (meth != null)
                return (Func<BasePlayer, bool>)Delegate.CreateDelegate(typeof(Func<BasePlayer, bool>), null, meth);
            return (p) => true;
        });

        private static bool CompatIsConnected(BasePlayer player)
        {
            if (player == null) return false;
            try { return _getIsConnected.Value(player); }
            catch { return true; }
        }

        // IsDestroyed: Oxide=属性, Carbon某些版本=方法
        private static readonly Lazy<Func<BaseNetworkable, bool>> _getIsDestroyed = new Lazy<Func<BaseNetworkable, bool>>(() =>
        {
            var prop = typeof(BaseNetworkable).GetProperty("IsDestroyed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null)
            {
                var getter = prop.GetGetMethod(true);
                if (getter != null)
                    return (Func<BaseNetworkable, bool>)Delegate.CreateDelegate(typeof(Func<BaseNetworkable, bool>), null, getter);
            }
            var meth = typeof(BaseNetworkable).GetMethod("IsDestroyed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (meth != null)
                return (Func<BaseNetworkable, bool>)Delegate.CreateDelegate(typeof(Func<BaseNetworkable, bool>), null, meth);
            return (e) => false;
        });

        private static bool CompatIsDestroyed(BaseEntity entity)
        {
            if (entity == null) return true;
            try { return _getIsDestroyed.Value(entity); }
            catch { return false; }
        }

        // IsAlive: 仅对 BaseCombatEntity 有意义，BaseEntity 不直接调用
        private static readonly Lazy<Func<BaseCombatEntity, bool>> _getIsAlive = new Lazy<Func<BaseCombatEntity, bool>>(() =>
        {
            var meth = typeof(BaseCombatEntity).GetMethod("IsAlive", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (meth != null)
                return (Func<BaseCombatEntity, bool>)Delegate.CreateDelegate(typeof(Func<BaseCombatEntity, bool>), null, meth);
            var prop = typeof(BaseCombatEntity).GetProperty("IsAlive", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null)
            {
                var getter = prop.GetGetMethod(true);
                if (getter != null)
                    return (Func<BaseCombatEntity, bool>)Delegate.CreateDelegate(typeof(Func<BaseCombatEntity, bool>), null, getter);
            }
            return (e) => true;
        });

        private static bool CompatIsAlive(BaseEntity entity)
        {
            if (entity == null) return false;
            var combat = entity as BaseCombatEntity;
            if (combat == null) return true;
            try { return _getIsAlive.Value(combat); }
            catch { return true; }
        }

        #endregion

        #region 字段

        private Configuration config;

        private HashSet<ulong> _processedContainers = new HashSet<ulong>();
        private readonly HashSet<string> _resourceCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _skullCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 已移除类级 StringBuilder，改为方法内局部变量，避免并发问题

        private const string PERM_ADMIN = "gathermultiplier.admin";

        private static readonly string[] ResourceKeywords =
        {
            "wood", "stones", "metal.ore", "sulfur.ore", "hq.metal.ore",
            "cloth", "leather", "fat.animal", "bone.fragments", "crude.oil",
            "metal.fragments", "sulfur", "charcoal", "metal.refined"
        };

        private static readonly string[] SkullKeywords = { "skull" };

        #endregion

        #region 配置类

        private class Configuration
        {
            public bool EnableResourceMultiplier = true;
            public float ResourceMultiplier = 600f;
            public bool EnableLootMultiplier = true;
            public float LootMultiplier = 10f;
            public bool ExcludeSkulls = true;
            public bool EnableDebugLog = false;
            public int CleanupInterval = 600;
            public bool EnableCorpseLoot = true;
        }

        private Configuration GetDefaultConfig()
        {
            return new Configuration
            {
                EnableResourceMultiplier = true,
                ResourceMultiplier = 600f,
                EnableLootMultiplier = true,
                LootMultiplier = 10f,
                ExcludeSkulls = true,
                EnableDebugLog = false,
                CleanupInterval = 600,
                EnableCorpseLoot = true
            };
        }

        protected override void LoadDefaultConfig()
        {
            Config.Clear();
            Config.WriteObject(GetDefaultConfig(), true);
        }

        private void LoadConfig()
        {
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null)
                {
                    LoadDefaultConfig();
                    config = Config.ReadObject<Configuration>();
                }
                if (config.ResourceMultiplier <= 0f) config.ResourceMultiplier = 600f;
                if (config.LootMultiplier <= 0f) config.LootMultiplier = 10f;
                if (config.CleanupInterval <= 0) config.CleanupInterval = 600;
            }
            catch (Exception ex)
            {
                PrintError($"加载配置失败: {ex.Message}");
                LoadDefaultConfig();
                config = Config.ReadObject<Configuration>();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(config, true);

        #endregion

        #region 初始化

        private void Init()
        {
            LoadConfig();
            permission.RegisterPermission(PERM_ADMIN, this);
        }

        private void OnServerInitialized()
        {
            Puts("========================================");
            Puts($"Gather Multiplier v4.6.0 已加载 (适配最新Rust)");
            Puts($"资源倍率: {config.ResourceMultiplier}x ({(config.EnableResourceMultiplier ? "启用" : "禁用")})");
            Puts($"战利品倍率: {config.LootMultiplier}x ({(config.EnableLootMultiplier ? "启用" : "禁用")})");
            Puts("命令: /gather, /loot, /gatherinfo, /gatherhelp");
            Puts("========================================");

            timer.Repeat(config.CleanupInterval, 0, CleanupContainers);
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null || player.IsNpc) return;
            timer.Once(3.5f, () =>
            {
                if (player == null || !CompatIsConnected(player)) return;
                SendReply(player, "========== 采集倍率 ==========");
                SendReply(player, $"/gather [倍率] - 查看/设置资源倍率 (当前: {config.ResourceMultiplier}x)");
                SendReply(player, $"/loot [倍率] - 查看/设置战利品倍率 (当前: {config.LootMultiplier}x)");
                SendReply(player, "/gatherinfo - 查看采集状态");
                SendReply(player, "/gatherhelp - 采集帮助");
                SendReply(player, "/help2 - 查看采集倍率命令");
                SendReply(player, "================================");
            });
        }

        private void Unload()
        {
            _processedContainers.Clear();
            _resourceCache.Clear();
            _skullCache.Clear();
        }

        #endregion

        #region 资源采集倍率

        // 最新Rust: OnDispenserGather / OnDispenserBonus 的第二个参数已统一为 BasePlayer
        private void OnDispenserGather(ResourceDispenser dispenser, BasePlayer entity, Item item)
        {
            ApplyResourceMultiplier(item);
        }

        private void OnDispenserBonus(ResourceDispenser dispenser, BasePlayer entity, Item item)
        {
            ApplyResourceMultiplier(item);
        }

        private void OnGrowableGathered(GrowableEntity growable, Item item, BasePlayer player)
        {
            ApplyResourceMultiplier(item);
        }

        private void OnQuarryGather(MiningQuarry quarry, Item item)
        {
            ApplyResourceMultiplier(item);
        }

        private void OnExcavatorGather(ExcavatorArm excavator, Item item)
        {
            ApplyResourceMultiplier(item);
        }

        // 最新Rust: 原 OnCollectiblePickup 已变更为 (CollectibleEntity, BasePlayer, bool) 并且是可覆盖钩子。
        // 采集后的物资统一在 OnCollectiblePickedup(CollectibleEntity, BasePlayer, Item) 中放大，
        // 直接修改实际发放的 Item，避免修改 itemList 造成重复拾取时倍率叠加。
        private void OnCollectiblePickedup(CollectibleEntity collectible, BasePlayer reciever, Item item)
        {
            ApplyResourceMultiplier(item);
        }

        private void ApplyResourceMultiplier(Item item)
        {
            if (!config.EnableResourceMultiplier || item?.info == null) return;
            if (IsSkullItem(item.info.shortname)) return;

            int original = item.amount;
            float mult = GetResourceMultiplier(item.info.shortname);
            if (Math.Abs(mult - 1f) > 0.001f)
            {
                item.amount = CalcAmount(original, mult);
            }
        }

        // 统一计算数量：double 运算 + int 上限保护，避免高倍率下溢出成负数
        private static int CalcAmount(int original, float mult)
        {
            if (original <= 0 || mult <= 0f) return original;
            double raw = original * (double)mult;
            if (raw >= int.MaxValue) return int.MaxValue;
            int next = (int)Math.Round(raw);
            return next < 1 ? 1 : next;
        }

        private float GetResourceMultiplier(string shortname)
        {
            if (IsSkullItem(shortname)) return 1f;
            return config.ResourceMultiplier;
        }

        #endregion

        #region 战利品倍率

        private void OnLootSpawn(LootContainer container)
        {
            if (!config.EnableLootMultiplier || container == null) return;
            if (config.LootMultiplier <= 0) return;

            timer.Once(0.1f, () =>
            {
                if (container == null || CompatIsDestroyed(container)) return;
                ApplyLootMultiplier(container);
            });
        }

        private void OnLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (!config.EnableLootMultiplier || entity == null) return;

            var container = entity as LootContainer;
            if (container != null)
            {
                ApplyLootMultiplier(container);
                return;
            }

            // 玩家尸体不应用战利品倍率（物资已通过采集倍率获得，避免二次乘算）
            var corpse = entity as LootableCorpse;
            if (corpse != null && config.EnableCorpseLoot)
            {
                if (IsPlayerCorpse(corpse)) return;

                ulong id = GetContainerId(corpse);
                if (_processedContainers.Contains(id)) return;
                var inv = corpse.containers?[0];
                if (inv != null)
                {
                    timer.Once(0.1f, () =>
                    {
                        if (corpse == null || CompatIsDestroyed(corpse)) return;
                        var c = corpse.containers?[0];
                        if (c != null) ApplyLootMultiplierToContainer(c, id);
                    });
                }
            }
        }

        private void OnCorpsePopulate(LootableCorpse corpse, BasePlayer player)
        {
            // 玩家尸体不应用战利品倍率，避免死亡后回收物资被二次乘算
            return;
        }

        private bool IsPlayerCorpse(LootableCorpse corpse)
        {
            if (corpse == null) return false;
            try
            {
                string typeName = corpse.GetType().Name;
                return typeName == "PlayerCorpse" || typeName == "HumanCorpse" || typeName == "NPCCorpse";
            }
            catch { }
            return false;
        }

        private void ApplyLootMultiplier(LootContainer container)
        {
            if (container == null || CompatIsDestroyed(container) || container.inventory == null) return;
            ulong id = GetContainerId(container);
            ApplyLootMultiplierToContainer(container.inventory, id);
        }

        private ulong GetContainerId(BaseEntity entity)
        {
            if (entity == null) return 0;
            if (entity.net != null)
            {
                try
                {
                    var netId = entity.net.ID;
                    // 兼容不同版本的 NetworkableId.Value (属性 or 字段)
                    var prop = netId.GetType().GetProperty("Value",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (prop != null)
                    {
                        var v = prop.GetValue(netId, null);
                        if (v is ulong ul) return ul;
                        if (v is uint ui) return ui;
                        if (v is long l) return (ulong)l;
                        if (v is int i) return (ulong)i;
                    }
                    var field = netId.GetType().GetField("Value",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (field != null)
                    {
                        var v = field.GetValue(netId);
                        if (v is ulong ul) return ul;
                        if (v is uint ui) return ui;
                    }
                }
                catch { }
            }
            return (ulong)(uint)entity.GetHashCode();
        }

        // 兼容 StringView (最新Rust ConsoleSystem.Arg.Args 是 StringView[])
        private static string ArgToStr(ConsoleSystem.Arg arg, int index)
        {
            if (arg == null || arg.Args == null || index < 0 || index >= arg.Args.Length) return null;
            try { return arg.Args[index].ToString(); }
            catch { return null; }
        }

        private void ApplyLootMultiplierToContainer(ItemContainer container, ulong containerId = 0)
        {
            if (container == null || config.LootMultiplier <= 0) return;

            if (containerId == 0)
                containerId = (ulong)(uint)container.GetHashCode();

            if (containerId > 0 && _processedContainers.Contains(containerId)) return;

            var items = container.itemList;
            if (items == null) return;

            bool changed = false;
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null || item.info == null) continue;
                if (IsResourceItem(item.info.shortname)) continue;
                if (IsSkullItem(item.info.shortname)) continue;
                if (item.info.category == ItemCategory.Attire) continue;
                if (IsEquipment(item)) continue;
                if (item.info.stackable <= 1) continue;

                int cur = item.amount;
                int next = CalcAmount(cur, config.LootMultiplier);
                if (next > cur && next <= item.info.stackable)
                {
                    item.amount = next;
                    changed = true;
                }
            }

            if (changed && containerId > 0)
                _processedContainers.Add(containerId);
        }

        private bool IsEquipment(Item item)
        {
            if (item?.info == null) return false;
            var cat = item.info.category;
            if (item.info.stackable <= 1)
            {
                return cat == ItemCategory.Weapon ||
                       cat == ItemCategory.Tool ||
                       cat == ItemCategory.Medical ||
                       cat == ItemCategory.Food ||
                       cat == ItemCategory.Construction;
            }
            return false;
        }

        #endregion

        #region 容器清理

        private void CleanupContainers()
        {
            if (_processedContainers.Count == 0) return;

            var toRemove = new List<ulong>();
            int count = 0;
            int max = 200;

            var enumerator = _processedContainers.GetEnumerator();
            while (enumerator.MoveNext() && count < max)
            {
                ulong id = enumerator.Current;
                count++;
                BaseEntity ent = null;
                try
                {
                    // 兼容不同版本的 NetworkableId 构造
                    var nidType = typeof(NetworkableId);
                    var ctor = nidType.GetConstructor(new Type[] { typeof(ulong) });
                    if (ctor != null)
                    {
                        var nid = (NetworkableId)ctor.Invoke(new object[] { id });
                        ent = BaseNetworkable.serverEntities.Find(nid) as BaseEntity;
                    }
                    else
                    {
                        var ctorU = nidType.GetConstructor(new Type[] { typeof(uint) });
                        if (ctorU != null)
                        {
                            var nid2 = (NetworkableId)ctorU.Invoke(new object[] { (uint)id });
                            ent = BaseNetworkable.serverEntities.Find(nid2) as BaseEntity;
                        }
                    }
                }
                catch { }
                if (ent == null || CompatIsDestroyed(ent))
                    toRemove.Add(id);
            }
            enumerator.Dispose();

            for (int i = 0; i < toRemove.Count; i++)
                _processedContainers.Remove(toRemove[i]);

            if (toRemove.Count > 0 && config.EnableDebugLog)
                Puts($"[调试] 清理了 {toRemove.Count} 个已销毁容器");
        }

        #endregion

        #region 工具方法

        private bool IsResourceItem(string shortname)
        {
            if (string.IsNullOrEmpty(shortname)) return false;
            if (_resourceCache.Contains(shortname)) return true;

            string lower = shortname.ToLower();
            bool isRes = false;
            for (int i = 0; i < ResourceKeywords.Length; i++)
            {
                if (lower.Contains(ResourceKeywords[i]))
                {
                    isRes = true;
                    break;
                }
            }
            if (isRes) _resourceCache.Add(shortname);
            return isRes;
        }

        private bool IsSkullItem(string shortname)
        {
            if (!config.ExcludeSkulls || string.IsNullOrEmpty(shortname)) return false;
            if (_skullCache.Contains(shortname)) return true;

            string lower = shortname.ToLower();
            bool isSkull = false;
            for (int i = 0; i < SkullKeywords.Length; i++)
            {
                if (lower.Contains(SkullKeywords[i]))
                {
                    isSkull = true;
                    break;
                }
            }
            if (isSkull) _skullCache.Add(shortname);
            return isSkull;
        }

        private bool HasPerm(BasePlayer player)
        {
            if (player == null) return false;
            if (player.IsAdmin || player.IsDeveloper) return true;
            if (permission.UserHasPermission(player.UserIDString, PERM_ADMIN)) return true;
            return false;
        }

        #endregion

        #region 聊天命令

        [ChatCommand("gather")]
        private void CmdGather(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            if (args == null || args.Length == 0)
            {
                if (config.EnableResourceMultiplier)
                    SendReply(player, $"[采集] 资源倍率: {config.ResourceMultiplier}x | 用法: /gather <倍率>  例: /gather 1000");
                else
                    SendReply(player, "[采集] 资源倍率已禁用");
                return;
            }

            if (!HasPerm(player))
            {
                SendReply(player, "[采集] 你没有权限执行此操作");
                return;
            }

            if (!float.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float mult) || mult <= 0f)
            {
                SendReply(player, "[采集] 倍率必须是大于 0 的数字，例: /gather 500");
                return;
            }

            config.ResourceMultiplier = mult;
            SaveConfig();
            SendReply(player, $"[采集] 资源倍率已设为 {config.ResourceMultiplier}x");
        }

        [ChatCommand("loot")]
        private void CmdLoot(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            if (args == null || args.Length == 0)
            {
                if (config.EnableLootMultiplier)
                    SendReply(player, $"[采集] 战利品倍率: {config.LootMultiplier}x | 用法: /loot <倍率>  例: /loot 20");
                else
                    SendReply(player, "[采集] 战利品倍率已禁用");
                return;
            }

            if (!HasPerm(player))
            {
                SendReply(player, "[采集] 你没有权限执行此操作");
                return;
            }

            if (!float.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float mult) || mult <= 0f)
            {
                SendReply(player, "[采集] 倍率必须是大于 0 的数字，例: /loot 10");
                return;
            }

            config.LootMultiplier = mult;
            SaveConfig();
            SendReply(player, $"[采集] 战利品倍率已设为 {config.LootMultiplier}x");
        }

        [ChatCommand("gatherinfo")]
        private void CmdGatherInfo(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            var sb = new StringBuilder(256);
            sb.Append("[采集] 状态: ");
            sb.Append($"资源 {config.ResourceMultiplier}x (");
            sb.Append(config.EnableResourceMultiplier ? "启用" : "禁用");
            sb.Append(") | 战利品 ");
            sb.Append(config.LootMultiplier);
            sb.Append("x (");
            sb.Append(config.EnableLootMultiplier ? "启用" : "禁用");
            sb.Append(") | 尸体战利品 ");
            sb.Append(config.EnableCorpseLoot ? "启用" : "禁用");
            sb.Append(" | 已处理容器: ");
            sb.Append(_processedContainers.Count);
            SendReply(player, sb.ToString());
        }

        [ChatCommand("gatherhelp")]
        private void CmdGatherHelp(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            var lines = new[]
            {
                "========== 采集倍率 帮助 ==========",
                "/gather [倍率] - 查看/设置资源采集倍率",
                "  例: /gather 500  (设置为500倍)",
                "/loot [倍率] - 查看/设置战利品倍率",
                "  例: /loot 10  (设置为10倍)",
                "/gatherinfo - 查看当前状态",
                "/gatherhelp - 显示此帮助",
                "/help2 - 查看采集倍率命令",
                "=================================="
            };
            SendReply(player, string.Join("\n", lines));
        }

        [ChatCommand("help2")]
        private void CmdHelp2(BasePlayer player, string command, string[] args)
        {
            CmdGatherHelp(player, command, args);
        }

        #endregion

        #region 控制台命令

        [ConsoleCommand("gathermultiplier.reload")]
        private void CcmdReload(ConsoleSystem.Arg arg)
        {
            LoadConfig();
            arg.ReplyWith("[采集] 配置已重载");
        }

        [ConsoleCommand("gathermultiplier.resource")]
        private void CcmdResource(ConsoleSystem.Arg arg)
        {
            string arg0 = ArgToStr(arg, 0);
            if (arg0 == null)
            {
                arg.ReplyWith("用法: gathermultiplier.resource <倍率>");
                return;
            }
            if (!float.TryParse(arg0, NumberStyles.Float, CultureInfo.InvariantCulture, out float mult) || mult <= 0f)
            {
                arg.ReplyWith("倍率必须大于 0");
                return;
            }
            config.ResourceMultiplier = mult;
            SaveConfig();
            arg.ReplyWith($"[采集] 资源倍率已设为 {config.ResourceMultiplier}x");
        }

        [ConsoleCommand("gathermultiplier.loot")]
        private void CcmdLoot(ConsoleSystem.Arg arg)
        {
            string arg0 = ArgToStr(arg, 0);
            if (arg0 == null)
            {
                arg.ReplyWith("用法: gathermultiplier.loot <倍率>");
                return;
            }
            if (!float.TryParse(arg0, NumberStyles.Float, CultureInfo.InvariantCulture, out float mult) || mult <= 0f)
            {
                arg.ReplyWith("倍率必须大于 0");
                return;
            }
            config.LootMultiplier = mult;
            SaveConfig();
            arg.ReplyWith($"[采集] 战利品倍率已设为 {config.LootMultiplier}x");
        }

        [ConsoleCommand("gathermultiplier.toggle")]
        private void CcmdToggle(ConsoleSystem.Arg arg)
        {
            config.EnableResourceMultiplier = !config.EnableResourceMultiplier;
            SaveConfig();
            arg.ReplyWith($"[采集] 资源倍率已{(config.EnableResourceMultiplier ? "启用" : "禁用")}");
        }

        [ConsoleCommand("gathermultiplier.merchantloot")]
        private void CcmdMerchantLoot(ConsoleSystem.Arg arg)
        {
            if (arg.Args == null || arg.Args.Length < 1)
            {
                arg.ReplyWith($"用法: gathermultiplier.merchantloot <倍率>  当前尸体战利品: {(config.EnableCorpseLoot ? "启用" : "禁用")}");
                return;
            }
            config.EnableCorpseLoot = !config.EnableCorpseLoot;
            SaveConfig();
            arg.ReplyWith($"[采集] 尸体战利品倍率已{(config.EnableCorpseLoot ? "启用" : "禁用")}");
        }

        [ConsoleCommand("gathermultiplier.status")]
        private void CcmdStatus(ConsoleSystem.Arg arg)
        {
            arg.ReplyWith($"[采集] 状态 - 资源: {config.ResourceMultiplier}x ({(config.EnableResourceMultiplier ? "启用" : "禁用")}) | 战利品: {config.LootMultiplier}x ({(config.EnableLootMultiplier ? "启用" : "禁用")}) | 尸体: {(config.EnableCorpseLoot ? "启用" : "禁用")} | 已处理: {_processedContainers.Count}");
        }

        #endregion
    }
}
