using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Oxide.Core;

namespace Oxide.Plugins
{
    [Info("Stack Multiplier", "Custom", "5.3.0")]
    [Description("物品堆叠倍率插件 - 支持聊天命令实时调整 (适配最新Rust, 反射兼容 itemList/stackable)")]
    class StackMultiplier : RustPlugin
    {
        #region 字段

        private Configuration config;
        private readonly Dictionary<int, int> _vanillaStacks = new Dictionary<int, int>();
        private readonly HashSet<int> _processedItems = new HashSet<int>();

        private const string PERM_ADMIN = "stackmultiplier.admin";

        #endregion

        #region 配置类

        private class Configuration
        {
            public bool Enabled = true;
            public float Multiplier = 50f;
            public bool ExcludeSkulls = true;
            public List<string> Blacklist = new List<string>();
        }

        private Configuration GetDefaultConfig()
        {
            return new Configuration
            {
                Enabled = true,
                Multiplier = 50f,
                ExcludeSkulls = true,
                Blacklist = new List<string> { "rocket.smoke", "ammo.rocket.smoke" }
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
                if (config.Multiplier <= 0f) config.Multiplier = 50f;
                if (config.Blacklist == null) config.Blacklist = new List<string>();
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
            TryApplyToAllItems();
            PrintStatus();
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null || player.IsNpc) return;
            timer.Once(3f, () =>
            {
                if (player == null || !player.IsConnected) return;
                SendReply(player, "========== 堆叠倍率 ==========");
                SendReply(player, "/stack [倍率] - 查看/设置堆叠倍率");
                SendReply(player, "/stackinfo - 查看堆叠状态");
                SendReply(player, "/stackhelp - 堆叠帮助");
                SendReply(player, "/help1 - 查看堆叠倍率命令");
                SendReply(player, "================================");
            });
        }

        private void Unload()
        {
            RestoreAllVanilla();
            _vanillaStacks.Clear();
            _processedItems.Clear();
        }

        private void PrintStatus()
        {
            Puts("========================================");
            Puts($"Stack Multiplier v5.3.0 已加载 (适配最新Rust)");
            Puts($"状态: {(config.Enabled ? "启用" : "禁用")}");
            Puts($"堆叠倍率: {config.Multiplier}x");
            Puts($"已缓存: {_vanillaStacks.Count} 个物品");
            Puts($"排除头骨: {config.ExcludeSkulls}");
            Puts($"黑名单: {config.Blacklist.Count} 个");
            Puts("命令: /stack, /stackhelp, /stackinfo");
            Puts("========================================");
        }

        // 兼容 StringView (最新Rust ConsoleSystem.Arg.Args 是 StringView[])
        private static string ArgToStr(ConsoleSystem.Arg arg, int index)
        {
            if (arg == null || arg.Args == null || index < 0 || index >= arg.Args.Length) return null;
            try { return arg.Args[index].ToString(); }
            catch { return null; }
        }

        #endregion

        #region 核心逻辑 - 兼容多版本API

