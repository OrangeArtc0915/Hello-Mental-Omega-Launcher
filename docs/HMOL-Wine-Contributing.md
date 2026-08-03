# 🤝 贡献指南 / Contributing

> 欢迎社区参与！本文档说明**可以**和**不可以**的贡献方式。

***

## 🚫 为什么本项目不接受代码贡献？

本项目使用 **HMOL Non-Commercial, No-Modification Source-Available License v2.2**，核心条款 §3 明确规定：

> 未经许可方事先书面许可,用户严禁对本软件进行任何形式的二次修改：
>
> 1. 修改源代码
> 2. 修改二进制
> 3. 反向工程
> 4. 创建衍生作品
> 5. 代码复用
> 6. 重新分发
> 7. 商业使用
> 8. 安全绕过
> 9. 标识移除

因此，**任何形式的 Pull Request（包含代码修改）将被直接关闭**，不接受合并。

***

## ✅ 欢迎的贡献类型

虽然不接受代码修改，但欢迎以下类型的贡献（**无需修改代码**）：

### 🐛 Bug 报告

在 [GitHub Issues](https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues) 提交。

**好的 Bug 报告应包含：**

- 清晰的标题和描述
- **复现步骤**（从打开程序到 bug 出现的完整步骤）
- **期望行为** vs **实际行为**
- 截图或录屏（如果适用）
- **系统信息**（Windows 版本、HMOL 版本）
- **错误日志**（`logs/HMOL_<日期>.log`）

**请使用** **[Bug Report 模板](https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues/new?template=bug_report.md)**

### 💡 功能建议

在 [GitHub Issues](https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues) 提出。

**好的功能建议应包含：**

- 清晰描述希望实现的功能
- 解释为什么这个功能有价值
- 描述期望的行为
- 提供替代方案或参考（如其他软件的做法）

**请使用** **[Feature Request 模板](https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues/new?template=feature_request.md)**

### 📝 文档改进

发现文档错误、表述不清、缺失内容时：

- 在 Issues 中指出具体问题
- 提供建议的修改内容（**纯文本描述**，无需提交 PR）

### 🌍 翻译建议

提供翻译建议：

- 在 Issues 中提交建议的翻译
- 标注需要翻译的具体段落
- 当前支持：简体中文 / English

### 🔒 安全报告

> 🚨 **请勿在公开 Issues 中提交安全漏洞！**

请通过 [GitHub Security Advisories](https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/security/advisories/new) 私密报告。

详见 [Security and License](HMOL-Wine-Security-and-License#报告漏洞)

### ⚖️ 侵权举报

发现违反许可证的行为（未经授权的二次修改、重新分发、商业使用等）：

- 使用 [Infringement Report 模板](https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues/new?template=infringement_report.md)
- 或通过 [GitHub 联系表单](https://github.com/contact/report-abuse) / [DMCA Takedown](https://github.com/contact/dmca) 举报

***

## ❌ 不会接受的贡献

以下类型的贡献会被直接拒绝：

| 类型                    | 原因              |
| --------------------- | --------------- |
| 代码修改 PR               | 违反许可证 §3 二次修改禁令 |
| 派生作品仓库                | 违反许可证 §3 第 4 条  |
| 商业使用申请（未授权）           | 违反许可证 §3 商业使用禁令 |
| Fork 仓库的 Pull Request | 派生作品不获接受        |
| 反向工程工具 / 教程           | 违反许可证 §3 第 3 条  |
| 绕过安全机制的工具             | 违反许可证 §3 第 8 条  |
| 修改/删除版权声明的 PR         | 违反许可证 §3 第 9 条  |

> 维护者保留在不另行通知的情况下**直接关闭**任何违规 PR / Issue 的权利。

***

## 📋 提交 Bug 报告的示例

### 好的标题

> ❌ `启动器坏了`
>
> ✅ `在 Windows 11 22H2 上安装 RAR 包时崩溃 (E3.08)`

### 好的内容

```markdown
## 环境
- wine版本
- winlator版本
- HMOL-wine v2.4 (2026-07-20)

## 复现步骤
1. 启动 HMOL
2. 进入"包"页面
3. 选择一个 .rar 格式的包
4. 点击"安装选中"
5. 进度条走到 50% 时崩溃

## 期望行为
正常安装完成，显示"安装成功"对话框

## 实际行为
弹出错误对话框：`RAR 解压失败: unable to find volume 'xxx.rar'`
然后启动器闪退

## 日志
[附加 logs/HMOL_2026-07-20.log]

## 截图
[附加崩溃前截图]
```

***

## 📞 联系方式

| 类型               | 渠道                                                                                                                                 |
| ---------------- | ---------------------------------------------------------------------------------------------------------------------------------- |
| 🐛 Bug 报告 / 功能建议 | [GitHub Issues](https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues)                                              |
| 🔒 安全漏洞          | [GitHub Security Advisories](https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/security/advisories/new)                |
| ⚖️ 侵权举报          | [Infringement Report 模板](https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues/new?template=infringement_report.md) |
| 💬 一般咨询          | QQ 群 `1034243331`                                                                                                                  |

***

## 🙏 致谢

感谢所有提交 Bug 报告、功能建议、文档改进的社区成员。**您的贡献让 HMOL 变得更好！**

***

**© 2026 HMOL Contributors. All Rights Reserved.**
