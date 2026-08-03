# ⚙️ 功能模块详解 / Features

> HMOL 启动器所有功能的深度文档：原理、配置、最佳实践。

***

## 📋 目录 / Table of Contents

1. [实例管理](#实例管理)
2. [插件包管理](#插件包管理)
3. [实例导出/导入](#实例导出导入)
4. [备份与还原](#备份与还原)
5. [笨蛋广场 (OneDrive)](#笨蛋广场-onedrive)
6. [QQ 喊话](#qq-喊话)
7. [DLC 启动器](#dlc-启动器)
8. [主题与外观](#主题与外观)
9. [侧边栏布局编辑器](#侧边栏布局编辑器)
10. [EULA 流程](#eula-流程)
11. [加密凭据存储](#加密凭据存储)
12. [安全审计](#安全审计)
13. [错误码手册](#错误码手册)

***

## 📂 实例管理 / Instance Management

### 设计目标

支持同一台机器上**多版本心灵终结共存**（如 v3.3.6 + 私人 mod），每个实例：

- 独立的游戏目录
- 独立的插件安装记录
- 独立的备份集
- 独立的导出包

### 数据结构

```
launcher_root/
├── HMOL_config.json           # 全局配置
└── instances/
    ├── <uuid-1>/
    │   ├── instance.json      # 实例元数据
    │   ├── install_record.json # 每个包安装的文件清单
    │   └── ...
    ├── <uuid-2>/
    └── ...
```

### instance.json 字段

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "name": "MO_主版本",
  "game_path": "D:\\Games\\MentalOmega",
  "created_at": "2026-07-20T10:30:00",
  "updated_at": "2026-07-20T11:15:00",
  "last_launched": "2026-07-20T14:00:00",
  "installed_packages": ["map_pack_2025", "campaign_cn"],
  "notes": "主用实例"
}
```

### 切换实例的内部流程

1. 关闭当前游戏子进程（如有）
2. 卸载当前实例的 `install_record.json` 内存映射
3. 加载目标实例的元数据
4. 更新 UI 状态栏
5. 触发 `<<Configure>>` 事件刷新插件包列表

### 限制

- **同一游戏目录只能绑定一个实例**（避免文件冲突）
- 实例名称必须全局唯一
- 删除实例前需先关闭游戏

***

## 📦 包管理 / Package Management

### 支持的格式

| 格式         | 必需依赖                    | 说明    |
| ---------- | ----------------------- | ----- |
| **`.zip`** | 无（标准库）                  | 推荐格式  |
| **`.7z`**  | `py7zr >= 0.21`         | 高压缩比  |
| **`.rar`** | `rarfile` + `unrar` CLI | 兼容旧资源 |

### 包识别规则

启动器通过以下规则识别包类型：

1. **标准任务包**：包含 `map.ini` 或 `mission.ini`
2. **资源包**：包含 `resources/` 目录
3. **整合包**：包含 `Mental_Omega_client.exe`（覆盖式安装）
4. **未知包**：按通用文件复制方式处理

### install\_record.json 结构

精确记录每个包安装的每个文件：

```json
{
  "package_name": "map_pack_2025",
  "version": "1.0",
  "installed_at": "2026-07-20T10:30:00",
  "files": [
    "Maps\\001.map",
    "Maps\\002.map",
    "INI\\MapPack.ini"
  ]
}
```

> ✅ 卸载时**只删除**这些文件，**绝不误删**其他包的内容。

### 安装流程

1. **解压**到 `temp/<package>/`
2. **冲突检测**：扫描游戏目录，列出将被覆盖的文件
3. **冲突确认**：弹出"覆盖 N 个文件"对话框
4. **复制**：使用 `shutil.copy2` 保留元数据
5. **记录写入**：`install_record.json` 原子写入
6. **清理**：删除 `temp/`

### 卸载流程

1. 读取 `install_record.json`
2. 逐个删除记录的文件
3. 收集**未找到**的文件列表（可能已被用户手动删除）
4. 询问是否清理 `install_record.json`
5. 二次确认

***

## 📤 实例导出/导入 / Export & Import

### 导出格式

| 格式  | 压缩比 | 速度 | 兼容性       |
| --- | --- | -- | --------- |
| ZIP | 中   | 快  | ✅ 所有系统    |
| 7Z  | 高   | 中  | 需 `py7zr` |
| RAR | 中   | 中  | 需 `unrar` |

### 导出目录结构

```
<实例名>_<时间戳>.zip
├── instance.json
├── game_files/
│   ├── Mental_Omega_client.exe
│   ├── RA2.exe
│   ├── Maps/
│   └── ...   (整个游戏目录内容)
└── manifest.json   (版本、校验和、文件数)
```

### 导入验证

- 解压 → 验证 `instance.json` 存在 → 验证 `game_files/` 含 `Mental_Omega_client.exe` → 注册到实例列表

### 异常处理

| 异常                 | 处理                  |
| ------------------ | ------------------- |
| 压缩包损坏              | 提示"ZIP/7Z 文件已损坏"并退出 |
| `instance.json` 缺失 | 提示"无效的实例文件"         |
| 游戏可执行文件缺失          | 提示"不是有效的心灵终结游戏目录"   |
| 目标目录无写权限           | 提示"权限不足"并建议以管理员运行   |

***

## 💾 备份与还原 / Backup & Restore

### 备份策略

- **全量备份**（v2.4 默认）：备份游戏目录全部内容
- **差异备份**：仅备份自上次备份以来的修改（未来版本）

### 备份目录结构

```
backups/
├── <实例ID>/
│   ├── <备份名>_<时间戳>/
│   │   ├── manifest.json
│   │   └── ...  (游戏目录内容)
│   └── ...
```

### manifest.json

```json
{
  "backup_name": "原版_20260720",
  "instance_id": "550e8400-...",
  "created_at": "2026-07-20T10:30:00",
  "game_path": "D:\\Games\\MentalOmega",
  "file_count": 1247,
  "total_size": 524288000,
  "checksum_sha256": "abc123..."
}
```

### 还原确认

```
⚠️ 全量恢复确认
即将覆盖游戏目录的所有现有内容:
D:\Games\MentalOmega

备份: 原版_20260720 (2026-07-20 10:30:00)
文件数: 1247
大小: 500 MB

是否继续?  [是]  [否]
```

### 名称校验

- 长度：1-100 字符
- 不允许：`< > : " / \ | ? *`、控制字符
- 不允许：Windows 保留名 `CON / PRN / AUX / NUL / COM1-9 / LPT1-9`

***

## ☁️ 笨蛋广场 (OneDrive) / SharePoint

> 内部模块名: SharePoint 资源浏览。UI 名称: 「笨蛋广场」。

### 架构

```
启动器
  ↓ (MSAL 设备代码流)
Microsoft Identity Platform
  ↓ (访问令牌)
Graph API / SharePoint REST
  ↓ (文件元数据 + 下载 URL)
本地 temp/ → 用户选择保存
```

### 登录流程（设备代码流）

1. 启动器向 Azure AD 请求设备代码
2. 返回：`https://microsoft.com/devicelogin` + 8 位用户码
3. 浏览器打开链接，用户输入码并登录
4. 启动器轮询 token endpoint
5. 收到 access\_token + refresh\_token
6. 令牌**加密**保存到 （AES-256-GCM）

### 资源浏览

- 通过 `share_url` 初始化 SP session
- 调用 `/_api/web/GetList` 获取文件列表
- 文件夹标 `[文件夹]`，可双击递归进入
- 文件标 `[文件]`，可下载

### 安全

- **仅公开共享链接**：MSA 个人账户必须将链接设为「知道链接的任何人可查看」
- **SSRF 防护**：禁止访问 10/8、172.16/12、192.168/16 等内网
- **TLS 1.2+** 强制
- **HTTPS-only**（HTTP 完全屏蔽）

### 已知限制

- SharePoint `nextLink` 解析依赖 OData 协议
- 单个分享链接可能有带宽/频率限制
- 不支持编辑/上传，仅浏览+下载

***

## 📢 QQ 喊话 / QQ Bot Shout

### 概述

通过 QQ 开放平台机器人 API，将联机房间信息发布到指定频道和群。

### 喊话内容字段

| 字段    | 字数上限 | 必填 | 说明         |
| ----- | ---- | -- | ---------- |
| MO 版本 | 10 字 | ✅  | 例 "v3.3.6" |
| 房间名   | 15 字 | ✅  | 例 "今晚八点对战" |
| 房间密码  | 10 字 | ❌  | 留空表示无密码    |

**总字数限制：500 字**

### 速率限制

- **5 次 / 分钟**（触发后需等待 N 秒）
- 同时发送到QQ 群

### 审核规则

禁止内容：

- ❌ 任何 URL（http/https/[www./baidu.com](http://www./baidu.com) 等）
- ❌ 混淆 URL（h t t p / h-t-t-p / 中文点号等）
- ❌ 侮辱性/脏话词汇
- ❌ 骚扰性内容

### 错误反馈

| 错误码 | 含义       |
| --- | -------- |
| 401 | 令牌过期/无效  |
| 403 | 机器人无发言权限 |
| 429 | 触发速率限制   |
| 5xx | QQ 服务端异常 |

详见 [Error Codes › E6](HMOL-Wine-Error-Codes#e6-qq-喊话错误)

***

## 🛒 DLC 启动器 / DLC Launcher

### DLC 目录结构

```
DLC/
├── DLC.json                  # 清单
├── 内存优化/
│   ├── DLC.json
│   ├── Game.HMOL
│   └── ...
└── VSC/
    ├── DLC.json
    ├── Yuri.HMOL
    └── ...
```

### 启动流程

1. 解析 `DLC.json` → 读取 `main_path`
2. 验证文件存在 + 有执行权限
3. 监控子进程状态

***

## 🎨 主题与外观 / Theme & Appearance

### 主题实现

- **CSS 风格**：tkinter 不支持 QSS，但可通过 `ttk.Style()` 配置
- **颜色常量**：每套主题导出 \~30 个色值
- **运行时切换**：无需重启

### 自定义背景图

- 支持格式：PNG、JPG（推荐 PNG）
- 大小限制：≤ 10 MB
- 存储：`HMOL_config.json` 中保存相对路径
- 渲染：Pillow 缩放至窗口大小

### 字体

- 主字体：`Microsoft YaHei UI`（含中文）
- 等宽字体：`Cascadia Code`（日志）
- 可调：10 / 11 / 12 / 14 pt

***

## 📐 侧边栏布局编辑器 / Sidebar Layout Editor

> v2.4 起开放给最终用户。可视化拖拽 + YAML 持久化。

### 数据结构

```yaml
# sidebar_layout.yaml
version: 1
sidebar_width: 0.18   # 占主窗口宽度的 18%
items:
  - id: home
    name: 首页
    icon: home
    visible: true
    order: 1
  - id: instances
    name: 实例管理
    icon: instances
    visible: true
    order: 2
  - id: packages
    name: 插件包
    icon: packages
    visible: true
    order: 3
  # ...
```

### 几何存储

- **以分数存储**（0..1 占父容器比例），窗口缩放自动适配
- **编辑时以像素操作**（流畅），释放时回到分数

### 拖拽算法

1. 按下 → 记录起始坐标
2. 移动 ≥ 6 px → 标记为「拖拽」模式
3. 实时绘制半透明覆盖层指示插入位置
4. 释放 → 通过 `winfo_containing(x, y)` 查找目标位置
5. 更新 `order` 字段 → 重排 UI

### 撤销/重做

- 每次操作压入快照栈
- "取消" 按钮恢复会话前状态
- 退出编辑模式（保存=true）时清空栈

### 导入/导出

- "导出方案" → 写入 `<name>.yaml`
- "导入方案" → 验证 YAML 格式 → 替换当前布局

### 防误触

- 拖拽阈值：6 px（避免吃掉普通点击）
- `ButtonRelease-1` 返回 `"break"` 抑制原点击事件
- 编辑模式外所有拖拽绑定被禁用

***

## 📜 EULA 流程 / EULA Flow

### 触发时机

1. **首次启动** — 弹出 EULA 对话框
2. **版本变更** — EULA 内容更新后再次启动时

### 流程

```
启动
  ↓
检查 HMOL_config.json 中 EULA 版本
  ├─ 不存在或版本不匹配
  │   ↓
  │   弹出 EULADialog (模态)
  │     ├─ 同意 → 写入 accepted_version → 进入主界面
  │     └─ 拒绝 → 清理 temp/ → os._exit(0)
  │
  └─ 已接受且版本一致
      ↓
      直接进入主界面
```

### 状态管理

- 接受记录：`HMOL_config.json` → `eula_accepted_version`
- 撤销接受：设置页 → 法律与协议 → 撤销（下次启动需重新接受）
- **拒绝不会强制退出**（仅设置页面）：仅记录状态

***

## 📚 错误码手册 / Error Codes

完整 149+ 条错误码见 [Error Codes](HMOL-Wine-Error-Codes) 页面，包括：

- **E1 实例管理**（14 条）
- **E2 导入/导出**（23 条）
- **E3 插件包安装**（26 条）
- **E4 插件包卸载**（11 条）
- **E5 备份/恢复**（10 条）
- **E6 QQ 喊话**（11 条）
- **E7 OneDrive/微软**（11 条）
- **E8 DLC 启动**（6 条）
- **E9 通用 UI/系统**（27 条）
- **E10 EULA/启动**（10 条）

每条均含：**原始提示、可能原因、解决方法**。

***

## 📚 下一步 / Next Steps

- ❓ [FAQ](HMOL-Wine-FAQ) — 常见问题
- 🔒 [Security and License](HMOL-Wine-Security-and-License) — 安全细节
- 🐛 [Troubleshooting](HMOL-Wine-Troubleshooting) — 问题排查

***

**© 2026 HMOL Contributors. All Rights Reserved.**