        private void TryApplyToAllItems()
        {
            if (!config.Enabled) return;

            int modified = 0;

            try
            {
                string[] fieldNames = { "itemDefinitions", "_itemDefinitions", "itemList", "_itemList" };
                foreach (var fieldName in fieldNames)
                {
                    var field = typeof(ItemManager).GetField(fieldName,
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Static);
                    if (field != null)
                    {
                        var value = field.GetValue(null);
                        if (value != null)
                        {
                            modified += ApplyFromCollection(value);
                            if (modified > 0) break;
                        }
                    }
                }

                if (modified == 0)
                {
                    var method = typeof(ItemManager).GetMethod("FindItemDefinition",
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.Static,
                        null,
                        new Type[] { typeof(int) },
                        null);
                    if (method == null)
                    {
                        // 退化：用完全匹配搜索带int参数的版本
                        var methods = typeof(ItemManager).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                            .Where(m => m.Name == "FindItemDefinition");
                        foreach (var m in methods)
                        {
                            var pars = m.GetParameters();
                            if (pars.Length == 1 && pars[0].ParameterType == typeof(int)) { method = m; break; }
                        }
                    }
                    if (method != null)
                    {
                        for (int i = 0; i < 5000; i++)
                        {
                            try
                            {
                                var def = method.Invoke(null, new object[] { i }) as ItemDefinition;
                                if (def == null) break;
                                if (ApplyToItem(def)) modified++;
                            }
                            catch { break; }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                PrintWarning($"批量应用时出错（将仅使用钩子模式）: {ex.Message}");
            }

            if (modified > 0)
                Puts($"已修改 {modified} 个物品的堆叠数");
            else
                Puts("未找到物品定义集合，将仅通过 OnMaxStackable 钩子运行");
        }

        private int ApplyFromCollection(object collection)
        {
            int modified = 0;
            var enumerable = collection as System.Collections.IEnumerable;
            if (enumerable == null) return 0;

            foreach (var item in enumerable)
            {
                ItemDefinition def = ExtractItemDef(item);
                if (def != null && ApplyToItem(def))
                    modified++;
            }
            return modified;
        }

        private ItemDefinition ExtractItemDef(object obj)
        {
            if (obj is ItemDefinition) return (ItemDefinition)obj;

            try
            {
                // 避免Ambiguous match：明确排除索引器，只选取返回ItemDefinition的实例属性
                var candidates = obj.GetType().GetProperties(
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic)
                    .Where(p => p.Name == "Value"
                             && p.GetIndexParameters().Length == 0
                             && typeof(ItemDefinition).IsAssignableFrom(p.PropertyType));

                var prop = candidates.FirstOrDefault();
                if (prop != null) return prop.GetValue(obj) as ItemDefinition;
            }
            catch { }

            try
            {
                // 退化2：直接遍历KeyValuePair
                var type = obj.GetType();
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
                {
                    var valProp = type.GetProperty("Value",
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public,
                        null, null, Type.EmptyTypes, null);
                    if (valProp != null) return valProp.GetValue(obj) as ItemDefinition;
                }
            }
            catch { }

            return null;
        }

        private bool ApplyToItem(ItemDefinition def)
        {
            if (def == null || string.IsNullOrEmpty(def.shortname)) return false;
            if (def.stackable <= 1) return false;
            if (ShouldSkip(def.shortname)) return false;
            if (_processedItems.Contains(def.itemid)) return false;

            _processedItems.Add(def.itemid);

            if (!_vanillaStacks.ContainsKey(def.itemid))
                _vanillaStacks[def.itemid] = def.stackable;

            int vanilla = _vanillaStacks[def.itemid];
            int next = CalcStack(vanilla);

            if (next > 0 && next != def.stackable)
            {
                def.stackable = next;
                return true;
            }
            return false;
        }

        // 统一计算堆叠数：以缓存的原始值为基准，避免切换倍率时叠加；同时防止 int 溢出
        private int CalcStack(int vanilla)
        {
            if (vanilla <= 0) return 1;
            double raw = vanilla * (double)config.Multiplier;
            if (raw <= 0) return vanilla;
            if (raw >= int.MaxValue) return int.MaxValue;
            int next = (int)Math.Round(raw);
            if (next < vanilla) next = vanilla;
            return next;
        }

        private void RestoreAllVanilla()
        {
            int restored = 0;
            foreach (var kv in _vanillaStacks)
            {
                var def = SafeFindItemDefinition(kv.Key);
                if (def != null && kv.Value > 0)
                {
                    def.stackable = kv.Value;
                    restored++;
                }
            }
            if (restored > 0)
                Puts($"已恢复 {restored} 个物品的原版堆叠");
        }

        // 兼容不同版本 ItemManager.FindItemDefinition (反射兜底)
        private static ItemDefinition SafeFindItemDefinition(int itemId)
        {
            try { return ItemManager.FindItemDefinition(itemId); }
            catch { }
            try
            {
                var mi = typeof(ItemManager).GetMethod("FindItemDefinition",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    null, new Type[] { typeof(int) }, null);
                if (mi != null) return mi.Invoke(null, new object[] { itemId }) as ItemDefinition;
            }
            catch { }
            return null;
        }

        private int GetVanillaStack(ItemDefinition info)
        {
            if (info == null) return 1;
            if (_vanillaStacks.TryGetValue(info.itemid, out int cached) && cached > 0)
                return cached;
            int vanilla = info.stackable > 1 ? info.stackable : 1;
            _vanillaStacks[info.itemid] = vanilla;
            return vanilla;
        }

        #endregion

        #region 工具方法

        private bool ShouldSkip(string shortname)
        {
            return IsSkull(shortname) || IsBlacklisted(shortname);
        }

        private bool IsSkull(string shortname)
        {
            if (!config.ExcludeSkulls || string.IsNullOrEmpty(shortname)) return false;
            return shortname.IndexOf("skull", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool IsBlacklisted(string shortname)
        {
            if (string.IsNullOrEmpty(shortname) || config.Blacklist == null) return false;
            for (int i = 0; i < config.Blacklist.Count; i++)
            {
                if (string.Equals(config.Blacklist[i], shortname, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private bool HasPerm(BasePlayer player)
        {
            if (player == null) return false;
            if (player.IsAdmin || player.IsDeveloper) return true;
            if (permission.UserHasPermission(player.UserIDString, PERM_ADMIN)) return true;
            return false;
        }

        #endregion

        #region Oxide 钩子

        private object OnMaxStackable(Item item)
        {
            if (!config.Enabled || item?.info == null) return null;
            if (ShouldSkip(item.info.shortname)) return null;

            int vanilla = GetVanillaStack(item.info);
            if (vanilla <= 1) return null;

            int max = CalcStack(vanilla);
            return max > 1 ? (object)max : null;
        }

        #endregion

        #region 聊天命令

        [ChatCommand("stack")]
        private void CmdStack(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            if (args == null || args.Length == 0)
            {
                if (config.Enabled)
                    SendReply(player, $"[堆叠] 当前倍率: {config.Multiplier}x | 用法: /stack <倍率>  例: /stack 100");
                else
                    SendReply(player, "[堆叠] 插件已禁用");
                return;
            }

            if (!HasPerm(player))
            {
                SendReply(player, "[堆叠] 你没有权限执行此操作");
                return;
            }

            if (!float.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float mult) || mult <= 0f)
            {
                SendReply(player, "[堆叠] 倍率必须是大于 0 的数字，例: /stack 50");
                return;
            }

            config.Multiplier = mult;
            SaveConfig();
            ReapplyAll();

            if (config.Enabled)
                SendReply(player, $"[堆叠] 倍率已设为 {config.Multiplier}x 并已应用");
            else
                SendReply(player, $"[堆叠] 倍率已保存: {config.Multiplier}x（插件当前禁用）");
        }

        [ChatCommand("stackinfo")]
        private void CmdStackInfo(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (config.Enabled)
                SendReply(player, $"[堆叠] 倍率: {config.Multiplier}x | 已缓存: {_vanillaStacks.Count} | 排除头骨: {config.ExcludeSkulls} | 黑名单: {config.Blacklist.Count}");
            else
                SendReply(player, "[堆叠] 插件已禁用");
        }

        [ChatCommand("stackhelp")]
        private void CmdStackHelp(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            var lines = new[]
            {
                "========== 堆叠倍率 帮助 ==========",
                "/stack [倍率] - 查看或设置堆叠倍率",
                "  例: /stack 50  (设置为50倍)",
                "/stackinfo - 查看当前状态",
                "/stackhelp - 显示此帮助",
                "/help1 - 查看堆叠倍率命令",
                "=================================="
            };
            SendReply(player, string.Join("\n", lines));
        }

        [ChatCommand("help1")]
        private void CmdHelp1(BasePlayer player, string command, string[] args)
        {
            CmdStackHelp(player, command, args);
        }

        private void ReapplyAll()
        {
            // 只清理"已处理"标记，保留 _vanillaStacks 中的原版数值
            // 否则再次设置倍率时会把"已放大后的数值"当作原版值，导致倍率叠加
            _processedItems.Clear();
            TryApplyToAllItems();
        }

        #endregion

        #region 控制台命令

        [ConsoleCommand("stackmultiplier.reload")]
        private void CcmdReload(ConsoleSystem.Arg arg)
        {
            LoadConfig();
            ReapplyAll();
            arg.ReplyWith("[堆叠] 配置已重载");
        }

        [ConsoleCommand("stackmultiplier.set")]
        private void CcmdSet(ConsoleSystem.Arg arg)
        {
            string arg0 = ArgToStr(arg, 0);
            if (arg0 == null)
            {
                arg.ReplyWith("用法: stackmultiplier.set <倍率>");
                return;
            }
            if (!float.TryParse(arg0, NumberStyles.Float, CultureInfo.InvariantCulture, out float mult) || mult <= 0f)
            {
                arg.ReplyWith("倍率必须大于 0");
                return;
            }
            config.Multiplier = mult;
            SaveConfig();
            ReapplyAll();
            arg.ReplyWith($"[堆叠] 倍率已设为 {config.Multiplier}x");
        }

        [ConsoleCommand("stackmultiplier.toggle")]
        private void CcmdToggle(ConsoleSystem.Arg arg)
        {
            config.Enabled = !config.Enabled;
            SaveConfig();
            if (config.Enabled)
            {
                ReapplyAll();
                arg.ReplyWith("[堆叠] 插件已启用");
            }
            else
            {
                RestoreAllVanilla();
                arg.ReplyWith("[堆叠] 插件已禁用");
            }
        }

        [ConsoleCommand("stackmultiplier.status")]
        private void CcmdStatus(ConsoleSystem.Arg arg)
        {
            arg.ReplyWith($"[堆叠] 状态 - 启用: {(config.Enabled ? "是" : "否")} | 倍率: {config.Multiplier}x | 已缓存: {_vanillaStacks.Count} | 黑名单: {config.Blacklist.Count}");
        }

        #endregion
    }
}
