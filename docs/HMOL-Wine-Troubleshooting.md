# 🐛 问题排查 / Troubleshooting

> 启动器常见问题的系统化排查流程与日志分析方法。

***

## 📋 目录 / Contents

- [通用排查流程](#通用排查流程)
- [启动问题](#启动问题)
- [运行卡顿/崩溃](#运行卡顿崩溃)
- [网络问题](#网络问题)
- [游戏启动问题](#游戏启动问题)
- [插件包问题](#插件包问题)
- [登录问题](#登录问题)
- [日志分析](#日志分析)
- [重置启动器](#重置启动器)

***

## 🔧 通用排查流程 / General Flow

### Step 1: 收集信息

```powershell
# 系统信息
systeminfo | findstr /B /C:"OS Name" /C:"OS Version"

# 启动器版本
type version.json   # 期望: {"version": "2.4"}

# 启动器目录
dir HMOL_config.json
```

### Step 2: 查看日志

```powershell
# 主日志（当天）
type logs\HMOL_%date:~0,4%-%date:~5,2%-%date:~8,2%.log

# 最近 50 行
powershell Get-Content "logs\HMOL_$((Get-Date).ToString('yyyy-MM-dd')).log" -Tail 50

# 审计日志
type logs\audit_help.log

# 搜索 ERROR
findstr /C:"ERROR" /C:"Traceback" logs\*.log
```

### Step 3: 应用通用修复

| # | 操作                      | 适用     |
| - | ----------------------- | ------ |
| 1 | 关闭所有 HMOL.exe 进程后重启     | 临时卡死   |
| 2 | 重启电脑                    | 系统级问题  |
| 3 | 关闭杀软实时防护                | 误报/拦截  |
| 4 | 移到非系统盘                  | 权限问题   |
| 5 | 以管理员身份运行                | 权限问题   |
| 6 | 删除 HMOL\_config.json 重置 | 配置损坏   |
| 7 | 重装 VC++ 运行库             | DLL 缺失 |
| 8 | 重装最新 EXE                | 文件损坏   |

***

## 🚀 启动问题 / Startup Issues

### 双击无反应

| 症状     | 排查                                         |
| ------ | ------------------------------------------ |
| 完全无反应  | 任务管理器看是否启动；杀软隔离区；换 EXE 重新下载                |
| 闪一下就退出 | 查看 `logs/HMOL_<日期>.log` 末尾异常；EULA 拒绝触发清理退出 |
| 卡在启动画面 | 显卡驱动问题；关闭杀软                                |

### 错误弹窗

详见 [Error Codes](HMOL-Wine-Error-Codes#e10-eula-与启动错误) 的 E10 章节。

#### E10.01 EULA 拒绝

- **原因**：首次启动 EULA 点「不同意」
- **解决**：重新启动 → 点「同意」

#### E10.03 无法弹出 EULA 对话框

- **原因**：tkinter 初始化失败
- **解决**：
  1. 升级显卡驱动
  2. 删除 `HMOL_config.json` 让程序重建流程
  3. 重启电脑

#### E10.04 保存 EULA 状态失败

- **原因**：配置文件写入失败（权限/磁盘）
- **解决**：以管理员身份运行；检查磁盘空间

#### E10.05 拒绝 EULA 自动清理退出

- **正常行为**：拒绝后清理 temp/ 后立即退出
- **重新使用**：重启并同意

#### E10.07 缺少 cryptography / py7zr / rarfile

- **EXE 用户**：不应出现；若出现则 EXE 文件损坏，重新下载

***

## ⚙️ 运行卡顿/崩溃 / Runtime Issues

### 程序卡死

**临时处理**：

```
任务管理器 → 详细信息 → 找到 HMOL.exe → 结束任务
```

**根本排查**：

| 可能原因                | 排查方法        |
| ------------------- | ----------- |
| 一次性导入大量 OneDrive 资源 | 等待或减少单次操作   |
| 频繁调用 OneDrive API   | 检查网络；触发速率限制 |
| 杀软实时扫描              | 启动器目录加入白名单  |
| 磁盘 I/O 瓶颈           | 检查磁盘健康      |

### 程序崩溃（无错误信息）

1. 立即查看 `logs/audit_help.log` 是否有退出记录
2. 查看 `logs/HMOL_<日期>.log` 末尾
3. 寻找 `Traceback` 关键字定位异常

```powershell
# 查找最近异常
Select-String -Path "logs\HMOL_*.log" -Pattern "Traceback" | Select-Object -Last 1
```

### 自动退出

| 触发条件    | 日志关键字                    | 解决       |
| ------- | ------------------------ | -------- |
| 调试器附加   | `Debugger detected`      | 移除调试器后重启 |
| 虚拟机中运行  | `VM detected`            | 切换到物理 PC |
| 完整性校验失败 | `Integrity check failed` | 重新下载 EXE |
| EULA 拒绝 | `EULA rejected`          | 重新启动并同意  |

***

## 🌐 网络问题 / Network Issues

### 无法连接微软服务器

**症状**：微软登录、OneDrive 浏览、QQ 喊话均失败

**排查**：

```powershell
# 测试连通性
Test-NetConnection login.microsoftonline.com -Port 443
Test-NetConnection graph.microsoft.com -Port 443

# DNS 解析
nslookup login.microsoftonline.com
```

**常见原因与解决**：

| 原因     | 解决                              |
| ------ | ------------------------------- |
| DNS 污染 | 改 `8.8.8.8` / `114.114.114.114` |
| 代理/VPN | 关闭后重试                           |
| 防火墙    | 放行 `D:\HMOL\HMOL.exe`           |
| 公司网络限制 | 切换到手机热点测试                       |

### HTTP 拒绝

启动器**默认禁用 HTTP**。如必须使用 HTTP，设置环境变量：

```powershell
$env:HMOL_ALLOW_HTTP = "1"
HMOL.exe
```

> ⚠️ 仅供调试使用。生产环境请使用 HTTPS。

### TLS 握手失败

| 错误                               | 原因         | 解决              |
| -------------------------------- | ---------- | --------------- |
| `SSL: CERTIFICATE_VERIFY_FAILED` | 系统证书过期     | 更新 Windows；同步时间 |
| `SSL: WRONG_VERSION_NUMBER`      | 目标站点仅支持老协议 | 联系维护者           |

### 触发 SSRF 防护

如果 API URL 指向内网（127.0.0.1、192.168.x.x 等），会被**安全层自动拒绝**。这是安全特性，不是 Bug。

***

## 🎮 游戏启动问题 / Game Launch Issues

### E3.24 未找到游戏主程序

**症状**：点击"启动游戏" → 弹窗 `未找到游戏主程序 (Mental_Omega_client.exe)`

**排查**：

1. 在「实例管理」查看实例的"游戏路径"
2. 用资源管理器打开该路径
3. 确认 `Mental_Omega_client.exe` 存在
4. 若不存在 → 重新安装游戏
5. 若存在但启动器找不到 → 重新添加实例

### E3.25 启动失败

**症状**：点击"启动游戏" → 弹窗 `启动失败: <异常>`

**排查**：

```powershell
# 1. 直接双击 Mental_Omega_client.exe 测试
# 2. 若能启动 → 启动器问题，杀软拦截
# 3. 若不能 → 系统/游戏问题
```

**解决**：

1. 以管理员身份运行启动器
2. 关闭杀软实时防护
3. 在「设置 → 启动参数」清空参数
4. 安装 [VC++ 2015-2022 Redistributable](https://aka.ms/vs/17/release/vc_redist.x64.exe)
5. 升级显卡驱动

### 游戏启动后无窗口

**可能原因**：

- DirectX 版本不匹配
- 显卡驱动问题
- 显示器缩放设置
- 窗口被推出屏幕外

**解决**：

1. ` `切换显示模式
2. ` `切换窗口
3. ` `移动窗口
4. 结束进程后重试

### 游戏崩溃

1. 查看游戏目录下的日志（`debug.log` / `exception.log`）
2. 检查 MOD 兼容性
3. 在「实例管理」中**切换到备份的实例**测试

***

## 📦 包问题 / Package Issues

### E3.05/E3.06/E3.07 解压库缺失

```powershell
# 7Z 支持
pip install py7zr

# RAR 支持
pip install rarfile
# Windows 还需安装 WinRAR (含 unrar.exe)
```

### E3.10 解压失败

**原因**：压缩包损坏
**解决**：重新下载资源包；用 7-Zip 验证完整性

### E3.14 安装失败: 所有文件无法复制

**原因**：目标目录权限严重不足
**解决**：

1. 关闭游戏
2. 关闭杀软实时防护
3. 以管理员身份运行
4. 检查游戏目录权限

### E3.21 已安装但保存安装记录失败

**症状**：包已复制到游戏目录，但 `install_record.json` 写入失败

**影响**：**能玩但卸载时无法精确删除**

**解决**：

1. 检查 `instances/<id>/` 目录权限
2. 手动创建该目录并赋予写权限
3. 重新安装包

### E4.03 卸载失败

**原因**：部分文件被占用/权限不足
**解决**：

1. 关闭游戏
2. 关闭可能占用文件的程序（OBS、录屏软件等）
3. 手动删除残留文件

***

## 🔐 登录问题 / Login Issues

### E7.04 MSAL 初始化失败

```powershell
# 1. 检查网络
Test-NetConnection login.microsoftonline.com -Port 443

# 2. 重新下载 EXE
```

### E7.05 Token cache save failed

**原因**：加密令牌保存失败
**解决**：

1. 确认启动器目录可写
2. 以管理员身份运行
3. 删除 `msal_cache.bin` 后重新登录

### E7.07 无法启动设备代码流

**排查**：

1. 网络能否访问 Azure AD？
2. Client ID 是否正确？
3. 时钟是否同步？

```powershell
# 同步时间
w32tm /resync
```

### E7.10 请重启程序完成登录

**原因**：登录成功但需重启主程序加载令牌
**操作**：关闭启动器后重新启动

***

## 📊 日志分析 / Log Analysis

### 日志位置

```
启动器目录/
└── logs/
     └── HMOL_2026-07-20.log         # 主日志 (INFO/WARN/ERROR)
    
```

### 日志格式

```
2026-07-20 10:30:15 [INFO] HMOL_tk: 游戏实例创建成功 name=MO_主版本
2026-07-20 10:30:20 [WARN] HMOL_secure_io: 跳过只读文件 path=D:\Games\MO\readme.txt
2026-07-20 10:30:25 [ERROR] HMOL_crypto: AES-GCM 解密失败 reason=authentication failed
```

### 常用查询

```powershell
# 查询 ERROR
Select-String -Path "logs\HMOL_*.log" -Pattern "ERROR" | Select-Object -Last 20

# 查询 Traceback
Select-String -Path "logs\HMOL_*.log" -Pattern "Traceback" -Context 0,10

# 查询特定模块
Select-String -Path "logs\HMOL_*.log" -Pattern "HMOL_QQ"

# 按日期筛选
Select-String -Path "logs\HMOL_2026-07-20.log" -Pattern "ERROR"
```

### 导出日志

**方法 A**：启动器 → 设置 → 日志 → 「导出日志」

**方法 B**：手动打包

```powershell
Compress-Archive -Path "logs\*" -DestinationPath "hmol_logs_$(Get-Date -Format 'yyyyMMdd').zip"
```

***

## ♻️ 重置启动器 / Reset Launcher

> ⚠️ **这会删除所有实例、备份、令牌。操作前请手动备份** **`HMOL_config.json`。**

### 部分重置

仅删除配置和缓存：

```powershell
cd /d D:\HMOL
del HMOL_config.json
del msal_cache.bin
rmdir /s /q temp
```

实例、插件包、备份**保留**。

### 完整重置

删除所有用户数据：

```powershell
cd /d D:\HMOL
del HMOL_config.json
del HMOL_crypto_entropy.bin
del msal_cache.bin
rmdir /s /q instances
rmdir /s /q backups
rmdir /s /q packages
rmdir /s /q temp
rmdir /s /q logs
```

下次启动会像首次一样要求接受 EULA。

### 卸载启动器

1. 关闭启动器
2. 删除启动器所在目录
3. 删除桌面快捷方式
4. 从杀软白名单移除

***

## 🆘 仍未解决？

### 收集反馈信息

提交 Issue 时请附上：

1. **HMOL 版本**（`type version.json`）
2. **Wine 版本**（`systeminfo | findstr /B /C:"OS"`）
3. **当日日志**（`logs/HMOL_<日期>.log`）
4. **错误弹窗截图**
5. **复现步骤**

### 联系方式

- 🐛 [GitHub Issues](https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues)
- 💬 QQ 群 `1034243331`

### 相关页面

- ❓ [FAQ](HMOL-Wine-FAQ) — 常见问题
- 📋 [Error Codes](HMOL-Wine-Error-Codes) — 错误码速查
- 🔒 [Security and License](HMOL-Wine-Security-and-License) — 安全细节
- 🤝 [Contributing](HMOL-Wine-Contributing) — 反馈模板

***

**© 2026 HMOL Contributors. All Rights Reserved.**
