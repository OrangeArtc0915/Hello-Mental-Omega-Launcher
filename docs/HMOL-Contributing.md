# 🤝 贡献指南

> ⚠️ **本项目采用「源代码可见但禁止二次修改」许可证。**
>
> 由于许可证禁止二次修改,**本项目不接受任何形式的 Pull Request**(PR 会被直接关闭)。
>
> 但欢迎以下**非代码修改**的贡献。

***

## 📑 目录

- [可接受的贡献类型](#可接受的贡献类型)
- [禁止的贡献类型](#禁止的贡献类型)
- [Bug 报告](#bug-报告)
- [功能建议](#功能建议)
- [文档改进](#文档改进)
- [翻译](#翻译)
- [安全漏洞报告](#安全漏洞报告)
- [侵权举报](#侵权举报)
- [行为准则](#行为准则)

***

## 可接受的贡献类型

| 类型            | 说明                      | 渠道                         |
| ------------- | ----------------------- | -------------------------- |
| 🐛 **Bug 报告** | 描述问题、复现步骤、期望行为          | GitHub Issues              |
| 💡 **功能建议**   | 提出新功能想法                 | GitHub Issues              |
| 📖 **文档改进**   | 指出 Wiki/README 错误或不清楚之处 | GitHub Issues              |
| 🔒 **安全研究**   | 报告安全漏洞(私密)              | GitHub Security Advisories |
| ⚖️ **侵权举报**   | 举报违反许可证的行为              | GitHub Issues / 联系表单       |
| 💬 **社区支持**   | 在 Issues/QQ 群帮助其他用户     | QQ 群                       |
| 🎨 **插件包**    | 为社区提供优质的 MO 插件包         | 独立分发                       |

***

## 禁止的贡献类型

| ❌ 禁止          | 原因                   |
| ------------- | -------------------- |
| 提交 PR 修改代码    | 违反 EULA §3 第 1 条     |
| 提交 PR 修改文档/注释 | 违反 EULA §3 第 1 条     |
| Fork 后修改并发布   | 违反 EULA §3 第 4 条     |
| 提交 PR 添加依赖    | 违反 EULA §3 第 1 条     |
| 提交 PR 修改构建脚本  | 违反 EULA §3 第 1 条     |
| 翻译代码/UI       | 违反 EULA §3 第 4 条(改编) |
| 提取代码片段到其他项目   | 违反 EULA §3 第 5 条     |

> 🚫 **所有代码修改类的 PR 会被直接关闭,不会审查。**

***

## Bug 报告

### 报告前的检查

1. **搜索现有 Issues**: 避免重复报告
   <https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues?q=is%3Aissue+>
2. **确认最新版本**: 升级到最新版本后问题是否仍然存在?
   <https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/releases>
3. **查看故障排查**: 也许已有解决方案
   [故障排查](HMOL-Troubleshooting)

### 提交 Bug 报告

**路径**: [GitHub Issues → New Issue → Bug Report](https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues/new?template=bug_report.md)

**必填内容**:

```markdown
## 环境
- HMOL 版本: v2.4 (commit xxxxxxx)
- Windows 版本: Windows 11 22H2 (Build 22621)
- 启动方式: EXE 

## 复现步骤
1. 打开 HMOL
2. 点击「🎮 实例」→「新建实例」
3. ...

## 期望行为
应该成功创建实例

## 实际行为
弹出错误对话框: "实例名称和路径不能为空"

## 截图/日志
(附加 logs/HMOL_<日期>.log 相关片段)
```

### 提供高质量日志

```powershell
# 1. 复现问题
# 2. 找到当天日志
Get-ChildItem logs\HMOL_*.log | Sort-Object LastWriteTime -Descending | Select-Object -First 1

# 3. 复制相关片段(脱敏后)
# 注意:access_token、refresh_token 等敏感字段已自动脱敏
```

***

## 功能建议

### 提交功能建议

**路径**: [GitHub Issues → New Issue → Feature Request](https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues/new?template=feature_request.md)

**必填内容**:

```markdown
## 需求描述
我希望能够在...

## 动机/背景
当前痛点是...这会影响...

## 提议方案
具体来说:
1. 步骤 1
2. 步骤 2

## 替代方案
也可以考虑...

## 优先级
- [ ] 紧急(影响使用)
- [ ] 重要(明显改善体验)
- [x] 一般(锦上添花)
```

### 评估标准

HMOL 维护者会从以下角度评估:

| 维度          | 说明               |
| ----------- | ---------------- |
| **符合 EULA** | 不引入违反许可证的功能      |
| **不影响安全性**  | 不削弱加密/反调试/完整性校验  |
| **跨平台**     | Windows 10/11 通用 |
| **向后兼容**    | 不破坏现有配置/实例/备份    |
| **性能**      | 不显著增加启动时间/内存占用   |
| **用户价值**    | 解决多数用户的真实痛点      |

***

## 文档改进

### 可改进的内容

- Wiki 中的错误/拼写问题
- 表述不清晰的部分
- 缺失的章节
- 过时的截图/链接

### 如何提交

由于不接受 PR,请通过 [GitHub Issues](https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues) 提交,使用 `documentation` 标签。

**Issue 模板**:

```markdown
## 文档位置
例如: wiki/Installation-Guide.md#系统要求

## 当前内容
> (引用相关段落)

## 问题
1. 错误: ...
2. 不清楚: ...
3. 缺失: ...

## 建议修改
(用文字描述,不需要直接修改文件)
```

***

## 翻译

### 现状

HMOL 启动器界面与文档的官方语言为**简体中文**,附带**英文参考版本**。

### 不接受的原因

翻译属于「改编」(EULA §3 第 4 条),违反许可证。

### 替代方案

- 用户可以自行使用翻译工具(浏览器翻译、DeepL、Google Translate)
- 我们欢迎通过 Issue 报告**翻译错误**,但不会发布官方翻译版本

***

## 安全漏洞报告

> 🔒 **请勿在公开 Issues 中报告安全漏洞!**

### 报告方式

通过 GitHub **私有漏洞报告**:
<https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/security/advisories/new>

### 响应时间

| 阶段   | 时间              |
| ---- | --------------- |
| 确认收到 | 48 小时内          |
| 初步评估 | 7 天内            |
| 修复计划 | 14 天内(复杂漏洞可能延长) |
| 公开披露 | 修复发布后 90 天      |

### 报告应包含

1. **漏洞类型**: CWE 编号(可选)
2. **影响范围**: 受影响的版本
3. **复现步骤**: 详细步骤
4. **概念验证 (PoC)**: 代码/截图
5. **影响评估**: CVSS 评分(可选)
6. **已知缓解**: 是否已有 workaround

### 致谢

安全研究者将在 [SECURITY.md](../SECURITY.md) 中致谢(经本人同意)。

***

## 侵权举报

如果您发现有人违反 HMOL 许可证,请举报。

### 举报方式

1. **GitHub Issues** (使用 Infringement Report 模板):
   <https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues/new?template=infringement_report.md>
2. **GitHub 联系表单**:
   <https://github.com/contact/report-abuse>
3. **DMCA Takedown**:
   <https://github.com/contact/dmca>

### 举报应包含

根据 DMCA / 中国《著作权法》要求:

| 项目    | 说明                 |
| ----- | ------------------ |
| 通知方信息 | 姓名、联系方式            |
| 被侵权作品 | HMOL Launcher v2.4 |
| 侵权位置  | 具体 URL/包名/店铺链接     |
| 侵权方信息 | GitHub 用户名、店铺名称等   |
| 善意声明  | "我相信该使用未经授权"       |
| 签名    | 电子签名               |

### 常见违规类型

- ❌ 未经授权的二次修改、二次开发
- ❌ 未经授权的二次封装、二次分发
- ❌ 未经授权的 Fork + 修改
- ❌ 未经授权的商业使用
- ❌ 移除版权声明、商标、作者信息
- ❌ 绕过安全机制

***

## 行为准则

### 期望的行为

- ✅ 尊重他人,文明讨论
- ✅ 提供建设性反馈
- ✅ 接受不同意见
- ✅ 专注于对社区最有利的事情
- ✅ 对新手保持耐心

### 不可接受的行为

- ❌ 人身攻击、辱骂、骚扰
- ❌ 性别歧视、种族歧视、宗教歧视
- ❌ 公开他人隐私信息
- ❌ 持续刷屏/抬杠
- ❌ 发布色情/暴力/违法内容
- ❌ 任何形式的作弊宣传

### 处理

违反行为准则的处理:

1. **首次**: 私下警告
2. **再次**: 公开警告
3. **屡次**: 永久禁言

***

## 致谢

感谢以下贡献者(按字母顺序):

- HMOL Security Team
- HMOL Beta Testers
- 提交 Bug 报告和功能建议的社区成员

> 由于许可证限制,贡献者无法将代码合并到本项目,但所有贡献者都被铭记在心。

***

## 联系

| 渠道              | 链接                                                                                      |
| --------------- | --------------------------------------------------------------------------------------- |
| GitHub Issues   | <https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues>                  |
| GitHub Security | <https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/security/advisories/new> |
| QQ 群            | 1034243331                                                                              |

***

