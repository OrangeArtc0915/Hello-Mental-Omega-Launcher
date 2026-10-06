# MO 客户端补丁 · 源码与构建说明

适用客户端：Mental Omega 的 CnCNet 客户端 **2.6.10.3**（`Resources\clientdx.exe` 那一套）。
本目录是把「两个补丁包」从原版文件重新构建出来的全部源码、脚本和步骤。

---

## 1. 两个补丁包各改了什么

| 补丁包 | 目标文件 | 工具模式 |
|---|---|---|
| **补丁1-中文输入** | `Resources\Binaries\Windows\MonoGame.Framework.dll` | `ime` |
| **补丁2-联机** | `Resources\clientdx.exe` / `clientogl.exe` / `clientxna.exe` | `lanip` → `relay` → `relaysend` → `relayhost` → `lobbyfix` → `kick` → `mention` → `shot` → `hashcheck` |
| **补丁2-联机** | `Resources\MoLanRelay.dll` + `Resources\Binaries\MoLanRelay.dll` | 由 `中继\MoLanRelay.cs` 编译 |

两组文件**完全不重叠**，所以两个包可以独立安装，也可以都装。

> 关于渲染器：客户端有三个 exe，各自使用 `Resources\Binaries\` 下的不同子目录
> （`clientdx.exe` → `Windows\`，`clientogl.exe` → `OpenGL\`，`clientxna.exe` → `XNA\`）。
> **中文输入补丁只改了 `Windows\` 那套**（`clientdx.exe`，也就是 MO 的默认渲染器）。
> `OpenGL\` 那套的 MonoGame 里根本没有 `WinFormsGameForm`（那是 SDL 构建），本补丁的改法不适用。

---

## 2. 目录结构

```
├─ 构建说明.md / README.md  ← 本文件（压缩包里叫「构建说明.md」，仓库里叫「README.md」）
├─ 中继\
│   └─ MoLanRelay.cs        ← 游戏内 UDP 中继（C#，编译成 DLL，不手写 IL）
├─ IL补丁工具\
│   ├─ Program.cs           ← dnlib 补丁器：所有模式都在这里
│   └─ dnlibpatch.csproj    ← net10.0 + PackageReference dnlib 4.4.0
├─ 脚本\
│   ├─ build-all.ps1        ← 一键：从原版文件重建两个补丁包（含自检）
│   ├─ build-relay.ps1      ← 只编译中继 DLL
│   ├─ JIT校验.ps1          ← 用 RuntimeHelpers.PrepareMethod 强制编译补过的方法
│   └─ 连通测试.ps1 / .bat  ← 本机监听 127.0.0.1:1233，验证直连真的连出来了
├─ 说明文本\
│   ├─ 补丁1-安装说明.txt   ← 会被 build-all.ps1 塞进补丁1.zip
│   └─ 补丁2-安装说明.txt   ← 会被 build-all.ps1 塞进补丁2.zip
└─ 设计文档\
    └─ 樱花Frp直连对接-客户端答复.md   ← 中继方案（路线 A）的设计与踩坑记录
