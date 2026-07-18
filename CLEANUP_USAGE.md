# cleanup_temp.py 使用说明

HMOL 临时文件自动清理工具 v1.0

---

## 简介

`cleanup_temp.py` 用于自动识别并清理系统或应用程序运行过程中产生的临时文件, 包括:

- 🗑️ 缓存文件 (`.cache`, `.tmp`, `.bak`, `.swp` 等)
- 📝 日志文件 (`.log`, `.log.1` 等)
- 📥 临时下载文件 (`.crdownload`, `.part` 等)
- ⚙️ 编译产物 (`.pyc`, `__pycache__/` 等)
- 📝 编辑器临时文件 (`*~`, `.swo` 等)
- 🖥️ 系统文件 (`.DS_Store`, `Thumbs.db` 等)

---

## 快速开始

### 1. 干跑模式 (推荐先试)

```bash
py cleanup_temp.py --dir ./build --dir ./dist --category compile --dry-run
```

仅显示要删除的文件, **不实际删除**。

### 2. 实际清理

```bash
py cleanup_temp.py --dir ./build --category compile --days 7 --yes
```

清理 `build/` 目录下超过 7 天的编译产物。

### 3. 查看帮助

```bash
py cleanup_temp.py --help
```

---

## 参数说明

| 参数 | 简写 | 说明 | 默认值 |
|---|---|---|---|
| `--dir` | `-d` | 要清理的目录 (**可多次指定**) | 必填 |
| `--ext` | `-e` | 文件扩展名 (可多次) | 无 |
| `--category` | `-c` | 预定义类型: `cache` / `log` / `download` / `compile` / `python` / `editor` / `system` | 无 |
| `--days` | `-D` | 保留天数 (0=全部) | 7 |
| `--dry-run` | `-n` | 干跑模式, 仅显示不删除 | False |
| `--log-file` | `-l` | 日志文件路径 | `cleanup_temp.log` |
| `--log-level` | | 日志级别: `DEBUG` / `INFO` / `WARNING` / `ERROR` | `INFO` |
| `--quiet` | `-q` | 静默模式, 仅输出错误 | False |
| `--yes` | `-y` | 跳过确认提示 | False |

---

## 预定义文件类型

| 类别 | 包含的文件 |
|---|---|
| `cache` | `.cache` `.tmp` `.temp` `.bak` `.old` `.swp` `.swo` |
| `log` | `.log` `.log.1` `.log.2` |
| `download` | `.crdownload` `.part` `.download` `.partial` |
| `compile` | `.pyc` `.pyo` `.pyd` `.o` `.obj` |
| `python` | `__pycache__` `.pytest_cache` `.mypy_cache` `.ruff_cache` |
| `editor` | `~` `.swp` `.swo` `.vscode-tmp` |
| `system` | `.DS_Store` `Thumbs.db` `desktop.ini` |

---

## 执行方式

### 场景 1: 清理 PyInstaller 临时文件

```bash
py cleanup_temp.py --dir ./build --dir ./dist --category compile --days 0
```

### 场景 2: 清理 HMOL 运行日志 (保留 30 天)

```bash
py cleanup_temp.py --dir ./logs --category log --days 30
```

### 场景 3: 清理 __pycache__ (即时)

```bash
py cleanup_temp.py --dir . --category python --days 0 --yes
```

### 场景 4: 自定义扩展名

```bash
py cleanup_temp.py --dir . --ext .bak --ext .tmp --days 1
```

### 场景 5: 清理多个目录 + 多种类型

```bash
py cleanup_temp.py \
  --dir ./build \
  --dir ./dist \
  --dir . \
  --category compile \
  --category cache \
  --days 7
```

### 场景 6: 计划任务 (Windows 任务计划程序)

```bash
# 每周日凌晨 3 点自动清理
py cleanup_temp.py --dir . --category cache --category log --days 30 --yes --quiet
```

---

## 注意事项

### ⚠️ 安全保护

