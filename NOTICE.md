# 第三方组件与许可（NOTICE）

本文件列出**本项目自己分发**的东西里包含的第三方组件及其许可，
以及上游客户端相关许可声明的位置。

---

## 1. 本项目分发的文件里，哪些是第三方组件

| 分发的文件 | 是什么 | 许可 | 许可文本在哪 |
|---|---|---|---|
| `tools\dnlib.dll` | **dnlib 4.4.0**（IL 读写库，我们的补丁器依赖它） | MIT | 本文件第 2 节（全文） |
| `tools\dnlibpatch.exe` | 本项目用 `csc` 编译的补丁器（源码：`IL补丁工具\Program.cs`） | GPL-3.0-or-later | 仓库根 `LICENSE` |
| `tools\MoLanRelay.dll` | 本项目编译的中继（源码：`中继\MoLanRelay.cs`） | GPL-3.0-or-later | 仓库根 `LICENSE` |
| `tools\install.ps1`、`tools\restore.ps1` | 本项目的安装/还原脚本 | GPL-3.0-or-later | 仓库根 `LICENSE` |
| `LICENSE-CnCNet-Client.md` | **上游 CnCNet 客户端的许可声明原文**（见第 3 节） | 该文件自身的说明 | 该文件 |

**本项目不分发**：MO 客户端、CnCNet 客户端的任何二进制文件（`clientdx.exe`、
`clientogl.exe`、`clientxna.exe`、`MonoGame.Framework.dll` 等）。
这些文件由 `tools\install.ps1` 在**使用者自己的机器上**就地修改。
`dnlib.dll` 是唯一随包分发的第三方二进制，它是 MIT 许可、允许自由再分发。

---

## 2. dnlib（MIT）

`tools\dnlib.dll` 是 dnlib 4.4.0（<https://github.com/0xd4d/dnlib>）的 `net45` 目标，
原样取自 NuGet 包 `dnlib 4.4.0`，未做任何修改。其许可全文如下：

```
Copyright (C) 2012-2019 de4dot@gmail.com

Permission is hereby granted, free of charge, to any person obtaining
a copy of this software and associated documentation files (the
"Software"), to deal in the Software without restriction, including
without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to
the following conditions:

The above copyright notice and this permission notice shall be
included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY
CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

---

## 3. 上游 CnCNet 客户端（GPL-3.0-or-later）与其中捆绑的第三方组件

本补丁**修改的对象**是 Mental Omega 所带的 CnCNet 客户端可执行文件，
该客户端来自开源项目 **XNA CnCNet Client**：

- 项目地址：<https://github.com/CnCNet/xna-cncnet-client>
- 版权：© 2013-2026 CnCNet, Rampastring
- 许可：**GNU GPL-3.0-or-later**

`LICENSE-CnCNet-Client.md` 是该上游仓库的许可声明原文（内容未做任何改动，
仅文件名不同），其中同时包含它捆绑的第三方组件通知，包括但不限于：

| 组件 | 许可 |
|---|---|
| Rampastring.Tools、Rampastring.XNAUI | MIT（© Rami "Rampastring" Pasanen） |
| MonoGame（客户端仓库里附带的编译产物） | MS-PL |
| Mono.Xna 相关部分 | MIT（© The Mono.Xna Team） |
| ClientUpdater、SecondStageUpdater | GNU GPL-3.0 |
| Newtonsoft.Json | MIT |
| Discord RPC（C# 实现） | MIT |
| lzo.net | MIT |
| OpenMCDF | Mozilla Public License |

本补丁**不修改**上述任何组件本身，只是在运行时调用它们已有的公开类型与方法。

---

## 4. 本项目自己的许可

本项目的全部内容（`IL补丁工具\`、`中继\`、`脚本\`、文档）均以
**GNU GPL-3.0-or-later** 发布，全文见仓库根目录的 [`LICENSE`](LICENSE)，
不额外附加任何限制。源码地址见 [`README.md`](README.md) 第 0 节。
