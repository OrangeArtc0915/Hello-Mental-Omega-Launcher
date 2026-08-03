# 🔒 安全与许可 / Security & License

> HMOL 启动器的安全架构、加密方案、合规要求与许可证条款摘要。

***

## 📋 目录 / Contents

- [安全特性](#安全特性)
- [加密凭据](#加密凭据)
- [网络层安全](#网络层安全)
- [运行时保护](#运行时保护)
- [审计日志](#审计日志)
- [EULA 协议](#eula-协议)
- [许可证摘要](#许可证摘要)
- [报告漏洞](#报告漏洞)
- [举报侵权](#举报侵权)

***

## ⚠️ 重要声明 / Important Disclaimer

> **This launcher is not affiliated with EA, the Red Alert 2 development team, or the Mental Omega development team.**
>
> **本启动器与 EA (Electronic Arts)、红色警戒 2 开发团队、心灵终结 (Mental Omega) 开发团队不存在任何关联、授权、赞助或背书关系。**

- "Red Alert 2"、"Command & Conquer"、"Yuri's Revenge" 是 **Electronic Arts Inc.** 的注册商标。
- "Mental Omega" 是独立的同人 mod 项目。
- 本启动器为**独立第三方工具**，与上述任何实体均无关。

***

## 📜 EULA 协议 / End User License Agreement

> 完整协议：[仓库根目录的 EULA.md](https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/blob/main/EULA.md)

### 核心条款摘要

#### §1 重要声明

- 本启动器与 EA、红警 2 开发组、心灵终结开发组**无任何关联**
- "Red Alert 2" / "Command & Conquer" 是 **Electronic Arts Inc.** 的注册商标
- "Mental Omega" 是独立的同人 mod 项目

#### §3 二次修改禁令 ⛔

**严禁**以下行为：

| # | 行为     | 说明             |
| - | ------ | -------------- |
| 1 | 修改源代码  | 任何对 `.py` 的改动  |
| 2 | 修改二进制  | 反编译后修改、补丁、热更新  |
| 3 | 反向工程   | 反编译、反汇编、静态分析   |
| 4 | 创建衍生作品 | Fork 后修改、改编、翻译 |
| 5 | 代码复用   | 用于其他项目         |
| 6 | 重新分发   | 上传至任何代码托管平台    |
| 7 | 商业使用   | 用于商业产品/服务/盈利   |
| 8 | 安全绕过   | 绕过加密、版权保护      |
| 9 | 标识移除   | 移除/隐藏版权、商标、许可  |

**违规处理**：使用许可自动立即终止 + 法律追责。

#### §4 许可授予

- ✅ 个人非商业使用
- ✅ 备份目的的复制
- ❌ 商业销售
- ❌ 再许可
- ❌ 反向工程

#### §8 隐私政策

- 默认**不收集**任何用户个人信息
- Microsoft 令牌使用 **AES-256-GCM 加密**本地存储
- 不使用 Cookies
- 不集成第三方追踪 SDK
- 不上传数据到 HMOL 中央服务器（**HMOL 不运营任何中央服务器**）

#### §13 联系

- GitHub Issues
- QQ 群 `1034243331`

***

## ⚖️ 许可证摘要 / License Summary

**HMOL Non-Commercial, No-Modification Source-Available License v2.2**

### ✅ 允许

| 行为           | 范围            |
| ------------ | ------------- |
| 📖 阅读源代码     | 个人            |
| 🖥️ 安装使用     | 个人非商业         |
| 🐛 提交 Bug 报告 | GitHub Issues |
| 📦 备份目的的复制   | 个人            |

### ❌ 严禁

| 行为            | 后果     |
| ------------- | ------ |
| 🔧 修改源码 / 二进制 | 许可自动终止 |
| 🔀 创建衍生作品     | 许可自动终止 |
| 📤 重新分发       | 法律追责   |
| 💰 商业使用       | 法律追责   |
| 🔓 绕过安全机制     | 法律追责   |
| 🏷️ 移除版权声明    | 法律追责   |

完整条款见 [LICENSE](https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/blob/main/LICENSE) 文件。

***

## 🚨 报告漏洞 / Reporting a Vulnerability

> **请勿在公开 Issue 中报告安全漏洞。**

请通过以下方式私密报告：

- **GitHub Security Advisories**: <https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/security/advisories/new>

**响应时效**：

- 确认收到：48 小时内
- 初步评估：7 天内
- 修复发布：随下个版本（无 SLA 保证）

### 应包含的信息

1. 漏洞的详细描述
2. 复现步骤
3. 影响范围
4. 概念验证（PoC）代码（如有）
5. 您的联系方式

***

## ⚖️ 举报侵权 / Reporting Infringement

> **如果您发现违反许可证 §3 的行为（未经授权的二次修改、二次分发、商业使用），请举报。**

### 举报方式

1. **GitHub Issues**（使用 [Infringement Report 模板](https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues/new?template=infringement_report.md)）
2. **GitHub 联系表单**：<https://github.com/contact/report-abuse>
3. **DMCA Takedown**：<https://github.com/contact/dmca>

### 举报应包含

- 通知方的姓名和联系方式
- 被侵权作品的标识（HMOL Launcher v2.4）
- 侵权内容的位置
- 侵权方的身份信息
- 善意声明（相信该使用未经授权）
- 电子或物理签名

***

## 📋 用户最佳实践 / Best Practices for Users

1. **不要**分享你的 `HMOL_secrets.json` 或环境变量
2. **不要**在公开场合发布 `HMOL `配合的熵文件
3. **使用** 防病毒软件保护本地凭据
4. **不要**从非官方渠道下载 HMOL（可能包含恶意修改）
5. **校验** 下载文件的 SHA-256 哈希
6. **及时** 更新到最新版本

***

## 📚 下一步 / Next Steps

- 🐛 [Troubleshooting](HMOL-Wine-Troubleshooting) — 问题排查
- 🐛 [Error Codes](HMOL-Wine-Error-Codes) — 错误码速查
- 🤝 [Contributing](HMOL-Wine-Contributing) — 反馈与建议

***

**© 2026 HMOL Contributors. All Rights Reserved.**