脚本内置白名单, **绝对不会删除**以下文件:

```
HMOL_qt.py          crypto_utils.py      anti_debug.py
input_validation.py rate_limiter.py     security_audit.py
obfuscate.py         cleanup_temp.py
HMOL_config.json    msal_token_cache.enc
README.md           LICENSE              EULA.md
requirements.txt    icon.ico
build.bat           build_enhanced.bat
.git/               .gitignore           .env
```

### 📋 最佳实践

1. **始终先 `--dry-run`**: 实际删除前先预览
2. **谨慎使用 `--days 0`**: 会清理所有匹配文件
3. **检查目标目录**: 避免误删 `C:\` 整个磁盘
4. **定期备份**: 删除前确认有备份
5. **查看日志**: 操作记录写入 `cleanup_temp.log`

### 🚫 不要做的事

```bash
# ❌ 千万不要这样做
py cleanup_temp.py --dir C:\ --category system --days 0
py cleanup_temp.py --dir / --ext .py --days 0
```

### 📊 输出示例

```
[INFO] HMOL 临时文件清理工具 v1.0 启动
[INFO]   目标: ['F:\\ra2\\mo3\\HMOL\\build']
[INFO]   扩展名: ['.pyc', '.pyo', '.pyd', '.o', '.obj']
[INFO]   保留: 7 天
[INFO] 扫描 F:\ra2\mo3\HMOL\build: 找到 234 个候选文件
[INFO] 共找到 234 个文件, 总大小 12.45 MB
============================================================
清理摘要 / Cleanup Summary
============================================================
  目标目录: F:\ra2\mo3\HMOL\build
  文件类型: .pyc, .pyo, .pyd, .o, .obj
  保留天数: 7
  模式: 实际删除
  耗时: 0.32 秒
------------------------------------------------------------
  成功删除: 234 个文件
  删除失败: 0 个文件
  释放空间: 12.45 MB
============================================================
```

---

## 日志格式

日志写入 `cleanup_temp.log` (默认, 可通过 `--log-file` 修改):

```
[2026-07-17 18:30:01] [INFO] HMOL 临时文件清理工具 v1.0 启动
[2026-07-17 18:30:01] [INFO]   目标: ['F:\\ra2\\mo3\\HMOL\\build']
[2026-07-17 18:30:02] [INFO] 扫描 F:\ra2\mo3\HMOL\build: 找到 234 个候选文件
[2026-07-17 18:30:02] [INFO] 已删除: F:\ra2\mo3\HMOL\build\module1.pyc
[2026-07-17 18:30:02] [INFO] 已删除: F:\ra2\mo3\HMOL\build\module2.pyc
...
```

**日志自动轮转**: 单个日志文件最大 1MB, 保留最近 3 个。

---

## 退出码

| 退出码 | 含义 |
|---|---|
| 0 | 成功 (无失败) |
| 1 | 部分文件删除失败 |
| 2 | 致命错误 |
| 130 | 用户中断 (Ctrl+C) |

---

## 集成到 HMOL 启动器

如需在 HMOL 启动时自动清理, 在 `HMOL_qt.py` 的 `main()` 中:

```python
import subprocess
# 启动时清理临时文件 (干跑模式, 仅记录不删)
subprocess.run([sys.executable, "cleanup_temp.py", "--dir", "./build",
                "--category", "compile", "--days", "0", "--dry-run"],
               capture_output=True)
```

或通过 Windows 任务计划程序每日定时执行。

---

## 故障排查

### Q: 提示"无权限"?

A: 以管理员身份运行 cmd, 或跳过系统保护目录 (`C:\Windows\System32\Temp` 等)。

### Q: 中文路径乱码?

A: 确保 Python 3.7+ 且终端使用 UTF-8 编码 (Windows: `chcp 65001`)。

### Q: 日志文件乱码?

A: 脚本默认使用 UTF-8 写入, 用支持 UTF-8 的编辑器查看。

---

**© 2026 HMOL Project Contributors.**