```

---

## 3. 构建环境

| 需要 | 说明 |
|---|---|
| .NET SDK 10 | 编译 `IL补丁工具`（项目 target 是 net10.0）。开发机上用的是便携版 `d:\F\MO\CNC\.dotnet10\dotnet.exe`。 |
| Roslyn 的 `csc.dll` | 编译中继 DLL。随 SDK 一起在 `sdk\10.0.100\Roslyn\bincore\csc.dll`。 |
| .NET Framework 4.0 程序集目录 | `C:\Windows\Microsoft.NET\Framework64\v4.0.30319`（`mscorlib` / `System` / `System.Core`）。 |
| 原版（未打补丁）文件 | 三个 exe 和 `Windows\MonoGame.Framework.dll`。**必须是真原版**，见第 5 节。 |

**为什么中继不能直接 `dotnet build net48`**：这台机器的 SDK 不支持 net48 target，
所以改成直接调 `csc.dll`，显式 `-nostdlib+` 并手工列出 `mscorlib` / `System` / `System.Core`，
产物就是标准的 .NET Framework 4.0 程序集。

---

## 4. 一键构建

```powershell
powershell -ExecutionPolicy Bypass -File .\脚本\build-all.ps1
```

输出（默认到 `..\..\_build\`）：

```
_build\补丁1-中文输入\...          以及  MO-补丁1-中文输入.zip
_build\补丁2-联机\...              以及  MO-补丁2-联机.zip
```

脚本最后会做一次**自检**：把刚构建出来的 6 个文件和「当前已部署」的文件逐个比 SHA256，
全部显示 `一致` 才算成功（`-VerifyAgainst` 可以改，传空字符串则跳过自检）。

参数都在脚本头部的 `param` 里，换机器时改路径即可：

```powershell
# 例：原版文件在别处
.\脚本\build-all.ps1 -OriginalExeDir "D:\orig\exes" -OriginalMono "D:\orig\MonoGame.Framework.dll"
```

---

## 5. 流水线（每一步都用「原版 → 成品」逐字节比对验证过）

### 5.1 补丁1 · 中文输入

```
原版 Windows\MonoGame.Framework.dll (926,720 B)
        │  dnlibpatch <原版> <输出> ime
        ▼
      MonoGame.Framework.dll (903,680 B)     ← 与线上部署文件 SHA256 完全一致
```

`ime` 模式一次做完三件事（都在 `Microsoft.Xna.Framework.Windows.WinFormsGameForm`）：

1. `WndProc` 里：建/关联 IME 上下文（`ImmCreateContext` + `ImmAssociateContext` + `ImmSetOpenStatus`），
   并记录点击锚点、用 `ImmSetCompositionWindow` / `ImmSetCandidateWindow` 定位候选窗。
   **根因**：WinForms 不会给「没有子控件的窗口」创建 IME 上下文，所以输入法根本不知道这个窗口能输入。
2. 用 1×1 的 `CreateCaret` + `SetCaretPos` + `ShowCaret` 把「输入位置」告诉输入法
   （微软拼音走 TSF，只认光标矩形，光设 composition window 不够）。
3. `OnKeyPress` 里补 `e.Handled = true`，修「你好」发出去变「你好你好」。

> `Program.cs` 里还有个 `patch` 模式（把 `WM_IME_CHAR` 当 `WM_CHAR` 处理）和 `xna` 模式
> （改 `Rampastring.XNAUI` 的 `KeyboardEventInput::HookProc`）。**最终方案里这两个都没有使用**：
> - XNAUI 那边上游本身就带 `WM_IME_CHAR(646)` 的处理（三个渲染器的 XNAUI 都验证过），不用改；
> - 最终生效的是 `ime` 模式（`ime → patch` 与线上文件不一致，单独 `ime` 才逐字节一致）。

### 5.2 补丁2 · 联机（三个 exe）

```
原版 clientdx.exe / clientogl.exe / clientxna.exe
        │  ① dnlibpatch <in> <out> lanip       底部「地址输入框」回车直连；TCP 端口取 EndPoint.Port；lanip.txt 对接
        │  ② dnlibpatch <in> <out> relay       spawn.ini 的 [OtherN] Ip/Port 改写 + 拉起中继；识别 MO-RELAY 帧
        │  ③ dnlibpatch <in> <out> relaysend   LANPlayerInfo::SendMessage 改走 Relay.SendLocked（共用写锁）
        │  ④ dnlibpatch <in> <out> relayhost   LANGameLobby / LANGameLoadingLobby 的 SetUp 注册 client；
        │                                      SendMessageToHost 改走 Relay.SendClientLocked
        │  ⑤ dnlibpatch <in> <out> lobbyfix    HandleFileHashCommand 补判空（修上游 NRE 崩溃）
        │  ⑥ dnlibpatch <in> <out> kick        GameLobbyBase::KickPlayer 换成中继实现（局域网踢人生效）
        ▼
      clientdx.exe / clientogl.exe / clientxna.exe   ← 三个都与线上部署文件 SHA256 完全一致
