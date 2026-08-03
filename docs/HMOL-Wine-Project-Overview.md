# 🏗️ 项目概述 / Project Overview

> 了解 HMOL 启动器的设计目标、技术架构、模块组成和版本历史。

***

## 📌 项目定位 / Mission

**Hello Mental Omega Launcher (HMOL)** 是一个**专为 Mental Omega 玩家打造**的桌面启动器，目标是：

- ✅ 提供**比官方启动器更丰富**的实例/插件/备份管理能力
- ✅ 集成**微软账号登录**、**OneDrive 资源共享**、**QQ 喊话**等社区功能
- ✅ 严守\*\*「源代码可见但禁止二次修改」许可证\*\*，保护项目完整性
- ✅ 在**安全**与**易用性**之间取得平衡（AES-256-GCM + tkinter 原生 UI）

***

***

## 🎯 设计原则 / Design Principles

1. **最小化外部依赖** — 仅用 tkinter + 标准库 + 必要第三方包
2. **不联网不收集** — 默认离线；微软/QQ/OneDrive 均为用户主动启用
3. **机器绑定** — 加密文件无法跨机器解密，防止凭据泄漏
4. **可逆操作** — 所有破坏性操作（删除/卸载）均需二次确认
5. **详细日志** — 所有错误都有 `ERROR_<日期>.log` 可供反馈
6. **防误触** — 拖拽阈值、卸载警告、覆盖确认

***

## 🔗 相关资源 / Related Resources

- 📜 [许可证](HMOL-Wine-Security-and-License) — 完整使用条款
- 🔒 [安全策略](HMOL-Wine-Security-and-License#安全特性) — 加密与防护细节
- 🐛 [错误码手册](HMOL-Wine-Error-Codes) — 149+ 错误码速查
- 🤝 [贡献指南](HMOL-Wine-Contributing) — 反馈与建议

***

**© 2026 HMOL Contributors. All Rights Reserved.**
