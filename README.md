# Hello Mental Omega Launcher (HMOL)

> 🚫 **本项目采用「源代码可见但禁止二次修改」许可证。任何形式的修改、二次开发、二次封装、二次分发均被严格禁止。**
>
> 🚫 **This project uses a "Source-Available, No-Modification" license. Any form of modification, derivative work, repackaging, or redistribution is strictly prohibited.**

[![Version](https://img.shields.io/badge/version-2.4-blue.svg)](version.json)
[![License](https://img.shields.io/badge/license-HMOL-red.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows-lightgrey.svg)](https://www.microsoft.com/windows)

---
# HMOL Auto-Update

HMOL 启动器自动更新清单分支。

## Manifest

| 平台 | 文件 |
|------|------|
| Windows | `update/HMOL.update.json` |
| Wine | `update/HMOL.wine.update.json` |

## CDN 地址

### HMOL
```
https://cdn.jsdelivr.net/gh/OrangeArtc0915/Hello-Mental-Omega-Launcher@update/update/HMOL.update.json
```
### HMOL-wine
```
https://cdn.jsdelivr.net/gh/OrangeArtc0915/Hello-Mental-Omega-Launcher@update/update/HMOL.wine.update.json
```

## 强制刷新 CDN 缓存

如果 CDN 返回的 manifest 版本滞后，访问以下 URL 即可强制 jsDelivr 重新拉取：

### HMOL
```
https://purge.jsdelivr.net/gh/OrangeArtc0915/Hello-Mental-Omega-Launcher@update/update/HMOL.update.json
```
### HMOL-wine
```
https://purge.jsdelivr.net/gh/OrangeArtc0915/Hello-Mental-Omega-Launcher@update/update/HMOL.wine.update.json
```
浏览器打开即刷新，无需参数。最长生效时间 24 小时，一般几分钟内完成。

## 工作原理

1. GitHub Release 发布 → 触发 `Auto-Update Manifest` Actions workflow
2. Workflow 自动下载 .exe asset、计算 SHA256、更新两个 manifest 文件
3. 推送到本分支 (update)
4. 启动器通过 jsDelivr CDN / raw GitHub 拉取 manifest 比对版本

---
## ⚠️ 重要声明 / Important Disclaimers

**本启动器与 EA (Electronic Arts)、红色警戒 2 开发团队、心灵终结 (Mental Omega) 开发团队不存在任何关联、授权、赞助或背书关系。**

"Red Alert 2"、"Command & Conquer"、"Yuri's Revenge" 是 Electronic Arts Inc. 的注册商标。
"Mental Omega" 是独立的同人 mod 项目。

**This launcher is NOT affiliated with EA (Electronic Arts), the Red Alert 2 development team, or the Mental Omega development team.**

---

## 📜 关于许可证 / About the License

本项目使用 **HMOL Non-Commercial, No-Modification Source-Available License v2.2**(HMOL 非商用 · 禁止二次修改许可证)。

### ✅ 允许的行为 / Permitted

- 📖 查看、阅读、研究源代码
- 🖥️ 在个人、非商业设备上安装并使用
- 🐛 在 GitHub Issues 提交 bug 报告

### ❌ 严禁的行为 / Strictly Prohibited

| # | 行为 / Action | 说明 / Description |
|---|--------------|-------------------|
| 1 | 修改源代码 | 任何对 .py、.json、.md、.bat 等源文件的改动 |
| 2 | 修改二进制 | 反编译后修改、补丁、热更新 |
| 3 | 反向工程 | 反编译、反汇编、静态分析、动态跟踪(除法律明确允许外) |
| 4 | 创建衍生作品 | Fork 后修改、改编、翻译、汇编、演绎 |
| 5 | 代码复用 | 将代码、算法、逻辑用于其他项目 |
| 6 | 重新分发 | 上传至任何代码托管平台或分发渠道 |
| 7 | 商业使用 | 用于商业产品、服务、营利活动 |
| 8 | 安全绕过 | 绕过、破解、规避安全机制、加密、版权保护 |
| 9 | 标识移除 | 移除、隐藏、修改版权声明、商标、许可声明 |

**完整许可条款请参见 [LICENSE](LICENSE) 文件。**

**For the full license terms, see the [LICENSE](LICENSE) file.**

---

## 🌟 项目简介 / Project Overview

Hello Mental Omega Launcher (HMOL) 是一个**专为 Mental Omega 玩家打造**的启动器,提供以下功能:

- 🎮 **游戏启动** — 一键启动 Mental Omega 客户端
- 📦 **实例管理** — 创建、管理、切换多个游戏实例
- 📚 **包管理** — 安装、卸载、更新包
- 🎨 **多主题** — 6 套精心设计的主题

### 🆚 与 Mental Omega 官方启动器的关系

- ❌ **不**是 Mental Omega 官方启动器
- ❌ **不**提供 Mental Omega 盗版下载
- ✅ **仅**是一个非官方的、社区开发的辅助工具
- ✅ 旨在改善 Mental Omega 玩家社区的体验

---


## 📥 下载与安装 / Download and Install

### 系统要求 / System Requirements

| 项目 / Item | 要求 / Requirement |
|------------|-------------------|
| 操作系统 | Windows 10 / 11 (x64) |
| 磁盘空间 | 至少 200 MB 可用空间 |

### 开启奇妙之旅~

1. 访问 [Releases 页面](https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/releases)
2. 下载最新版本
3. 运行安装程序,按照向导完成安装
4. 双击桌面图标启动 HMOL

> ⚠️ **请仅从官方 GitHub Releases 和 微软商店 下载!**任何其他渠道提供的二进制均可能
> 已被修改,违反本许可证,可能包含恶意代码。


## 📋 使用协议 / EULA

使用本软件前,**必须**阅读并同意 [EULA.md](EULA.md) 中的条款。

协议核心条款:

- §3 **二次修改禁令** — 严禁任何形式的二次修改
- §4 许可授予 — 仅限个人非商业用途
- §7 责任限制 — "原样"提供,无担保
- §9 禁止行为 — 不得用于非法活动

---

## 🤝 贡献 / Contributing

> 🚫 **本项目不接受任何形式的代码贡献、PR、Issue 中的代码修改。**

由于许可证禁止二次修改,本项目**不接受 Pull Request**。

但欢迎以下类型的贡献(无需修改代码):

- 🐛 **Bug 报告** — 在 [Issues](https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues) 中描述
- 💡 **功能建议** — 在 Issues 中提出

**请不要**提交包含代码修改的 Pull Request(会被直接关闭)。


## 📞 联系方式 / Contact

| 渠道            | 链接 / Link                                                                                                    |
| ------------- | ------------------------------------------------------------------------------------------------------------ |
| GitHub 仓库     | <https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher>                                              |
| GitHub Issues | <https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues>                                       |
| 漏洞报告          | [Security Advisories](https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/security/advisories/new) |
| QQ 群          | 1034243331                                                                                                   |

***

## 📜 许可证 / License

本项目采用 **HMOL Non-Commercial, No-Modification Source-Available License v2.2**。

🚫 **严禁任何形式的二次修改、二次开发、二次封装、二次分发。**

完整条款请参见 [LICENSE](LICENSE) 文件。

---

## 🙏 致谢 / Acknowledgments

- **Command & Conquer: Red Alert 2 - Yuri's Revenge** — © 2001-2026 Electronic Arts Inc.
- **Mental Omega** — 独立同人 mod 项目
- **HMOL 贡献者** — 感谢所有提交 bug 报告和功能建议的社区成员

---

**© 2026 HMOL Contributors. All Rights Reserved.**

**Made with ❤️ for the Mental Omega community.**


[def]: SECURITY.md
