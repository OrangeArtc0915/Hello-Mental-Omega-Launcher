# MO 局域网补丁（**非官方**）· 中文输入 + 联机修复

> **本补丁是根据开源的 CnCNet 客户端（XNA CnCNet Client）修改而来，完全遵照其开源许可协议
> （GNU GPL-3.0-or-later）发布。**
>
> **本项目与 Mental Omega（MO）开发组、MO 客户端官方没有任何关联**，不是官方补丁，
> 未获得其授权、认可或支持；一切与 MO 官方有关的问题请不要去打扰 MO 开发组。
> 本补丁也不是 MO 官方发布的更新，MO 客户端自带的更新器与本补丁无关。
>
> 本项目同样与 CnCNet 官方团队无隶属关系。
>
> 本补丁**不含、也未修改 MO 的任何游戏素材**（美术、音频、地图、规则 ini 等），
> 不改动游戏本体 `gamemd.exe`，不修改 MO 的更新源或服务端。

---

## 0. 源码在这里

**本补丁的完整源码、构建脚本、构建说明全部公开在 GitHub：**

> **<https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/tree/patch>**

（注意是 **`patch` 分支**，不是默认分支。）

**工具包**里**不含任何来自 MO 客户端或 CnCNet 客户端的二进制文件**——
被改动的那几个文件（`clientdx.exe` 等）由包里的工具在**你自己的机器上就地打补丁**生成，
具体见第 3 节。

包里带的二进制只有我们的工具本身，以及 MIT 许可的 `dnlib.dll`
（唯一随包分发的第三方二进制，允许自由再分发）——明细见 [NOTICE.md](NOTICE.md)。

