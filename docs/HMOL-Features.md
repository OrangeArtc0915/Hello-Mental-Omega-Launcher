# 🧩 功能模块详解

> 本章深入讲解 HMOL 启动器各核心模块的设计原理、技术实现与使用方法。

***

## 1. 实例管理模块 (Instance Manager)

### 1.1 数据结构

每个实例对应 `instances/<实例ID>/` 目录:

```
instances/
└── <UUID>/
    ├── config.json            # 实例配置
    └── install_record.json    # 精确安装记录
```

#### config.json

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "name": "MO_主版本",
  "path": "D:\\Games\\MentalOmega",
  "note": "主版本+部分地图",
  "created_at": "2026-07-20T10:30:00",
  "updated_at": "2026-07-22T15:42:11",
  "installed_packages": {
    "map": ["map_test.map"],
    "mod": ["BetterUI_v1.2"]
  }
}
```

#### install\_record.json

```json
{
  "<package_name>": {
    "package_type": "mod",
    "installed_at": "2026-07-20T10:35:00",
    "files": [
      "Maps/Custom/some.map",
      "Resources/some.bmp"
    ],
    "source_archive": "BetterUI_v1.2.zip"
  }
}
```

### 1.2 核心操作

| 函数                              | 功能     | 关键校验                          |
| ------------------------------- | ------ | ----------------------------- |
| `add_instance(name, path)`      | 新建实例   | 路径含 Mental\_Omega\_client.exe |
| `update_instance(id, **kwargs)` | 更新实例   | 不能与其他实例路径冲突                   |
| `delete_instance(id)`           | 删除实例   | 仅删除实例配置,不动游戏目录                |
| `switch_instance(id)`           | 切换实例   | 触发跨线程信号刷新 UI                  |
| `list_instances()`              | 列出所有实例 | 启动时扫描 instances/              |

### 1.3 路径校验

```python
[用户点击卸载]
      │
      ▼
[二次确认]
      │
      ▼
[查找 install_record.json 中的精确记录]
      │
      ├─ 找到 → [精确卸载]
      │           ├─ 删除记录中列出的所有文件
      │           └─ 从 MO 备份还原被覆盖的原版文件
      │
      └─ 未找到 → [全量恢复(危险)]
                  ├─ 删除游戏目录全部内容
                  └─ 从 MO 原版备份复制
```

***

## 2. 包管理模块 (Package Manager)

### 2.1 支持的格式

| 扩展名    | 解析库           | 备注                     |
| ------ | ------------- | ---------------------- |
| `.map` | 直接复制          | 单文件,安装到 `Maps/Custom/` |
| `.zip` | `zipfile`(内置) | 标准支持                   |
| `.7z`  | `py7zr`       | 需 `pip install py7zr`  |
| `.rar` | `rarfile`     | 需系统安装 unrar/WinRAR     |

### 2.2 安装流程

```
[用户点击安装]
      │
      ▼
[确认对话框] ──取消──→ 退出
      │
      ▼
[创建进度对话框]
      │
      ▼
[若为压缩包:解压到 temp/]
      │
      ▼
[扫描解压结果,识别 game_files / root]
      │
      ▼
[目标位置冲突检测]
      ├─ 无冲突 → 直接复制
      ├─ 单文件冲突 → 询问覆盖
      └─ 目录文件冲突 → 三选项弹窗
              ├─ 覆盖全部
              ├─ 跳过已有
              └─ 取消
      │
      ▼
[文件级合并复制(不删除游戏根目录)]
      │
      ▼
[完整性校验:目标目录非空]
      │
      ▼
[写入 install_record.json]
      │
      ▼
[清理临时目录 + 关闭进度条]
```

### 2.3 卸载流程

```
[用户点击卸载]
      │
      ▼
[二次确认]
      │
      ▼
[查找 install_record.json 中的精确记录]
      │
      ├─ 找到 → [精确卸载]
      │           ├─ 删除记录中列出的所有文件
      │           └─ 从 MO 备份还原被覆盖的原版文件
      │
      └─ 未找到 → [全量恢复(危险)]
                  ├─ 删除游戏目录全部内容
                  └─ 从 MO 原版备份复制
