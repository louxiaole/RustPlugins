using System;
using System.Collections.Generic;
using Oxide.Core;

namespace Oxide.Plugins
{
    [Info("ExamplePlugin", "louxiaole", "1.0.0")]
    [Description("插件模板：复制本文件夹并改名后开始编写")]
    class ExamplePlugin : RustPlugin
    {
        #region 生命周期

        private void OnServerInitialized()
        {
            PrintWarning("ExamplePlugin 已加载");
        }

        private void Unload()
        {
            Puts("ExamplePlugin 已卸载");
        }

        #endregion

        #region 聊天命令

        [ChatCommand("example")]
        private void CmdExample(BasePlayer player, string command, string[] args)
        {
            if (player == null)
            {
                return;
            }

            player.ChatMessage("插件模板运行正常");
        }

        #endregion
    }
}
