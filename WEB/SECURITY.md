# Security Policy

> 🚫 **本项目采用「源代码可见但禁止二次修改」许可证,详见 [LICENSE](LICENSE)。**
> 🚫 **This project uses a "Source-Available, No-Modification" license. See [LICENSE](LICENSE).**

## 支持的版本 / Supported Versions

| Version | Supported          |
|---------|--------------------|
| HMOL        | :white_check_mark: |
| HMOL-wine   | :white_check_mark: |
| HMOL-DLC    | :white_check_mark: |

---

## 🚨 报告漏洞 / Reporting a Vulnerability

**请勿在公开 issue 中报告安全漏洞。**

请通过以下方式私密报告:

- **GitHub Security Advisories**: 使用 [私有漏洞报告](https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/security/advisories/new)

我们会在 48 小时内确认收到,并在 7 天内提供初步评估。

---

## 🚫 报告侵权 / Reporting License Infringement

**本项目采用「源代码可见但禁止二次修改」许可证。如果您发现违反许可证的行为,请举报。**

### 违规类型

包括但不限于:

- ❌ 未经授权的二次修改、二次开发
- ❌ 未经授权的二次封装、二次分发
- ❌ 未经授权的 Fork + 修改
- ❌ 未经授权的商业使用
- ❌ 移除版权声明、商标、作者信息
- ❌ 绕过安全机制

### 举报方式

请通过以下方式之一举报:

1. **GitHub Issues**(使用 [Infringement Report 模板](https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues/new?template=infringement_report.md))
2. **GitHub 联系表单**:https://github.com/contact/report-abuse
3. **DMCA Takedown**:https://github.com/contact/dmca

### 举报应包含的信息

- 通知方的姓名和联系方式
- 被侵权作品的标识(本项目为 HMOL Launcher v2.2)
- 侵权内容在软件中的具体位置
- 侵权方的身份信息(GitHub 用户名、仓库链接等)
- 善意声明(相信该使用未经授权)
- 电子或物理签名

详见 [LICENSE](LICENSE) §7。

---

## 🔒 安全特性 / Security Features

### 凭据保护

1. **加密存储**: 所有 API 密钥、令牌、群组 ID 都使用 AES-256-GCM 加密存储
2. **机器绑定**: 加密密钥基于机器指纹派生,跨机器不可移植
3. **环境变量支持**: 支持通过 `HMOL_QQ_BOT_APPSECRET` 等环境变量注入凭据
4. **外部配置**: 支持 `HMOL_secrets.json` 配置文件(不提交到 git)
5. **优先级链**: 环境变量 > 外部配置 > 加密 seal 模块

### 代码保护

1. **PyInstaller --key**: 发布 EXE 时使用 AES 加密字节码
2. **运行时保护**: HMOL_protection.py 提供反调试/反 VM/完整性检查
3. **完整性校验**: 关键模块 SHA-256 哈希校验
4. **审计日志**: 所有凭据访问、解密操作都有审计日志

### 提交前检查

- 运行 `python HMOL_security_audit.py --strict` 确保无 critical/high 问题
- 确认 `HMOL_secrets.json` 未被提交
- 确认无明文 API 密钥
- 运行 `python scripts/add_copyright_headers.py --check` 确保所有源文件包含版权头

---

## 🔐 安全更新 / Security Updates

安全更新会在 GitHub Security Advisories 中发布,并在 CHANGELOG 中标记。

---

## 📋 最佳实践 / Best Practices

### 用户

1. **不要**分享你的 `HMOL_secrets.json` 或环境变量
2. **不要**在公开场合发布 HMOL_secrets_seal.py 配合的熵文件
3. **定期**运行 `python HMOL_seal_secrets.py --rotate` 轮换密钥
4. **使用** 防病毒软件保护本地凭据
5. **不要**从非官方渠道下载 HMOL(可能包含恶意修改)

### 贡献者

1. **绝不**提交任何明文 API 密钥
2. **运行** `python HMOL_security_audit.py` 在每次 PR 前
3. **遵循** [OWASP Python Security](https://owasp.org/www-project-python-security/) 指南
4. **使用** `subprocess` 列表形式参数,避免 `shell=True`
5. **理解** 本项目不接受代码修改类的 PR

---

## 🔍 加密算法说明

| 算法 | 用途 | 标准 |
|------|------|------|
| AES-256-GCM | 对称加密(认证) | NIST SP 800-38D |
| PBKDF2-HMAC-SHA256 | 密钥派生 | NIST SP 800-132 (200,000 iter) |
| HKDF-SHA256 | 子密钥派生 | RFC 5869 |
| RSA-2048-OAEP-SHA256 | 非对称加密 | RFC 8017 |
| HMAC-SHA256 | 完整性校验 | RFC 2104 |

---

## 🙏 致谢 / Acknowledgments

感谢以下安全研究者和贡献者(按字母排序):

- HMOL Security Team

---

## 📞 联系方式 / Contact

| 类型 | 渠道 |
|------|------|
| 安全漏洞 | [GitHub Security Advisories](https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/security/advisories/new) |
| 侵权举报 | [GitHub Issues](https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues/new?template=infringement_report.md) |
| 一般咨询 | [GitHub Issues](https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues) |
| QQ 群 | 1034243331 |

---

**© 2026 HMOL Contributors. All Rights Reserved.**