```

**顺序不能变**：`relay` 模式开头会检查「MO-RELAY 补丁是否已打过」（看有没有 `MoLanRelay.Relay` 的类型引用），
而 `relaysend` / `relayhost` 都会引入这个引用 —— 所以 `relay` 必须排在它们前面。
另外两个 exe（`clientdx` / `clientogl`）的 **局域网 4 项修复**（队伍 A/B/C/D、spawn.ini 越界、already connected、AI 裁剪）
已经包含在 `lanip` 模式里，不需要单独一步。

### 5.3 补丁2 · 中继 DLL

```
中继\MoLanRelay.cs
        │  csc -noconfig -nostdlib+ -target:library -platform:anycpu -optimize+ -deterministic+
        │      -reference:mscorlib / System / System.Core
        │      -reference:<原版 clientxna.exe>          ← 提供 LANPlayerInfo 类型
        │      -reference:Resources\Binaries\Windows\*.dll
        ▼
      MoLanRelay.dll (15,360 B)
```

`-deterministic+` 是为了让产物可复现（不加的话 csc 会写随机 MVID/时间戳，两次编译哈希都不一样）。

**为什么必须引用原版 exe**：打过补丁的 exe 强名称签名已失效，
拿它当引用会报 `error CS0009: 公钥无效`。
**为什么只取 `Binaries\Windows\*.dll`**：`Binaries\` 和 `Binaries\Windows\` 里有同名程序集，
两个目录一起引用会报 `CS1704`（引用重复）。

---

## 6. 工具与验证

`IL补丁工具` 的用法：

```
dnlibpatch <in> <out> <mode>
```

| mode | 作用 |
|---|---|
| `ime` | **补丁1**：MonoGame 的 IME 上下文 + caret 候选框定位 + `Handled` |
| `lanip` | **补丁2 ①**：局域网修复 + 底部地址输入框（回车直连）+ `lanip.txt` |
| `relay` | **补丁2 ②**：`WriteSpawnIni` 改写 `[OtherN] Ip/Port` 并拉起中继；两处消息入口识别 `MO-RELAY` |
| `relaysend` | **补丁2 ③**：`LANPlayerInfo::SendMessage` → `Relay.SendLocked` |
| `relayhost` | **补丁2 ④**：两个大厅类的 `SetUp` 注册 client，`SendMessageToHost` → `Relay.SendClientLocked` |
| `lobbyfix` | **补丁2 ⑤**：`LANGameLobby::HandleFileHashCommand` 补判空（修上游漏判空导致的崩溃） |
| `kick` | **补丁2 ⑥**：`GameLobbyBase::KickPlayer` → `Relay.KickPlayer`（局域网踢人生效；在线大厅有自己的重写，不受影响） |
| `mention` | **补丁2 ⑦**：`LANGameLobby::Player_HandleChatCommand` 里挂 `Relay.MentionColor`（@ 我 → 金色 + 提示音） |
| `shot` | **补丁2 ⑧**：`LANGameLobby::Update` 挂 `Relay.Tick`（F5/F6 热键 + F7 按住语音都在这里轮询）；两个消息入口挂 `MO-CTRL` 识别（截图 / 语音 / 版本自检共用这条通道）。**截图显示已撤掉**（弹窗会抢走客户端焦点 → 客户端停止绘制 → 看起来卡死+没鼠标），待改成"聊天区内联显示"再放出来 |
| `hashcheck` | **补丁2 ⑨**：`LANGameLobby::HandleFileHashCommand` 开头挂 `Relay.FileHashResult`（文件不一致时给明确提示） |
| `patch` / `xna` | 早期方案，最终未使用（见 5.1 的说明） |
| `checkstack` | 全模块栈自检 |
| `types` / `methods` / `refs` / `dumpm` / `pinvokes` | 只读的探查工具，用来定位类型/方法/指令/引用/P-Invoke |

跑完每个补丁模式都会顺带对改动过的方法做**自写栈校验**（BFS 模拟栈深度，含
「下溢」「分支汇合处深度不一致」「`ret` 时栈必须为空」三条规则）。
改动过之后再用 `脚本\JIT校验.ps1` 强制 JIT 一遍补过的方法：

```powershell
# 32 位 PowerShell（游戏 exe 是 32 位，64 位下会 BadImageFormatException）
C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe `
  -File .\脚本\JIT校验.ps1 -folder <含 exe 和依赖的目录> -asmFile clientxna.exe `
  -typeName DTAClient.DXGUI.Multiplayer.GameLobby.LANGameLobby -methodName SetUp