> 另有自用的「直接覆盖包」，里面**确实带着**改过的客户端 exe/dll（GPL 派生二进制），
> 只在自用/朋友间传递，不放在本仓库；它的合规要求见 [2.6](#26-直接覆盖包自用)。

---

## 1. 这是什么

给 **Mental Omega 的 CnCNet 客户端**（版本 `2.6.10.3`，即 `Resources\clientdx.exe` 那一套）
做的两个小补丁，源码、构建脚本和每一步的验证记录都在本仓库里。

| 补丁包 | 目标文件 | 作用 |
|---|---|---|
| **补丁1-中文输入** | `Resources\Binaries\Windows\MonoGame.Framework.dll` + `Rampastring.XNAUI.dll` | 让客户端窗口能正常用中文输入法：候选框跟随光标、不重复上屏；聊天框组词过程中按回车选词不再被当成「发送」、按 Esc 不再清空整行 |
| **补丁2-联机** | `Resources\clientdx.exe` / `clientogl.exe` / `clientxna.exe` + `MoLanRelay.dll` | 局域网联机修复（队伍 A/B/C/D、spawn.ini 越界、重复连接、AI 裁剪）+ 底部 IP 直连框 + 游戏内 UDP 中继（配合樱花 Frp 等只开一条 TCP 隧道的场景）+ 局域网踢人 + @ 提醒与 Tab 名字补全 + **地图同步**（房主点按钮才推、客机确认后写入）+ 聊天命令（`/diag` 诊断包、`/sync`）+ 锁房/满员原因提示 + 客机自动准备 + 聊天时间戳 |

两个包改的文件**完全不重叠**，可以各自单独装，也可以都装。

详细的界面说明、安装步骤、验证方法和踩坑记录见 **[构建说明.md](构建说明.md)**。

---

## 2. 许可与归属（请务必看完）

### 2.1 上游组件

| 组件 | 版权 | 许可 |
|---|---|---|
| CnCNet Client（XNA CnCNet Client） | © 2013-2026 CnCNet, Rampastring | **GNU GPL-3.0-or-later** |
| Rampastring.Tools / Rampastring.XNAUI | © 2022 Rami "Rampastring" Pasanen | MIT |
| MonoGame（客户端附带的编译产物） | © MonoGame Foundation | MS-PL |
| Mono.Xna 相关部分 | © The Mono.Xna Team | MIT |
| ClientUpdater / SecondStageUpdater | © CnCNet | GNU GPL-3.0 |
| Newtonsoft.Json / Discord RPC / lzo.net | 各自作者 | MIT |
| dnlib 4.4.0（我们的补丁器依赖，**随包分发**） | © 2012-2019 de4dot@gmail.com | MIT |

- 上游项目地址：<https://github.com/CnCNet/xna-cncnet-client>
- 上游客户端的**完整许可声明原文**（GPL-3.0 + 上述各组件的 MIT / MS-PL 等通知）见
  [LICENSE-CnCNet-Client.md](LICENSE-CnCNet-Client.md)，该文件内容原样取自上游仓库（仅文件名不同）。
- **GPL-3.0 许可证全文**见 [LICENSE](LICENSE)。
- 本项目**实际分发**的东西里用到的第三方组件、以及它们许可文本的落位，
  逐项列在 [NOTICE.md](NOTICE.md) 里（含 dnlib 的 MIT 全文）。

### 2.2 本项目

本项目的全部改动（`IL补丁工具\`、`中继\`、`脚本\` 及说明文档）
**同样以 GNU GPL-3.0-or-later 发布**：

- 你可以自由使用、修改、再分发；
- 条件与上游一致 —— 保留版权与许可声明、把改动同样以 GPL 发布、向接收者提供对应源码；
- **本仓库不额外附加任何限制**（不禁止商用、不限制用途），除上述上游条款外没有别的条件；
- 仓库里**每个文件**都适用上面这条（含源码、脚本、文档；没有单独标注的文件同样如此），
  唯一的例外是 `LICENSE-CnCNet-Client.md` 与 `NOTICE.md` 中引用的第三方许可文本，
  它们属于各自的版权人。

### 2.3 修改声明（GPL-3.0 §5(a)）

| 项 | 内容 |
|---|---|
| **被修改的文件（补丁2）** | `Resources\clientdx.exe`、`Resources\clientogl.exe`、`Resources\clientxna.exe` |
| **被修改的文件（补丁1）** | `Resources\Binaries\Windows\MonoGame.Framework.dll`、`Resources\Binaries\Windows\Rampastring.XNAUI.dll` |
| **修改日期** | 首次修改 2026-10-06；最近修改 2026-11-15（中继 v0.6.0） |
| **修改版本标识** | 中继 DLL 的日志第一行 `======== MoLanRelay v<版本> ========` |
| **修改性质** | 主要是**对原文件追加 IL 指令**；少数位置会改写个别指令或补一段判断（例如直连入口的 TCP 端口取值、上游 `HandleFileHashCommand` 漏判空处补上空值判断）。改动**只涉及客户端（大厅/聊天/联机）逻辑**；游戏本体、在线联机流程、MO 的游戏素材均未改动 |
| **原文件来源** | 你自己安装的 MO 客户端自带文件；安装时会自动备份到 `<游戏根>\_MO补丁备份\`，**该备份只存在于你本机，不随包分发** |
| **谁执行修改** | 由本项目的工具在你的机器上就地修改（我们不分发被修改过的客户端二进制） |
| **显著提示** | 安装器启动时会打印本声明（含日期与源码地址）；改动后的客户端在开局时日志首行会打印 `======== MoLanRelay v<版本> ========` 作为修改标识 |

### 2.4 本补丁没有做的事

- **不含任何 MO 的游戏素材**：美术、音频、地图、规则 ini 等一律没有。
- **不修改游戏本体**：`gamemd.exe` / `Syringe.exe` 保持原样，仅沿用其原有的“以管理员身份运行”设置。
- **不修改 MO 的更新源/服务端**，不绕过任何校验；只移除/隐藏客户端自己的 UI 入口
  （CnCNet 大厅、更新标签页、下载音乐包按钮）。
- **不包含**客户端的完整源码，也不是客户端的重新分发版本（例外见 2.6「直接覆盖包」）。

### 2.4.1 改动边界：只改开源客户端，不碰闭源 MO 本体

**MO（Mental Omega）是闭源商业模组，我们既不改它的文件，也不分发它的文件。**
本补丁动到的全部是开源的 CnCNet 客户端（GPL-3.0-or-later）那一层：

| 会改（客户端层，均在 `Resources\` 下） | 补丁 |
|---|---|
| `Resources\clientdx.exe` / `clientogl.exe` / `clientxna.exe` | 补丁2 |
| `Resources\Binaries\Windows\MonoGame.Framework.dll` | 补丁1 |
| `Resources\Binaries\Windows\Rampastring.XNAUI.dll` | 补丁1 |
| `Resources\MainMenu.ini`（**仅工具包**；直接覆盖版不改，由中继启动后隐藏入口） | 补丁2 |
| `Resources\MoLanRelay.dll`、`Resources\Binaries\MoLanRelay.dll`（新增文件，原版没有） | 补丁2 |

| 从不触碰（MO 本体） |
|---|
| `gamemd.exe`、`Syringe.exe`、`rulesmo.ini`、`artmo.ini`、`aimo.ini`、`*.mix`、`MapsMO\`、`INI\`、`Map Editor\`、`RA2MO.ini`、`ddraw.ini` |

装好之后，**运行时会新增或重写**下面这些文件。它们都是客户端自己本来就会写的，
不是 MO 的发行文件，删掉不影响游戏：

| 文件 | 谁写的 | 说明 |
|---|---|---|
| `spawn.ini` | 客户端 `GameLobbyBase.WriteSpawnIni`（每局点“开始游戏”时重写） | 补丁只把 `[OtherN]` 的 `Ip`/`Port` 指向 `127.0.0.1:12340+N`，让联机流量走本机中继。不改任何单位/规则/地图数据 |
| `Resources\MoLanPatch.ini` | 本补丁 | 设置窗口里那些开关 |
| `MoLanRelay.log` | 本补丁 | 中继日志 |
| `Resources\lanip.txt` | 本补丁 | 直连框的临时输入文件（读完即删） |

### 2.5 关于「对应源码」

GPL-3.0 要求分发二进制时向接收者提供对应源码。**工具包从设计上就避开了这个义务**
（“直接覆盖包”例外，见 [2.6](#26-直接覆盖包自用)）：

1. **工具包不分发任何 MO / CnCNet 客户端的二进制。** 包里只有我们自己写的东西
   （`dnlibpatch.exe` + `MoLanRelay.dll` + 安装脚本 + 文档），
   外加一个 MIT 许可、允许自由再分发的 `dnlib.dll`（其许可文本见 [NOTICE.md](NOTICE.md)）。
   被改的 `clientdx.exe` 等**不经过我们分发**，而是由包里的工具在你自己本机就地修改 ——
   GPL-3.0 的 §0 定义里，"propagate"（需要承担义务的行为）明确把
   "在一台计算机上运行它" 和 "修改一份私人副本" 排除在外，§2 也只对"convey"（分发）设条件。
2. **我们自己写的全部源码都在本仓库**，完整、可构建，地址见第 0 节；
3. **上游源码**在 <https://github.com/CnCNet/xna-cncnet-client>，按 tag / commit 获取；
4. **就地打补丁的结果是确定性可复现的**：拿同一份 MO 客户端原版文件跑一遍
   `脚本\build-all.ps1` 的端到端自检，可得到与已验证成品**逐字节相同**的 exe
   （流程内含 dnlib 栈自检、`脚本\JIT校验.ps1` 强制 JIT、SHA256 比对）；
5. 若 MO 客户端自身包含未公开的官方改动，那部分源码不在我们手上，请向 MO 官方索取；
6. 对源码或构建方式有任何疑问，欢迎在本仓库开 Issue 索取。

### 2.6 直接覆盖包（自用）

「直接覆盖包」（`MO-补丁1+2-直接覆盖-<日期>.zip`）是把改好的客户端文件直接打包的版本，
装上就能用、不跑脚本。它**确实分发了 GPL 派生二进制**，因此：

- 包里同时带 `LICENSE`（GPL-3.0 全文）、`LICENSE-CnCNet-Client.md`（上游声明原文）、
  `使用说明.txt`，转发时必须原样带走，**不要删许可文件**；
- 对应源码 = 上游 <https://github.com/CnCNet/xna-cncnet-client> + 本仓库 `patch` 分支
  （`IL补丁工具\`、`中继\`、`脚本\`），任何人都能按第 4 节重建出同一份字节；
- 包内只有客户端层文件（见 2.4.1），没有任何 MO 本体文件；
- 它不是“原创客户端”，不要改个名字当成自己的作品对外分发。

### 2.7 发行格式：`.HMOL`（HMOLPKG v1）

直接覆盖包对外**只以 `patch.HMOL` 一个文件**发行（HMOL 启动器识别的容器格式）。
它本质是「明文 JSON 头 + AES-256-GCM 加密的标准 zip」，解密后就是上面那个压缩包，
安装链路完全不变。

- 格式规范、字节布局、密钥派生、解密步骤与**可直接粘贴的 C# 参考实现**：
  见 [`设计文档\HMOL包格式与解密说明.md`](设计文档/HMOL包格式与解密说明.md)；
- 打包/解包工具源码在本仓库 [`打包工具\HmolPack\`](打包工具/HmolPack)，
  一键脚本 `脚本\打包hmol.ps1`；
- 该格式只是**防误改、防随手解包**（共享密钥），不构成强保密；
- 与 2.6 的许可义务无关：`.HMOL` 里仍带 GPL 派生二进制，转发时同样要遵守 2.6，
  并同时提供源码地址。

---

## 3. 补丁包怎么用（就地安装）

补丁包里**没有客户端的 exe/dll 成品**，只有一个安装器
（包里那几个 .exe/.dll 都是本补丁自己的工具和 dnlib，不是客户端文件）：

1. 把补丁包解压到**任意位置**；
2. 双击 `安装.bat`：
   - 先自动找游戏根目录（含 `MentalOmegaClient.exe` 和 `Resources\clientdx.exe` 的那层），
     找不到会让你把游戏目录拖进窗口；
   - 检查客户端/游戏是否在运行（在运行会直接拒绝，**绝不半更新**）；
   - 把你机器上的原文件备份到 `<游戏根>\_**MO补丁备份**\`（只在你自己机器上）；
   - 在临时文件里跑完所有 IL 补丁步骤，成功后才覆盖回去；**中途任何一步失败会自动回滚**；
3. 想回到原版：双击 `还原.bat`。

> `_MO补丁备份\` 里是**你自己机器上的原版文件**，属于你的 MO 安装的一部分，
> 请不要把它一起发给别人。

---

## 4. 构建（从源码重建）

```powershell
powershell -ExecutionPolicy Bypass -File .\脚本\build-all.ps1
```

环境要求、完整流水线、每个 dnlib 模式的作用、以及所有踩过的坑，
都写在 **[构建说明.md](构建说明.md)**。

---

## 5. 免责声明

本补丁按「现状」提供，不附带任何明示或暗示的担保。
使用本补丁产生的任何后果（包括但不限于联机不同步、客户端异常、存档损坏）
由使用者自行承担。安装器会先自动备份，但仍建议你自己再备份一份 `Resources\` 目录。

**再次强调：本项目与 MO 开发组、MO 客户端官方无任何关联。**