```

***

## 3. 备份恢复模块 (Backup)

### 3.1 MO 原版备份机制

首次创建实例时,如果检测到 MO 安装目录可读,启动器会:

- 在 `backups/original/` 保存原版游戏文件快照
- 用于后续的全量恢复 / 精确还原

***

## 4. OneDrive 模块 (Online)

### 4.1 速率限制

为避免触发 Microsoft 反爬虫:

- 单次请求间隔 ≥ 1 秒
- 列表请求使用 `next_link` 分页
- 超时重试最多 3 次(指数退避)

***

## 5. QQ 喊话模块 (QQ Shout)

### 5.1 消息规范

发送格式:

```
【MO版本】<version>
【房间名字】<room>
【密码】<password>     
【HMOL版本】<hmol_ver>
```

### 5.2 内容审核

**三层检测**:

1. **URL 检测**
   - 正则: `https?://[^\s]+`、`www\.[^\s]+`
   - 混淆 URL: `\b\w+\s*\.\s*com\b` 等
2. **敏感词检测**
   - 预定义侮辱性词汇列表
   - 跳过变体选择符(U+FE00-FE0F)和 ZWJ(U+200D)
3. **字数/格式校验**
   - 单条 ≤ 500 字
   - 子字段字数限制

### 5.3 发送流程

```
[用户点击发送]
      │
      ▼
[速率限制检查:5次/分钟]
      │ 超出
      ▼
[内容审核 + 字数校验]
      │ 违规
      ▼
[禁用发送按钮 + 显示"发送中"]
      │
      ▼
[后台线程发送]
      ├─ 获取 QQ Bot Token(带缓存)
      ├─ 发送到 QQ 频道 
      └─ 发送到 QQ 群 
      │
      ▼
[Qt Signal (QueuedConnection) 通知主线程]
      │
      ▼
[主线程更新 UI:成功/失败 + 自动关闭对话框]
```

### 5.4 关键安全设计

- **AppID/AppSecret 混淆存储**(`deobfuscate_string`)
- **速率限制** 防刷屏(`rate_limiter.py`)
- **审计日志** 记录每次发送(脱敏处理)
- **跨线程通信** 使用 Qt Signal 避免 UI 死锁

***

## 6. 加密模块 (Crypto)

### 6.1 支持的算法

| 算法                       | 用途       | 标准                             |
| ------------------------ | -------- | ------------------------------ |
| **AES-256-GCM**          | 对称加密(认证) | NIST SP 800-38D                |
| **PBKDF2-HMAC-SHA256**   | 密钥派生     | NIST SP 800-132 (200,000 iter) |
| **HKDF-SHA256**          | 子密钥派生    | RFC 5869                       |
| **RSA-2048-OAEP-SHA256** | 非对称加密    | RFC 8017                       |
| **HMAC-SHA256**          | 完整性校验    | RFC 2104                       |

<br />

***

## 7. 主题系统 (Theme)

### 7.1 主题应用

- **全局 QSS**: 启动时一次性加载
- **QPalette**: 兼容旧 Qt 控件
- **图标资源**: 主题相关的 SVG 图标

### 7.2 自定义背景

- 支持 JPG/PNG/WEBP/AVIF
- 使用 Pillow 加载与缩放
- 高 DPI 适配

***

## 8. 日志系统 (Logger)

### 8.1 日志格式

```
2026-07-22 18:30:45 | INFO  | Install    | 解压完成,共 128 个文件
2026-07-22 18:30:46 | WARN  | Install    | 跳过 Maps/old.map: 权限不足
2026-07-22 18:30:47 | ERROR | Backup     | 备份失败: PermissionError
```

### 8.2 日志级别

| 级别       | 说明            |
| -------- | ------------- |
| DEBUG    | 详细调试信息(需手动开启) |
| INFO     | 一般操作日志        |
| WARN     | 警告(可恢复)       |
| ERROR    | 错误(功能失败)      |
| CRITICAL | 严重(程序可能崩溃)    |

### 8.3 日志管理

- **路径**: `logs/HMOL_<日期>.log`
- **保留天数**: 默认 30 天
- **导出**: 支持导出选中日期的日志
- **清理**: 旧日志自动清理

***

## 9. EULA 模块

### 9.1 接受流程

```
[程序启动]
    │
    ▼
[读取 HMOL_config.json]
    │
    ├─ eula_accepted = True 且 version 匹配 → 跳过
    │
    └─ 否则 → [弹出 EULA 弹窗]
                  │
                  ├─ 用户同意 → 写回 config.json
                  │
                  └─ 用户拒绝 → 清理临时数据 + sys.exit(0)
```

<br />

***

**下一步**: [❓ FAQ](HMOL-FAQ)