```

---

## 7. 踩过的坑（改代码前建议先看）

**IL 改写**

1. **不要用 Mono.Cecil**：对 MonoGame 这种程序集，Cecil 连「只读+回写」都会产出非法 IL。用 dnlib。
2. **栈必须平衡，且 `ret` 时栈必须为空**。曾经因为拼 payload 时多压了一对 `ldarg.0`，整块结尾多 2 个栈元素 → `InvalidProgramException`。
   定位办法是二分：用截断到第 N 条指令（`MO_LIMIT` 那个调试开关）逐步缩小范围。
3. **不能从外部跳进 try 区域**。所以直连入口插在 `TbChatInput_EnterPressed`（无 try），
   而不是 `BtnJoinGame_LeftClick`（有 try）。
4. **dnlib 没有 `PInvokeInfo`**，是 `MethodDefUser.ImplMap = new ImplMapUser(moduleRef, name, PInvokeAttributes.CallConvWinapi)`。
5. `Ldarg` / `Ldfld` 会消耗栈上的 `this` —— 顺序错了就是「下溢」，靠栈自检才发现。
6. 找不到的字段要**全模块扫描**：例如 `PLAYERNAME` 是静态属性 `get_PLAYERNAME`（不是字段），`GamePath` 是字段。

**协议 / 寻址**

7. `spawn.ini` 的段是 `[Other1]`、`[Other2]`…，**序号从 1 开始**；本地中继端口必须是 `12340 + 序号`，
   写成 0-based 会整体差 1、包全部落空。
8. **客机侧 `Players` 里的 `LANPlayerInfo.TcpClient` 是 `null`**，真正通往房主的是
   `LANGameLobby::client`（客机由 `SetUp` 保存调用方传入的那条连接）—— 这就是 `relayhost` 存在的原因。
9. 帧和大厅消息**共用同一条 TCP 流**，两边的写必须共用同一把锁（`sendLock`），否则会互相交错写坏。
10. 客户端把 `client` 注册进来以后，`SendToHost` 才真正发得出去；否则是**静默丢弃**。

**部署**

11. `MoLanRelay.dll` **要放两个位置**：`Resources\` 和 `Resources\Binaries\`。
    客户端实际加载的是 `Resources\` 那份，但两个位置版本不一致时非常难查。
12. 中继日志第一行是版本横幅 + 实际加载的 DLL 路径：
    ```
    ======== MoLanRelay v2026-10-04.4 ========
    载入的 DLL: D:\...\Resources\MoLanRelay.dll
    ```
    **多人联机出问题，先比对几台机器这一行**——版本不一致会出现「某台能收不能发」这类诡异现象。
13. **PowerShell 脚本里的中文必须带 UTF-8 BOM**，否则 PowerShell 5.1 会按 GBK 读，中文变乱码、脚本语法都报错。

**上游 Bug（不是我们引入的，但顺手修了）**

14. `LANGameLobby::HandleFileHashCommand` 里
    `PlayerInfo pInfo = Players.Find(p => p.Name == sender); pInfo.Verified = true;`
    **没有判空**。只要发送者的连接还活着、但它已经不在 `Players` 里
    （典型场景：退出大厅重新建房后旧连接残留，之后又收到一条旧消息），
    就会 `未将对象引用设置到对象的实例。` → 整个客户端 KABOOOOOOM。
    在线大厅 `CnCNetGameLobby` 同一处写的是 `if (pInfo != null)`，只有局域网这份漏了 ——
    这就是 `lobbyfix` 模式存在的原因（原版 exe 里同样没有判空，可以拿原版验证）。
