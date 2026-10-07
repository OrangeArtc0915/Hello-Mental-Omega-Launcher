# Hello Mental Omega Launcher (HMOL)

> 本项目以 **PolyForm Noncommercial License 1.0.0** 授权：免费用于一切**非商业**用途，禁止商用；属于「源码可见」（source-available），**不是**开源许可证。详见 [LICENSE](LICENSE)。

[![Version](https://img.shields.io/badge/version-1.0.0-blue.svg)](LICENSE)
[![License](https://img.shields.io/badge/license-PolyForm%20Noncommercial%201.0.0-orange.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows-lightgrey.svg)](https://www.microsoft.com/windows)

本仓库包含 **HMOL 启动器（C# / .NET 8 版）的源码**，以及它的**官网与文档站**（`Web/`）。

---

## 📖 项目简介

**HMOL（Hello Mental Omega Launcher）** 是一款面向《心灵终结》（Mental Omega）玩家的第三方桌面启动器，使用 **C# / .NET 8 / WPF** 编写，以**自包含单文件 `HMOL.exe`** 分发。

- 🎮 **一键启动** — 按固定顺序在实例目录中查找游戏主程序并启动，并把游戏输出捕获到「运行日志」
- 📚 **多实例管理** — 同时管理多个游戏实例，每个实例独立记录已安装的包与备份
- 🧩 **四类资源包** — INI / 地图 / 任务 / 插件，支持 `.zip` / `.7z` / `.rar` 导入与精确卸载
- 💾 **备份与还原** — 用户备份 + 固定位置的「原版游戏」备份，支持精确卸载与全量恢复
- 🌐 **联机组网** — 内置 **EasyTier**（三层，推荐）与 **n2n**（二层，需 TAP 驱动），含联机大厅、房间聊天、文件传输与游戏内 HUD
- 🎨 **主题与外观** — 浅色 / 深色 / 跟随系统，多套强调色；可自定义主页背景与背景音乐
- 🧩 **声明式扩展** — 用一份 JSON 清单在主页加一张小组件卡片；**不执行代码、不读写扩展目录以外的文件**
- ⏰ **开机自启与系统托盘**、🛡️ **启动门锁与安全自检**

### 🆚 与 Mental Omega 官方启动器的关系

- ❌ **不**是 Mental Omega 官方启动器
- ❌ **不**提供游戏本体下载
- ✅ 仅是一个非官方的、社区开发的辅助工具

**本启动器与 EA (Electronic Arts)、红色警戒 2 开发团队、心灵终结 (Mental Omega) 开发团队不存在任何关联、授权、赞助或背书关系。**

---

## 📦 仓库结构

```
HMOL.sln                 # 解决方案：src/HMOL.Core + src/HMOL.App
build.bat                # 发行构建脚本（自包含单文件 + 打包 zip）
一键编译并运行.bat         # 本地开发：编译 Debug 并启动
src/                     # 启动器源码（C# / .NET 8 / WPF）
runtime/                 # 随包分发的组网组件（EasyTier / n2n / TAP / WinIPBroadcast）
docs/                    # 扩展模块与第三方组件说明
Web/                     # 官网（Astro 站点，文档就是站内页面）
  └─ src/                # 站点源码（页面 / 内容 / 文档）
```

- 官网线上地址：<https://orangeartc0915.github.io/Hello-Mental-Omega-Launcher/>
- 文档入口：<https://orangeartc0915.github.io/Hello-Mental-Omega-Launcher/docs/>

---

## 🛠️ 构建发行版

需要 **.NET 8 SDK**（脚本 `ensure-dotnet-sdk.bat` 会检查并在缺失时提示处理）。

```bat
build.bat                 :: 完整构建（含混淆）
build.bat --skip-obfuscate  :: 跳过混淆，出一个未混淆的调试版本
```

构建完成后产物在 `publish\` 下：

| 产物 | 说明 |
| --- | --- |
| `publish\HMOL.exe` | **自包含单文件**程序（`--self-contained true` + `PublishSingleFile`，内含 .NET 8 运行时） |
| `publish\HMOL-v<版本>-win-x64.zip` | 发行压缩包（exe + `README.md` / `LICENSE` / `NOTICE` + `runtime\` 组网组件） |

> 版本号取自 `src/HMOL.Core/App/AppInfo.cs`，发行包名形如 `HMOL-v1.0.0-win-x64.zip`。
> 组网组件（EasyTier / n2n / TAP 驱动）**不内嵌进 exe**，随 `runtime\` 目录分发，因此发布时必须完整打包该目录。

---

## 💻 本地开发

```bat
:: 方式一：一键编译并运行（Debug）
一键编译并运行.bat

:: 方式二：手动构建
dotnet build HMOL.sln
dotnet build src\HMOL.App\HMOL.App.csproj -c Debug
```

编译产物：`src\HMOL.App\bin\Debug\net8.0-windows\HMOL.exe`。

### 官网

官网是 **Astro** 站点，文档就是站点内的页面：`src/content/docs/**` 下的 Markdown 由 `src/pages/docs/[...slug].astro` 渲染到 `/docs/<slug>/`，**没有独立的文档子站**。

```bat
:: 构建整站（在 Web/ 目录下）
corepack pnpm install
corepack pnpm build        :: = astro build + pagefind
```

> 若本机 `pnpm` 不可用，请统一用 `corepack pnpm ...` 调用。

---

## 📥 下载

请**仅**从官方渠道下载，其它渠道的二进制可能已被修改，同时也违反本项目的许可：

| 渠道 | 链接 |
| --- | --- |
| GitHub Releases | <https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/releases> |
| Gitee Releases | <https://gitee.com/orangearc655743/Hello-Mental-Omega-Launcher/releases> |
| QQ 群 | `1034243331` |

---

## 📚 文档与链接

| 类型 | 链接 |
| --- | --- |
| 使用文档 | <https://orangeartc0915.github.io/Hello-Mental-Omega-Launcher/docs/> |
| 最终用户声明 | [EULA.md](EULA.md) |
| GitHub 仓库 | <https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher> |
| Gitee 仓库 | <https://gitee.com/orangearc655743/Hello-Mental-Omega-Launcher> |
| 问题反馈 | <https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues> |
| 安全漏洞（私有报告） | <https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/security/advisories/new> |
| QQ 群 | `1034243331`（一键加群：<https://qm.qq.com/q/ia8Zv2AtEY>） |

---

## 🤝 贡献

> 🚫 **由于许可声明限制二次修改，本项目不接受任何形式的 Pull Request**（代码 / 文档 / 构建脚本类的 PR 会被直接关闭）。

欢迎以下**非代码修改**的贡献：

- 🐛 **Bug 报告** — 在 [Issues](https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues) 中描述问题与复现步骤（建议附上「运行日志」页导出的日志）
- 💡 **功能建议** — 在 Issues 中提出
- 📖 **文档改进** — 指出文档的错误或不清楚之处
- 💬 **社区支持** — 在 Issues / QQ 群帮助其他用户

---

## 📜 许可 / License

本项目以 **PolyForm Noncommercial License 1.0.0** 授权：免费用于一切**非商业**用途，**禁止任何商业用途**。它**不是** OSI 认可的开源许可证，属于「源码可见」（source-available）。

- ✅ **允许**：免费下载、安装、运行本软件用于任何非商业目的；为非商业目的复制、修改、创作新作品与分发——但分发时必须把许可条款（或官方链接）与 `Required Notice` 版权声明原样带给收到的人。
- ❌ **禁止**：任何商业用途（出售、收费分发、付费下载、捆绑销售，或用于你经营的业务）；去掉或篡改许可与版权声明后再分发；再许可（sublicense）或把权利转让给他人；宣称本软件由你开发，或暗示与作者存在关联、获得其认可。

完整条款请参见 [LICENSE](LICENSE) 文件。

---

## 🙏 致谢

- **Command & Conquer: Red Alert 2 - Yuri's Revenge** — © Electronic Arts Inc.
- **Mental Omega** — 独立同人 mod 项目
- **HMOL Contributors** — 感谢所有提交 bug 报告与功能建议的社区成员

---

**© 2026 mmm. 以 PolyForm Noncommercial License 1.0.0 授权。**

**Made with ❤️ for the Mental Omega community.**
