using System;
using System.Collections.Generic;
using System.Globalization;
using Oxide.Core;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("KeycardDetector 卡片检测器", "Custom", "3.3.1")]
    [Description("检测附近的绿卡、蓝卡、红卡，并显示方向和位置 (适配最新Rust, 改用serverEntities避免Unity FindObjects废弃问题)")]
    class KeycardDetector : RustPlugin
    {
        #region 常量

        private const string PERM_ADMIN = "carddetector.admin";

        #endregion

        #region 配置数据

        private Configuration config;

        private class Configuration
        {
            public PluginSettings Settings = new PluginSettings();
        }

        private class PluginSettings
        {
            public float DetectionRange = 50.0f;
            public float CheckInterval = 10.0f;
            public bool CheckDroppedItems = true;
            public bool CheckContainers = true;
            public float ContainerCheckRange = 15.0f;
            public string ChatPrefix = "[卡片检测]";
            public string ChatPrefixColor = "#00FF00";
            public bool EnableDebugLog = false;
            public bool AutoStartOnConnect = true;
            public int MaxDroppedItemsCheck = 100;
            public int MaxContainersCheck = 50;
            public int MaxCardsPerContainer = 5;
        }

        #endregion

        #region 初始化

        private Dictionary<ulong, Timer> _playerTimers = new Dictionary<ulong, Timer>();
        private Dictionary<string, string> _cardNames = new Dictionary<string, string>();
        private HashSet<string> _cardShortnames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private void Init()
        {
            MigrateLegacyConfig();
            LoadConfigData();
            InitializeCardData();
            permission.RegisterPermission(PERM_ADMIN, this);
        }

        // 插件名从纯中文改为"英文 中文"后，自动沿用旧配置，避免配置被重置
        private void MigrateLegacyConfig()
        {
            try
            {
                string dir = Interface.Oxide.ConfigDirectory;
                if (string.IsNullOrEmpty(dir)) return;

                string oldPath = System.IO.Path.Combine(dir, "卡片检测器.json");
                string newPath = System.IO.Path.Combine(dir, "KeycardDetector 卡片检测器.json");
                if (System.IO.File.Exists(oldPath) && !System.IO.File.Exists(newPath))
                    System.IO.File.Copy(oldPath, newPath);
            }
            catch { }
        }

        private void OnServerInitialized()
        {
            Puts("========================================");
            Puts("KeycardDetector 卡片检测器 v3.3.1 已加载！(适配最新Rust)");
            Puts($"检测范围: {config.Settings.DetectionRange} 米");
            Puts($"检测间隔: {config.Settings.CheckInterval} 秒");
            Puts($"默认开启: {(config.Settings.AutoStartOnConnect ? "是" : "否")}");
            Puts("可用命令: /carddetect, /cd");
            Puts("管理员命令: /carddetect range <值>, /carddetect reload");
            Puts("========================================");

            if (config.Settings.AutoStartOnConnect)
            {
                timer.Once(2.0f, () =>
                {
                    var players = BasePlayer.activePlayerList;
                    if (players == null) return;
                    for (int i = 0; i < players.Count; i++)
                    {
                        var player = players[i];
                        if (player != null && player.IsConnected)
                        {
                            StartDetection(player);
                        }
                    }
                });
            }
        }

        private void Unload()
        {
            foreach (var timer in _playerTimers.Values)
            {
                timer?.Destroy();
            }
            _playerTimers.Clear();
        }

        private void InitializeCardData()
        {
            _cardNames["keycard_green"] = "绿卡";
            _cardNames["keycard_blue"] = "蓝卡";
            _cardNames["keycard_red"] = "红卡";

            _cardShortnames.Add("keycard_green");
            _cardShortnames.Add("keycard_blue");
            _cardShortnames.Add("keycard_red");
        }

        // 最新Rust 已全面弃用 UnityEngine.Object.FindObjectsOfType。
        // 直接遍历服务器实体表，既避开 Unity API 变更，也避免全场景扫描带来的性能开销。
        private static List<T> FindNearbyEntities<T>(Vector3 center, float range, int maxCount) where T : BaseEntity
        {
            var result = new List<T>();
            if (maxCount <= 0) return result;

            float rangeSqr = range * range;
            try
            {
                foreach (var networkable in BaseNetworkable.serverEntities)
                {
                    if (result.Count >= maxCount) break;

                    var entity = networkable as T;
                    if (entity == null) continue;

                    Vector3 pos;
                    try { pos = entity.transform.position; }
                    catch { continue; }

                    if ((pos - center).sqrMagnitude > rangeSqr) continue;
                    result.Add(entity);
                }
            }
            catch { }
            return result;
        }

        // 兼容 StringView (最新Rust ConsoleSystem.Arg.Args 是 StringView[])
        private static string ArgToStr(ConsoleSystem.Arg arg, int index)
        {
            if (arg == null || arg.Args == null || index < 0 || index >= arg.Args.Length) return null;
            try { return arg.Args[index].ToString(); }
            catch { return null; }
        }

        #endregion

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
                Settings = new PluginSettings
                {
                    DetectionRange = 50.0f,
                    CheckInterval = 10.0f,
                    CheckDroppedItems = true,
                    CheckContainers = true,
                    ContainerCheckRange = 15.0f,
                    ChatPrefix = "[卡片检测]",
                    ChatPrefixColor = "#00FF00",
                    EnableDebugLog = false,
                    AutoStartOnConnect = true,
                    MaxDroppedItemsCheck = 100,
                    MaxContainersCheck = 50,
                    MaxCardsPerContainer = 5
                }
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
                if (config.Settings.MaxCardsPerContainer <= 0)
                    config.Settings.MaxCardsPerContainer = 5;
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

        #region 命令处理

        [ChatCommand("carddetect")]
        private void CardDetectCommand(BasePlayer player, string command, string[] args)
        {
            HandleCardDetectCommand(player, args);
        }

        [ChatCommand("cd")]
        private void CardDetectCommandShort(BasePlayer player, string command, string[] args)
        {
            HandleCardDetectCommand(player, args);
        }

        [ChatCommand("cardtest")]
        private void CardTestCommand(BasePlayer player, string command, string[] args)
        {
            HandleCardTestCommand(player);
        }

        [ChatCommand("cdtest")]
        private void CardTestCommandShort(BasePlayer player, string command, string[] args)
        {
            HandleCardTestCommand(player);
        }

        private void HandleCardTestCommand(BasePlayer player)
        {
            if (player == null) return;

            var testCards = new List<CardInfo>();
            var playerPos = player.transform.position;

            var greenCardPos = playerPos + new Vector3(17.7f, 0f, 17.7f);
            testCards.Add(new CardInfo
            {
                CardName = "绿卡",
                Position = greenCardPos,
                Distance = 25.0f,
                Direction = "东北",
                Type = "掉落物品"
            });

            var blueCardPos = playerPos + new Vector3(0f, 0f, -15f);
            testCards.Add(new CardInfo
            {
                CardName = "蓝卡",
                Position = blueCardPos,
                Distance = 15.0f,
                Direction = "南",
                Type = "容器中"
            });

            var redCardPos = playerPos + new Vector3(-35f, 0f, 0f);
            testCards.Add(new CardInfo
            {
                CardName = "红卡",
                Position = redCardPos,
                Distance = 35.0f,
                Direction = "西",
                Type = "掉落物品"
            });

            SendReply(player, $"{config.Settings.ChatPrefixColor}{config.Settings.ChatPrefix}</color> <color=#FFFF00>[测试模式]</color> 模拟检测结果：");
            DisplayCardInfo(player, testCards);
        }

        private void HandleCardDetectCommand(BasePlayer player, string[] args)
        {
            if (player == null) return;

            if (args == null || args.Length == 0)
            {
                StartDetection(player);
                SendReply(player, $"已开启卡片检测 (范围: {config.Settings.DetectionRange} 米)");
                return;
            }

            switch (args[0].ToLower())
            {
                case "off":
                    StopDetection(player);
                    SendReply(player, "已关闭卡片检测");
                    break;

                case "range":
                    if (!HasAdminPerm(player))
                    {
                        SendReply(player, "你没有权限使用此命令！");
                        return;
                    }
                    if (args.Length < 2 || !float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float range) || range <= 0)
                    {
                        SendReply(player, $"用法: /carddetect range <数值>  当前: {config.Settings.DetectionRange}");
                        return;
                    }
                    config.Settings.DetectionRange = Mathf.Clamp(range, 5f, 500f);
                    SaveConfig();
                    SendReply(player, $"检测范围已设为 {config.Settings.DetectionRange} 米");
                    break;

                case "interval":
                    if (!HasAdminPerm(player))
                    {
                        SendReply(player, "你没有权限使用此命令！");
                        return;
                    }
                    if (args.Length < 2 || !float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float interval) || interval < 1f)
                    {
                        SendReply(player, $"用法: /carddetect interval <秒数>  当前: {config.Settings.CheckInterval}");
                        return;
                    }
                    config.Settings.CheckInterval = Mathf.Clamp(interval, 1f, 120f);
                    SaveConfig();
                    SendReply(player, $"检测间隔已设为 {config.Settings.CheckInterval} 秒");
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

                case "status":
                    SendReply(player, $"卡片检测状态 - 范围: {config.Settings.DetectionRange}m | 间隔: {config.Settings.CheckInterval}s | 掉落物: {(config.Settings.CheckDroppedItems ? "开" : "关")} | 容器: {(config.Settings.CheckContainers ? "开" : "关")}");
                    break;

                case "help":
                    ShowHelp(player);
                    break;

                default:
                    StartDetection(player);
                    SendReply(player, $"已开启卡片检测 (范围: {config.Settings.DetectionRange} 米)");
                    break;
            }
        }

        private void ShowHelp(BasePlayer player)
        {
            var lines = new[]
            {
                "========== 卡片检测器 帮助 ==========",
                "/carddetect - 开启检测",
                "/carddetect off - 关闭检测",
                "/carddetect status - 查看当前设置",
                "/carddetect range <值> - 设置检测范围 (管理员)",
                "/carddetect interval <值> - 设置检测间隔 (管理员)",
                "/carddetect reload - 重载配置 (管理员)",
                "/help3 - 查看卡片检测器命令",
                "===================================="
            };
            SendReply(player, string.Join("\n", lines));
        }

        [ChatCommand("help3")]
        private void CmdHelp3(BasePlayer player, string command, string[] args)
        {
            ShowHelp(player);
        }

        private bool HasAdminPerm(BasePlayer player)
        {
            if (player == null) return false;
            if (player.IsAdmin || player.IsDeveloper) return true;
            return permission.UserHasPermission(player.UserIDString, PERM_ADMIN);
        }

        #endregion

        #region 检测功能

        private void StartDetection(BasePlayer player)
        {
            if (player == null) return;

            StopDetection(player);
            CheckForCards(player);

            var playerTimer = timer.Repeat(config.Settings.CheckInterval, 0, () =>
            {
                if (player == null || !player.IsConnected)
                {
                    StopDetection(player);
                    return;
                }
                CheckForCards(player);
            });

            _playerTimers[player.userID] = playerTimer;
        }

        private void StopDetection(BasePlayer player)
        {
            if (player == null) return;

            if (_playerTimers.TryGetValue(player.userID, out var t))
            {
                t?.Destroy();
                _playerTimers.Remove(player.userID);
            }
        }

        private void CheckForCards(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;

            var playerPos = player.transform.position;
            var foundCards = new List<CardInfo>();

            if (config.Settings.CheckDroppedItems)
            {
                CheckDroppedCards(playerPos, foundCards);
            }

            if (config.Settings.CheckContainers)
            {
                CheckContainerCards(playerPos, foundCards);
            }

            if (foundCards.Count > 0)
            {
                SortCards(foundCards);
                DisplayCardInfo(player, foundCards);
            }
        }

        private void SortCards(List<CardInfo> cards)
        {
            cards.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        }

        private void CheckDroppedCards(Vector3 playerPos, List<CardInfo> foundCards)
        {
            var droppedItems = FindNearbyEntities<DroppedItem>(
                playerPos, config.Settings.DetectionRange, config.Settings.MaxDroppedItemsCheck);
            if (droppedItems.Count == 0) return;

            for (int i = 0; i < droppedItems.Count; i++)
            {
                var droppedItem = droppedItems[i];
                if (droppedItem == null || droppedItem.item == null || droppedItem.item.info == null) continue;

                var item = droppedItem.item;
                var shortname = item.info.shortname;
                if (!_cardShortnames.Contains(shortname)) continue;

                var itemPos = droppedItem.transform.position;
                var distance = (itemPos - playerPos).magnitude;
                var direction = GetDirection(playerPos, itemPos);

                foundCards.Add(new CardInfo
                {
                    CardName = _cardNames.ContainsKey(shortname) ? _cardNames[shortname] : shortname,
                    Position = itemPos,
                    Distance = distance,
                    Direction = direction,
                    Type = "掉落物品"
                });
            }
        }

        private void CheckContainerCards(Vector3 playerPos, List<CardInfo> foundCards)
        {
            var containers = FindNearbyEntities<LootContainer>(
                playerPos, config.Settings.ContainerCheckRange, config.Settings.MaxContainersCheck);
            if (containers.Count == 0) return;

            for (int i = 0; i < containers.Count; i++)
            {
                var container = containers[i];
                if (container == null || container.inventory == null) continue;

                var containerPos = container.transform.position;
                var distance = (containerPos - playerPos).magnitude;
                var direction = GetDirection(playerPos, containerPos);

                var items = container.inventory.itemList;
                if (items == null || items.Count == 0) continue;

                int cardsFound = 0;
                int maxCards = config.Settings.MaxCardsPerContainer;
                int itemCheckCount = 0;
                int maxItemCheck = 30;

                for (int j = 0; j < items.Count && itemCheckCount < maxItemCheck && cardsFound < maxCards; j++)
                {
                    var item = items[j];
                    itemCheckCount++;

                    if (item == null || item.info == null) continue;

                    var shortname = item.info.shortname;
                    if (!_cardShortnames.Contains(shortname)) continue;

                    foundCards.Add(new CardInfo
                    {
                        CardName = _cardNames.ContainsKey(shortname) ? _cardNames[shortname] : shortname,
                        Position = containerPos,
                        Distance = distance,
                        Direction = direction,
                        Type = "容器中"
                    });

                    cardsFound++;
                }
            }
        }

        private string GetDirection(Vector3 from, Vector3 to)
        {
            var direction = (to - from).normalized;
            var angle = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;

            if (angle < 0) angle += 360;

            if (angle >= 337.5 || angle < 22.5) return "北";
            if (angle >= 22.5 && angle < 67.5) return "东北";
            if (angle >= 67.5 && angle < 112.5) return "东";
            if (angle >= 112.5 && angle < 157.5) return "东南";
            if (angle >= 157.5 && angle < 202.5) return "南";
            if (angle >= 202.5 && angle < 247.5) return "西南";
            if (angle >= 247.5 && angle < 292.5) return "西";
            if (angle >= 292.5 && angle < 337.5) return "西北";

            return "未知";
        }

        private void DisplayCardInfo(BasePlayer player, List<CardInfo> cards)
        {
            if (player == null || cards == null || cards.Count == 0) return;

            var message = $"{config.Settings.ChatPrefixColor}{config.Settings.ChatPrefix}</color> 检测到 {cards.Count} 张卡片：\n";

            for (int i = 0; i < cards.Count; i++)
            {
                var card = cards[i];
                message += $"  • {card.CardName} ({card.Type}) - {card.Direction}方向，距离 {card.Distance:F1} 米\n";
                message += $"    坐标: ({card.Position.x:F1}, {card.Position.y:F1}, {card.Position.z:F1})\n";
            }

            SendReply(player, message.TrimEnd());
        }

        private class CardInfo
        {
            public string CardName;
            public Vector3 Position;
            public float Distance;
            public string Direction;
            public string Type;
        }

        #endregion

        #region 玩家事件

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;

            timer.Once(2.5f, () =>
            {
                if (player == null || !player.IsConnected) return;
                SendReply(player, $"{config.Settings.ChatPrefixColor}{config.Settings.ChatPrefix}</color> 命令列表:");
                SendReply(player, "  /carddetect 或 /cd - 开启/关闭卡片检测");
                SendReply(player, "  /carddetect status - 查看状态");
                SendReply(player, "  /carddetect help - 查看帮助");
                SendReply(player, "  /help3 - 查看卡片检测器命令");
            });

            if (config.Settings.AutoStartOnConnect)
            {
                timer.Once(1.0f, () =>
                {
                    if (player != null && player.IsConnected)
                    {
                        StartDetection(player);
                    }
                });
            }
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player != null)
            {
                StopDetection(player);
            }
        }

        #endregion

        #region 控制台命令

        [ConsoleCommand("carddetector.reload")]
        private void CcmdReload(ConsoleSystem.Arg arg)
        {
            LoadConfigData();
            arg.ReplyWith("[卡片检测] 配置已重载");
        }

        [ConsoleCommand("carddetector.range")]
        private void CcmdRange(ConsoleSystem.Arg arg)
        {
            string arg0 = ArgToStr(arg, 0);
            if (arg0 == null)
            {
                arg.ReplyWith($"用法: carddetector.range <数值>  当前: {config.Settings.DetectionRange}");
                return;
            }
            if (!float.TryParse(arg0, NumberStyles.Float, CultureInfo.InvariantCulture, out float range) || range <= 0)
            {
                arg.ReplyWith("数值必须大于 0");
                return;
            }
            config.Settings.DetectionRange = Mathf.Clamp(range, 5f, 500f);
            SaveConfig();
            arg.ReplyWith($"[卡片检测] 范围已设为 {config.Settings.DetectionRange} 米");
        }

        [ConsoleCommand("carddetector.interval")]
        private void CcmdInterval(ConsoleSystem.Arg arg)
        {
            string arg0 = ArgToStr(arg, 0);
            if (arg0 == null)
            {
                arg.ReplyWith($"用法: carddetector.interval <秒数>  当前: {config.Settings.CheckInterval}");
                return;
            }
            if (!float.TryParse(arg0, NumberStyles.Float, CultureInfo.InvariantCulture, out float interval) || interval < 1f)
            {
                arg.ReplyWith("间隔必须大于等于 1 秒");
                return;
            }
            config.Settings.CheckInterval = Mathf.Clamp(interval, 1f, 120f);
            SaveConfig();
            arg.ReplyWith($"[卡片检测] 间隔已设为 {config.Settings.CheckInterval} 秒");
        }

        [ConsoleCommand("carddetector.status")]
        private void CcmdStatus(ConsoleSystem.Arg arg)
        {
            arg.ReplyWith($"[卡片检测] 状态 - 范围: {config.Settings.DetectionRange}m | 间隔: {config.Settings.CheckInterval}s | 掉落物: {(config.Settings.CheckDroppedItems ? "开" : "关")} | 容器: {(config.Settings.CheckContainers ? "开" : "关")} | 活跃玩家: {_playerTimers.Count}");
        }

        #endregion

        #region 辅助方法

        private void LogDebug(string message)
        {
            if (config.Settings.EnableDebugLog)
            {
                Puts($"[调试] {message}");
            }
        }

        #endregion
    }
}
