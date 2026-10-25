# 插件总仓库框架

这里用于集中存放你自己制作的 Rust 插件，并通过 GitHub Releases 让「Rust Solo Tools → 插件下载」页面自动识别版本、自动更新。

工具端不需要任何服务器，只读取这个仓库的 **Releases / Tags**。

---

## 一、目录约定

```
PluginRepository/
├── plugins/                     # 插件源码，一个插件一个文件夹
│   ├── KeycardDetector/         # 卡片检测器
│   ├── KillFeed/                # 击杀提示
│   ├── ResourceGatherMultiplier/# 采集 / 战利品倍率
│   ├── StackMultiplier/         # 物品堆叠倍率
│   └── TeleportSystem/          # 传送系统
├── template/                    # 插件模板（复制后改名）
│   └── ExamplePlugin/
│       └── ExamplePlugin.cs
├── dist/                        # 打包输出的 zip（不提交 git）
├── 打包插件.bat                 # 一键生成待上传的 zip
├── .gitignore
└── README.md
```

**关键规则：tag 前缀必须使用插件文件名（不含 .cs），不要用带空格的显示名。**

例如插件文件 `TeleportSystem.cs`，tag 应为 `TeleportSystem-v1.2.0`。

---

## 二、当前仓库内插件

| 插件文件（tag 前缀） | 游戏内显示名 | 当前版本 | 建议 tag |
|---|---|---|---|
| KeycardDetector | 卡片检测器 | 3.2.0 | `KeycardDetector-v3.2.0` |
| KillFeed | 击杀提示 | 3.3.0 | `KillFeed-v3.3.0` |
| ResourceGatherMultiplier | Gather Multiplier | 4.5.0 | `ResourceGatherMultiplier-v4.5.0` |
| StackMultiplier | Stack Multiplier | 5.2.0 | `StackMultiplier-v5.2.0` |
| TeleportSystem | 传送系统 | 3.9.0 | `TeleportSystem-v3.9.0` |

> 版本号以 `.cs` 头部 `[Info("名称", "作者", "版本")]` 为准。
> 更新插件时先改 `[Info]` 里的版本号，再用相同版本号打包并建 tag，工具才能正确判断「可更新」。

---

## 三、发布 / 更新一个插件

1. 把插件放进 `plugins/<插件名>/<插件名>.cs`；
2. 双击 `打包插件.bat`，按提示输入插件名和版本号（如 `1.2.0`）；
   - 产物：`dist/<插件名>-v1.2.0.zip`
   - 脚本会打印应使用的 tag：`<插件名>-v1.2.0`
3. 打开 GitHub 仓库 → **Releases → Draft a new release**；
4. **Choose a tag** 填入 `TeleportSystem-v1.2.0` → 选择「Create new tag on publish」；
5. 标题随意（如 `TeleportSystem 1.2.0`），把 `dist` 里的 zip 上传为附件；
6. 点击 **Publish release**。

完成后，工具端进入「插件下载」页会自动出现该插件；已安装旧版的用户会看到「可更新」。

> 更新插件时只需发布一个版本号更高的新 tag，工具会自动取每个插件的最高版本。

---

## 四、工具端行为说明

| 项 | 说明 |
|---|---|
| 版本识别 | tag 形如 `插件名-v1.2.0`、`插件名_v1.2.0` 均可 |
| 取版本 | 同一插件有多个 tag 时只取最高版本 |
| 下载优先级 | `.zip` > `.dll` > `.cs`；没有附件时回退源码包并提取 `.cs/.dll` |
| 安装位置 | `<Rust服务端>/Carbon/Plugins` |
| 禁用插件 | 存在同名 `.disabled` 文件时会自动解除禁用 |
| 版本比对 | 安装记录写入 `config/settings.json` 的 `my_plugins.installed` |
| 检查缓存 | 5 分钟，避免触发 GitHub API 频率限制 |

---

## 五、注意事项

- 仓库必须是 **公开** 的；私有仓库需要 Token，当前版本不支持。
- 不要用 `v1.0.0` 这种纯版本号 tag，否则工具无法判断是哪个插件。
- zip 内可以有子目录，工具会递归提取所有 `.cs` / `.dll`。
- 插件显示名（`[Info("...")]`）可以和文件名不同，但 tag 必须用文件名。
- 建议版本号遵循 `主版本.次版本.修订号`，便于正确比较更新。
