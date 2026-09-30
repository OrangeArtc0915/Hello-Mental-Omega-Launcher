# 第三方组件

本程序（Hello Mental Omega Launcher）自身为专有软件、保留所有权利（见仓库根
`LICENSE`）。本程序使用的第三方组件分两类：**随包分发的二进制**（运行时被调用）
与**编译期引用的库**。这些组件受其各自许可证约束，其许可**不受本程序许可声明影响**。

## 7-Zip（独立命令行 7za.exe）

| 项 | 内容 |
|---|---|
| 路径 | `runtime\7zip\7za.exe`（附 `runtime\7zip\License.txt`） |
| 版本 | 7-Zip 26.03 (x64)，2026-09-03 |
| 来源 | https://www.7-zip.org/download.html → `7z2603-extra.7z`（GitHub 官方镜像 `ip7z/7zip` 的 release 资产） |
| 许可 | GNU LGPL 2.1 或更高（另附 unRAR 限制条款，因其内含 RAR 解压代码） |
| 用途 | **仅用于创建 .7z 压缩包**（实例导出） |

### 为什么必须外挂

项目已引的 SharpCompress 只能**写** Zip / Tar / GZip，**没有 7z 写入能力**；7z 的容器格式也没有可用的托管实现。因此实例导出 `.7z` 依赖这个独立命令行程序。

### 为什么不做 .rar 导出

RAR 是专有格式，**没有任何开源库能创建 rar**，7-Zip 同样**不能**创建 rar。唯一途径是调用 WinRAR 自带的 `rar.exe`，而那是商业软件、不能随包分发。因此：

- **导入** `.rar` 可行（解压只依赖程序自身的解压实现，不需要外部组件）
- **导出** `.rar` 不可行，界面只提供 `.zip` 与 `.7z`

### 分发说明

7-Zip 采用 LGPL，允许随商业/自由软件一同分发。若你替换 `runtime\7zip\7za.exe`，请一并更新本文件与同目录下的 `License.txt`，并确认替换版本的许可仍然允许再分发。

## RePKG（壁纸包解包工具，内嵌）

| 项 | 内容 |
|---|---|
| 路径 | `src\HMOL.Core\Resources\RePKG.exe`（编译期内嵌资源，运行时释放到 `Data\Cache`） |
| 来源 | https://github.com/notscuffed/RePKG |
| 许可 | MIT |
| 用途 | 解包 Wallpaper Engine 壁纸包（用于主页背景素材导入） |

## 组网组件（`runtime\`，仅「联机」功能使用）

联机功能调用的组网二进制，均为发行时随包分发，本程序只调用、不改写、不反编译：

| 组件 | 路径 | 许可 |
|---|---|---|
| EasyTier | `runtime\easytier\` | Apache-2.0 |
| n2n | `runtime\n2n\` | GPL-3.0 |
| TAP-Windows 驱动（含 tapinstall / devcon，OpenVPN 项目） | `runtime\tap\` | 见其上游发布 |
| WinIPBroadcast | `runtime\winipbroadcast\` | 见其上游发布 |

## 编译期引用的第三方库

以下库参与编译、随程序一同分发，受各自许可证约束：

| 库 | 用途 | 许可 |
|---|---|---|
| lucide | 界面图标（SVG，`src\HMOL.App\Assets\IconPacks\lucide\`） | ISC（部分图标 MIT，见同目录 `LICENSE.txt`） |
| NAudio.Core / NAudio.Wasapi / NAudio.WinMM 2.2.1 | 背景音乐解码与播放 | MIT |
| SharpCompress 0.38.0 | zip / tar / gzip 解压 | MIT |
| MQTTnet 4.3.7.1207 | 联机大厅 MQTT 通信 | MIT |

如需更换上述任一组件或其版本，请一并更新本文件。
