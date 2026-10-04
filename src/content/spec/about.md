
## 关于 HMOL

HMOL (Hello Mental Omega Launcher) 是一款为心灵终结玩家打造的独立第三方启动器。

当前版本是 **C# / .NET 8 / WPF 桌面程序**（`{{HMOL_VERSION}}`），以**自包含单文件**压缩包形式发行：
多实例管理、资源包安装卸载与备份还原、EasyTier / n2n / 樱花 Frp 联机组网、运行日志、背景与布局自定义、
声明式扩展模块；无需安装任何额外运行库或框架。组网组件与 7-Zip 不随包分发，首次使用联机时在「下载」页按需获取。

::github{repo="OrangeArtc0915/Hello-Mental-Omega-Launcher"}

### 鸣谢

感谢每一位参与测试与反馈的朋友（排名不分先后）：

| 测试人员 | 头衔 |
| --- | --- |
| 罒ω罒 | — |
| 绮梦 | 猪 |
| 心弦连 | — |
| a114514 | — |
| 艾尔登皮蛋 | — |
| 安然的岛不叫安然 | — |
| 白小梦です | — |
| 雪枫 | — |
| Drawer😳 | — |
| 枫～ | — |
| 飞行 | — |
| fs bga3 | — |
| 鸽尔德⁧ ~咕⁧‭ | — |
| GinkgobilobaL | — |
| 靖安司管 | — |
| Lmsh | — |
| 墨笙 | — |
| ovide（内奸） | — |
| 浅梦 | — |
| 世蓝喧 | — |
| 月見ヤチヨ是一 | — |
| 夜幕 | — |
| YUN✨RÚ | — |
| zxc | — |
| 悲伤的天使 | — |
| ㅤ弃世 | — |
| 07 | — |

### 友链

**同样在做的项目**

- **PAL-CE 启动器** —— 另一款好用的红警启动器（官网还在部署中，链接待补）。

**启动器用到的开源项目**

- [7-Zip](https://www.7-zip.org/) —— 创建与解压 7z 压缩包
- [RePKG](https://github.com/notscuffed/RePKG) —— 解包 Wallpaper Engine 壁纸包
- [EasyTier](https://github.com/EasyTier/EasyTier) —— 三层组网（联机方案之一）
- [n2n](https://github.com/ntop/n2n) —— 二层组网（联机方案之一）
- [TAP-Windows6 / OpenVPN](https://github.com/OpenVPN/tap-windows6) —— TAP 虚拟网卡驱动
- [WinIPBroadcast](https://github.com/dechamps/WinIPBroadcast) —— 局域网广播转发
- [cnc-ddraw](https://github.com/FunkyFr3sh/cnc-ddraw) —— 红警系列黑屏 / 兼容修复
- [SharpCompress](https://github.com/adamhathcock/sharpcompress) —— zip / tar / gzip 解压
- [NAudio](https://github.com/naudio/NAudio) —— 背景音乐解码与播放
- [MQTTnet](https://github.com/dotnet/MQTTnet) —— 联机大厅通信
- [lucide](https://lucide.dev/) —— 界面图标
- [ConfuserEx](https://github.com/mkaring/ConfuserEx) —— 发布构建时的代码混淆
- 运行环境与游戏依赖：[.NET](https://dotnet.microsoft.com/)、[Visual C++ 运行库](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist)、[DirectX](https://www.microsoft.com/download/details.aspx?id=35)、[XNA Framework](https://www.microsoft.com/download/details.aspx?id=20914)

**本站（官网）用到的开源项目**

- [Astro](https://astro.build/) —— 站点框架
- [Mizuki](https://github.com/matsuzaka-yuki/mizuki) —— 站点主题（基于 [Fuwari](https://github.com/saicaca/fuwari)）
- [Svelte](https://svelte.dev/) / [Tailwind CSS](https://tailwindcss.com/) —— 前端组件与样式
- [Iconify](https://iconify.design/) —— 图标
- [KaTeX](https://katex.org/) / [Mermaid](https://mermaid.js.org/) —— 数学公式与图表
- [Expressive Code](https://expressive-code.com/) —— 代码块渲染
- [Pagefind](https://pagefind.app/) —— 站内搜索
- [Twikoo](https://twikoo.js.org/) —— 评论系统

### 安装前请确认

1. 仅从 Gitee 或 GitHub Releases 下载，拒绝一切二次封包版本。
2. 核对发行包的 SHA-256 校验值是否与 Release 页面公布的一致。
3. 发行包只需**完整解压**后再运行；组网组件与 7-Zip 首次使用联机时会在「下载」页提示获取，`runtime\` 缺失只影响联机与 `.7z` 导出。
4. 本程序为专有软件、保留所有权利，禁止用于商业用途与二次分发。

### QQ 交流群

- **官方群**: 1034243331 — [一键加群](https://qm.qq.com/q/ia8Zv2AtEY)

### 赞助支持

如果本项目对你有帮助，欢迎赞助支持我们的服务器和开发。

| 支付宝 | 微信 |
|--------|------|
| <img src="../docs/alipay.png" alt="支付宝" loading="eager" fetchpriority="high" decoding="sync" /> | <img src="../docs/wechat.jpg" alt="微信" loading="eager" fetchpriority="high" decoding="sync" /> |

---

*HMOL 独立第三方启动器，与 EA 及 Mental Omega 开发团队无关联。*
