---
title: 贡献指南
description: HMOL 可接受与不接受的贡献方式（不接受任何 PR）、Bug 报告与功能建议的提交流程、文档改进、安全漏洞与侵权举报渠道。
section: contributing
order: 230
---

> ⚠️ **本项目为专有软件，保留所有权利（All Rights Reserved），并非开源软件。**
>
> 由于许可声明限制二次修改，**本项目不接受任何形式的 Pull Request**（代码 / 文档 / 构建脚本类的 PR 会被直接关闭）。
>
> 但欢迎以下**非代码修改**的贡献。

***

## 📑 目录

- [可接受的贡献类型](#可接受的贡献类型)
- [不接受的行为](#不接受的行为)
- [Bug 报告](#bug-报告)
- [功能建议](#功能建议)
- [文档改进](#文档改进)
- [安全漏洞报告](#安全漏洞报告)
- [侵权举报](#侵权举报)
- [行为准则](#行为准则)

***

## 可接受的贡献类型

| 类型            | 说明                              | 渠道                         |
| --------------- | --------------------------------- | ---------------------------- |
| 🐛 **Bug 报告** | 描述问题、复现步骤、期望行为      | GitHub Issues                |
| 💡 **功能建议** | 提出新功能想法                    | GitHub Issues                |
| 📖 **文档改进** | 指出文档 / README 的错误或不清处 | GitHub Issues                |
| 🔒 **安全研究** | 私下报告安全漏洞                  | GitHub Security Advisories   |
| ⚖️ **侵权举报** | 举报违反许可声明的行为            | GitHub Issues / 联系表单     |
| 💬 **社区支持** | 在 Issues / QQ 群帮助其他用户     | QQ 群                        |
| 🧩 **资源包 / 扩展** | 为社区提供优质的 MO 资源包，或分享主页扩展清单 | 独立分发 / QQ 群 |

***

## 不接受的行为

| ❌ 不接受                | 原因                                   |
| ------------------------ | -------------------------------------- |
| 提交 PR 修改代码         | 违反 LICENSE「三、使用限制」第 2 条    |
| 提交 PR 修改文档 / 注释  | 同上                                   |
| Fork 后修改并发布        | 违反「不得创建衍生作品 / 不得分发」    |
| 提交 PR 添加依赖         | 同上                                   |
| 提交 PR 修改构建脚本     | 同上                                   |
| 提取代码片段到其他项目   | 违反「不得复制本软件的任何部分」       |
| 上传本软件到任何分发平台 | 违反「三、使用限制」第 5 条            |

> 🚫 **所有代码修改类的 PR 会被直接关闭，不会进入审查。**

***

## Bug 报告

### 报告前的检查

1. **搜索现有 Issues**，避免重复：<https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues?q=is%3Aissue>
2. **确认是最新版本**：<https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/releases>
3. **先查故障排查**：[故障排查](/docs/troubleshooting/) 与 [错误信息与解决](/docs/error-codes/)

### 提交 Bug 报告

**路径**：[GitHub Issues → New Issue](https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues/new)

**建议附上**：

```markdown
## 环境
- HMOL 版本：v1.0.0
- Windows 版本：Windows 11 22H2 (Build 22621)
- 运行方式：直接运行 HMOL.exe / 管理员运行

## 复现步骤
1. …
2. …

## 期望行为
…

## 实际行为
（贴出界面提示的原文，例如某条中文提示）

## 日志
（「运行日志」页导出的 TXT/CSV，或 Data\Log\ 下相关片段）
```

### 提供高质量日志

1. 复现问题；
2. 打开「**运行日志**」页 → **磁盘日志**，选最新一份；
3. 点「导出 TXT」，把相关片段贴在 Issue 里（**建议脱敏**：把真实用户名、绝对路径里的个人信息去掉）。

> 只想快速定位时，也可在「运行日志」页切换到**应用日志**看当前会话的记录。

***

## 功能建议

**路径**：[GitHub Issues → New Issue](https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues/new)，说明：

- **需求描述**：希望能在……
- **动机 / 背景**：当前痛点是……
- **提议方案**：具体步骤……
- **优先级**：紧急 / 重要 / 一般

请尽量说明你用的是哪个页面 / 哪个功能，便于定位。

***

## 文档改进

### 可改进的内容

- 文档中的错误 / 拼写问题；
- 表述不清晰、与当前实现不符之处；
- 缺失的章节。

### 如何提交

由于不接受 PR，请通过 [GitHub Issues](https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues) 提交（可用 `documentation` 标签），并说明：

- 文档位置（页面路径 + 小节）；
- 当前内容（引用原文）；
- 问题（错误 / 不清楚 / 缺失）；
- 建议修改（文字描述即可）。

***

## 安全漏洞报告

> 🔒 **请勿在公开 Issues 中报告安全漏洞！**

### 报告方式

通过 GitHub **私有漏洞报告**：
<https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/security/advisories/new>

### 报告应包含

1. 漏洞类型（可选 CWE 编号）；
2. 影响范围（受影响的版本）；
3. 复现步骤；
4. 概念验证（PoC）或截图；
5. 影响评估（可选 CVSS）；
6. 已知缓解方式。

### 请注意

- 本项目**内置了启动门锁与安全自检**（调试器检测、完整性校验等）；指出「存在这些机制」本身不算漏洞；
- 请勿在报告中包含从非官方渠道获取的二进制，或要求我们去分析被修改过的发行包。

***

## 侵权举报

如果发现有人违反本项目的许可声明（例如二次修改、二次封装、二次分发、移除版权信息），可通过以下渠道举报：

1. **GitHub Issues**：<https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues>
2. **GitHub 联系表单**：<https://github.com/contact/report-abuse>
3. **DMCA Takedown**：<https://github.com/contact/dmca>

举报时请提供：通知方信息、被侵权作品（HMOL Launcher）、侵权位置（具体 URL / 包名 / 链接）、善意声明与签名。

***

## 行为准则

### 期望的行为

- ✅ 尊重他人，文明讨论；
- ✅ 提供建设性反馈；
- ✅ 接受不同意见；
- ✅ 对新手保持耐心。

### 不可接受的行为

- ❌ 人身攻击、辱骂、骚扰；
- ❌ 歧视性言论；
- ❌ 公开他人隐私信息；
- ❌ 持续刷屏 / 抬杠；
- ❌ 发布色情 / 暴力 / 违法内容；
- ❌ 任何形式的作弊宣传。

***

## 联系

| 渠道            | 链接                                                                                    |
| --------------- | --------------------------------------------------------------------------------------- |
| GitHub Issues   | <https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues>                  |
| GitHub Security | <https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/security/advisories/new> |
| Gitee 仓库      | <https://gitee.com/orangearc655743/Hello-Mental-Omega-Launcher>                         |
| QQ 群           | `1034243331`                                                                            |
