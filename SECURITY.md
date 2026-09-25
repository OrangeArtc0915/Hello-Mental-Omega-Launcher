# 安全政策 / Security Policy

> 本软件为**专有软件，保留所有权利（All Rights Reserved）**，详见 [LICENSE](LICENSE)。

***

## 支持的版本 / Supported Versions

当前只有一个程序：**HMOL（Hello Mental Omega Launcher）**，`C# / .NET 8 / WPF` 自包含单文件。

| 版本 | 支持 |
| --- | --- |
| v1.0.0 | :white_check_mark: |

支持的操作系统：**Windows 10 1809+ / Windows 11（x64）**。

***

## 🚨 报告安全漏洞 / Reporting a Vulnerability

**请不要在公开 Issue 中披露漏洞细节。**

首选渠道是 GitHub 的**私有漏洞报告**：

- <https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/security/advisories/new>

也可以先在 QQ 群 **`1034243331`** 联系维护者，约定私下沟通；但**请勿**把复现细节直接贴在公开 Issues 或群文件里。

报告建议包含：

1. 漏洞类型（可选 CWE 编号）；
2. 受影响版本；
3. 复现步骤；
4. 概念验证（PoC）或截图；
5. 影响评估（可选 CVSS）；
6. 已知缓解方式。

### 请注意

- 本项目**内置启动门锁与安全自检**（调试器检测、完整性校验、沙箱检测等）。指出「存在这些机制」本身不算漏洞；
- 这些自检**只写日志告警，不阻断使用**；
- 请勿在报告中包含从非官方渠道获取的二进制，或要求维护者去分析被修改过的发行包；
- 请仅从官方渠道下载本软件（见下文「下载渠道」）。

***

## 🔒 当前的安全设计 / Current Security Design

- **数据全部在本机**：设置、实例、资源包目录、备份与日志都保存在程序目录下的 `Data\`（可用环境变量 `HMOL_DATA` 重定向）；程序不运营任何中央服务器，也没有账号体系，不保存任何登录凭据。
- **启动门锁**：启动时联网读取一个开关（GitHub 优先、Gitee 备用），读到「暂停支持」或联网读不到配置都会**阻止启动**（错误代码 `E12.01`），这是刻意的严格模式。
- **安全自检**：启动时做调试器 / 完整性 / 沙箱检查，**只记日志告警**，不会因此退出程序。
- **扩展模块不执行代码**：扩展只是一份 JSON 清单，启动器不加载程序集、不引入脚本引擎、不读写扩展目录以外的文件；清单里可选配的数据源只做 `http` / `https` 的**只读 GET**，且有总开关可一键关闭。
- **组网组件**：联机功能调用的 EasyTier / n2n / TAP 驱动等随发行包 `runtime\` 分发，程序只调用、不改写。

***

## 🚫 报告侵权 / Reporting License Infringement

如果发现有人违反本项目的许可声明（例如二次修改、二次封装、二次分发、移除版权信息），请举报：

1. **GitHub Issues**：<https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues>
2. **GitHub 联系表单**：<https://github.com/contact/report-abuse>
3. **DMCA Takedown**：<https://github.com/contact/dmca>

举报时请提供：通知方信息、被侵权作品（HMOL Launcher）、侵权位置（具体 URL / 包名 / 链接）、善意声明与签名。

***

## 📥 下载渠道 / Official Download Channels

请仅从以下官方渠道下载，其它渠道的二进制可能已被修改：

- GitHub Releases：<https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/releases>
- Gitee Releases：<https://gitee.com/orangearc655743/Hello-Mental-Omega-Launcher/releases>
- QQ 群：`1034243331`

***

## 🙏 致谢 / Acknowledgments

感谢所有私下报告安全问题、帮助改进 HMOL 的社区成员。

***

## 📞 联系方式 / Contact

| 类型 | 渠道 |
| --- | --- |
| 安全漏洞 | [GitHub 私有漏洞报告](https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/security/advisories/new) |
| 一般问题 / 功能建议 | [GitHub Issues](https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues) |
| 即时交流 | QQ 群 `1034243331` |

***

**© 2026 mmm. 保留所有权利（All Rights Reserved）。**
