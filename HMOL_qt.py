#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Hello Mental Omega Launcher v2.2 ( PySide6 / Qt6 )
=========================================================

"""

# =====================================================================
# 第一部分: 标准库 / 依赖导入
# =====================================================================
import os
import re
import shutil
import json
import sys
import zipfile
import threading
import subprocess
import base64
import tempfile
import queue
import time
import traceback
import webbrowser
from datetime import datetime
from io import BytesIO
from pathlib import Path

# PIL 用于图标
try:
    from PIL import Image
    PIL_AVAILABLE = True
except ImportError:
    PIL_AVAILABLE = False

# 7z 支持
try:
    import py7zr
    SEVENZIP_AVAILABLE = True
except ImportError:
    SEVENZIP_AVAILABLE = False
    log_info("App", "7z文件支持不可用，请安装py7zr库：pip install py7zr")

# RAR 支持(可选,需要安装 rarfile 库)
#   rarfile 本身只解析 RAR 格式,实际解压通常需要系统已安装 unrar/winrar
#   Windows 上推荐安装 WinRAR;若未安装 unrar,rarfile 会抛 NeedFirstVolume/ReadError
try:
    import rarfile
    RARFILE_AVAILABLE = True
except ImportError:
    RARFILE_AVAILABLE = False
    rarfile = None  # 占位,避免 NameError
    log_info("App", "RAR文件支持不可用，请安装rarfile库：pip install rarfile")

# Microsoft 账号认证
try:
    import msal
    import requests as ms_requests
    MSAL_AVAILABLE = True
except ImportError:
    MSAL_AVAILABLE = False
    msal = None
    ms_requests = None

# PySide6
import PySide6
from PySide6.QtCore import (
    Qt, QSize, QTimer, QThread, Signal, QObject, QPoint, QRect, QPropertyAnimation,
    QEasingCurve, QAbstractAnimation, QEvent, QStandardPaths, QFileSystemWatcher
)
from PySide6.QtGui import (
    QIcon, QPixmap, QColor, QFont, QPainter, QPainterPath, QBrush, QPen, QAction, QCursor,
    QGuiApplication, QImage, QLinearGradient, QMovie
)
from PySide6.QtWidgets import (
    QApplication, QMainWindow, QWidget, QVBoxLayout, QHBoxLayout, QGridLayout,
    QStackedWidget, QSplitter, QListWidget, QListWidgetItem, QPushButton, QLabel,
    QLineEdit, QTextEdit, QPlainTextEdit, QComboBox, QCheckBox, QRadioButton,
    QTabWidget, QProgressBar, QProgressDialog, QStatusBar, QToolBar, QMenuBar, QMenu, QDialog,
    QDialogButtonBox, QFileDialog, QMessageBox, QInputDialog, QFrame, QSizePolicy,
    QScrollArea, QButtonGroup, QGroupBox, QSlider, QSpinBox, QTableWidget,
    QTableWidgetItem, QHeaderView, QAbstractItemView, QGraphicsDropShadowEffect,
    QGraphicsOpacityEffect, QSystemTrayIcon, QStyle, QToolButton, QWidgetAction,
)


# =====================================================================
# 第二部分: 运行时配置加载 (外部化敏感信息)
# =====================================================================
# 安全设计 (HMOL111.txt FINDING-01/02/04/05):
#   - 所有真实密钥 / PII / 客户端 ID 必须从环境变量或 .env 读取
#   - 不得在源码中硬编码 (即使是 OBF1: 混淆也不可接受)
#   - AppSecret 类机密变量缺失时, 强制退出, 拒绝启动
#   - 公开值 (URL) 提供 fallback 便于开发, 启动时打印告警
#
# 题外话: 早期版本里所有密钥都老老实实写在源码里, 当时觉得"反正不公布"——
# 后来想想, 不公布的代码是没有意义的代码, 不公布的密钥就只是没被发现的密钥.
# 于是就有了这一节. (给未来的自己留个台阶: 别再走回头路了.)
def _load_dotenv(path: str = ".env") -> None:
    """
    极简 .env 加载器 (避免引入 python-dotenv 依赖)
    - 仅处理 KEY=VALUE 格式
    - 跳过空行与 # 注释
    - 已存在的环境变量优先级高于 .env (allow override via shell)
    - 引号 (单/双) 会被剥离
    """
    try:
        if not os.path.isfile(path):
            return
        with open(path, "r", encoding="utf-8") as f:
            for raw_line in f:
                line = raw_line.strip()
                if not line or line.startswith("#"):
                    continue
                if "=" not in line:
                    continue
                key, _, value = line.partition("=")
                key = key.strip()
                value = value.strip()
                # 去除首尾引号
                if len(value) >= 2 and value[0] == value[-1] and value[0] in ("'", '"'):
                    value = value[1:-1]
                # 已存在则不覆盖 (允许 shell 环境变量优先)
                if key and key not in os.environ:
                    os.environ[key] = value
    except OSError:
        # .env 读取失败不致命, 由各调用方决定是否强制要求
        pass


# 在模块加载时执行一次, 之后所有 os.environ.get() 即可读到 .env 内容
_load_dotenv()


# =====================================================================
# 第三部分: 主题色板 (现代化 Fluent / WinUI 风格)
# =====================================================================

# ---------------------------------------------------------------------
# 主题色方案(6 套,用户可在 ⚙️ 设置 中切换)
#   每套含: name(中文名)/ primary(主色)/ secondary(次色)/ accent(强调)
#   应用位置: 侧边栏渐变 / Hero 顶栏 / 导航激活态 / 对话框
# ---------------------------------------------------------------------
GRADIENT_THEMES = {
    "蓝紫": {
        "name": "🔵 蓝紫渐变", "desc": "想要更多主题？进群反馈吧！",
        "primary": "#667eea", "secondary": "#764ba2",
        "accent": "#5d4fcf", "accent_hover": "#7c6dd6",
        "sidebar_bg": "#e8eaf6", "sidebar_border": "#c4c6e8",
        "sidebar_bg_dark": "#18182e", "sidebar_border_dark": "#2a2a4a",
    },
    "蓝青": {
        "name": "🌊 蓝青渐变", "desc": "想要更多主题？进群反馈吧！",
        "primary": "#2193b0", "secondary": "#6dd5ed",
        "accent": "#1d87a3", "accent_hover": "#34a8c2",
        "sidebar_bg": "#e0f2f5", "sidebar_border": "#b3dce4",
        "sidebar_bg_dark": "#0f1e22", "sidebar_border_dark": "#1a3a42",
    },
    "橘红": {
        "name": "🌅 橘红渐变", "desc": "想要更多主题？进群反馈吧！",
        "primary": "#f12711", "secondary": "#f5af19",
        "accent": "#e0451a", "accent_hover": "#f36622",
        "sidebar_bg": "#fef0ed", "sidebar_border": "#f5c6bd",
        "sidebar_bg_dark": "#2a1512", "sidebar_border_dark": "#4a221d",
    },
    "森林绿": {
        "name": "🌲 森林绿渐变", "desc": "想要更多主题？进群反馈吧！",
        "primary": "#11998e", "secondary": "#38ef7d",
        "accent": "#0d8479", "accent_hover": "#13a597",
        "sidebar_bg": "#e0f2f0", "sidebar_border": "#b3d9d5",
        "sidebar_bg_dark": "#0f1e1c", "sidebar_border_dark": "#1a3a36",
    },
    "玫瑰粉": {
        "name": "🌸 玫瑰粉渐变", "desc": "想要更多主题？进群反馈吧！",
        "primary": "#ee9ca7", "secondary": "#ffdde1",
        "accent": "#d97a85", "accent_hover": "#e899a3",
        "sidebar_bg": "#fdf0f2", "sidebar_border": "#f5d1d5",
        "sidebar_bg_dark": "#2a1820", "sidebar_border_dark": "#4a2834",
    },
    "暮光紫": {
        "name": "🌌 暮光紫渐变", "desc": "想要更多主题？进群反馈吧！",
        "primary": "#654ea3", "secondary": "#eaafc8",
        "accent": "#523e87", "accent_hover": "#6b53b0",
        "sidebar_bg": "#edeaf6", "sidebar_border": "#d8cde8",
        "sidebar_bg_dark": "#171428", "sidebar_border_dark": "#2d244a",
    },
    # ---- 雾感弥散光色板 (3 套) ----
    "Flow Shade": {
        "name": "🌊 Flow Shade", "desc": "流动暗影 — #FFDE7D → #00B8AA",
        "primary": "#FFDE7D", "secondary": "#00B8AA",
        "accent": "#FA3F6C", "accent_hover": "#e02a56",
        "sidebar_bg": "#fffdf5", "sidebar_border": "#e8e0c0",
        "sidebar_bg_dark": "#1f1e18", "sidebar_border_dark": "#3a3628",
    },
    "Gentle Radiance": {
        "name": "🎀 Gentle Radiance", "desc": "柔光粉雾 — #F6F6F6 → #8785A2",
        "primary": "#F6F6F6", "secondary": "#8785A2",
        "accent": "#FFE2E2", "accent_hover": "#ffc7c7",
        "sidebar_bg": "#faf9fb", "sidebar_border": "#e0dde8",
        "sidebar_bg_dark": "#1c1c24", "sidebar_border_dark": "#323244",
    },
    "Fresh Aura": {
        "name": "🍃 Fresh Aura", "desc": "清新气息 — #ABEDD8 → #3D84A8",
        "primary": "#ABEDD8", "secondary": "#3D84A8",
        "accent": "#48466D", "accent_hover": "#3b3960",
        "sidebar_bg": "#f0faf8", "sidebar_border": "#c8e8e0",
        "sidebar_bg_dark": "#141e24", "sidebar_border_dark": "#243240",
    },
}
DEFAULT_GRADIENT_THEME = "蓝紫"

# =====================================================================
# 第四部分: 加密 / 安全工具
# =====================================================================
# 引入本地安全模块 (AES-256-GCM 加密、密钥派生、内存保护)
try:
    from crypto_utils import (
        SecureString, derive_key, encrypt, decrypt,
        encrypt_to_base64, decrypt_from_base64,
        obfuscate_string, deobfuscate_string,
        secure_hash, get_master_key, generate_salt,
        is_strong_encryption_available,
    )
    CRYPTO_AVAILABLE = is_strong_encryption_available()
    if not CRYPTO_AVAILABLE:
        log_info("Security", "cryptography 库不可用,降级到标准库 PBKDF2 (安全性降低)")
except ImportError as e:
    log_error("Security", f"crypto_utils 不可用,密钥将以明文存储: {e}")
    CRYPTO_AVAILABLE = False
    # fallback 空实现
    def SecureString(s): return type('SS', (), {'get': lambda self: s, 'clear': lambda self: None})()
    def derive_key(*a, **kw): raise NotImplementedError
    def encrypt(*a, **kw): raise NotImplementedError
    def decrypt(*a, **kw): raise NotImplementedError
    def encrypt_to_base64(*a, **kw): raise NotImplementedError
    def decrypt_from_base64(*a, **kw): raise NotImplementedError
    def obfuscate_string(s): return s
    def deobfuscate_string(s): return s
    def secure_hash(b): import hashlib; return hashlib.sha256(b).hexdigest()
    def get_master_key(*a, **kw): raise NotImplementedError
    def generate_salt(): return os.urandom(16)
    def is_strong_encryption_available(): return False

# =====================================================================
# 第四-A 部分: 反调试 / 完整性校验 (启动时自检)
# =====================================================================
# 延迟导入,避免在 GUI 显示前阻塞 (sanity check 在 main() 启动时执行)
try:
    from anti_debug import verify_runtime_integrity, is_debugger_present, get_self_hash
    ANTI_DEBUG_AVAILABLE = True
except ImportError:
    ANTI_DEBUG_AVAILABLE = False

# 输入验证 / 速率限制
try:
    from input_validation import (
        sanitize_filename, safe_path_join, validate_package_path,
        check_zip_bomb, validate_url, is_safe_member_path,
    )
    INPUT_VALIDATION_AVAILABLE = True
except ImportError:
    INPUT_VALIDATION_AVAILABLE = False

try:
    from rate_limiter import (
        get_login_limiter, get_qq_shout_limiter, get_api_limiter,
    )
    RATE_LIMITER_AVAILABLE = True
except ImportError:
    RATE_LIMITER_AVAILABLE = False

# =====================================================================
# 第五部分: Microsoft 账号认证配置
# =====================================================================
# 安全设计 (HMOL111.txt FINDING-05):
#   - Client ID 从 .env 读取, 源码中不留任何痕迹
#   - 公共可重建的 URL 同样外部化, 避免被作为识别 HMOL 部署的指纹
#   - 若 .env 未设置, 启动时打印告警并使用占位符, 不阻断登录流程
def _env_or_default(key: str, default: str = "") -> str:
    """从环境变量读取配置, 若缺失返回 default 并打印一次性告警"""
    value = os.environ.get(key, "").strip()
    if value:
        return value
    if default:
        # 仅在 main() 启动后才会触发; 模块加载阶段静默
        try:
            log_warn("Config", f"环境变量 {key} 未设置, 使用占位符")
        except (NameError, AttributeError):
            pass
    return default


MSAL_CLIENT_ID = _env_or_default(
    "MSAL_CLIENT_ID",
    "00000000-0000-0000-0000-000000000000",  # 占位符, 实际使用必须配置
)
MSAL_AUTHORITY = _env_or_default(
    "MSAL_AUTHORITY",
    "https://login.microsoftonline.com/consumers",
)
MSAL_CACHE_FILE = "msal_token_cache.enc"
GRAPH_API_BASE = _env_or_default(
    "GRAPH_API_BASE",
    "https://graph.microsoft.com/v1.0",
)
MSAL_SCOPES = ["User.Read", "Files.Read.All"]
NET_CHECK_TIMEOUT = 5
MSAL_LOGIN_TIMEOUT = 120  # 用户完成登录的最大等待秒数

# =====================================================================
# 第六部分: QQ 机器人配置 (密钥强制外部化, 缺失则拒绝启动)
# =====================================================================
# 安全设计 (HMOL111.txt FINDING-01):
#   - AppSecret **绝不允许**硬编码或 XOR 混淆, 必须从 .env 读取
#   - 若 .env 中 QQ_BOT_APP_SECRET 缺失, 调用 _require_qq_bot_secret() 时
#     抛出 RuntimeError, 由调用方捕获并向用户展示配置说明
#   - AppID / TokenURL 等公开值保留 fallback 便于开发
QQ_BOT_APPID = _env_or_default("QQ_BOT_APPID", "1905175533")
QQ_BOT_TOKEN_URL = _env_or_default(
    "QQ_BOT_TOKEN_URL",
    "https://bots.qq.com/app/getAppAccessToken",
)
QQ_BOT_MSG_URL = "https://api.sgroup.qq.com/channels/{}/messages"
QQ_BOT_GROUP_MSG_URL = "https://api.sgroup.qq.com/v2/groups/{}/messages"
QQ_BOT_MSG_MAX_LENGTH = 500
QQ_BOT_TOKEN_CACHE = {}  # 内存缓存, 应用级生命周期
_QQ_BOT_SECRET_MISSING_WARNED = False  # 防止刷屏


def _require_qq_bot_secret() -> str:
    """
    读取 QQ Bot AppSecret. 强制从环境变量获取, 缺失时抛错.

    安全约束:
      1. 不得提供任何 fallback 默认值
      2. 不得记录到日志 (即使是 debug 级别)
      3. 内存中仅在使用时驻留, 调用结束即丢弃引用
    """
    global _QQ_BOT_SECRET_MISSING_WARNED
    secret = os.environ.get("QQ_BOT_APP_SECRET", "").strip()
    if not secret:
        if not _QQ_BOT_SECRET_MISSING_WARNED:
            _QQ_BOT_SECRET_MISSING_WARNED = True
            log_warn(
                "QQBot",
                "QQ_BOT_APP_SECRET 未配置, QQ 机器人功能不可用. "
                "获取地址: https://bots.qq.com → 应用管理 → AppSecret\n"
                "  小提示: 复制项目根目录的 .env.example 为 .env, "
                "然后填进去就行, 整个过程 30 秒.",
            )
        raise RuntimeError("QQ_BOT_APP_SECRET 未配置")
    return secret


def _get_qq_bot_token() -> str:
    """获取 QQ Bot Access Token (带缓存)"""
    import time
    now = time.time()
    if QQ_BOT_TOKEN_CACHE.get("token") and float(QQ_BOT_TOKEN_CACHE.get("expiry", 0)) > now + 60:
        return QQ_BOT_TOKEN_CACHE["token"]
    try:
        # 密钥在使用时才加载, 减少内存中明文驻留时间
        app_id = QQ_BOT_APPID
        app_secret = _require_qq_bot_secret()
        resp = ms_requests.post(
            QQ_BOT_TOKEN_URL,
            json={"appId": app_id, "clientSecret": app_secret},
            headers={"Content-Type": "application/json"},
            timeout=10,
        )
        if resp.status_code == 200:
            data = resp.json()
            token = data.get("access_token", "")
            expires_in = int(data.get("expires_in", 7200))
            QQ_BOT_TOKEN_CACHE["token"] = token
            QQ_BOT_TOKEN_CACHE["expiry"] = now + expires_in
            log_info("QQBot", f"Token 获取成功, 有效期 {expires_in}s")
            return token
        else:
            log_error("QQBot", f"Token 请求失败: HTTP {resp.status_code} — {resp.text[:300]}")
    except RuntimeError:
        # 配置缺失, 已在 _require_qq_bot_secret 中告警, 此处静默
        pass
    except Exception as e:
        log_error("QQBot", f"Token 请求异常: {e}")
    return ""


def _get_qq_channel_id() -> str:
    """返回 QQ 频道子频道 ID (从 .env 读取, 缺失则使用默认值)"""
    return _env_or_default("QQ_BOT_CHANNEL_ID", "736539421")


def _get_qq_group_id() -> str:
    """返回 QQ 群号 (从 .env 读取, 缺失则使用默认值)"""
    return _env_or_default("QQ_BOT_GROUP_ID", "1034243331")

# =====================================================================
# 结构化日志系统 (LogManager)
# =====================================================================
_LOG_LOCK = threading.Lock()
_LOG_BUFFER: list[dict] = []  # [{timestamp, level, module, message}]
_LOG_MAX_BUFFER = 5000
_LOG_QUEUE = queue.Queue()
_LOG_FILE_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "logs")
_LOG_MAX_DAYS = 7

def _log_writer_worker():
    """后台线程: 从队列消费日志条目并写入文件"""
    os.makedirs(_LOG_FILE_PATH, exist_ok=True)
    today = datetime.now().strftime("%Y-%m-%d")
    current_fp = os.path.join(_LOG_FILE_PATH, f"HMOL_{today}.log")
    fh = open(current_fp, "a", encoding="utf-8")
    _last_date = today
    while True:
        try:
            entry = _LOG_QUEUE.get()
            if entry is None:  # 停止信号
                fh.close()
                break
            dt_str = entry["timestamp"]
            entry_date = dt_str[:10]
            # 跨天(或换文件)则滚动
            if entry_date != _last_date:
                fh.close()
                _last_date = entry_date
                current_fp = os.path.join(_LOG_FILE_PATH, f"HMOL_{entry_date}.log")
                fh = open(current_fp, "a", encoding="utf-8")
            line = f"[{entry['timestamp']}] [{entry['level']}] [{entry['module']}] {entry['message']}"
            fh.write(line + "\n")
            fh.flush()
        except Exception:
            try:
                fh.close()
            except Exception:
                pass
            break

_LOG_WRITER = threading.Thread(target=_log_writer_worker, daemon=True)
_LOG_WRITER_STARTED = False

def _start_log_writer():
    global _LOG_WRITER_STARTED
    if not _LOG_WRITER_STARTED:
        _LOG_WRITER_STARTED = True
        _LOG_WRITER.start()


def log_info(module: str, message: str):
    _log("INFO", module, message)

def log_warn(module: str, message: str):
    _log("WARN", module, message)

def log_error(module: str, message: str):
    _log("ERROR", module, message)

def log_debug(module: str, message: str):
    _log("DEBUG", module, message)

def _sanitize_log_message(message: str) -> str:
    """
    日志脱敏: 移除/截断敏感字段 (access_token, refresh_token, appSecret, password 等)
    防止密钥意外写入日志文件
    """
    import re
    # 1. access_token=xxx → access_token=eyJ...(前8字符)...
    message = re.sub(
        r'(access_token["\']?\s*[:=]\s*["\']?)([A-Za-z0-9._\-+/=]{8,})',
        lambda m: m.group(1) + m.group(2)[:8] + "...[REDACTED]",
        message,
        flags=re.IGNORECASE,
    )
    # 2. refresh_token=xxx
    message = re.sub(
        r'(refresh_token["\']?\s*[:=]\s*["\']?)([A-Za-z0-9._\-+/=]{8,})',
        lambda m: m.group(1) + m.group(2)[:8] + "...[REDACTED]",
        message,
        flags=re.IGNORECASE,
    )
    # 3. clientSecret / appSecret
    message = re.sub(
        r'((?:client|app)?[Ss]ecret["\']?\s*[:=]\s*["\']?)([A-Za-z0-9._\-+/=]{6,})',
        lambda m: m.group(1) + "***[REDACTED]***",
        message,
    )
    # 4. password=xxx
    message = re.sub(
        r'([Pp]assword["\']?\s*[:=]\s*["\']?)([^\s,;"\']{4,})',
        lambda m: m.group(1) + "***[REDACTED]***",
        message,
    )
    # 5. Authorization: Bearer xxx
    message = re.sub(
        r'(Bearer\s+)([A-Za-z0-9._\-+/=]{8,})',
        lambda m: m.group(1) + m.group(2)[:8] + "...[REDACTED]",
        message,
        flags=re.IGNORECASE,
    )
    return message


def _log(level: str, module: str, message: str):
    """写入内存缓冲区 + 异步队列"""
    ts = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    # 脱敏后再写日志, 防止敏感字段落盘
    sanitized = _sanitize_log_message(str(message))
    entry = {"timestamp": ts, "level": level, "module": module, "message": sanitized}
    with _LOG_LOCK:
        _LOG_BUFFER.append(entry)
        if len(_LOG_BUFFER) > _LOG_MAX_BUFFER:
            _LOG_BUFFER.pop(0)
    _start_log_writer()
    _LOG_QUEUE.put(entry)
    # 同时输出到 stdout 以便调试
    print(f"[{ts}] [{level}] [{module}] {sanitized}")

def get_logs(level_filter: str = "", keyword: str = "",
             date_from: str = "", date_to: str = "",
             limit: int = 2000) -> list[dict]:
    """获取过滤后的日志列表(供 UI 查看器调用)"""
    with _LOG_LOCK:
        entries = list(_LOG_BUFFER)
    if level_filter:
        entries = [e for e in entries if e["level"] == level_filter.upper()]
    if keyword:
        kw = keyword.lower()
        entries = [e for e in entries
                   if kw in e["message"].lower() or kw in e["module"].lower()]
    if date_from:
        entries = [e for e in entries if e["timestamp"] >= date_from]
    if date_to:
        entries = [e for e in entries if e["timestamp"] <= date_to + " 23:59:59"]
    return entries[-limit:]

def export_logs_txt(file_path: str, entries: list[dict]):
    """导出日志为 TXT"""
    with open(file_path, "w", encoding="utf-8") as f:
        for e in entries:
            f.write(f"[{e['timestamp']}] [{e['level']}] [{e['module']}] {e['message']}\n")

def export_logs_csv(file_path: str, entries: list[dict]):
    """导出日志为 CSV"""
    import csv
    with open(file_path, "w", encoding="utf-8-sig", newline="") as f:
        w = csv.writer(f)
        w.writerow(["timestamp", "level", "module", "message"])
        for e in entries:
            w.writerow([e["timestamp"], e["level"], e["module"], e["message"]])

def cleanup_old_logs(keep_days: int = _LOG_MAX_DAYS):
    """清理超过保留天数的日志文件"""
    from datetime import timedelta
    cutoff = datetime.now() - timedelta(days=keep_days)
    if os.path.isdir(_LOG_FILE_PATH):
        for fname in os.listdir(_LOG_FILE_PATH):
            if fname.startswith("HMOL_") and fname.endswith(".log"):
                try:
                    date_part = fname[5:15]
                    fdate = datetime.strptime(date_part, "%Y-%m-%d")
                    if fdate < cutoff:
                        os.remove(os.path.join(_LOG_FILE_PATH, fname))
                        log_info("LogCleaner", f"已清理过期日志: {fname}")
                except Exception:
                    pass

# =====================================================================
# OneDrive / SharePoint 文件源配置
# =====================================================================
# 安全设计 (HMOL111.txt FINDING-02):
#   - SharePoint 共享 URL 含个人 Microsoft 账户标识 (PII), 不得硬编码
#   - 全部从 .env 读取, 缺失时该子页面在 UI 隐藏而非崩溃
#   - 维护者应使用专用中性账户托管共享资源, 而非个人账户
def _make_onedrive_source(key: str, name: str, icon: str, description: str) -> dict | None:
    """
    从环境变量构造 OneDrive 源配置.
    若 .env 中未配置对应 URL, 返回 None (调用方隐藏该子页面).
    """
    env_key = f"ONEDRIVE_{key.upper()}_URL"
    url = os.environ.get(env_key, "").strip()
    if not url:
        return None
    return {
        "name": name,
        "icon": icon,
        "url": url,
        "description": description,
    }


ONEDRIVE_SOURCES = {
    _k: src for _k, src in [
        ("game_resources", _make_onedrive_source(
            "game_resources", "游戏资源下载", "🎮",
            "游戏相关资源,包括插件包、地图、皮肤等",
        )),
        ("runtime_env", _make_onedrive_source(
            "runtime_env", "运行环境", "⚙️",
            "游戏运行所需的运行库与环境组件",
        )),
        ("program_extend", _make_onedrive_source(
            "program_extend", "程序DLC下载", "🧩",
            "HMOL 程序扩展、插件与辅助工具",
        )),
    ] if src is not None
}

# 文件类型图标映射
FILE_ICON_MAP = {
    ".zip": "📦", ".7z": "📦", ".rar": "📦", ".tar": "📦", ".gz": "📦",
    ".exe": "⚙️", ".msi": "⚙️", ".bat": "📜", ".cmd": "📜", ".ps1": "📜",
    ".dll": "🔧", ".sys": "🔧", ".ini": "📝", ".cfg": "📝", ".json": "📝",
    ".xml": "📝", ".yaml": "📝", ".yml": "📝", ".toml": "📝", ".txt": "📄",
    ".md": "📄", ".log": "📄", ".pdf": "📕", ".doc": "📘", ".docx": "📘",
    ".png": "🖼️", ".jpg": "🖼️", ".jpeg": "🖼️", ".gif": "🖼️", ".bmp": "🖼️",
    ".svg": "🖼️", ".ico": "🖼️", ".webp": "🖼️",
    ".mp3": "🎵", ".wav": "🎵", ".ogg": "🎵", ".flac": "🎵", ".m4a": "🎵",
    ".mp4": "🎬", ".avi": "🎬", ".mkv": "🎬", ".mov": "🎬", ".wmv": "🎬",
    ".mix": "🎮", ".csf": "🌐", ".pal": "🎨", ".shp": "🎨",
    ".py": "🐍", ".js": "📜", ".html": "🌐", ".css": "🎨", ".cpp": "⚙️",
    ".h": "⚙️", ".cs": "⚙️",
}

# 浅色模式
LIGHT = {
    "name": "浅色模式",
    "primary": "#1a1a2e",
    "secondary": "#16213e",
    "accent": "#0f4c75",
    "accent_hover": "#1a5a8a",
    "success": "#27ae60",
    "warning": "#3498db",
    "error": "#e74c3c",
    "bg": "#f8f9fa",
    "bg_alt": "#ffffff",
    "bg_sidebar": "#eef1f5",
    "surface": "#ffffff",
    "surface_alt": "#f1f3f5",
    "border": "#d0d7de",
    "border_focus": "#0f4c75",
    "text": "#2c3e50",
    "text_secondary": "#6c757d",
    "text_disabled": "#adb5bd",
    "text_inverse": "#ffffff",
    "shadow": "rgba(0, 0, 0, 35)",
    "scroll_thumb": "#c1c8cd",
    "scroll_thumb_hover": "#a8b2b8",
    "selection": "#cfe2ff",
}

# 深色模式
DARK = {
    "name": "深色模式",
    "primary": "#5dade2",
    "secondary": "#34495e",
    "accent": "#3498db",
    "accent_hover": "#5dade2",
    "success": "#2ecc71",
    "warning": "#f39c12",
    "error": "#e74c3c",
    "bg": "#1a1a2e",
    "bg_alt": "#16213e",
    "bg_sidebar": "#0f0f1e",
    "surface": "#16213e",
    "surface_alt": "#1e2a3a",
    "border": "#2c3e50",
    "border_focus": "#5dade2",
    "text": "#ecf0f1",
    "text_secondary": "#95a5a6",
    "text_disabled": "#5a6c7d",
    "text_inverse": "#1a1a2e",
    "shadow": "rgba(0, 0, 0, 200)",
    "scroll_thumb": "#34495e",
    "scroll_thumb_hover": "#5dade2",
    "selection": "#34495e",
}


# =====================================================================
# 第三部分: 全局样式 (QSS) - 现代化 Fluent 风格
# =====================================================================

def build_qss(theme: dict, gradient: dict = None) -> str:
    """根据主题生成 QSS 样式表(支持渐变主题色,含雾感弥散光应用)"""
    if gradient is None:
        gradient = GRADIENT_THEMES[DEFAULT_GRADIENT_THEME]
    # accent 角色用渐变(让全局 primary 按钮跟随渐变)
    primary = gradient['primary']
    secondary = gradient['secondary']
    accent_hover = gradient['accent_hover']
    # 侧边栏颜色: 从主题方案派生(浅色/深色自适应)
    sbg = gradient.get('sidebar_bg', theme['bg_sidebar'])
    sbg_dark = gradient.get('sidebar_bg_dark', theme['bg_sidebar'])
    sborder = gradient.get('sidebar_border', theme['border'])
    sborder_dark = gradient.get('sidebar_border_dark', theme['border'])
    # 根据当前是浅/深色模式选取相应颜色
    if theme['text_inverse'] == '#ffffff':
        _sbg = sbg
        _sborder = sborder
    else:
        _sbg = sbg_dark
        _sborder = sborder_dark
    # 解析渐变主副色为 RGB(用于背景雾感 RGBA)
    _hex_to_rgb = lambda h: (int(h[1:3], 16), int(h[3:5], 16), int(h[5:7], 16))
    pr, pg, pb = _hex_to_rgb(primary)
    sr, sg, sb = _hex_to_rgb(secondary)
    primary_rgba = f"{pr}, {pg}, {pb}"
    secondary_rgba = f"{sr}, {sg}, {sb}"
    return f"""
    /* === 全局 === */
    QWidget {{
        font-family: "Microsoft YaHei", "Segoe UI", "PingFang SC", sans-serif;
        font-size: 10pt;
        color: {theme['text']};
        background-color: transparent;
    }}

    QMainWindow, QDialog {{
        background-color: {theme['bg']};
        background: qlineargradient(x1:0, y1:0, x2:1, y2:1,
            stop:0 rgba({primary_rgba}, 0.08),
            stop:0.3 {theme['bg']},
            stop:0.7 {theme['bg']},
            stop:1 rgba({secondary_rgba}, 0.08));
    }}

    /* === 内容区域: 主题色渐变背景, 各页面统一应用 === */
    QStackedWidget#contentStack {{
        background-color: {theme['bg']};
        background: qlineargradient(x1:0, y1:0, x2:1, y2:1,
            stop:0 rgba({primary_rgba}, 0.10),
            stop:0.25 rgba({primary_rgba}, 0.06),
            stop:0.5 {theme['bg']},
            stop:0.75 rgba({secondary_rgba}, 0.06),
            stop:1 rgba({secondary_rgba}, 0.10));
    }}
    QWidget[role="content_page"] {{
        background: transparent;
    }}
    QFrame#sub_topbar {{
        background-color: {theme['surface_alt']};
        border-bottom: 1px solid {theme['border']};
    }}

    /* === 标签 === */
    QLabel {{
        color: {theme['text']};
        background: transparent;
        padding: 0px;
    }}

    QLabel[role="title"] {{
        font-size: 18pt;
        font-weight: 600;
        color: {theme['primary']};
    }}

    QLabel[role="subtitle"] {{
        font-size: 11pt;
        font-weight: 500;
        color: {theme['text']};
    }}

    QLabel[role="caption"] {{
        font-size: 9pt;
        color: {theme['text_secondary']};
    }}

    QLabel[role="success"] {{ color: {theme['success']}; }}
    QLabel[role="warning"] {{ color: {theme['warning']}; }}
    QLabel[role="error"]   {{ color: {theme['error']}; }}
    QLabel[role="accent"]  {{ color: {primary}; }}

    /* === 按钮(全局渐变 - 跟随主题色方案) === */
    QPushButton {{
        background: qlineargradient(x1:0, y1:0, x2:1, y2:0,
            stop:0 {primary}, stop:1 {secondary});
        color: {theme['text_inverse']};
        border: none;
        border-radius: 6px;
        padding: 8px 18px;
        font-weight: 500;
        min-height: 18px;
    }}

    QPushButton:hover {{
        background: qlineargradient(x1:0, y1:0, x2:1, y2:0,
            stop:0 {accent_hover}, stop:1 {secondary});
    }}

    QPushButton:pressed {{
        background-color: {theme['secondary']};
    }}

    QPushButton:disabled {{
        background-color: {theme['text_disabled']};
        color: {theme['text_secondary']};
    }}

    QPushButton[role="accent"] {{
        background-color: {theme['accent']};
        font-weight: 600;
    }}

    QPushButton[role="secondary"] {{
        background-color: {theme['surface_alt']};
        color: {theme['text']};
        border: 1px solid {theme['border']};
    }}

    QPushButton[role="secondary"]:hover {{
        background-color: {theme['border']};
    }}

    QPushButton[role="success"] {{
        background-color: {theme['success']};
    }}

    QPushButton[role="danger"] {{
        background-color: {theme['error']};
    }}

    QPushButton[role="warning"] {{
        background-color: {theme['warning']};
        color: {theme['text']};
    }}

    /* === 输入框 === */
    QLineEdit, QTextEdit, QPlainTextEdit, QSpinBox {{
        background-color: {theme['surface']};
        color: {theme['text']};
        border: 1px solid {theme['border']};
        border-radius: 6px;
        padding: 6px 10px;
        selection-background-color: {gradient['accent']};
        selection-color: white;
    }}

    QLineEdit:focus, QTextEdit:focus, QPlainTextEdit:focus, QSpinBox:focus {{
        border: 2px solid {gradient['accent']};
    }}

    QLineEdit:disabled {{
        background-color: {theme['surface_alt']};
        color: {theme['text_disabled']};
    }}

    /* === 组合框 === */
    QComboBox {{
        background-color: {theme['surface']};
        color: {theme['text']};
        border: 1px solid {theme['border']};
        border-radius: 6px;
        padding: 6px 10px;
        min-width: 80px;
    }}

    QComboBox:hover {{
        border: 1px solid {gradient['accent']};
    }}

    QComboBox::drop-down {{
        border: none;
        width: 24px;
    }}

    QComboBox QAbstractItemView {{
        background-color: {theme['surface']};
        color: {theme['text']};
        border: 1px solid {theme['border']};
        border-radius: 6px;
        selection-background-color: {gradient['accent']};
        selection-color: white;
        padding: 4px;
        outline: 0px;
    }}

    /* === 复选框 === */
    QCheckBox, QRadioButton {{
        color: {theme['text']};
        spacing: 8px;
        background: transparent;
    }}

    QCheckBox::indicator, QRadioButton::indicator {{
        width: 16px;
        height: 16px;
        background-color: {theme['surface']};
        border: 1.5px solid {theme['border']};
    }}

    QCheckBox::indicator {{
        border-radius: 3px;
    }}

    QRadioButton::indicator {{
        border-radius: 8px;
    }}

    QCheckBox::indicator:hover, QRadioButton::indicator:hover {{
        border: 1.5px solid {gradient['accent']};
    }}

    QCheckBox::indicator:checked {{
        background-color: {gradient['accent']};
        border: 1.5px solid {gradient['accent']};
    }}

    QRadioButton::indicator:checked {{
        background-color: {gradient['accent']};
        border: 4px solid {theme['surface']};
        border-radius: 8px;
    }}

    /* === 列表 === */
    QListWidget {{
        background-color: {theme['surface']};
        color: {theme['text']};
        border: 1px solid {theme['border']};
        border-radius: 8px;
        padding: 4px;
        outline: 0px;
    }}

    QListWidget::item {{
        padding: 8px 10px;
        border-radius: 4px;
        margin: 1px 0px;
    }}

    QListWidget::item:hover {{
        background-color: {theme['surface_alt']};
    }}

    QListWidget::item:selected {{
        background-color: {gradient['accent']};
        color: {theme['text_inverse']};
    }}

    /* === 标签页 === */
    QTabWidget::pane {{
        background-color: {theme['surface']};
        border: 1px solid {theme['border']};
        border-radius: 8px;
        top: -1px;
    }}

    QTabBar {{
        background: transparent;
    }}

    QTabBar::tab {{
        background-color: transparent;
        color: {theme['text_secondary']};
        padding: 10px 22px;
        margin-right: 2px;
        border-top-left-radius: 6px;
        border-top-right-radius: 6px;
        font-weight: 500;
    }}

    QTabBar::tab:hover {{
        background-color: {theme['surface_alt']};
        color: {theme['text']};
    }}

    QTabBar::tab:selected {{
        background: qlineargradient(x1:0, y1:0, x2:1, y2:0,
            stop:0 {primary}, stop:1 {secondary});
        color: {theme['text_inverse']};
    }}

    /* === 进度条 === */
    QProgressBar {{
        background-color: {theme['surface_alt']};
        border: none;
        border-radius: 6px;
        text-align: center;
        color: {theme['text']};
        min-height: 18px;
    }}

    QProgressBar::chunk {{
        background-color: {gradient['accent']};
        border-radius: 6px;
    }}

    /* === 滚动条 === */
    QScrollBar:vertical {{
        background: transparent;
        width: 12px;
        margin: 4px 2px 4px 0px;
    }}

    QScrollBar::handle:vertical {{
        background: {theme['scroll_thumb']};
        min-height: 24px;
        border-radius: 6px;
    }}

    QScrollBar::handle:vertical:hover {{
        background: {theme['scroll_thumb_hover']};
    }}

    QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical {{
        background: transparent;
        height: 0px;
    }}

    QScrollBar:horizontal {{
        background: transparent;
        height: 12px;
        margin: 0px 4px 2px 4px;
    }}

    QScrollBar::handle:horizontal {{
        background: {theme['scroll_thumb']};
        min-width: 24px;
        border-radius: 6px;
    }}

    QScrollBar::handle:horizontal:hover {{
        background: {theme['scroll_thumb_hover']};
    }}

    QScrollBar::add-line:horizontal, QScrollBar::sub-line:horizontal {{
        background: transparent;
        width: 0px;
    }}

    /* === 状态栏 === */
    QStatusBar {{
        background-color: {theme['surface_alt']};
        color: {theme['text']};
        border-top: 1px solid {theme['border']};
    }}

    /* === 工具栏 === */
    QToolBar {{
        background-color: {theme['surface']};
        border-bottom: 1px solid {theme['border']};
        spacing: 4px;
        padding: 4px;
    }}

    /* === 分割器 === */
    QSplitter::handle {{
        background-color: {theme['border']};
    }}

    QSplitter::handle:horizontal {{
        width: 1px;
    }}

    QSplitter::handle:vertical {{
        height: 1px;
    }}

    /* === 分组框 === */
    QGroupBox {{
        background-color: {theme['surface']};
        border: 1px solid {theme['border']};
        border-radius: 8px;
        margin-top: 14px;
        padding: 14px 8px 8px 8px;
        font-weight: 600;
    }}

    QGroupBox::title {{
        subcontrol-origin: margin;
        subcontrol-position: top left;
        left: 12px;
        padding: 0 6px;
        color: {theme['primary']};
        background-color: {theme['surface']};
    }}

    /* === 菜单 === */
    QMenuBar {{
        background-color: {theme['surface']};
        color: {theme['text']};
        border-bottom: 1px solid {theme['border']};
    }}

    QMenuBar::item {{
        background: transparent;
        padding: 6px 12px;
    }}

    QMenuBar::item:selected {{
        background-color: {theme['surface_alt']};
    }}

    QMenu {{
        background-color: {theme['surface']};
        color: {theme['text']};
        border: 1px solid {theme['border']};
        border-radius: 6px;
        padding: 4px;
    }}

    QMenu::item {{
        padding: 6px 24px 6px 12px;
        border-radius: 4px;
    }}

    QMenu::item:selected {{
        background-color: {gradient['accent']};
        color: {theme['text_inverse']};
    }}

    /* === 卡片 (Frame) === */
    QFrame[role="card"] {{
        background-color: {theme['surface']};
        border: 1px solid {theme['border']};
        border-radius: 10px;
    }}

    QFrame[role="sidebar"] {{
        background-color: {_sbg};
        border-right: 1px solid {_sborder};
    }}

    QFrame[role="topbar"] {{
        background-color: {theme['surface']};
        border-bottom: 1px solid {theme['border']};
    }}

    /* === 滚动区域 === */
    QScrollArea {{
        background: transparent;
        border: none;
    }}

    /* === 工具提示 === */
    QToolTip {{
        background-color: {theme['bg_alt']};
        color: {theme['text']};
        border: 1px solid {theme['border']};
        border-radius: 4px;
        padding: 4px 8px;
    }}
    """


# =====================================================================
# 第四部分: 业务逻辑类 (UI 无关, 直接复用原版逻辑)
# =====================================================================

class FileOperationThread(QObject):
    """多线程文件操作类 - 复用原版逻辑, 改为 QObject+信号通知"""

    task_completed = Signal(bool, object, object)  # success, callback_args, error

    def __init__(self, max_threads=4):
        super().__init__()
        self.max_threads = max_threads
        self.active_threads = 0
        self.task_queue = queue.Queue()
        self.is_running = False
        self.lock = threading.Lock()
        self.threads = []

    def start(self):
        if not self.is_running:
            self.is_running = True
            for i in range(self.max_threads):
                t = threading.Thread(target=self._worker, daemon=True)
                t.start()
                self.threads.append(t)

    def stop(self):
        self.is_running = False

    def _worker(self):
        """工作线程: 用 blocking get 替代 busy-wait"""
        while self.is_running:
            try:
                task = self.task_queue.get(timeout=0.5)
                if task:
                    self._execute_task(task)
                    self.task_queue.task_done()
            except queue.Empty:
                continue
            except Exception as e:
                log_error("App", f"文件操作线程错误: {str(e)}")
                try:
                    self.task_queue.task_done()
                except Exception:
                    pass

    def _execute_task(self, task):
        task_type = task.get('type')
        try:
            if task_type == 'copy':
                self._copy_file(task)
            elif task_type == 'move':
                self._move_file(task)
            elif task_type == 'delete':
                self._delete_file(task)
        except Exception as e:
            log_error("App", f"执行文件操作任务错误: {str(e)}")
            if 'callback' in task:
                self.task_completed.emit(False, task.get('callback_args'), str(e))

    def _copy_file(self, task):
        source = task['source']
        target = task['target']
        callback = task.get('callback')
        try:
            target_dir = os.path.dirname(target)
            if not os.path.exists(target_dir):
                os.makedirs(target_dir, exist_ok=True)
            shutil.copy2(source, target)
            if callback:
                self.task_completed.emit(True, task.get('callback_args'), None)
        except Exception as e:
            self.task_completed.emit(False, task.get('callback_args'), str(e))

    def _move_file(self, task):
        source = task['source']
        target = task['target']
        callback = task.get('callback')
        try:
            target_dir = os.path.dirname(target)
            if not os.path.exists(target_dir):
                os.makedirs(target_dir, exist_ok=True)
            shutil.move(source, target)
            if callback:
                self.task_completed.emit(True, task.get('callback_args'), None)
        except Exception as e:
            self.task_completed.emit(False, task.get('callback_args'), str(e))

    def _delete_file(self, task):
        path = task['path']
        callback = task.get('callback')
        try:
            if os.path.isdir(path):
                shutil.rmtree(path)
            else:
                os.remove(path)
            if callback:
                self.task_completed.emit(True, task.get('callback_args'), None)
        except Exception as e:
            self.task_completed.emit(False, task.get('callback_args'), str(e))

    def add_task(self, task):
        self.task_queue.put(task)

    def copy_file(self, source, target, callback=None, callback_args=None):
        self.add_task({
            'type': 'copy',
            'source': source,
            'target': target,
            'callback': callback,
            'callback_args': callback_args
        })

    def move_file(self, source, target, callback=None, callback_args=None):
        self.add_task({
            'type': 'move',
            'source': source,
            'target': target,
            'callback': callback,
            'callback_args': callback_args
        })

    def delete_file(self, path, callback=None, callback_args=None):
        self.add_task({
            'type': 'delete',
            'path': path,
            'callback': callback,
            'callback_args': callback_args
        })

    def copy_directory(self, source_dir, target_dir, progress_callback=None):
        """复制目录 (多线程)"""
        if not os.path.exists(source_dir):
            if progress_callback:
                progress_callback(0, 0, "源目录不存在")
            return False
        if not os.path.exists(target_dir):
            os.makedirs(target_dir, exist_ok=True)

        files_to_copy = []
        total_size = 0
        for root, dirs, files in os.walk(source_dir):
            for file in files:
                file_path = os.path.join(root, file)
                rel_path = os.path.relpath(file_path, source_dir)
                target_path = os.path.join(target_dir, rel_path)
                files_to_copy.append((file_path, target_path))
                try:
                    total_size += os.path.getsize(file_path)
                except OSError:
                    pass

        if not files_to_copy:
            if progress_callback:
                progress_callback(100, 100, "目录为空")
            return True

        copied_count = [0]
        copied_size = [0]

        def file_copy_callback(success, error, args):
            with self.lock:
                copied_count[0] += 1
                if success:
                    current_file = files_to_copy[copied_count[0] - 1]
                    try:
                        copied_size[0] += os.path.getsize(current_file[0])
                    except OSError:
                        pass
                    progress = int((copied_count[0] / len(files_to_copy)) * 100)
                    file_name = os.path.basename(current_file[0])
                    if progress_callback:
                        progress_callback(progress, 100, f"正在复制: {file_name}")
                    if copied_count[0] >= len(files_to_copy):
                        if progress_callback:
                            progress_callback(100, 100, "复制完成")
                else:
                    if progress_callback:
                        progress_callback(0, 100, f"错误: {error}")

        for source, target in files_to_copy:
            self.copy_file(source, target, file_copy_callback)
        return True


class GameInstance:
    """游戏实例 - 复用原版"""

    def __init__(self, name, path):
        self.name = name
        self.path = path
        self.created_time = datetime.now()
        self.id = f"instance_{datetime.now().strftime('%Y%m%d_%H%M%S')}_{hash(name)}"
        self.installed_packages = {
            "ini": [], "map": [], "mission": [], "voice": [],
            "plugin": [], "beautification": [], "music": []
        }

    def to_dict(self):
        return {
            "name": self.name,
            "path": self.path,
            "created_time": self.created_time.isoformat(),
            "id": self.id,
            "installed_packages": self.installed_packages
        }

    @classmethod
    def from_dict(cls, data):
        instance = cls(data["name"], data["path"])
        instance.created_time = datetime.fromisoformat(data["created_time"])
        instance.id = data["id"]
        # 注意: data.get(key, default) 在 key 存在但值为 None 时,仍返回 None
        # 因此必须显式判断 None
        saved_pkgs = data.get("installed_packages")
        if not isinstance(saved_pkgs, dict):
            saved_pkgs = {
                "ini": [], "map": [], "mission": [], "voice": [],
                "plugin": [], "beautification": [], "music": []
            }
        else:
            # 清理: 任何非 list 的值都视为空 list(防数据损坏)
            for k in list(saved_pkgs.keys()):
                if not isinstance(saved_pkgs[k], list):
                    saved_pkgs[k] = []
            # 兼容旧配置: "mod" 旧 key → "ini"(INI 包的目录名已由 mod 改为 ini)
            if "mod" in saved_pkgs and "ini" not in saved_pkgs:
                saved_pkgs["ini"] = saved_pkgs.pop("mod")
            elif "mod" in saved_pkgs and "ini" in saved_pkgs:
                # 两者并存时,合并并去重
                merged = list(dict.fromkeys(list(saved_pkgs.get("ini", [])) + list(saved_pkgs.get("mod", []))))
                saved_pkgs["ini"] = merged
                del saved_pkgs["mod"]
        # 确保所有必要的 key 都存在, 并对每个子列表去重
        for k in ("ini", "map", "mission", "voice", "plugin", "beautification", "music"):
            if k not in saved_pkgs or not isinstance(saved_pkgs[k], list):
                saved_pkgs[k] = []
            else:
                saved_pkgs[k] = list(dict.fromkeys(saved_pkgs[k]))
        instance.installed_packages = saved_pkgs
        return instance

    def get_instance_dir(self, base_path):
        safe_name = re.sub(r'[<>:"/\\|?*]', '_', self.name)
        return os.path.join(base_path, "instances", safe_name)

    def get_backup_path(self, base_path):
        return os.path.join(self.get_instance_dir(base_path), "backup", "Mental_Omega")

    def get_config_path(self, base_path):
        return os.path.join(self.get_instance_dir(base_path), "config.json")

    def get_install_records_dir(self, base_path):
        """获取安装记录目录(用于按包记录的精确卸载)"""
        return os.path.join(self.get_instance_dir(base_path), "install_records")


class InstanceManager(QObject):
    """实例管理器 - 复用原版逻辑"""

    instances_changed = Signal()

    def __init__(self, app, base_path):
        super().__init__()
        self.app = app
        self.base_path = base_path
        self.instances = {}
        self.current_instance = None

    def load_instances(self):
        instances_dir = os.path.join(self.base_path, "instances")
        if not os.path.exists(instances_dir):
            return
        for instance_dir in os.listdir(instances_dir):
            instance_path = os.path.join(instances_dir, instance_dir)
            if os.path.isdir(instance_path):
                config_path = os.path.join(instance_path, "config.json")
                if os.path.exists(config_path):
                    try:
                        with open(config_path, 'r', encoding='utf-8') as f:
                            data = json.load(f)
                        instance = GameInstance.from_dict(data)
                        if self.app.is_mo_directory(instance.path):
                            self.instances[instance.id] = instance
                            self._ensure_instance_dirs(instance)
                    except Exception as e:
                        log_error("App", f"加载实例失败: {instance_dir}, 错误: {str(e)}")

    def _ensure_instance_dirs(self, instance):
        instance_dir = instance.get_instance_dir(self.base_path)
        for dir_path in [
            os.path.join(instance_dir, "backup"),
            os.path.join(instance_dir, "backup", "Mental_Omega")
        ]:
            os.makedirs(dir_path, exist_ok=True)

    def save_instances(self):
        for instance in self.instances.values():
            self._save_instance_config(instance)

    def _save_instance_config(self, instance):
        """保存实例配置。失败抛出异常,由调用方决定如何提示用户。"""
        try:
            os.makedirs(instance.get_instance_dir(self.base_path), exist_ok=True)
        except Exception as e:
            raise RuntimeError(f"无法创建实例目录 {instance.get_instance_dir(self.base_path)}: {e}")
        config_path = instance.get_config_path(self.base_path)
        try:
            with open(config_path, 'w', encoding='utf-8') as f:
                json.dump(instance.to_dict(), f, ensure_ascii=False, indent=4)
        except Exception as e:
            raise RuntimeError(f"无法写入实例配置 {config_path}: {e}")

    def add_instance(self, name, path):
        if not name or not path:
            return False, "实例名称和路径不能为空"
        for instance in self.instances.values():
            if instance.name == name:
                return False, "实例名称已存在"
        if not self.app.is_mo_directory(path):
            return False, "指定路径不是有效的心灵终结游戏目录"
        for instance in self.instances.values():
            if os.path.abspath(instance.path) == os.path.abspath(path):
                return False, "该游戏路径已被其他实例使用"
        instance = GameInstance(name, path)
        self.instances[instance.id] = instance
        self._ensure_instance_dirs(instance)
        self._save_instance_config(instance)
        self.instances_changed.emit()
        return True, f"实例 '{name}' 创建成功"

    def remove_instance(self, instance_id):
        if instance_id not in self.instances:
            return False, "实例不存在"
        instance = self.instances[instance_id]
        instance_dir = instance.get_instance_dir(self.base_path)
        try:
            if self.current_instance and self.current_instance.id == instance_id:
                self.current_instance = None
            if os.path.exists(instance_dir):
                shutil.rmtree(instance_dir)
            del self.instances[instance_id]
            self.instances_changed.emit()
            return True, f"实例 '{instance.name}' 已删除"
        except Exception as e:
            return False, f"删除实例时出错: {str(e)}"

    def rename_instance(self, instance_id, new_name):
        if not new_name:
            return False, "实例名称不能为空"
        for instance in self.instances.values():
            if instance.id != instance_id and instance.name == new_name:
                return False, "实例名称已存在"
        if instance_id not in self.instances:
            return False, "实例不存在"
        old_name = self.instances[instance_id].name
        self.instances[instance_id].name = new_name
        self._save_instance_config(self.instances[instance_id])
        self.instances_changed.emit()
        return True, f"实例重命名成功: '{old_name}' → '{new_name}'"

    def update_instance(self, instance_id, new_name=None, new_path=None):
        """编辑实例:支持修改名称、路径、描述(任意子集)。

        - 仅校验发生变化的字段。
        - 路径变更时必须仍是有效 MO 目录,且未被其他实例占用。
        - 成功后会持久化新配置。
        """
        if instance_id not in self.instances:
            return False, "实例不存在"
        instance = self.instances[instance_id]
        if new_name is not None and new_name != instance.name:
            if not new_name.strip():
                return False, "实例名称不能为空"
            for other in self.instances.values():
                if other.id != instance_id and other.name == new_name:
                    return False, "实例名称已存在"
            instance.name = new_name.strip()
        if new_path is not None and new_path != instance.path:
            if not self.app.is_mo_directory(new_path):
                return False, "指定路径不是有效的心灵终结游戏目录"
            for other in self.instances.values():
                if other.id != instance_id and os.path.abspath(other.path) == os.path.abspath(new_path):
                    return False, "该游戏路径已被其他实例使用"
            instance.path = new_path
        self._save_instance_config(instance)
        self.instances_changed.emit()
        return True, "实例信息已更新"

    def get_instance_list(self):
        return list(self.instances.values())

    def set_current_instance(self, instance_id):
        if instance_id and instance_id in self.instances:
            self.current_instance = self.instances[instance_id]
            return True
        else:
            self.current_instance = None
            return False

    def get_current_instance(self):
        return self.current_instance

    def open_instance_directory(self, instance_id):
        if instance_id not in self.instances:
            return False, "实例不存在"
        instance = self.instances[instance_id]
        instance_dir = instance.get_instance_dir(self.base_path)
        try:
            if sys.platform == 'win32':
                os.startfile(instance_dir)
            else:
                return False, f"实例目录: {instance_dir}"
            return True, "已打开实例目录"
        except Exception as e:
            return False, f"打开目录时出错: {str(e)}"

    def export_instance(self, instance_id, export_path, progress_callback=None,
                        compress_level="标准", preserve_metadata=True):
        """导出实例到压缩文件。

        参数:
            instance_id: 实例 ID
            export_path: 输出文件路径(.zip / .7z)
            progress_callback: 可选进度回调
            compress_level: "快速" | "标准" | "最高压缩"
            preserve_metadata: 是否保留文件修改时间和权限属性
        """
        if instance_id not in self.instances:
            return False, "实例不存在"
        instance = self.instances[instance_id]
        if not os.path.exists(instance.path):
            return False, f"游戏路径不存在: {instance.path}"
        # 校验导出目标路径
        if not export_path:
            return False, "导出路径不能为空"
        export_dir = os.path.dirname(os.path.abspath(export_path))
        if not os.path.isdir(export_dir):
            return False, f"导出目录不存在: {export_dir}"
        # 目标路径若已存在,给出明确提示
        if os.path.exists(export_path):
            return False, f"目标文件已存在,请先删除或更换名称: {export_path}"

        try:
            ext = os.path.splitext(export_path)[1].lower()
            # 使用 os.scandir 递归扫描,比 os.walk + os.path.getsize 快(避免重复 stat)
            files_to_zip = []
            total_size = 0
            scan_errors = 0

            def _scan_dir(path):
                nonlocal total_size, scan_errors
                try:
                    with os.scandir(path) as entries:
                        for entry in entries:
                            if entry.is_file(follow_symlinks=False):
                                try:
                                    st = entry.stat()
                                    file_size = st.st_size
                                except OSError as e:
                                    scan_errors += 1
                                    log_warn("Export", f"跳过无法读取的文件 {entry.path}: {e}")
                                    continue
                                rel_path = os.path.relpath(entry.path, instance.path)
                                files_to_zip.append((entry.path, rel_path, file_size))
                                total_size += file_size
                            elif entry.is_dir(follow_symlinks=False):
                                _scan_dir(entry.path)
                except OSError as e:
                    log_warn("Export", f"扫描目录失败 {path}: {e}")

            _scan_dir(instance.path)
            if not files_to_zip:
                return False, "实例中没有文件可导出"

            # 直接从当前实例的 installed_packages 读取(更准确,避免误用全局)
            installed_packages = {}
            for package_type, packages in instance.installed_packages.items():
                if packages:
                    installed_packages[package_type] = list(packages)

            export_info = {
                "name": instance.name,
                "original_path": instance.path,
                "export_date": datetime.now().isoformat(),
                "manager_version": "2.2",
                "game_files_count": len(files_to_zip),
                "total_size_bytes": total_size,
                "instance_id": instance.id,
                "created_time": instance.created_time.isoformat(),
                "installed_packages": installed_packages,
                "compress_level": compress_level,
                "preserve_metadata": preserve_metadata,
            }

            if ext == '.zip':
                # 压缩等级映射
                level_map = {"快速": 1, "标准": 6, "最高压缩": 9}
                clevel = level_map.get(compress_level, 6)
                ok, err = self._export_to_zip(export_path, files_to_zip, export_info,
                                              total_size, progress_callback,
                                              compress_level=clevel,
                                              preserve_metadata=preserve_metadata)
            elif ext == '.7z':
                ok, err = self._export_to_7z(export_path, files_to_zip, export_info,
                                             total_size, progress_callback,
                                             compress_level=compress_level,
                                             preserve_metadata=preserve_metadata)
            elif ext == '.rar':
                ok, err = self._export_to_rar(export_path, files_to_zip, export_info,
                                              total_size, progress_callback)
            else:
                # 未知后缀:自动改为 .zip
                export_path = os.path.splitext(export_path)[0] + '.zip'
                if os.path.exists(export_path):
                    return False, f"目标文件已存在: {export_path}"
                level_map = {"快速": 1, "标准": 6, "最高压缩": 9}
                clevel = level_map.get(compress_level, 6)
                ok, err = self._export_to_zip(export_path, files_to_zip, export_info,
                                              total_size, progress_callback,
                                              compress_level=clevel,
                                              preserve_metadata=preserve_metadata)
            if ok:
                # 校验最终文件
                if not os.path.isfile(export_path):
                    return False, "导出过程中发生错误: 输出文件未生成"
                final_size = os.path.getsize(export_path)
                if final_size <= 0:
                    return False, "导出过程中发生错误: 输出文件为空"
                fmt = ext.lstrip('.').upper() or "ZIP"
                size_str = _format_size(final_size)
                meta_note = "(已保留文件属性)" if preserve_metadata else "(未保留文件属性)"
                msg = (f"实例 '{instance.name}' 已导出到:\n{export_path}\n\n"
                       f"格式: {fmt} | 压缩等级: {compress_level}\n"
                       f"源文件: {len(files_to_zip)} 个 ({_format_size(total_size)})\n"
                       f"压缩包: {size_str} | {meta_note}")
                if scan_errors > 0:
                    msg += f"\n(注:导出过程中有 {scan_errors} 个文件读取失败,已自动跳过)"
                return True, msg
            return False, err or "导出过程中发生错误"
        except PermissionError as e:
            return False, f"导出失败:权限不足 ({e})。请尝试以管理员身份运行,或更换导出目录。"
        except OSError as e:
            return False, f"导出失败:磁盘/系统错误 ({e})。请检查磁盘空间和路径权限。"
        except Exception as e:
            return False, f"导出失败:{type(e).__name__}: {e}"

    def _export_to_zip(self, zip_path, files_to_zip, export_info, total_size,
                       progress_callback, compress_level=6, preserve_metadata=True):
        """返回 (ok, error_message)。

        参数:
            compress_level: 0-9 的 DEFLATE 等级(0=不压缩, 9=最高压缩)
            preserve_metadata: 是否保留文件修改时间和权限属性
        注意: 使用流式写入避免大文件全量载入内存,防止 OOM 和速度骤降。
        """
        try:
            with zipfile.ZipFile(zip_path, 'w', zipfile.ZIP_DEFLATED,
                                 compresslevel=compress_level) as zipf:
                config_json = json.dumps(export_info, ensure_ascii=False, indent=4)
                zipf.writestr('instance_info.json', config_json)
                processed_size = 0
                last_pct = [-1]  # 节流: 仅在百分比变化时回调
                for file_path, rel_path, file_size in files_to_zip:
                    arcname = f'game_files/{rel_path}'.replace('\\', '/')
                    try:
                        if preserve_metadata:
                            # 用 os.stat 获取 mtime,创建 ZipInfo 后流式写入
                            st = os.stat(file_path)
                            zinfo = zipfile.ZipInfo(arcname)
                            zinfo.date_time = time.localtime(st.st_mtime)[:6]
                            # 保留 POSIX 权限位
                            zinfo.external_attr = (st.st_mode & 0xFFFF) << 16
                            with open(file_path, 'rb') as src:
                                with zipf.open(zinfo, 'w') as dst:
                                    shutil.copyfileobj(src, dst, 1024 * 1024)
                        else:
                            zipf.write(file_path, arcname)
                    except (PermissionError, OSError) as fe:
                        # 单个文件失败,跳过它继续打包(导出时尽量包含更多内容)
                        log_warn("ZIP", f"跳过无法写入的文件 {file_path}: {fe}")
                        continue
                    processed_size += file_size
                    if progress_callback:
                        pct = int((processed_size / total_size) * 100) if total_size > 0 else 0
                        # 节流: 仅百分比真正变化时才回调
                        if pct != last_pct[0]:
                            last_pct[0] = pct
                            progress_callback(pct, 100, f"正在压缩: {rel_path}")
            return True, ""
        except PermissionError as e:
            return False, f"ZIP 导出失败:权限不足 ({e})。请检查目标目录是否可写。"
        except OSError as e:
            return False, f"ZIP 导出失败:磁盘/系统错误 ({e})。请检查磁盘空间。"
        except Exception as e:
            return False, f"ZIP 导出失败:{type(e).__name__}: {e}"

    def _export_to_7z(self, zip_path, files_to_zip, export_info, total_size,
                      progress_callback, compress_level="标准", preserve_metadata=True):
        """返回 (ok, error_message)。"""
        if not SEVENZIP_AVAILABLE:
            return False, "7z 支持不可用,请先安装 py7zr 库 (pip install py7zr)"
        try:
            with py7zr.SevenZipFile(zip_path, mode='w') as sz:
                config_json = json.dumps(export_info, ensure_ascii=False, indent=4)
                sz.writestr('instance_info.json', config_json)
                processed_size = 0
                last_pct = [-1]
                for file_path, rel_path, file_size in files_to_zip:
                    arcname = f'game_files/{rel_path}'.replace('\\', '/')
                    try:
                        # py7zr.write() 默认就会保留 mtime 和 st_mode 元数据
                        sz.write(file_path, arcname)
                    except (PermissionError, OSError) as fe:
                        log_warn("7z", f"跳过无法写入的文件 {file_path}: {fe}")
                        continue
                    processed_size += file_size
                    if progress_callback:
                        pct = int((processed_size / total_size) * 100) if total_size > 0 else 0
                        if pct != last_pct[0]:
                            last_pct[0] = pct
                            progress_callback(pct, 100, f"正在压缩: {rel_path}")
            return True, ""
        except PermissionError as e:
            return False, f"7z 导出失败:权限不足 ({e})。请检查目标目录是否可写。"
        except OSError as e:
            return False, f"7z 导出失败:磁盘/系统错误 ({e})。请检查磁盘空间。"
        except Exception as e:
            return False, f"7z 导出失败:{type(e).__name__}: {e}"

    def _export_to_rar(self, rar_path, files_to_zip, export_info, total_size, progress_callback):
        """导出为 RAR 格式。返回 (ok, error_message)。
        Python 原生 rarfile 不支持写入,因此采用两步策略:
        1) 优先尝试调用系统已安装的 WinRAR / rar CLI 创建 RAR 归档
        2) 若 CLI 不可用,降级为生成 .zip 并提示用户手动改名
        """
        # 1) 尝试使用 WinRAR CLI
        winrar_paths = [
            r"C:\Program Files\WinRAR\WinRAR.exe",
            r"C:\Program Files (x86)\WinRAR\WinRAR.exe",
        ]
        rar_cli = None
        for p in winrar_paths:
            if os.path.isfile(p):
                rar_cli = p
                break
        # 也尝试 PATH 中的 rar / winrar
        if rar_cli is None:
            for name in ("rar", "winrar"):
                from shutil import which
                w = which(name)
                if w:
                    rar_cli = w
                    break

        if rar_cli is not None:
            try:
                # 先把所有文件收集到一个临时目录(便于 CLI 添加整个目录)
                with tempfile.TemporaryDirectory() as stage_dir:
                    config_json = json.dumps(export_info, ensure_ascii=False, indent=4)
                    with open(os.path.join(stage_dir, 'instance_info.json'), 'w', encoding='utf-8') as f:
                        f.write(config_json)
                    game_dir = os.path.join(stage_dir, 'game_files')
                    os.makedirs(game_dir, exist_ok=True)
                    processed_size = 0
                    for file_path, rel_path, file_size in files_to_zip:
                        target = os.path.join(game_dir, rel_path)
                        os.makedirs(os.path.dirname(target), exist_ok=True)
                        try:
                            shutil.copy2(file_path, target)
                        except (PermissionError, OSError) as ce:
                            log_warn("RAR", f"跳过无法复制的文件 {file_path}: {ce}")
                            continue
                        processed_size += file_size
                        if progress_callback:
                            progress = int((processed_size / total_size) * 80) if total_size > 0 else 0
                            progress_callback(progress, 100, f"正在准备: {rel_path}")
                    if progress_callback:
                        progress_callback(85, 100, "正在创建 RAR 归档...")
                    # a - 压缩, -r - 递归, -ep1 - 忽略打包路径
                    cmd = [rar_cli, "a", "-r", "-ep1", "-y", rar_path,
                           os.path.join(stage_dir, "instance_info.json"),
                           os.path.join(game_dir, "*")]
                    creationflags = 0
                    if sys.platform == "win32":
                        creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
                    # 1 hour timeout for RAR archive creation
                    rc = subprocess.run(cmd, capture_output=True, text=True,
                                        creationflags=creationflags, timeout=3600)
                    if rc.returncode == 0 and os.path.isfile(rar_path):
                        if progress_callback:
                            progress_callback(100, 100, "RAR 导出完成")
                        return True, ""
                    log_warn("RAR", f"CLI 退出码 {rc.returncode}: {rc.stderr}")
            except Exception as e:
                log_warn("RAR", f"CLI 导出失败,降级为 ZIP: {e}")

        # 2) 降级: 使用 ZIP 格式(自动改后缀为 .zip)
        zip_path = os.path.splitext(rar_path)[0] + ".zip"
        if os.path.exists(zip_path):
            return False, f"未检测到 WinRAR CLI,且同名 .zip 已存在:{zip_path}。请删除该文件或安装 WinRAR 后重试。"
        if progress_callback:
            progress_callback(0, 100, "未检测到 WinRAR,降级为 ZIP 格式...")
        log_warn("RAR", f"未找到 rar/winrar CLI,降级为 ZIP 格式: {zip_path}")
        return self._export_to_zip(zip_path, files_to_zip, export_info, total_size, progress_callback)

    def import_instance(self, import_path, progress_callback=None):
        if not os.path.exists(import_path):
            return False, "文件不存在", None
        ext = os.path.splitext(import_path)[1].lower()
        with tempfile.TemporaryDirectory() as temp_dir:
            try:
                if progress_callback:
                    progress_callback(0, 100, "正在解压文件...")
                if ext == '.zip':
                    with zipfile.ZipFile(import_path, 'r') as zipf:
                        zipf.extractall(temp_dir)
                elif ext == '.7z':
                    if SEVENZIP_AVAILABLE:
                        with py7zr.SevenZipFile(import_path, mode='r') as sz:
                            sz.extractall(path=temp_dir)
                    else:
                        return False, "7z支持不可用，请安装py7zr库", None
                elif ext == '.rar':
                    if not RARFILE_AVAILABLE:
                        return False, "RAR支持不可用，请安装rarfile库：pip install rarfile", None
                    try:
                        with rarfile.RarFile(import_path, 'r') as rf:
                            rf.extractall(path=temp_dir)
                    except rarfile.RarCannotExec as e:
                        return False, ("RAR 解压需要系统已安装 unrar/WinRAR。"
                                       f"请安装后重试。({e})"), None
                    except Exception as e:
                        return False, f"RAR 解压失败: {e}", None
                else:
                    return False, "不支持的文件格式", None

                config_path = os.path.join(temp_dir, 'instance_info.json')
                if not os.path.exists(config_path):
                    return False, "无效的实例文件：缺少配置文件", None
                with open(config_path, 'r', encoding='utf-8') as f:
                    export_info = json.load(f)
                original_name = export_info.get('name', '导入的实例')
                final_name = self._handle_name_collision(original_name)
                game_files_dir = os.path.join(temp_dir, 'game_files')
                if not os.path.exists(game_files_dir):
                    return False, "无法找到游戏文件目录（game_files）", None
                if not self.app.is_mo_directory(game_files_dir):
                    return False, "解压的文件不是有效的心灵终结游戏目录", None

                if progress_callback:
                    progress_callback(30, 100, "正在创建实例目录...")
                instance_dir = os.path.join(self.base_path, "instances", final_name)
                game_target_dir = instance_dir
                os.makedirs(instance_dir, exist_ok=True)
                if progress_callback:
                    progress_callback(50, 100, "正在复制游戏文件...")
                # 使用 copy_files 的进度反馈与错误计数
                def _import_progress(cur, total, cur_file):
                    # 50% ~ 80% 区间映射复制进度
                    if total > 0:
                        pct = 50 + int(cur / total * 30)
                        try:
                            short_name = os.path.basename(cur_file)
                        except Exception:
                            short_name = "..."
                        progress_callback(pct, 100, f"复制中 ({cur}/{total}): {short_name}")
                copy_result = self.app.copy_files(
                    game_files_dir, game_target_dir, _import_progress
                )
                # 兼容旧的 None 返回值和新的元组
                if isinstance(copy_result, tuple) and len(copy_result) == 3:
                    success, total, failed = copy_result
                    if total > 0 and failed == total:
                        # 完全失败,回滚实例目录
                        shutil.rmtree(instance_dir, ignore_errors=True)
                        return False, f"复制游戏文件失败:所有 {total} 个文件均无法复制", None
                    if failed > 0:
                        # 部分失败,记录警告但继续
                        log_warn("App", f"导入实例时 {failed}/{total} 个文件失败")
                # 复制成功(或部分成功),继续后续流程

                if progress_callback:
                    progress_callback(80, 100, "正在保存配置...")
                instance_config = {
                    "name": final_name,
                    "path": game_target_dir,
                    "created_time": datetime.now().isoformat(),
                    "id": f"instance_{datetime.now().strftime('%Y%m%d_%H%M%S')}_{hash(final_name)}"
                }
                config_file_path = os.path.join(instance_dir, "config.json")
                with open(config_file_path, 'w', encoding='utf-8') as f:
                    json.dump(instance_config, f, ensure_ascii=False, indent=4)

                new_instance = GameInstance(final_name, game_target_dir)
                new_instance.created_time = datetime.now()
                new_instance.id = instance_config["id"]
                self.instances[new_instance.id] = new_instance
                self._ensure_instance_dirs(new_instance)

                installed_packages = export_info.get('installed_packages', {})
                if installed_packages:
                    # 合并到导入实例的 installed_packages
                    for package_type, packages in installed_packages.items():
                        if package_type in new_instance.installed_packages:
                            existing = set(new_instance.installed_packages[package_type])
                            for package in packages:
                                if package not in existing:
                                    new_instance.installed_packages[package_type].append(package)
                    self._save_instance_config(new_instance)

                if progress_callback:
                    progress_callback(100, 100, "导入完成")
                self.instances_changed.emit()
                return True, f"实例 '{final_name}' 导入成功", new_instance.id
            except json.JSONDecodeError:
                return False, "配置文件格式错误", None
            except Exception as e:
                return False, f"导入失败: {str(e)}", None

    def _handle_name_collision(self, original_name):
        name = original_name
        counter = 1
        existing_names = [inst.name for inst in self.instances.values()]
        while name in existing_names:
            name = f"{original_name} ({counter})"
            counter += 1
            if counter > 100:
                name = f"{original_name}_{datetime.now().strftime('%Y%m%d_%H%M%S')}"
                break
        return name

    def get_instance_size(self, instance_id, force=False):
        """获取实例大小(缓存, force=True 强制重新扫描)"""
        if instance_id not in self.instances:
            return 0
        instance = self.instances[instance_id]
        # 缓存检查
        cache_key = f"_size_cache_{instance_id}"
        if not force and hasattr(self, cache_key):
            cached = getattr(self, cache_key)
            if cached.get("mtime") == os.path.getmtime(instance.path):
                return cached["size"]
        total_size = 0
        if os.path.exists(instance.path):
            for root, dirs, files in os.walk(instance.path):
                for f in files:
                    fp = os.path.join(root, f)
                    try:
                        total_size += os.lstat(fp).st_size
                    except OSError:
                        pass
        # 写入缓存
        setattr(self, cache_key, {
            "size": total_size,
            "mtime": os.path.getmtime(instance.path),
        })
        return total_size

    def format_size(self, size_bytes):
        if size_bytes == 0:
            return "0B"
        size_names = ["B", "KB", "MB", "GB"]
        i = 0
        while size_bytes >= 1024 and i < len(size_names) - 1:
            size_bytes /= 1024.0
            i += 1
        return f"{size_bytes:.2f}{size_names[i]}"




# =====================================================================
# 第五部分: UI 工具函数
# =====================================================================

def apply_shadow(widget, blur=20, offset_y=4, opacity=80):
    """为 widget 添加柔和阴影"""
    shadow = QGraphicsDropShadowEffect(widget)
    shadow.setBlurRadius(blur)
    shadow.setOffset(0, offset_y)
    shadow.setColor(QColor(0, 0, 0, opacity))
    widget.setGraphicsEffect(shadow)
    return shadow


def detect_system_theme() -> str:
    """检测 Windows 系统主题"""
    try:
        if sys.platform == 'win32':
            import winreg
            registry = winreg.OpenKey(
                winreg.HKEY_CURRENT_USER,
                r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"
            )
            value, _ = winreg.QueryValueEx(registry, "AppsUseLightTheme")
            winreg.CloseKey(registry)
            return "浅色模式" if value == 1 else "深色模式"
    except Exception as e:
        # 仅在调试模式下打印,避免无 winreg 平台(Linux/Mac)刷屏
        import sys
        if hasattr(sys, '_called_from_test'):
            log_debug("Theme", str(e))
    return "浅色模式"


def get_app_icon_pixmap() -> QPixmap:
    """加载窗口图标"""
    try:
        icon_path = os.path.join(get_program_base_path(), "icon.ico")
        if os.path.exists(icon_path):
            return QPixmap(icon_path)
    except Exception:
        pass
    try:
        icon_data = (
            "R0lGODlhEAAQAPIAAAAAAJmZmf///+7u7sDAwKqqqgAAAAAAAAAAACH5BAEAAA"
            "UALAAAAAAQABAAAAMzWLrc/jDKSau9VgQHBwQIx1EaKJbBipXgSY4sC78B7MlgrB"
            "2NmOCxKCQeh0gkEQEAOw=="
        )
        img = QImage()
        img.loadFromData(base64.b64decode(icon_data))
        return QPixmap.fromImage(img)
    except Exception:
        return QPixmap()


def extract_emoji_icon(text: str) -> str:
    """从文本开头提取 emoji 图标(支持 surrogate pair 与组合字符)。
    返回第一个 emoji 序列;若无则返回空串。
    """
    if not text:
        return ""
    s = text.lstrip()
    chars = []
    for ch in s:
        cp = ord(ch)
        # 跳过变体选择符 (0xFE00-0xFE0F) 与 ZWJ (0x200D)
        if 0xFE00 <= cp <= 0xFE0F or cp == 0x200D:
            chars.append(ch)
            continue
        # 普通空白/控制字符则结束
        if cp <= 0x201F or cp == 0x20:
            break
        chars.append(ch)
    return "".join(chars).strip()


def strip_leading_emoji(text: str) -> str:
    """去掉开头的 emoji 与后面的空白,返回剩余文字(UTF-16 安全)"""
    if not text:
        return ""
    s = text.lstrip()
    if not s:
        return ""
    icon = extract_emoji_icon(s)
    if not icon:
        return s.strip()
    try:
        return s[len(icon):].lstrip()
    except Exception:
        return s[len(icon):].strip()


def _hex_to_rgba(hex_color: str, alpha: float) -> str:
    """将 hex 颜色转为 CSS rgba() 字符串"""
    try:
        c = QColor(hex_color)
        return f"{c.red()},{c.green()},{c.blue()},{alpha:.2f}"
    except Exception:
        return f"0,0,0,{alpha:.2f}"


# =====================================================================
# 第六部分: 自定义按钮 (Fluent 风格)
# =====================================================================

class FluentButton(QPushButton):
    """Fluent 风格按钮: 通过 QSS :hover/:pressed/:disabled 实现状态变化,
    不再使用透明度动画(避免快速 hover 时闪烁)"""

    def __init__(self, text: str, role: str = "accent", parent=None):
        super().__init__(text, parent)
        self.setProperty("role", role)
        self.setCursor(QCursor(Qt.PointingHandCursor))
        # 不再创建 GraphicsEffect,避免 setStyleSheet 覆盖时与 effect 冲突
        self.setMouseTracking(False)


# =====================================================================
# 第七部分: 包管理标签页 (核心)
# =====================================================================

ICONS = {
    "ini": "🎮",
    "map": "🗺️",
    "mission": "🎯",
    "voice": "🎤",
    "plugin": "🔌",
    "beautification": "✨",
    "music": "🎵",
}

PACKAGE_ICONS = {
    ".zip": "📦",
    ".7z": "🗜️",
    ".rar": "🗜️",
    ".map": "🗺️",
    ".ini": "📝",
}


class PackageManagerTab(QWidget):
    """单个包管理标签页 - INI包/地图包/任务包 等共用"""

    install_requested = Signal(str, str)        # name, package_type
    uninstall_requested = Signal(str, str)
    remove_requested = Signal(str, str)
    import_requested = Signal(str)
    download_requested = Signal(str)
    open_dir_requested = Signal(str)

    def __init__(self, app, package_type: str, package_config: dict):
        super().__init__()
        self.app = app
        self.type = package_type
        self.config = package_config
        self.package_dir = app.get_package_dir(package_type)
        self.theme = app.theme

        self._build_ui()
        self.refresh_lists()

    def apply_theme(self, theme: dict):
        self.theme = theme

    def _build_ui(self):
        outer = QVBoxLayout(self)
        outer.setContentsMargins(12, 12, 12, 12)
        outer.setSpacing(8)

        # 标题 + 按钮(横向可滚动,适应窄屏窗口)
        header_scroll = QScrollArea()
        header_scroll.setWidgetResizable(True)
        header_scroll.setFrameShape(QFrame.NoFrame)
        header_scroll.setHorizontalScrollBarPolicy(Qt.ScrollBarAsNeeded)
        header_scroll.setVerticalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
        header_scroll.setFixedHeight(52)

        header = QHBoxLayout()
        header.setContentsMargins(0, 4, 0, 4)
        header.setSpacing(6)

        title = QLabel(f"{ICONS.get(self.type, '📦')} {self.config['name']}管理")
        title.setProperty("role", "subtitle")
        header.addWidget(title)
        header.addStretch()

        install_btn = FluentButton("🚀 安装", "accent")
        install_btn.clicked.connect(self._on_install)
        header.addWidget(install_btn)

        remove_btn = FluentButton("🗑️ 移除", "secondary")
        remove_btn.clicked.connect(self._on_remove)
        header.addWidget(remove_btn)

        uninstall_btn = FluentButton("🗑️ 卸载", "secondary")
        uninstall_btn.clicked.connect(self._on_uninstall)
        header.addWidget(uninstall_btn)

        sep = QFrame()
        sep.setFrameShape(QFrame.VLine)
        sep.setFrameShadow(QFrame.Sunken)
        header.addWidget(sep)

        download_btn = FluentButton("⬇️ 下载", "secondary")
        download_btn.clicked.connect(self._on_download)
        header.addWidget(download_btn)

        import_btn = FluentButton("📥 导入", "secondary")
        import_btn.clicked.connect(self._on_import)
        header.addWidget(import_btn)

        dir_btn = FluentButton("📁 目录", "secondary")
        dir_btn.clicked.connect(self._on_open_dir)
        header.addWidget(dir_btn)

        refresh_btn = FluentButton("🔄 刷新", "secondary")
        refresh_btn.clicked.connect(self.refresh_lists)
        header.addWidget(refresh_btn)

        header_container = QWidget()
        header_container.setLayout(header)
        header_scroll.setWidget(header_container)
        outer.addWidget(header_scroll)

        split = QSplitter(Qt.Horizontal)
        split.setChildrenCollapsible(False)

        left_group = QGroupBox("📦 可用包")
        left_layout = QVBoxLayout(left_group)
        self.available_list = QListWidget()
        self.available_list.itemDoubleClicked.connect(lambda _: self._on_install())
        left_layout.addWidget(self.available_list)
        split.addWidget(left_group)

        right_group = QGroupBox("✅ 已安装")
        right_layout = QVBoxLayout(right_group)
        self.installed_list = QListWidget()
        self.installed_list.itemDoubleClicked.connect(lambda _: self._on_uninstall())
        right_layout.addWidget(self.installed_list)
        split.addWidget(right_group)

        split.setSizes([500, 500])
        outer.addWidget(split, 1)

    def _on_install(self):
        name = self._get_selected_available()
        if name:
            self.install_requested.emit(name, self.type)

    def _on_remove(self):
        name = self._get_selected_available()
        if name:
            self.remove_requested.emit(name, self.type)

    def _on_uninstall(self):
        name = self._get_selected_installed()
        if name:
            self.uninstall_requested.emit(name, self.type)

    def _on_import(self):
        self.import_requested.emit(self.type)

    def _on_download(self):
        self.download_requested.emit(self.type)

    def _on_open_dir(self):
        self.open_dir_requested.emit(self.type)

    def refresh_lists(self):
        self.available_list.setUpdatesEnabled(False)
        self.installed_list.setUpdatesEnabled(False)
        try:
            self._refresh_available()
            self._refresh_installed()
        finally:
            self.available_list.setUpdatesEnabled(True)
            self.installed_list.setUpdatesEnabled(True)

    def _refresh_available(self):
        self.available_list.clear()
        self.package_dir = self.app.get_package_dir(self.type)
        if not os.path.exists(self.package_dir):
            os.makedirs(self.package_dir, exist_ok=True)

        packages = []
        if self.type == "map":
            for root, _, files in os.walk(self.package_dir):
                for file in files:
                    if any(file.lower().endswith(ext) for ext in self.config["extensions"]):
                        rel_path = os.path.relpath(os.path.join(root, file), self.package_dir)
                        packages.append(rel_path)
        else:
            for item in os.listdir(self.package_dir):
                item_path = os.path.join(self.package_dir, item)
                if item.startswith('.') or item.startswith('desktop.ini'):
                    continue
                if os.path.isdir(item_path):
                    if item and (not item[0].isalpha() or not item[0].isprintable()):
                        continue
                if os.path.isdir(item_path):
                    if self._is_valid_package_directory(item_path):
                        packages.append(item)
                elif any(item.lower().endswith(ext) for ext in self.config["extensions"]):
                    if self._is_valid_package_file(item_path):
                        packages.append(item)

        packages.sort()
        for package in packages:
            icon = self._get_package_icon(package)
            self.available_list.addItem(f"{icon}  {package}")

    def _refresh_installed(self):
        self.installed_list.clear()
        current_instance = self.app.instance_manager.get_current_instance()
        if current_instance:
            installed_packages = current_instance.installed_packages.get(self.type, [])
            # 防御: 旧版实例可能存为 None 或其它非 list
            if not isinstance(installed_packages, list):
                installed_packages = []
        else:
            installed_packages = []

        if not installed_packages:
            it = QListWidgetItem("暂无已安装的包")
            it.setFlags(Qt.NoItemFlags)
            self.installed_list.addItem(it)
            return
        seen = set()
        for package in installed_packages:
            if not isinstance(package, str) or not package:
                continue  # 跳过非法记录
            if package in seen:
                continue  # 去重
            seen.add(package)
            icon = self._get_package_icon(package)
            self.installed_list.addItem(f"{icon}  {package}")

    def _get_package_icon(self, name: str) -> str:
        n = name.lower()
        for ext, icon in PACKAGE_ICONS.items():
            if n.endswith(ext):
                return icon
        return "📦"

    def _is_valid_package_directory(self, dir_path: str) -> bool:
        try:
            dir_name = os.path.basename(dir_path).lower()
            if self.type == "map":
                return True
            for file in os.listdir(dir_path):
                file_path = os.path.join(dir_path, file)
                if os.path.isfile(file_path):
                    if any(file.lower().endswith(ext) for ext in self.config["extensions"]):
                        return True
                    if file.lower().endswith('.ini') and any(k in dir_name for k in ['config', 'setting', 'mod', 'theme']):
                        return True
            if not os.listdir(dir_path):
                return False
            return False
        except (OSError, PermissionError):
            return False

    def _is_valid_package_file(self, file_path: str) -> bool:
        try:
            file_name = os.path.basename(file_path)
            if not any(file_name.lower().endswith(ext) for ext in self.config["extensions"]):
                return False
            if os.path.getsize(file_path) == 0:
                return False
            if file_name.startswith('.'):
                return False
            return True
        except (OSError, PermissionError):
            return False

    def _get_selected_available(self) -> str:
        item = self.available_list.currentItem()
        if not item:
            QMessageBox.warning(self, "警告", f"请先选择一个{self.config['name']}")
            return ""
        return self._strip_icon(item.text())

    def _get_selected_installed(self) -> str:
        item = self.installed_list.currentItem()
        if not item:
            QMessageBox.warning(self, "警告", f"请先选择一个已安装的{self.config['name']}")
            return ""
        text = item.text()
        if text == "暂无已安装的包":
            return ""
        return self._strip_icon(text)

    def _strip_icon(self, text: str) -> str:
        """去掉开头的 emoji 与空白(UTF-16 安全)"""
        return strip_leading_emoji(text)


# =====================================================================
# 第八部分: 子窗口对话框
# =====================================================================

class AddInstanceDialog(QDialog):
    """添加游戏实例对话框

    提供:
      - 实例名称(必填,唯一)
      - 游戏路径(必填,必须是有效 MO 目录)
      - 描述(可选,留空则使用默认值)
    实例 ID 会在创建时由 InstanceManager 自动生成(installed_packages 元数据同步初始化)。
    """

    def __init__(self, parent, app, edit_instance=None):
        super().__init__(parent)
        self.app = app
        self.theme = app.theme
        self.edit_instance = edit_instance  # 编辑模式:传入 GameInstance
        self.setWindowTitle("编辑游戏实例" if edit_instance else "添加游戏实例")
        self.setMinimumSize(520, 460)
        self.setModal(True)
        self._build_ui()
        if edit_instance:
            self._load_from_instance(edit_instance)

    def _build_ui(self):
        layout = QVBoxLayout(self)
        layout.setContentsMargins(24, 20, 24, 20)
        layout.setSpacing(12)

        title = QLabel("✏️ 编辑游戏实例" if self.edit_instance else "🎮 添加新游戏实例")
        title.setProperty("role", "title")
        layout.addWidget(title)

        # 实例名称
        name_lbl = QLabel("📝 实例名称:")
        name_lbl.setProperty("role", "subtitle")
        layout.addWidget(name_lbl)
        self.name_edit = QLineEdit()
        self.name_edit.setPlaceholderText("给您的游戏实例起一个容易识别的名字")
        self.name_edit.setMaxLength(64)
        layout.addWidget(self.name_edit)

        # 游戏路径
        path_lbl = QLabel("📁 游戏路径:")
        path_lbl.setProperty("role", "subtitle")
        layout.addWidget(path_lbl)
        path_row = QHBoxLayout()
        self.path_edit = QLineEdit()
        self.path_edit.setPlaceholderText("选择心灵终结游戏的安装目录(含 Mental_Omega 子目录或可执行文件)")
        path_row.addWidget(self.path_edit, 1)
        browse_btn = FluentButton("📂 浏览", "secondary")
        browse_btn.clicked.connect(self._browse)
        path_row.addWidget(browse_btn)
        layout.addLayout(path_row)

        # 路径有效性提示
        self.path_status = QLabel("")
        self.path_status.setProperty("role", "caption")
        self.path_status.setWordWrap(True)
        layout.addWidget(self.path_status)
        self.path_edit.textChanged.connect(self._validate_path)

        # 描述/备注(可选)
        desc_lbl = QLabel("💬 备注(可选):")
        desc_lbl.setProperty("role", "subtitle")
        layout.addWidget(desc_lbl)
        from PySide6.QtWidgets import QTextEdit
        self.desc_edit = QTextEdit()
        self.desc_edit.setPlaceholderText("可记录该实例的用途、配置思路等(留空即可)")
        self.desc_edit.setMaximumHeight(80)
        layout.addWidget(self.desc_edit)

        # 帮助信息
        help_text = QLabel(
            "💡 使用说明:\n"
            "• 实例名称:在同一台电脑上必须唯一\n"
            "• 游戏路径:必须是有效的心灵终结游戏目录\n"
            "• 备注:仅记录在本地配置文件中,不会影响游戏本体\n"
            "• 每个实例都有独立的包管理、设置和备份"
        )
        help_text.setProperty("role", "caption")
        help_text.setWordWrap(True)
        layout.addWidget(help_text)

        layout.addStretch()

        # 按钮
        btn_row = QHBoxLayout()
        btn_row.addStretch()
        cancel_btn = FluentButton("❌ 取消", "secondary")
        cancel_btn.clicked.connect(self.reject)
        btn_row.addWidget(cancel_btn)
        btn_text = "💾 保存修改" if self.edit_instance else "✅ 添加实例"
        ok_btn = FluentButton(btn_text, "accent")
        ok_btn.setDefault(True)
        ok_btn.clicked.connect(self._submit)
        btn_row.addWidget(ok_btn)
        layout.addLayout(btn_row)

    def _load_from_instance(self, inst):
        self.name_edit.setText(inst.name)
        self.path_edit.setText(inst.path)
        # 描述可能没有,安全 get
        desc = ""
        try:
            desc = (inst.to_dict().get("description") or "")
        except Exception:
            desc = ""
        if desc:
            self.desc_edit.setPlainText(desc)

    def _browse(self):
        path = QFileDialog.getExistingDirectory(self, "选择心灵终结安装目录")
        if path:
            self.path_edit.setText(path)

    def _validate_path(self, text):
        text = (text or "").strip()
        if not text:
            self.path_status.setText("")
            return
        if not os.path.isdir(text):
            self.path_status.setText("⚠️ 该目录不存在")
            return
        if self.app.is_mo_directory(text):
            self.path_status.setText("✅ 已识别为有效的心灵终结游戏目录")
        else:
            self.path_status.setText(
                "⚠️ 该目录未被识别为有效游戏目录\n(需含 Mental_Omega 子目录,或 MentalOmegaClient.exe / Mental Omega.exe)"
            )

    def _submit(self):
        name = self.name_edit.text().strip()
        path = self.path_edit.text().strip()
        desc = self.desc_edit.toPlainText().strip()
        if not name:
            QMessageBox.warning(self, "警告", "请输入实例名称")
            return
        if not path:
            QMessageBox.warning(self, "警告", "请选择游戏路径")
            return
        if not os.path.isdir(path):
            QMessageBox.warning(self, "警告", f"游戏路径不存在:\n{path}")
            return
        if not self.app.is_mo_directory(path):
            QMessageBox.warning(
                self, "无效的游戏目录",
                f"所选目录不是有效的心灵终结游戏目录:\n{path}\n\n"
                f"有效目录需满足:\n"
                f"• 含 Mental_Omega 子目录,或\n"
                f"• 含 MentalOmegaClient.exe / Mental Omega.exe,或\n"
                f"• 父目录含 MentalOmegaClient.exe / Mental Omega.exe"
            )
            return
        if self.edit_instance:
            success, message = self.app.instance_manager.update_instance(
                self.edit_instance.id, new_name=name, new_path=path
            )
            # 编辑模式支持备注
            if success and desc:
                try:
                    self.edit_instance.description = desc
                    self.app.instance_manager._save_instance_config(self.edit_instance)
                except Exception:
                    pass
        else:
            success, message = self.app.instance_manager.add_instance(name, path)
            # 写入备注
            if success and desc:
                inst = None
                for v in self.app.instance_manager.instances.values():
                    if v.name == name and v.path == path:
                        inst = v
                        break
                if inst is not None:
                    try:
                        inst.description = desc
                        self.app.instance_manager._save_instance_config(inst)
                    except Exception:
                        pass
        if success:
            QMessageBox.information(self, "成功", message)
            self.accept()
        else:
            QMessageBox.critical(self, "错误", message)


class InstanceManagementDialog(QDialog):
    """实例管理对话框"""

    def __init__(self, parent, app):
        super().__init__(parent)
        self.app = app
        self.theme = app.theme
        self.setWindowTitle("管理游戏实例")
        self.setMinimumSize(700, 500)
        self.setModal(True)
        self._build_ui()
        self.refresh_list()

    def _build_ui(self):
        layout = QVBoxLayout(self)
        layout.setContentsMargins(16, 16, 16, 16)
        layout.setSpacing(10)

        # 标题行
        title_row = QHBoxLayout()
        title = QLabel("🎮 游戏实例管理")
        title.setProperty("role", "title")
        title_row.addWidget(title)
        title_row.addStretch()

        for label, role, slot in [
            ("➕ 添加", "accent", self._add),
            ("✏️ 编辑", "secondary", self._edit),
            ("🗑️ 删除", "secondary", self._delete),
            ("📤 导出", "secondary", self._export),
            ("📋 预览导出配置", "secondary", self._preview_export_config),
            ("📥 导入", "secondary", self._import),
            ("🔗 资源", "secondary", self._download),
            ("🔄 刷新", "secondary", self.refresh_list),
        ]:
            btn = FluentButton(label, role)
            btn.clicked.connect(slot)
            title_row.addWidget(btn)
        layout.addLayout(title_row)

        # 列表
        self.list_widget = QListWidget()
        self.list_widget.itemDoubleClicked.connect(lambda _: self._open_dir())
        self.list_widget.itemSelectionChanged.connect(self._update_size)
        layout.addWidget(self.list_widget, 1)

        # 底部
        bottom = QHBoxLayout()
        self.size_label = QLabel("")
        self.size_label.setProperty("role", "caption")
        bottom.addWidget(self.size_label)
        bottom.addStretch()
        open_dir_btn = FluentButton("📁 打开目录", "secondary")
        open_dir_btn.clicked.connect(self._open_dir)
        bottom.addWidget(open_dir_btn)
        close_btn = FluentButton("关闭", "secondary")
        close_btn.clicked.connect(self.accept)
        bottom.addWidget(close_btn)
        layout.addLayout(bottom)

    def refresh_list(self):
        self.list_widget.clear()
        instances = self.app.instance_manager.get_instance_list()
        if not instances:
            it = QListWidgetItem("暂无游戏实例,请添加新实例")
            it.setFlags(Qt.NoItemFlags)
            self.list_widget.addItem(it)
            self.size_label.setText("")
            return
        current = self.app.instance_manager.get_current_instance()
        for inst in instances:
            is_current = current and current.id == inst.id
            mark = "✅ " if is_current else "    "
            item = QListWidgetItem(f"{mark}{inst.name}  ({inst.path})")
            item.setData(Qt.UserRole, inst.id)
            self.list_widget.addItem(item)
        # 注意:itemSelectionChanged.connect 只在 _build_ui 调用一次,
        # 避免在 refresh_list 内重复 connect 导致 _update_size 被调用多次

    def _update_size(self):
        inst = self._get_selected()
        if inst:
            size = self.app.instance_manager.get_instance_size(inst.id)
            self.size_label.setText(f"占用空间: {self.app.instance_manager.format_size(size)}")
        else:
            self.size_label.setText("")

    def _get_selected(self):
        item = self.list_widget.currentItem()
        if not item:
            return None
        inst_id = item.data(Qt.UserRole)
        if not inst_id:
            return None
        return self.app.instance_manager.instances.get(inst_id)

    def _add(self):
        dlg = AddInstanceDialog(self, self.app)
        if dlg.exec() == QDialog.Accepted:
            self.refresh_list()
            self.app.update_instance_combo()

    def _edit(self):
        """编辑实例:支持修改名称、路径、备注(复用 AddInstanceDialog 的 edit 模式)"""
        inst = self._get_selected()
        if not inst:
            QMessageBox.warning(self, "警告", "请先选择一个实例")
            return
        dlg = AddInstanceDialog(self, self.app, edit_instance=inst)
        if dlg.exec() == QDialog.Accepted:
            self.refresh_list()
            self.app.update_instance_combo()
            self.statusBar().showMessage(f"✅ 实例 '{inst.name}' 信息已更新", 4000)

    def _delete(self):
        inst = self._get_selected()
        if not inst:
            QMessageBox.warning(self, "警告", "请先选择一个实例")
            return
        # 强确认:要求用户输入实例名称以确认删除(防止误操作)
        confirm_text, ok = QInputDialog.getText(
            self, "⚠️ 二次确认 - 删除实例",
            f"此操作不可撤销!\n将彻底删除实例的所有数据(配置/安装包记录/备份索引等)。\n\n"
            f"待删除实例:\n  名称: {inst.name}\n  路径: {inst.path}\n\n"
            f"请输入实例名称 '{inst.name}' 以确认删除:",
            QLineEdit.Normal, ""
        )
        if not ok:
            return
        if confirm_text.strip() != inst.name:
            QMessageBox.warning(
                self, "取消",
                f"输入的名称不匹配,已取消删除。\n\n您输入: {confirm_text}\n实例名称: {inst.name}"
            )
            self.statusBar().showMessage("⚠️ 已取消删除(名称不匹配)", 3000)
            return
        success, message = self.app.instance_manager.remove_instance(inst.id)
        if success:
            QMessageBox.information(self, "成功", message)
            self.refresh_list()
            self.app.update_instance_combo()
        else:
            QMessageBox.critical(self, "错误", message)
        self.statusBar().showMessage(message, 5000)

    def _download(self):
        """跳转到资源中心页面 - 功能已移除"""
        inst = self._get_selected()
        if not inst:
            QMessageBox.warning(self, "警告", "请先选择一个实例")
            return
        QMessageBox.information(self, "提示", "资源中心功能已移除，请在运行环境中手动配置资源。")

    def _export(self):
        inst = self._get_selected()
        if not inst:
            QMessageBox.warning(self, "警告", "请先选择一个实例")
            return
        # 默认输出到实例的父目录(用户选择游戏目录时,通常与游戏同级的位置)
        default_dir = os.path.dirname(inst.path) if inst.path and os.path.isdir(inst.path) else ""
        default_path = os.path.join(default_dir, f"{inst.name}.zip") if default_dir else f"{inst.name}.zip"
        # 仅展示 zip / 7z 两种压缩格式(按用户要求)
        filt = "压缩文件 (*.zip *.7z);;ZIP 文件 (*.zip);;7Z 文件 (*.7z)"
        path, sel_filter = QFileDialog.getSaveFileName(
            self, "导出游戏实例", default_path, filt
        )
        if not path:
            return
        # 根据用户选择的扩展名决定格式
        ext = os.path.splitext(path)[1].lower()
        if ext not in (".zip", ".7z"):
            # 用户键入了不带扩展名的文件名,根据当前选择的过滤器决定
            if "7z" in (sel_filter or "").lower() and SEVENZIP_AVAILABLE:
                path = path + ".7z"
                ext = ".7z"
            else:
                path = path + ".zip"
                ext = ".zip"
        # 若目标已存在,要求确认
        if os.path.exists(path):
            ret = QMessageBox.question(
                self, "目标已存在",
                f"文件已存在:\n{path}\n\n是否覆盖?",
                QMessageBox.Yes | QMessageBox.No, QMessageBox.No
            )
            if ret != QMessageBox.Yes:
                return
            try:
                os.remove(path)
            except Exception as e:
                QMessageBox.critical(self, "错误", f"无法删除已存在文件: {e}")
                return
        # 启动进度对话框并导出
        progress = QProgressDialog("正在准备导出...", "取消", 0, 100, self)
        progress.setWindowTitle("导出实例")
        progress.setWindowModality(Qt.WindowModal)
        progress.setMinimumDuration(0)
        progress.setLabelText(f"正在导出 {inst.name} ...")

        def progress_cb(value, total, status):
            progress.setValue(int(value))
            progress.setLabelText(status or f"导出中... {int(value)}%")
            QApplication.processEvents()
            if progress.wasCanceled():
                return

        QTimer.singleShot(50, lambda: self._do_export(
            inst.id, path, progress, progress_cb, "标准", True
        ))

    def _do_export(self, instance_id, path, progress, progress_cb, level="标准", preserve_meta=True):
        success, message = self.app.instance_manager.export_instance(
            instance_id, path, progress_cb,
            compress_level=level, preserve_metadata=preserve_meta
        )
        progress.close()
        if success:
            QMessageBox.information(self, "导出成功", message)
        else:
            QMessageBox.critical(self, "错误", message)

    def _preview_export_config(self):
        """预览当前实例的导出配置文件内容"""
        inst = self._get_selected()
        if not inst:
            QMessageBox.warning(self, "警告", "请先选择一个实例")
            return

        # 扫描实例文件(与导出逻辑一致)
        files_to_zip = []
        total_size = 0
        scan_errors = 0

        def _scan_dir(path):
            nonlocal scan_errors, total_size
            try:
                with os.scandir(path) as it:
                    for entry in it:
                        if entry.is_dir():
                            _scan_dir(entry.path)
                        elif entry.is_file():
                            try:
                                file_size = entry.stat().st_size
                            except OSError:
                                scan_errors += 1
                                continue
                            rel_path = os.path.relpath(entry.path, inst.path)
                            files_to_zip.append((entry.path, rel_path, file_size))
                            total_size += file_size
            except OSError:
                pass

        _scan_dir(inst.path)

        # 构建导出配置预览
        installed_packages = {}
        for package_type, packages in inst.installed_packages.items():
            if packages:
                installed_packages[package_type] = list(packages)

        export_info = {
            "name": inst.name,
            "original_path": inst.path,
            "export_date": datetime.now().isoformat(),
            "manager_version": "2.2",
            "game_files_count": len(files_to_zip),
            "total_size_bytes": total_size,
            "total_size_readable": _format_size(total_size),
            "instance_id": inst.id,
            "created_time": inst.created_time.isoformat(),
            "installed_packages": installed_packages,
        }

        # 格式化 JSON
        info_json = json.dumps(export_info, ensure_ascii=False, indent=2)

        # 构建显示内容
        preview_lines = [
            f"📋 导出配置文件预览",
            f"",
            f"实例名称: {inst.name}",
            f"实例 ID:   {inst.id}",
            f"游戏路径:  {inst.path}",
            f"创建时间:  {inst.created_time.isoformat()}",
            f"",
            f"──────────────────────────────",
            f"  扫描结果",
            f"──────────────────────────────",
            f"文件数量:  {len(files_to_zip)} 个",
            f"总大小:    {_format_size(total_size)}",
        ]
        if scan_errors:
            preview_lines.append(f"读取失败:  {scan_errors} 个文件(将被跳过)")
        if installed_packages:
            pkg_count = sum(len(v) for v in installed_packages.values())
            preview_lines.extend([
                f"",
                f"──────────────────────────────",
                f"  已安装程序包 ({pkg_count} 个)",
                f"──────────────────────────────",
            ])
            for pkg_type, pkgs in installed_packages.items():
                for pkg in pkgs:
                    preview_lines.append(f"  • [{pkg_type}] {pkg}")

        preview_lines.extend([
            f"",
            f"──────────────────────────────",
            f"  完整的 export_info.json",
            f"──────────────────────────────",
            info_json,
        ])

        preview_text = "\n".join(preview_lines)

        # 使用自定义对话框展示
        dlg = QDialog(self)
        dlg.setWindowTitle(f"导出配置预览 — {inst.name}")
        dlg.setMinimumSize(600, 500)

        dlv = QVBoxLayout(dlg)
        dlv.setContentsMargins(16, 16, 16, 16)
        dlv.setSpacing(12)

        header = QLabel(f"📋 {inst.name} — 导出配置预览")
        header.setStyleSheet("font-size: 13pt; font-weight: 700;")
        dlv.addWidget(header)

        text_edit = QTextEdit()
        text_edit.setReadOnly(True)
        font = QFont("Consolas", 10)
        font.setStyleHint(QFont.Monospace)
        text_edit.setFont(font)
        text_edit.setPlainText(preview_text)
        dlv.addWidget(text_edit, 1)

        # 按钮行
        btn_row = QHBoxLayout()
        btn_row.setSpacing(8)

        copy_btn = FluentButton("📋 复制 JSON", "secondary")
        copy_btn.clicked.connect(lambda: (QApplication.clipboard().setText(info_json),
                                           self.statusBar().showMessage("✅ export_info.json 已复制到剪贴板", 3000)))
        btn_row.addWidget(copy_btn)

        btn_row.addStretch()
        close_btn = FluentButton("关闭", "secondary")
        close_btn.clicked.connect(dlg.accept)
        btn_row.addWidget(close_btn)

        dlv.addLayout(btn_row)
        dlg.exec()

    def _import(self):
        path, _ = QFileDialog.getOpenFileName(
            self, "导入实例",
            "", "压缩文件 (*.zip *.7z *.rar)"
        )
        if not path:
            return
        progress = QProgressDialog("正在导入...", "取消", 0, 100, self)
        progress.setWindowModality(Qt.WindowModal)
        progress.setMinimumDuration(0)

        def progress_cb(value, total, status):
            progress.setValue(int(value))
            progress.setLabelText(status)
            QApplication.processEvents()

        QTimer.singleShot(50, lambda: self._do_import(path, progress, progress_cb))

    def _do_import(self, path, progress, progress_cb):
        success, message, _ = self.app.instance_manager.import_instance(path, progress_cb)
        progress.close()
        if success:
            QMessageBox.information(self, "成功", message)
            self.refresh_list()
            self.app.update_instance_combo()
        else:
            QMessageBox.critical(self, "错误", message)

    def _open_dir(self):
        inst = self._get_selected()
        if not inst:
            QMessageBox.warning(self, "警告", "请先选择一个实例")
            return
        success, message = self.app.instance_manager.open_instance_directory(inst.id)
        if not success:
            QMessageBox.information(self, "目录", message)


class PackageDirectoryBrowserDialog(QDialog):
    """包目录可视化浏览器

    提供:
      - 树形/列表 双视图浏览
      - 文件预览(文本文件内容/图片缩略图/元数据)
      - 基本操作:打开/重命名/删除/在系统资源管理器中显示
    """

    TEXT_PREVIEW_EXTS = {".txt", ".ini", ".md", ".json", ".log", ".cfg", ".xml", ".yaml", ".yml", ".csv"}
    IMAGE_PREVIEW_EXTS = {".png", ".jpg", ".jpeg", ".bmp", ".gif", ".ico"}

    def __init__(self, parent, app, package_type: str, root_path: str):
        super().__init__(parent)
        self.app = app
        self.theme = app.theme
        self.package_type = package_type
        self.root_path = os.path.abspath(root_path)
        self.setWindowTitle(f"📁 包目录浏览器 - {app.package_configs[package_type]['name']}包")
        self.setMinimumSize(820, 560)
        self.setModal(True)
        self._build_ui()
        self._refresh_list()

    def _build_ui(self):
        layout = QVBoxLayout(self)
        layout.setContentsMargins(12, 12, 12, 12)
        layout.setSpacing(8)

        # 顶部:路径 + 操作按钮
        top = QHBoxLayout()
        path_lbl = QLabel("📂 当前目录:")
        path_lbl.setProperty("role", "subtitle")
        top.addWidget(path_lbl)
        self.path_edit = QLineEdit(self.root_path)
        self.path_edit.setReadOnly(True)
        top.addWidget(self.path_edit, 1)
        refresh_btn = FluentButton("🔄 刷新", "secondary")
        refresh_btn.clicked.connect(self._refresh_list)
        top.addWidget(refresh_btn)
        explorer_btn = FluentButton("🗂️ 资源管理器", "secondary")
        explorer_btn.clicked.connect(self._open_in_explorer)
        top.addWidget(explorer_btn)
        layout.addLayout(top)

        # 主分割:左侧文件列表,右侧预览
        split = QSplitter(Qt.Horizontal)
        split.setChildrenCollapsible(False)

        # 左:文件列表
        left = QWidget()
        left_layout = QVBoxLayout(left)
        left_layout.setContentsMargins(0, 0, 0, 0)
        self.file_list = QListWidget()
        self.file_list.itemSelectionChanged.connect(self._on_select)
        self.file_list.itemDoubleClicked.connect(self._on_double_click)
        left_layout.addWidget(self.file_list, 1)

        file_ops = QHBoxLayout()
        rename_btn = FluentButton("✏️ 重命名", "secondary")
        rename_btn.clicked.connect(self._rename_selected)
        file_ops.addWidget(rename_btn)
        delete_btn = FluentButton("🗑️ 删除", "secondary")
        delete_btn.clicked.connect(self._delete_selected)
        file_ops.addWidget(delete_btn)
        file_ops.addStretch()
        left_layout.addLayout(file_ops)
        split.addWidget(left)

        # 右:预览
        right = QWidget()
        right_layout = QVBoxLayout(right)
        right_layout.setContentsMargins(0, 0, 0, 0)
        right_layout.addWidget(QLabel("📄 预览:"))
        self.preview = QTextEdit()
        self.preview.setReadOnly(True)
        self.preview.setPlaceholderText("选择左侧文件以预览内容/查看元数据")
        right_layout.addWidget(self.preview, 1)

        # 状态信息
        self.meta_lbl = QLabel("")
        self.meta_lbl.setProperty("role", "caption")
        self.meta_lbl.setWordWrap(True)
        right_layout.addWidget(self.meta_lbl)
        split.addWidget(right)

        split.setSizes([400, 400])
        layout.addWidget(split, 1)

        # 底部
        bottom = QHBoxLayout()
        bottom.addStretch()
        close_btn = FluentButton("关闭", "secondary")
        close_btn.clicked.connect(self.accept)
        bottom.addWidget(close_btn)
        layout.addLayout(bottom)

    def _refresh_list(self):
        self.file_list.clear()
        if not os.path.isdir(self.root_path):
            self.meta_lbl.setText("⚠️ 目录不存在或已被删除")
            return
        entries = []
        try:
            for name in os.listdir(self.root_path):
                if name.startswith('.') or name.lower() == 'desktop.ini':
                    continue
                full = os.path.join(self.root_path, name)
                entries.append((name, full))
        except (PermissionError, OSError) as e:
            QMessageBox.warning(self, "错误", f"读取目录失败: {e}")
            return
        # 目录优先 + 名称排序
        entries.sort(key=lambda x: (not os.path.isdir(x[1]), x[0].lower()))
        for name, full in entries:
            if os.path.isdir(full):
                icon = "📁"
                size_str = "(目录)"
            else:
                icon = self._file_icon(name)
                try:
                    size_str = _format_size(os.path.getsize(full))
                except OSError:
                    size_str = "(无权限)"
            self.file_list.addItem(f"{icon}  {name}    [{size_str}]")
        self.meta_lbl.setText(
            f"共 {len(entries)} 项 | 目录: {self.root_path}"
        )

    def _file_icon(self, name: str) -> str:
        ext = os.path.splitext(name)[1].lower()
        if ext in self.IMAGE_PREVIEW_EXTS:
            return "🖼️"
        if ext in self.TEXT_PREVIEW_EXTS:
            return "📄"
        if ext in (".zip", ".7z", ".rar"):
            return "📦"
        if ext in (".mp3", ".wav", ".ogg"):
            return "🎵"
        if ext in (".mp4", ".avi", ".mkv"):
            return "🎬"
        return "📃"

    def _on_select(self):
        item = self.file_list.currentItem()
        if not item:
            self.preview.clear()
            return
        name = self._item_name(item.text())
        full = os.path.join(self.root_path, name)
        self._preview_file(full)

    def _on_double_click(self, item):
        name = self._item_name(item.text())
        full = os.path.join(self.root_path, name)
        if os.path.isdir(full):
            # 切换到子目录
            self.root_path = full
            self.path_edit.setText(full)
            self._refresh_list()
        else:
            # 在系统资源管理器中打开
            self._open_in_explorer(full)

    def _item_name(self, display_text: str) -> str:
        """从显示文本中提取文件名(去掉 icon + size)"""
        # 格式: "<icon>  <name>    [<size>]"
        # 提取第二列到 size 前
        s = display_text.strip()
        if "  " in s:
            parts = s.split("  ", 1)
            if len(parts) == 2:
                rest = parts[1]
                # 切掉 [size] 后缀
                if "    [" in rest:
                    rest = rest.rsplit("    [", 1)[0]
                return rest.strip()
        return s

    def _preview_file(self, full: str):
        if not os.path.exists(full):
            self.preview.setPlainText("❌ 文件不存在")
            return
        if os.path.isdir(full):
            self._preview_directory(full)
            return
        # 列出元数据
        try:
            st = os.stat(full)
            size = _format_size(st.st_size)
            mtime = time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(st.st_mtime))
            self.meta_lbl.setText(
                f"📄 {os.path.basename(full)}    |    大小: {size}    |    修改: {mtime}"
            )
        except OSError as e:
            self.meta_lbl.setText(f"❌ 读取文件信息失败: {e}")
            return
        ext = os.path.splitext(full)[1].lower()
        # 文本预览
        if ext in self.TEXT_PREVIEW_EXTS:
            try:
                with open(full, "r", encoding="utf-8", errors="replace") as f:
                    content = f.read(64 * 1024)  # 限制最大 64KB
                if len(content) >= 64 * 1024:
                    content += "\n\n... (内容过长,已截断)"
                self.preview.setPlainText(content)
            except (PermissionError, OSError) as e:
                self.preview.setPlainText(f"❌ 读取失败: {e}")
            return
        # 图片预览(显示尺寸信息,实际渲染需要 QLabel 单独处理)
        if ext in self.IMAGE_PREVIEW_EXTS:
            try:
                with open(full, "rb") as f:
                    head = f.read(64)
                # 简单识别尺寸
                self.preview.setPlainText(
                    f"🖼️ 图片文件\n\n"
                    f"格式: {ext.upper().lstrip('.')}\n"
                    f"大小: {_format_size(st.st_size)}\n"
                    f"路径: {full}\n\n"
                    f"提示: 双击可在系统资源管理器中打开。"
                )
            except Exception as e:
                self.preview.setPlainText(f"❌ 读取图片信息失败: {e}")
            return
        # 压缩包预览
        if ext in (".zip", ".7z", ".rar"):
            try:
                with open(full, "rb") as f:
                    head = f.read(8)
                self.preview.setPlainText(
                    f"📦 压缩包文件\n\n"
                    f"格式: {ext.upper().lstrip('.')}\n"
                    f"大小: {_format_size(st.st_size)}\n"
                    f"路径: {full}\n\n"
                    f"提示: 双击可在系统资源管理器中查看。"
                )
            except Exception as e:
                self.preview.setPlainText(f"❌ 读取压缩包信息失败: {e}")
            return
        # 默认:不预览,显示提示
        self.preview.setPlainText(
            f"📃 {ext.upper().lstrip('.') or '未知'} 文件\n\n"
            f"大小: {_format_size(st.st_size)}\n"
            f"修改时间: {time.strftime('%Y-%m-%d %H:%M:%S', time.localtime(st.st_mtime))}\n"
            f"路径: {full}\n\n"
            f"提示: 该类型文件不支持文本预览。双击可在系统资源管理器中打开。"
        )

    def _preview_directory(self, full: str):
        try:
            entries = os.listdir(full)
            self.meta_lbl.setText(f"📁 目录    |    项数: {len(entries)}")
            text = f"📁 目录: {full}\n\n子项({len(entries)}):\n"
            for name in sorted(entries)[:50]:
                sub = os.path.join(full, name)
                if os.path.isdir(sub):
                    text += f"  📁 {name}/\n"
                else:
                    try:
                        text += f"  📄 {name}  ({_format_size(os.path.getsize(sub))})\n"
                    except OSError:
                        text += f"  📄 {name}  (无权限)\n"
            if len(entries) > 50:
                text += f"  ... 还有 {len(entries) - 50} 项"
            self.preview.setPlainText(text)
        except (PermissionError, OSError) as e:
            self.preview.setPlainText(f"❌ 读取目录失败: {e}")

    def _open_in_explorer(self, target: str = None):
        path = target or self.root_path
        if not os.path.exists(path):
            QMessageBox.warning(self, "警告", f"路径不存在: {path}")
            return
        try:
            if sys.platform == "win32":
                # 在资源管理器中显示(若为文件,定位父目录并选中)
                if os.path.isfile(path):
                    subprocess.run(["explorer", "/select,", os.path.normpath(path)])
                else:
                    os.startfile(path)
            else:
                QMessageBox.information(self, "目录", path)
            self.statusBar_msg(f"已打开: {path}")
        except Exception as e:
            QMessageBox.critical(self, "错误", f"打开失败: {e}")

    def _rename_selected(self):
        item = self.file_list.currentItem()
        if not item:
            QMessageBox.warning(self, "警告", "请先选择要重命名的项目")
            return
        old_name = self._item_name(item.text())
        old_path = os.path.join(self.root_path, old_name)
        new_name, ok = QInputDialog.getText(
            self, "重命名",
            f"将 '{old_name}' 重命名为:",
            QLineEdit.Normal, old_name
        )
        if not ok or not new_name.strip():
            return
        new_name = new_name.strip()
        if new_name == old_name:
            return
        # 合法性校验
        if any(c in new_name for c in '<>:"/\\|?*'):
            QMessageBox.warning(
                self, "非法名称",
                "文件名不能包含以下字符:\n  < > : \" / \\ | ? *"
            )
            return
        new_path = os.path.join(self.root_path, new_name)
        if os.path.exists(new_path):
            ret = QMessageBox.question(
                self, "目标已存在",
                f"目标已存在:\n{new_path}\n\n是否覆盖?",
                QMessageBox.Yes | QMessageBox.No, QMessageBox.No
            )
            if ret != QMessageBox.Yes:
                return
        try:
            os.rename(old_path, new_path)
            self.statusBar_msg(f"✅ 已重命名: {old_name} → {new_name}")
            self._refresh_list()
            # 通知外部:包管理列表需刷新
            if hasattr(self.app, "package_tabs") and self.package_type in self.app.package_tabs:
                self.app.package_tabs[self.package_type].refresh_lists()
        except Exception as e:
            QMessageBox.critical(self, "错误", f"重命名失败: {e}")

    def _delete_selected(self):
        item = self.file_list.currentItem()
        if not item:
            QMessageBox.warning(self, "警告", "请先选择要删除的项目")
            return
        name = self._item_name(item.text())
        path = os.path.join(self.root_path, name)
        if not os.path.exists(path):
            QMessageBox.warning(self, "警告", f"项目不存在: {path}")
            return
        # 二次确认(显示更多信息)
        if os.path.isdir(path):
            try:
                size = _format_size(_dir_size(path))
                count = _dir_file_count(path)
            except Exception:
                size, count = "未知", 0
            ret = QMessageBox.question(
                self, "⚠️ 确认删除目录",
                f"确定要删除目录 '{name}' 吗?\n\n"
                f"  包含: {count} 个文件,合计 {size}\n"
                f"  路径: {path}\n\n"
                f"此操作不可撤销!",
                QMessageBox.Yes | QMessageBox.No, QMessageBox.No
            )
        else:
            try:
                size = _format_size(os.path.getsize(path))
            except OSError:
                size = "未知"
            ret = QMessageBox.question(
                self, "确认删除文件",
                f"确定要删除文件 '{name}' 吗?\n\n"
                f"  大小: {size}\n"
                f"  路径: {path}\n\n"
                f"此操作不可撤销!",
                QMessageBox.Yes | QMessageBox.No, QMessageBox.No
            )
        if ret != QMessageBox.Yes:
            return
        try:
            if os.path.isfile(path):
                os.remove(path)
            else:
                shutil.rmtree(path, ignore_errors=False)
            self.statusBar_msg(f"✅ 已删除: {name}")
            self._refresh_list()
            if hasattr(self.app, "package_tabs") and self.package_type in self.app.package_tabs:
                self.app.package_tabs[self.package_type].refresh_lists()
        except (PermissionError, OSError) as e:
            QMessageBox.critical(
                self, "错误",
                f"删除失败:文件可能被占用或权限不足。\n\n{e}"
            )
        except Exception as e:
            QMessageBox.critical(self, "错误", f"删除失败: {e}")

    def statusBar_msg(self, text: str):
        """向主窗口状态栏写入消息(若可访问)"""
        try:
            top = self.parent()
            if top and hasattr(top, "statusBar"):
                top.statusBar().showMessage(text, 4000)
        except Exception:
            pass


class SettingsDialog(QDialog):
    """设置对话框"""

    def __init__(self, parent, app):
        super().__init__(parent)
        self.app = app
        self.theme = app.theme
        self.setWindowTitle("设置")
        self.setMinimumSize(560, 480)
        self.setModal(True)
        self._build_ui()

    def _build_ui(self):
        layout = QVBoxLayout(self)
        layout.setContentsMargins(16, 16, 16, 16)
        layout.setSpacing(10)

        self.tabs = QTabWidget()
        layout.addWidget(self.tabs, 1)

        # 主题设置
        self.tabs.addTab(self._build_theme_tab(), "🎨 主题设置")
        # 常规设置
        self.tabs.addTab(self._build_general_tab(), "⚙️ 常规设置")
        # 关于
        self.tabs.addTab(self._build_about_tab(), "ℹ️ 关于")

        # 底部按钮
        btn_row = QHBoxLayout()
        btn_row.addStretch()
        save_btn = FluentButton("保存", "accent")
        save_btn.clicked.connect(self._save)
        btn_row.addWidget(save_btn)
        cancel_btn = FluentButton("取消", "secondary")
        cancel_btn.clicked.connect(self.reject)
        btn_row.addWidget(cancel_btn)
        layout.addLayout(btn_row)

    def _build_theme_tab(self) -> QWidget:
        w = QWidget()
        v = QVBoxLayout(w)
        v.setContentsMargins(20, 20, 20, 20)
        v.setSpacing(12)

        title = QLabel("主题设置")
        title.setProperty("role", "title")
        v.addWidget(title)

        row = QHBoxLayout()
        row.addWidget(QLabel("主题模式:"))
        self.theme_mode_combo = QComboBox()
        self.theme_mode_combo.addItems(["浅色模式", "深色模式", "跟随系统"])
        self.theme_mode_combo.setCurrentText(self.app.config.get("theme_mode", "跟随系统"))
        row.addWidget(self.theme_mode_combo)
        row.addStretch()
        v.addLayout(row)

        self.theme_status = QLabel("")
        self.theme_status.setProperty("role", "caption")
        v.addWidget(self.theme_status)
        self._update_theme_status()

        self.theme_mode_combo.currentTextChanged.connect(self._update_theme_status)

        help_lbl = QLabel(
            "💡 主题说明:\n"
            "• 浅色模式:白色\n"
            "• 深色模式:黑色\n"
            "• 跟随系统:系统设置"
        )
        help_lbl.setProperty("role", "caption")
        help_lbl.setWordWrap(True)
        v.addWidget(help_lbl)
        v.addStretch()
        return w

    def _update_theme_status(self):
        mode = self.theme_mode_combo.currentText()
        status = {
            "浅色模式": "☀️ 使用浅色主题界面",
            "深色模式": "🌙 使用深色主题界面",
            "跟随系统": "🖥️ 根据系统设置自动切换主题",
        }
        self.theme_status.setText(status.get(mode, ""))

    def _build_general_tab(self) -> QWidget:
        w = QWidget()
        v = QVBoxLayout(w)
        v.setContentsMargins(20, 20, 20, 20)
        v.setSpacing(12)

        title = QLabel("常规设置")
        title.setProperty("role", "title")
        v.addWidget(title)

        # 自动检测
        self.auto_detect_check = QCheckBox("启动时自动检测游戏路径")
        self.auto_detect_check.setChecked(self.app.config.get("auto_detect_path", True))
        v.addWidget(self.auto_detect_check)

        v.addStretch()
        return w

    def _build_about_tab(self) -> QWidget:
        w = QWidget()
        v = QVBoxLayout(w)
        v.setContentsMargins(20, 20, 20, 20)
        v.setSpacing(8)
        title = QLabel("Hello Mental Omega Launcher")
        title.setProperty("role", "title")
        v.addWidget(title)
        v.addWidget(QLabel(f"版本: {self.app.config.get('version', '1.9')}"))
        v.addWidget(QLabel("基于 PySide6 (Qt6) 现代化 UI 重构"))
        v.addWidget(QLabel("作者: mmm"))
        v.addWidget(QLabel("GitHub: https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher"))
        v.addStretch()
        return w

    def _save(self):
        self.app.config["theme_mode"] = self.theme_mode_combo.currentText()
        self.app.config["auto_detect_path"] = self.auto_detect_check.isChecked()
        self.app.save_config()
        self.app.apply_theme_mode(self.theme_mode_combo.currentText())
        QMessageBox.information(self, "成功", "设置已保存")
        self.accept()


class AboutDialog(QDialog):
    """关于对话框(v2.2)

    提供两种使用方式:
    1. AboutDialog(parent, app)  - 弹窗模式(已不再被主窗口调用)
    2. AboutDialog.build_widget(app, parent) - 内嵌模式(返回完整 widget,
       可直接放入主窗口的 content_stack / 任意布局中)
    """

    QQ_GROUP_NUMBER = "1034243331"   # 官方 QQ 群号
    QQ_GROUP_URL = "https://qm.qq.com/q/E1YzVGzxjU"  # 群邀请链接
    QQ_CHANNEL_NUMBER = "pd07649139"  # QQ 频道号
    QQ_CHANNEL_URL = "https://pd.qq.com/s/a6yl81qi5"  # QQ 频道链接
    GITHUB_URL = "https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher"
    GITHUB_ISSUES_URL = "https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues"
    GITHUB_RELEASES_URL = "https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/releases"
    AUTHOR = "mmm"

    def __init__(self, parent, app):
        super().__init__(parent)
        self.app = app
        self.theme = app.theme
        self.setWindowTitle("关于 - v2.0")
        self.setMinimumSize(500, 460)
        self.setModal(True)
        # 弹窗模式下用 build_widget 创建 UI 并 setLayout
        widget = self.build_widget(app, self, in_dialog=True)
        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.addWidget(widget)

    @staticmethod
    def build_widget(app, parent=None, in_dialog: bool = False) -> QWidget:
        """创建关于页面的完整 widget(用于内嵌到主窗口中,不再单独开窗)

        :param app: 主程序实例(用于 theme / _open_feedback 等)
        :param parent: 父 widget
        :param in_dialog: True 表示用于 QDialog 中(隐藏返回/关闭按钮)
        :return: 构建好的 QWidget(调用方直接放入布局即可)
        """
        container = QWidget(parent)
        outer = QVBoxLayout(container)
        outer.setContentsMargins(0, 0, 0, 0)
        outer.setSpacing(0)

        # 顶部标题区(固定)
        header = QWidget()
        hv = QVBoxLayout(header)
        hv.setContentsMargins(24, 20, 24, 16)
        hv.setSpacing(4)
        title = QLabel("🎮 Hello Mental Omega Launcher")
        title.setProperty("role", "title")
        title.setAlignment(Qt.AlignCenter)
        hv.addWidget(title)
        ver = QLabel("版本: v2.0")
        ver.setProperty("role", "subtitle")
        ver.setAlignment(Qt.AlignCenter)
        hv.addWidget(ver)
        author = QLabel(f"作者: {AboutDialog.AUTHOR}")
        author.setProperty("role", "caption")
        author.setAlignment(Qt.AlignCenter)
        hv.addWidget(author)
        outer.addWidget(header)

        # 中部滚动区
        scroll = QScrollArea()
        scroll.setWidgetResizable(True)
        scroll.setFrameShape(QFrame.NoFrame)
        content = QWidget()
        cv = QVBoxLayout(content)
        cv.setContentsMargins(20, 12, 20, 12)
        cv.setSpacing(10)

        # 2) 元信息区(QQ 群 + 联系方式)
        contact_card = QFrame()
        contact_card.setProperty("role", "card")
        ccv = QVBoxLayout(contact_card)
        ccv.setContentsMargins(16, 12, 16, 12)
        ccv.setSpacing(6)
        cc_title = QLabel("📞 联系我们")
        cc_title.setProperty("role", "subtitle")
        ccv.addWidget(cc_title)

        # QQ 群号行
        qq_row = QHBoxLayout()
        qq_row.addWidget(QLabel("🏠 官方 QQ 群:"))
        qq_number_label = QLabel(AboutDialog.QQ_GROUP_NUMBER)
        qq_number_label.setStyleSheet(
            "font-weight: 700; color: #3498db; font-size: 12pt; padding: 0 4px;"
        )
        qq_number_label.setTextInteractionFlags(Qt.TextSelectableByMouse)
        qq_row.addWidget(qq_number_label)
        copy_qq_btn = FluentButton("📋 复制", "secondary")
        copy_qq_btn.setFixedHeight(28)
        copy_qq_btn.clicked.connect(
            lambda: AboutDialog._copy_qq_to_clipboard(AboutDialog.QQ_GROUP_NUMBER))
        qq_row.addWidget(copy_qq_btn)
        join_qq_btn = FluentButton("💬 一键加群", "accent")
        join_qq_btn.setFixedHeight(28)
        join_qq_btn.clicked.connect(
            lambda: webbrowser.open(AboutDialog.QQ_GROUP_URL))
        qq_row.addWidget(join_qq_btn)
        qq_row.addStretch()
        ccv.addLayout(qq_row)

        # QQ 频道行
        qch_row = QHBoxLayout()
        qch_row.addWidget(QLabel("📡 QQ 频道:"))
        qch_label = QLabel(AboutDialog.QQ_CHANNEL_NUMBER)
        qch_label.setStyleSheet(
            "font-weight: 700; color: #3498db; font-size: 12pt; padding: 0 4px;"
        )
        qch_label.setTextInteractionFlags(Qt.TextSelectableByMouse)
        qch_row.addWidget(qch_label)
        copy_qch_btn = FluentButton("📋 复制", "secondary")
        copy_qch_btn.setFixedHeight(28)
        copy_qch_btn.clicked.connect(
            lambda: AboutDialog._copy_qq_to_clipboard(AboutDialog.QQ_CHANNEL_NUMBER))
        qch_row.addWidget(copy_qch_btn)
        join_qch_btn = FluentButton("📡 加入频道", "accent")
        join_qch_btn.setFixedHeight(28)
        join_qch_btn.clicked.connect(
            lambda: webbrowser.open(AboutDialog.QQ_CHANNEL_URL))
        qch_row.addWidget(join_qch_btn)
        qch_row.addStretch()
        ccv.addLayout(qch_row)

        # GitHub 行
        gh_row = QHBoxLayout()
        gh_row.addWidget(QLabel("🐙 GitHub:"))
        gh_link = QLabel(AboutDialog.GITHUB_URL)
        gh_link.setStyleSheet("color: #3498db;")
        gh_link.setTextInteractionFlags(Qt.TextSelectableByMouse)
        gh_row.addWidget(gh_link, 1)
        open_gh_btn = FluentButton("🌐 打开", "secondary")
        open_gh_btn.setFixedHeight(28)
        open_gh_btn.clicked.connect(
            lambda: webbrowser.open(AboutDialog.GITHUB_URL))
        gh_row.addWidget(open_gh_btn)
        ccv.addLayout(gh_row)

        # GitHub Releases
        rel_row = QHBoxLayout()
        rel_row.addWidget(QLabel("📦 Releases:"))
        rel_link = QLabel(AboutDialog.GITHUB_RELEASES_URL)
        rel_link.setStyleSheet("color: #3498db;")
        rel_link.setTextInteractionFlags(Qt.TextSelectableByMouse)
        rel_row.addWidget(rel_link, 1)
        open_rel_btn = FluentButton("🌐 打开", "secondary")
        open_rel_btn.setFixedHeight(28)
        open_rel_btn.clicked.connect(
            lambda: webbrowser.open(AboutDialog.GITHUB_RELEASES_URL))
        rel_row.addWidget(open_rel_btn)
        ccv.addLayout(rel_row)

        ccv.addWidget(QLabel(f"👤 作者: {AboutDialog.AUTHOR}"))

        # === 使用协议查看入口 ===
        eula_row = QHBoxLayout()
        eula_row.setSpacing(8)
        eula_btn = FluentButton("📜 查看使用协议", "accent")
        eula_btn.setMinimumHeight(36)
        eula_btn.setToolTip("查看完整的使用协议 / EULA 文本")
        # 绑定到外部导入的 show_eula_viewer (延迟导入避免循环引用)
        def _on_view_eula():
            try:
                show_eula_viewer(parent=container.window())
            except Exception as e:
                log_warn("EULA", f"打开协议查看器失败: {e}")
        eula_btn.clicked.connect(_on_view_eula)
        eula_row.addWidget(eula_btn)
        ccv.addLayout(eula_row)

        ccv.addStretch()
        cv.addWidget(contact_card)

        cv.addStretch()
        scroll.setWidget(content)
        outer.addWidget(scroll, 1)

        # 底部按钮(弹窗模式才显示,内嵌模式不显示 - 由主窗口的导航代替)
        if in_dialog:
            footer = QWidget()
            fv = QHBoxLayout(footer)
            fv.setContentsMargins(20, 12, 20, 16)
            fv.addStretch()
            close_btn = FluentButton("关闭", "accent")
            close_btn.clicked.connect(container.window().close)
            fv.addWidget(close_btn)
            outer.addWidget(footer)

        return container

    @staticmethod
    def _copy_qq_to_clipboard(text: str):
        try:
            QGuiApplication.clipboard().setText(text)
            return True
        except Exception as e:
            log_error("Clipboard", str(e))
            return False


class ShoutDialog(QDialog):
    """联机喊话 — 向 QQ 频道发送联机信息"""

    FIELD_LIMITS = {
        "mo_version": 10,   # MO版本 字数限制
        "room_name": 15,    # 房间名字 字数限制
        "room_pwd": 10,     # 密码 字数限制
    }

    # 侮辱性词汇屏蔽列表
    # ── 侮辱性词汇屏蔽 ──
    # 原始词汇(小写), 匹配时会先标准化文本再检测
    _PROFANITY_KEYWORDS = [
        # 中文核心词
        "傻逼", "草泥马", "艹你妈", "操你妈", "cnm",
        "我操", "我艹", "卧槽",
        "他妈", "特么",
        "妈逼", "妈蛋", "尼玛", "你妈",
        "去死", "杂种", "废物", "贱人", "垃圾人",
        "狗日的", "狗东西", "狗娘养",
        "日你", "日了", "日死",
        "滚蛋", "滚开", "滚吧",
        "白痴", "弱智", "脑残", "智障", "脑瘫",
        "婊子", "贱货", "骚货", "荡妇",
        "死全家", "全家死",
        "草你", "操你", "艹你",
        "二逼", "2b", "sb",
        # 英文
        "fuck", "shit", "bitch", "damn",
        "cunt", "asshole", "bastard",
        "dick", "piss", "slut", "whore",
        "retard", "moron", "idiot",
        "dumbass", "jackass", "douche",
        "motherfucker", "bullshit",
    ]

    # 字符替换映射: 用于拆解变形词 (数字→字母, 特殊符→字母)
    _HOMOGLYPH_MAP = str.maketrans({
        '0': 'o', '1': 'i', '2': 'z', '3': 'e',
        '4': 'a', '5': 's', '6': 'g', '7': 't',
        '8': 'b', '9': 'g',
        '@': 'a', '$': 's',
        '·': '', '•': '', '　': '',  # 移除间隔符
    })

    @classmethod
    def _normalize_text(cls, text: str) -> str:
        """标准化文本: 去空格/特殊符/数字替换 → 小写 → 用于匹配"""
        # 移除空白
        t = re.sub(r'\s+', '', text)
        # 字符替换 (数字→字母, 去间隔符)
        t = t.translate(cls._HOMOGLYPH_MAP)
        return t.lower()

    # ── URL / 网址检测 ──
    _URL_PATTERN = re.compile(
        r'https?://\S*'
        r'|www\.\S*\.\S+'
        r'|[a-zA-Z0-9][-a-zA-Z0-9]*\.'
        r'(com|cn|net|org|cc|top|xyz|info|me|tv|io|co|dev|app|link|'
        r'online|site|club|shop|store|xin|vip|work|life|world|fun|run|'
        r'biz|pro|tech|ltd|group|space|press|news|blog|wiki|pub|'
        r'ink|guru|today|email)'
        r'(?:\.[a-z]{2,})?\b',
        re.IGNORECASE,
    )

    # 混淆 URL 检测: 用空格/括号/方括号替换了点或协议的
    _OBFUSCATED_URL_RE = re.compile(
        r'https?\s*:\s*/\s*/'
        r'|www\s*\.'
        r'|[a-zA-Z0-9]+\s*[\[\(\{（【]\s*\.\s*[\]\)\}）】]\s*[a-zA-Z]+'
        r'|[a-zA-Z0-9]+\s+\.\s+[a-zA-Z]+',  # "baidu . com"
        re.IGNORECASE,
    )

    def __init__(self, parent, app, default_msg: str = ""):
        super().__init__(parent)
        self.app = app
        self._hmol_ver = self.app.config.get("version", "2.0")
        self.setWindowTitle("📢 联机喊话 — 发送到 QQ 频道")
        self.setMinimumSize(500, 420)
        self.setModal(True)

        layout = QVBoxLayout(self)
        layout.setContentsMargins(20, 20, 20, 20)
        layout.setSpacing(10)

        # 标题
        title = QLabel("📢 联机喊话")
        title.setStyleSheet("font-size: 14pt; font-weight: 700;")
        layout.addWidget(title)

        desc = QLabel("填写联机信息,消息将发送到配置的 QQ 频道。")
        desc.setStyleSheet("font-size: 9pt; color: #888; margin-bottom: 4px;")
        desc.setWordWrap(True)
        layout.addWidget(desc)

        # --- 结构化表单 ---
        self._inputs = {}
        fields = [
            ("mo_ver", "🎮 MO 版本", 10,
             "例如: 3.3.6原版、Apra2合作版"),
            ("room",  "🏠 房间名字", 15,
             "输入游戏房间名称"),
            ("pwd",   "🔑 密码", 10,
             "设置房间密码 (留空=无密码)"),
        ]
        for key, label_text, maxlen, hint in fields:
            row = QHBoxLayout()
            lbl = QLabel(label_text)
            lbl.setFixedWidth(110)
            lbl.setStyleSheet("font-size: 11pt; font-weight: 600;")
            row.addWidget(lbl)
            le = QLineEdit()
            le.setMaxLength(maxlen)
            le.setPlaceholderText(hint)
            le.setStyleSheet(
                "QLineEdit { padding: 6px 10px; border-radius: 6px; "
                "border: 1px solid #ccc; font-size: 11pt; }"
                "QLineEdit:focus { border-color: #2196F3; }")
            le.textChanged.connect(self._on_field_changed)
            row.addWidget(le)
            cnt = QLabel(f"0/{maxlen}")
            cnt.setFixedWidth(40)
            cnt.setStyleSheet("font-size: 9pt; color: #888;")
            cnt.setAlignment(Qt.AlignRight)
            row.addWidget(cnt)
            layout.addLayout(row)

            self._inputs[key] = {
                "edit": le, "limit": maxlen, "count_label": cnt,
            }

        # HMOL版本 (只读,自动)
        hver_row = QHBoxLayout()
        hver_lbl = QLabel("📦 HMOL 版本")
        hver_lbl.setFixedWidth(110)
        hver_lbl.setStyleSheet("font-size: 11pt; font-weight: 600;")
        hver_row.addWidget(hver_lbl)
        hver_val = QLabel(self._hmol_ver)
        hver_val.setStyleSheet(
            "font-size: 11pt; color: #27ae60; font-weight: 700; "
            "padding: 4px 8px; border: 1px solid #bdc3c7; "
            "border-radius: 4px; background: #f8f9fa;")
        hver_row.addWidget(hver_val)
        hver_row.addStretch()
        layout.addLayout(hver_row)

        # 消息预览
        preview_label = QLabel("📋 消息预览")
        preview_label.setStyleSheet("font-size: 10pt; font-weight: 600; margin-top: 6px;")
        layout.addWidget(preview_label)
        self._preview = QLabel("")
        self._preview.setStyleSheet(
            "font-size: 9pt; color: #555; padding: 8px; border: 1px solid #ddd; "
            "border-radius: 4px; background: #fafafa;")
        self._preview.setWordWrap(True)
        self._preview.setMinimumHeight(60)
        layout.addWidget(self._preview)

        # 状态标签（错误提示）
        self._status_label = QLabel("")
        self._status_label.setStyleSheet("font-size: 9pt; color: #e74c3c;")
        self._status_label.setWordWrap(True)
        self._status_label.setVisible(False)
        layout.addWidget(self._status_label)

        # 按钮区
        btn_layout = QHBoxLayout()
        btn_layout.addStretch()

        cancel_btn = QPushButton("取消")
        cancel_btn.setCursor(QCursor(Qt.PointingHandCursor))
        cancel_btn.setStyleSheet(
            "QPushButton { padding: 8px 20px; border-radius: 6px; "
            "border: 1px solid #ccc; color: #555; font-size: 11pt; }"
            "QPushButton:hover { background: #f0f0f0; }")
        cancel_btn.clicked.connect(self.reject)
        btn_layout.addWidget(cancel_btn)

        self._send_btn = QPushButton("📤 发送")
        self._send_btn.setCursor(QCursor(Qt.PointingHandCursor))
        self._send_btn.setStyleSheet(
            "QPushButton { background-color: #2196F3; color: white; "
            "padding: 8px 24px; border-radius: 6px; border: none; font-size: 11pt; }"
            "QPushButton:hover { background-color: #1976D2; }"
            "QPushButton:disabled { background-color: #BDBDBD; }")
        self._send_btn.clicked.connect(self._do_send)
        self._send_btn.setEnabled(False)
        btn_layout.addWidget(self._send_btn)

        layout.addLayout(btn_layout)

        # 连接发送结果信号
        self.app._qq_shout_signal.connect(self._on_send_result)

        # 初始预览刷新
        self._refresh_preview()

    def closeEvent(self, event):
        try:
            self.app._qq_shout_signal.disconnect(self._on_send_result)
        except Exception:
            pass
        super().closeEvent(event)

    def _check_content(self, text: str) -> str:
        """检测内容是否违规, 返回错误描述(空字符串=合法)"""
        if not text:
            return ""

        # ── URL 检测 ──
        # 第一层: 标准 URL 正则
        urls = self._URL_PATTERN.findall(text)
        if urls:
            first = urls[0].strip()
            return f"⚠️ 消息中不能包含网址: {first}…"
        # 第二层: 混淆 URL (空格/括号分隔, 如 "bai du . com")
        if self._OBFUSCATED_URL_RE.search(text):
            return f"⚠️ 消息中不能包含网址, 请移除"

        # ── 侮辱性词汇检测 ──
        normalized = self._normalize_text(text)
        for kw in self._PROFANITY_KEYWORDS:
            if kw in normalized:
                return f"⚠️ 消息中包含不文明词汇, 请修改后重试"
        return ""

    def _refresh_preview(self):
        """根据当前输入生成消息预览并更新发送按钮状态"""
        mo = self._inputs["mo_ver"]["edit"].text().strip()
        room = self._inputs["room"]["edit"].text().strip()
        pwd = self._inputs["pwd"]["edit"].text().strip()
        lines = [
            f"【MO版本】{mo or '(未填)'}",
            f"【房间名字】{room or '(未填)'}",
        ]
        if pwd:
            lines.append(f"【密码】{pwd}")
        lines.append(f"【HMOL版本】{self._hmol_ver}")
        self._preview.setText("\n".join(lines))
        # 至少 MO版本 或 房间名字 有一个非空即可发送
        can_send = bool(mo and room)
        # 内容审核
        error = self._check_content(mo + room + pwd)
        if error:
            self._show_error(error)
            can_send = False
        else:
            self._status_label.setVisible(False)
        self._send_btn.setEnabled(can_send)

    def _on_field_changed(self):
        """任一输入框文字变化时更新字数和预览"""
        for key, info in self._inputs.items():
            length = len(info["edit"].text())
            limit = info["limit"]
            cnt_lbl = info["count_label"]
            cnt_lbl.setText(f"{length}/{limit}")
            if length > limit:
                cnt_lbl.setStyleSheet(
                    "font-size: 9pt; color: #e74c3c; font-weight: bold;")
            else:
                cnt_lbl.setStyleSheet("font-size: 9pt; color: #888;")
        self._refresh_preview()

    def _build_message(self) -> str:
        """构建最终发送的消息文本"""
        mo = self._inputs["mo_ver"]["edit"].text().strip()
        room = self._inputs["room"]["edit"].text().strip()
        pwd = self._inputs["pwd"]["edit"].text().strip()
        lines = [f"【MO版本】{mo}", f"【房间名字】{room}"]
        if pwd:
            lines.append(f"【密码】{pwd}")
        lines.append(f"【HMOL版本】{self._hmol_ver}")
        return "\n".join(lines)

    def _do_send(self):
        # 速率限制: 防刷屏 (5 次/分钟)
        if RATE_LIMITER_AVAILABLE:
            allowed, retry = get_qq_shout_limiter().check("qq_shout")
            if not allowed:
                self._show_error(f"操作过于频繁, 请等待 {retry:.0f} 秒后再试")
                return
        msg = self._build_message()
        if not msg.strip():
            self._show_error("消息不能为空")
            return
        # 最终内容审核(安全网)
        full_text = ("".join(
            self._inputs[k]["edit"].text().strip()
            for k in ("mo_ver", "room", "pwd")
        ))
        error = self._check_content(full_text)
        if error:
            self._show_error(error)
            return
        if len(msg) > QQ_BOT_MSG_MAX_LENGTH:
            self._show_error(f"消息超过 {QQ_BOT_MSG_MAX_LENGTH} 字限制")
            return
        # 校验子字段字数限制
        for key, info in self._inputs.items():
            if len(info["edit"].text()) > info["limit"]:
                self._show_error(f"「{info['edit'].placeholderText()}」超过 {info['limit']} 字限制")
                return
        # 禁用按钮, 显示发送中
        self._send_btn.setEnabled(False)
        self._send_btn.setText("⏳ 发送中...")
        self._status_label.setVisible(False)
        QApplication.processEvents()
        # 后台发送
        signal = self.app._qq_shout_signal

        def _send_worker(m=msg, sig=signal):
            token = _get_qq_bot_token()
            if not token:
                sig.emit(False, "QQ Bot Token 获取失败,请检查 AppID/AppSecret 配置")
                return
            headers = {
                "Authorization": f"QQBot {token}",
                "Content-Type": "application/json",
            }
            body = {"content": m, "msg_type": 0}
            results = []

            # 1) 发送到 QQ 频道
            ch_id = _get_qq_channel_id()
            if ch_id:
                try:
                    r_ch = ms_requests.post(
                        QQ_BOT_MSG_URL.format(ch_id), json=body,
                        headers=headers, timeout=15)
                    if r_ch.status_code == 200:
                        d = r_ch.json()
                        results.append(f"频道:ok({d.get('id','?')})")
                    else:
                        results.append(f"频道:err({r_ch.status_code})")
                except Exception as e:
                    results.append(f"频道:ex({e})")

            # 2) 发送到 QQ 群
            group_id = _get_qq_group_id()
            if group_id:
                try:
                    r_grp = ms_requests.post(
                        QQ_BOT_GROUP_MSG_URL.format(group_id), json=body,
                        headers=headers, timeout=15)
                    if r_grp.status_code == 200:
                        dg = r_grp.json()
                        results.append(f"群:ok({dg.get('id','?')})")
                    else:
                        results.append(f"群:err({r_grp.status_code})")
                except Exception as e:
                    results.append(f"群:ex({e})")

            result_str = " | ".join(results) if results else "未发送到任何目标"
            all_ok = all("ok" in r for r in results)
            sig.emit(all_ok, result_str)
        threading.Thread(target=_send_worker, daemon=True).start()

    def _on_send_result(self, success: bool, message: str):
        if success:
            self._status_label.setStyleSheet(
                "font-size: 9pt; color: #27ae60; font-weight: bold;")
            self._status_label.setText(f"✅ {message}")
            self._status_label.setVisible(True)
            QTimer.singleShot(2000, self.accept)
        else:
            self._status_label.setStyleSheet(
                "font-size: 9pt; color: #e74c3c; font-weight: bold;")
            self._status_label.setText(f"❌ {message}")
            self._status_label.setVisible(True)
            self._send_btn.setEnabled(True)
            self._send_btn.setText("🔄 重试")

    def _show_error(self, msg: str):
        self._status_label.setText(f"⚠️ {msg}")
        self._status_label.setStyleSheet(
            "font-size: 9pt; color: #e74c3c; font-weight: bold;")
        self._status_label.setVisible(True)


class PackagePreviewDialog(QDialog):
    """包内文件预览对话框 — 自动检测「搬运许可.jpg」和「说明.txt」"""

    TARGET_IMAGE = "搬运许可.jpg"
    TARGET_TEXT = "说明.txt"

    def __init__(self, parent, package_path: str, package_name: str):
        super().__init__(parent)
        self._package_path = package_path
        self._package_name = package_name
        self._image_data = None       # bytes
        self._text_content = None     # str
        self._zoom = 1.0
        self._scan_and_extract()
        self._build_ui()

    # ------------------------------------------------------------------
    def _scan_and_extract(self):
        """扫描压缩包,提取「搬运许可.jpg」和「说明.txt」(不区分大小写,任意层级)"""
        ext = os.path.splitext(self._package_path)[1].lower()
        try:
            if ext == ".zip":
                with zipfile.ZipFile(self._package_path, "r") as zf:
                    self._scan_zip(zf)
            elif ext == ".7z" and SEVENZIP_AVAILABLE:
                import py7zr
                with py7zr.SevenZipFile(self._package_path, "r") as sz:
                    self._scan_7z(sz)
            elif ext == ".rar" and RARFILE_AVAILABLE:
                import rarfile as _rarfile
                with _rarfile.RarFile(self._package_path, "r") as rf:
                    self._scan_rar(rf)
        except Exception as ex:
            log_error("PreviewDlg", f"扫描包内容失败: {ex}")

    def _match(self, name: str) -> str | None:
        bn = os.path.basename(name).lower()
        if bn == self.TARGET_IMAGE.lower():
            return "image"
        if bn == self.TARGET_TEXT.lower():
            return "text"
        return None

    def _scan_zip(self, zf):
        for name in zf.namelist():
            t = self._match(name)
            if t == "image" and self._image_data is None:
                self._image_data = zf.read(name)
            elif t == "text" and self._text_content is None:
                raw = zf.read(name)
                self._text_content = self._decode_text(raw)
            if self._image_data and self._text_content:
                break

    def _scan_7z(self, sz):
        import py7zr
        names = sz.getnames() if hasattr(sz, "getnames") else []
        for name in names:
            t = self._match(name)
            if t is None:
                continue
            if t == "image" and self._image_data is not None:
                continue
            if t == "text" and self._text_content is not None:
                continue
            try:
                data = sz.read([name])
                if data and name in data:
                    content = data[name]
                    if t == "image" and self._image_data is None:
                        self._image_data = content.read() if hasattr(content, "read") else content
                    elif t == "text" and self._text_content is None:
                        raw = content.read() if hasattr(content, "read") else content
                        self._text_content = self._decode_text(raw)
            except Exception:
                pass

    def _scan_rar(self, rf):
        for info in rf.infolist():
            t = self._match(info.filename)
            if t == "image" and self._image_data is None:
                self._image_data = rf.read(info.filename)
            elif t == "text" and self._text_content is None:
                raw = rf.read(info.filename)
                self._text_content = self._decode_text(raw)
            if self._image_data and self._text_content:
                break

    @staticmethod
    def _decode_text(raw: bytes) -> str:
        for enc in ("utf-8", "gbk", "gb2312", "latin-1"):
            try:
                return raw.decode(enc)
            except (UnicodeDecodeError, LookupError):
                continue
        return raw.decode("utf-8", errors="replace")

    # ------------------------------------------------------------------
    def _build_ui(self):
        self.setWindowTitle(f"📦 包内预览 — {self._package_name}")
        self.resize(620, 520)
        self.setMinimumSize(420, 340)

        root = QVBoxLayout(self)
        root.setContentsMargins(16, 16, 16, 12)
        root.setSpacing(10)

        # --- 标题 ---
        title = QLabel(f"<b>📦 {self._package_name}</b>")
        title.setStyleSheet("font-size: 13pt; padding-bottom: 4px;")
        root.addWidget(title)

        # --- 内容区（Tab 切换） ---
        tab = QTabWidget()
        root.addWidget(tab, 1)

        # ---- 图片 Tab ----
        img_tab = QWidget()
        img_layout = QVBoxLayout(img_tab)
        img_layout.setContentsMargins(0, 0, 0, 0)

        if self._image_data:
            self._img_label = QLabel()
            self._img_label.setAlignment(Qt.AlignCenter)
            self._img_label.setMinimumHeight(200)
            self._img_label.setStyleSheet("background: #f0f0f0; border-radius: 6px;")
            self._load_image()

            scroll = QScrollArea()
            scroll.setWidgetResizable(True)
            scroll.setWidget(self._img_label)
            scroll.setStyleSheet("QScrollArea { border: none; background: transparent; }")
            img_layout.addWidget(scroll)

            # 缩放按钮
            zoom_row = QHBoxLayout()
            zoom_row.setSpacing(8)
            for label, z in [("🔍+ 放大", 1.25), ("🔍- 缩小", 0.8), ("↺ 原始", 0.0)]:
                btn = QPushButton(label)
                btn.setFixedHeight(28)
                btn.setStyleSheet(
                    "QPushButton { background: #e0e0e0; border: 1px solid #ccc; "
                    "border-radius: 4px; padding: 2px 12px; font-size: 9pt; }"
                    "QPushButton:hover { background: #d0d0d0; }"
                )
                btn.clicked.connect(lambda _, fac=z: self._do_zoom(fac))
                zoom_row.addWidget(btn)
            zoom_row.addStretch()
            img_layout.addLayout(zoom_row)
        else:
            img_layout.addWidget(QLabel("⚠️ 包内未找到「搬运许可.jpg」"))
            self._img_label = None

        tab.addTab(img_tab, "🖼️ 搬运许可")

        # ---- 文本 Tab ----
        txt_tab = QWidget()
        txt_layout = QVBoxLayout(txt_tab)
        txt_layout.setContentsMargins(0, 0, 0, 0)

        self._txt_edit = QTextEdit()
        self._txt_edit.setReadOnly(True)
        self._txt_edit.setStyleSheet(
            "QTextEdit { font-size: 10pt; border: 1px solid #ccc; border-radius: 4px; "
            "padding: 8px; background: #fafafa; }"
        )
        if self._text_content is not None:
            self._txt_edit.setPlainText(self._text_content)
        else:
            self._txt_edit.setHtml(
                "<i style='color:#999;'>⚠️ 包内未找到「说明.txt」</i>"
            )
        txt_layout.addWidget(self._txt_edit)

        # 复制按钮
        copy_row = QHBoxLayout()
        copy_row.setSpacing(8)
        copy_btn = QPushButton("📋 复制全文")
        copy_btn.setFixedHeight(28)
        copy_btn.setStyleSheet(
            "QPushButton { background: #e0e0e0; border: 1px solid #ccc; "
            "border-radius: 4px; padding: 2px 12px; font-size: 9pt; }"
            "QPushButton:hover { background: #d0d0d0; }"
        )
        copy_btn.clicked.connect(self._copy_text)
        copy_row.addWidget(copy_btn)
        copy_row.addStretch()
        txt_layout.addLayout(copy_row)

        tab.addTab(txt_tab, "📄 说明")

        # 底部按钮
        btn_row = QHBoxLayout()
        btn_row.addStretch()
        close_btn = QPushButton("关闭预览")
        close_btn.setFixedHeight(34)
        close_btn.setStyleSheet(
            "QPushButton { background: #2196F3; color: white; border: none; "
            "border-radius: 4px; padding: 6px 24px; font-size: 10pt; font-weight: bold; }"
            "QPushButton:hover { background: #1976D2; }"
        )
        close_btn.clicked.connect(self.accept)
        btn_row.addWidget(close_btn)
        root.addLayout(btn_row)

        self._status_lbl = QLabel("")
        self._status_lbl.setStyleSheet("font-size: 9pt; color: #666;")
        root.addWidget(self._status_lbl)

    def _load_image(self):
        pix = QPixmap()
        if pix.loadFromData(self._image_data):
            w = min(pix.width(), 560)
            h = min(pix.height(), 380)
            self._img_label.setPixmap(pix.scaled(w, h, Qt.KeepAspectRatio, Qt.SmoothTransformation))
        else:
            self._img_label.setText("❌ 无法解析「搬运许可.jpg」")

    def _do_zoom(self, factor: float):
        if self._img_label is None:
            return
        pix = QPixmap()
        if not pix.loadFromData(self._image_data):
            return
        if factor == 0.0:
            self._zoom = 1.0
        else:
            self._zoom = max(0.2, min(5.0, self._zoom * factor))
        w = int(pix.width() * self._zoom)
        h = int(pix.height() * self._zoom)
        self._img_label.setPixmap(pix.scaled(w, h, Qt.KeepAspectRatio, Qt.SmoothTransformation))

    def _copy_text(self):
        if self._text_content:
            QApplication.clipboard().setText(self._text_content)
            self._status_lbl.setText("已复制到剪贴板")
        else:
            self._status_lbl.setText("无可复制内容")


class FeedbackDialog(QDialog):
    """反馈方式选择对话框(旧版风格: QQ群 / GitHub 二选一, 直接打开链接)"""

    QQ_GROUP_URL = "https://qm.qq.com/q/yb9alU6DpA"
    QQ_CHANNEL_URL = "https://pd.qq.com/s/a6yl81qi5"
    GITHUB_URL = "https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues"

    def __init__(self, parent, app):
        super().__init__(parent)
        self.app = app
        self.theme = app.theme
        self.setWindowTitle("💬 反馈问题")
        self.setMinimumSize(450, 320)
        self.setModal(True)
        self._build_ui()
        self._apply_theme()

    def _build_ui(self):
        layout = QVBoxLayout(self)
        layout.setContentsMargins(20, 16, 20, 16)
        layout.setSpacing(10)

        # 标题
        title = QLabel("💬 请选择反馈方式")
        title.setProperty("role", "title")
        title.setAlignment(Qt.AlignCenter)
        layout.addWidget(title)

        # 说明
        info_text = (
            "感谢您使用本软件!\n"
            "请选择反馈问题的渠道:\n\n"
            "💬 QQ群反馈 (推荐) - 实时交流、问题解答\n"
            "📡 QQ频道 - 优先获取更新与公告\n"
            "🐙 GitHub反馈 - Bug 报告、功能建议"
        )
        info_label = QLabel(info_text)
        info_label.setProperty("role", "caption")
        info_label.setWordWrap(True)
        info_label.setAlignment(Qt.AlignCenter)
        layout.addWidget(info_label)

        layout.addSpacing(10)

        # QQ群按钮 (主推, 蓝底)
        self.qq_btn = QPushButton("💬 QQ群反馈 (推荐)")
        self.qq_btn.setMinimumHeight(48)
        self.qq_btn.setCursor(QCursor(Qt.PointingHandCursor))
        self.qq_btn.clicked.connect(self._open_qq)
        layout.addWidget(self.qq_btn)

        # QQ频道按钮
        self.qch_btn = QPushButton("📡 QQ频道")
        self.qch_btn.setMinimumHeight(48)
        self.qch_btn.setCursor(QCursor(Qt.PointingHandCursor))
        self.qch_btn.clicked.connect(self._open_qch)
        layout.addWidget(self.qch_btn)

        # GitHub 按钮 (深灰底)
        self.github_btn = QPushButton("🐙 GitHub 反馈")
        self.github_btn.setMinimumHeight(48)
        self.github_btn.setCursor(QCursor(Qt.PointingHandCursor))
        self.github_btn.clicked.connect(self._open_github)
        layout.addWidget(self.github_btn)

        layout.addSpacing(10)

        # 关闭
        close_btn = QPushButton("关闭")
        close_btn.setMinimumHeight(36)
        close_btn.setCursor(QCursor(Qt.PointingHandCursor))
        close_btn.clicked.connect(self.accept)
        layout.addWidget(close_btn)

    def _apply_theme(self):
        t = self.theme
        # QQ 按钮: 蓝底白字
        self.qq_btn.setStyleSheet(f"""
            QPushButton {{
                background-color: {t['accent']};
                color: #ffffff;
                border: none;
                border-radius: 8px;
                padding: 10px;
                font-size: 11pt;
                font-weight: 600;
            }}
            QPushButton:hover {{
                background-color: {t['accent_hover']};
            }}
            QPushButton:pressed {{
                background-color: {t['secondary']};
            }}
        """)
        # GitHub 按钮: 深灰底白字
        self.github_btn.setStyleSheet("""
            QPushButton {
                background-color: #24292e;
                color: #ffffff;
                border: none;
                border-radius: 8px;
                padding: 10px;
                font-size: 11pt;
                font-weight: 500;
            }
            QPushButton:hover {
                background-color: #2f363d;
            }
            QPushButton:pressed {
                background-color: #1b1f23;
            }
        """)
        # 关闭按钮
        self.setStyleSheet(f"""
            QDialog {{
                background-color: {t['bg']};
            }}
            QPushButton {{
                background-color: {t['surface_alt']};
                color: {t['text']};
                border: 1px solid {t['border']};
                border-radius: 6px;
                padding: 8px;
            }}
            QPushButton:hover {{
                background-color: {t['border']};
            }}
        """)

    def _open_qq(self):
        webbrowser.open(self.QQ_GROUP_URL)
        self.accept()

    def _open_qch(self):
        webbrowser.open(self.QQ_CHANNEL_URL)
        self.accept()

    def _open_github(self):
        webbrowser.open(self.GITHUB_URL)
        self.accept()


class DependencyWarningDialog(QDialog):
    """依赖警告对话框"""

    def __init__(self, parent, missing_libs):
        super().__init__(parent)
        self.setWindowTitle("⚠️ 依赖缺失")
        self.setMinimumSize(520, 380)
        self.setModal(True)
        self.missing_libs = missing_libs
        self._build_ui()

    def _build_ui(self):
        layout = QVBoxLayout(self)
        layout.setContentsMargins(20, 16, 20, 16)
        layout.setSpacing(10)

        title = QLabel("⚠️ 部分功能依赖缺失")
        title.setProperty("role", "warning")
        title.setStyleSheet("font-size: 14pt; font-weight: 600;")
        layout.addWidget(title)

        info = QLabel(
            "以下功能依赖的 Python 库未安装:\n\n" +
            "\n".join(f"  • {lib}" for lib in self.missing_libs) +
            "\n\n部分功能将无法使用,可通过以下命令安装:"
        )
        info.setWordWrap(True)
        layout.addWidget(info)

        cmd_edit = QPlainTextEdit()
        cmd_edit.setPlainText("pip install " + " ".join(self.missing_libs))
        cmd_edit.setReadOnly(True)
        cmd_edit.setMaximumHeight(60)
        layout.addWidget(cmd_edit)

        layout.addStretch()
        btn_row = QHBoxLayout()
        copy_btn = FluentButton("📋 复制命令", "secondary")
        copy_btn.clicked.connect(lambda: QApplication.clipboard().setText(cmd_edit.toPlainText()))
        btn_row.addWidget(copy_btn)
        btn_row.addStretch()
        close_btn = FluentButton("知道了", "accent")
        close_btn.clicked.connect(self.accept)
        btn_row.addWidget(close_btn)
        layout.addLayout(btn_row)


# =====================================================================
# =====================================================================
# 第九部分: 主窗口
# =====================================================================

# ---------------------------------------------------------------------
# 默认背景资源(内嵌 Base64,确保首次启动也有背景显示)
# ---------------------------------------------------------------------
# 心灵终结主题渐变默认背景 - 使用程序内置渐变作为兜底
DEFAULT_HOME_BG_KEY = "__default_gradient__"


# ---------------------------------------------------------------------
# 实例下拉列表项 - 复用于"启动游戏"按钮的下拉菜单
# ---------------------------------------------------------------------
class InstanceListItemWidget(QWidget):
    """下拉列表中的单个实例项 - 自定义 widget
    视觉布局(参考图示):
        [🎮 图标] [名称(粗体)        ] [版本徽章]
                  [版本/路径(灰色小字)]
    交互:
        - hover: 背景变浅灰
        - 当前选中: 背景变浅蓝
        - 点击: 触发 clicked 信号
    """

    clicked = Signal(object)  # 参数: GameInstance

    def __init__(self, instance, is_current=False, parent=None):
        super().__init__(parent)
        self.instance = instance
        self._is_current = is_current
        self._is_hovered = False
        self.setMouseTracking(True)
        self.setAttribute(Qt.WA_Hover, True)

        # 主布局: 横向 [图标] [文字区域] [徽章]
        h = QHBoxLayout(self)
        h.setContentsMargins(12, 8, 12, 8)
        h.setSpacing(10)

        # 1) 图标(实例图标或默认游戏图标)
        self._icon_label = QLabel()
        self._icon_label.setFixedSize(36, 36)
        self._icon_label.setAlignment(Qt.AlignCenter)
        self._set_icon_for_instance(instance)
        h.addWidget(self._icon_label, 0, Qt.AlignVCenter)

        # 2) 文字区域(垂直: 名称 + 副标题)
        text_v = QVBoxLayout()
        text_v.setSpacing(2)
        text_v.setContentsMargins(0, 0, 0, 0)

        name_label = QLabel(self._format_name(instance))
        name_font = name_label.font()
        name_font.setPointSize(13)
        name_font.setBold(True)
        name_label.setFont(name_font)
        text_v.addWidget(name_label)

        subtitle = self._format_subtitle(instance)
        sub_label = QLabel(subtitle)
        sub_font = sub_label.font()
        sub_font.setPointSize(10)
        sub_label.setFont(sub_font)
        sub_label.setStyleSheet("color: #888;")
        sub_label.setWordWrap(False)
        # 单行截断(过长显示省略号)
        fm = sub_label.fontMetrics()
        elided = fm.elidedText(subtitle, Qt.ElideRight, 280)
        sub_label.setText(elided)
        sub_label.setToolTip(subtitle)  # 鼠标悬停显示完整
        text_v.addWidget(sub_label)

        h.addLayout(text_v, 1)

        # 3) 版本徽章(从实例名解析或显示"当前")
        version_badge_text = self._get_version_badge(instance)
        if version_badge_text:
            badge = QLabel(version_badge_text)
            badge.setAlignment(Qt.AlignCenter)
            badge.setFixedHeight(22)
            badge.setMinimumWidth(36)
            badge.setStyleSheet("""
                QLabel {
                    background-color: #4CAF50;
                    color: white;
                    border-radius: 4px;
                    padding: 2px 8px;
                    font-size: 11px;
                    font-weight: bold;
                }
            """)
            h.addWidget(badge, 0, Qt.AlignVCenter)
        elif is_current:
            # 当前实例: 显示 ✓ 标记
            check = QLabel("✓")
            check.setAlignment(Qt.AlignCenter)
            check.setFixedWidth(24)
            check.setStyleSheet("color: #4CAF50; font-size: 18px; font-weight: bold;")
            h.addWidget(check, 0, Qt.AlignVCenter)

        # 固定高度,让菜单项视觉一致
        self.setFixedHeight(56)
        self.setMinimumWidth(300)

    # ---------- 辅助方法 ----------
    def _set_icon_for_instance(self, instance):
        """为实例设置图标 - 尝试加载实例目录中的图标,失败则用默认"""
        icon_path = None
        if hasattr(instance, 'path') and instance.path:
            # 尝试常见的图标路径
            for name in ("icon.png", "logo.png", "instance.png", "thumbnail.png"):
                candidate = os.path.join(instance.path, name)
                if os.path.isfile(candidate):
                    icon_path = candidate
                    break
        if icon_path:
            pixmap = QPixmap(icon_path)
            if not pixmap.isNull():
                self._icon_label.setPixmap(
                    pixmap.scaled(36, 36, Qt.KeepAspectRatio, Qt.SmoothTransformation)
                )
                return
        # 默认图标: 渐变背景 + 字母
        self._set_default_icon(self._format_name(instance)[:1])

    def _set_default_icon(self, letter):
        """生成默认图标(带首字母的彩色方块)"""
        pixmap = QPixmap(36, 36)
        pixmap.fill(Qt.transparent)
        painter = QPainter(pixmap)
        try:
            painter.setRenderHint(QPainter.Antialiasing, True)
            # 渐变背景
            gradient = QLinearGradient(0, 0, 36, 36)
            gradient.setColorAt(0, QColor("#667eea"))
            gradient.setColorAt(1, QColor("#764ba2"))
            painter.setBrush(QBrush(gradient))
            painter.setPen(Qt.NoPen)
            painter.drawRoundedRect(0, 0, 36, 36, 8, 8)
            # 字母
            painter.setPen(QColor(255, 255, 255))
            f = painter.font()
            f.setPointSize(16)
            f.setBold(True)
            painter.setFont(f)
            painter.drawText(pixmap.rect(), Qt.AlignCenter, letter.upper() if letter else "G")
        finally:
            painter.end()
        self._icon_label.setPixmap(pixmap)

    def _format_name(self, instance):
        """获取实例名称"""
        return getattr(instance, 'name', '未命名')

    def _format_subtitle(self, instance):
        """格式化副标题(显示路径或版本信息)"""
        path = getattr(instance, 'path', '')
        # 截断过长的路径
        if len(path) > 60:
            path = "..." + path[-57:]
        return path or "(无路径)"

    def _get_version_badge(self, instance):
        """从实例名中解析版本号,作为徽章显示"""
        name = getattr(instance, 'name', '')
        # 简单正则: 提取 x.y.z 或 x.y 形式的版本号
        import re as _re
        m = _re.search(r'(\d+\.\d+(?:\.\d+)?)', name)
        if m:
            return m.group(1)
        return None

    def set_current(self, is_current):
        self._is_current = is_current
        self.update()

    # ---------- 事件 ----------
    def enterEvent(self, event):
        self._is_hovered = True
        self.update()
        super().enterEvent(event)

    def leaveEvent(self, event):
        self._is_hovered = False
        self.update()
        super().leaveEvent(event)

    def mousePressEvent(self, event):
        if event.button() == Qt.LeftButton:
            self.clicked.emit(self.instance)
        super().mousePressEvent(event)

    def paintEvent(self, event):
        """自定义背景绘制 - 选中/hover 时高亮"""
        painter = QPainter(self)
        try:
            if self._is_current:
                painter.fillRect(self.rect(), QColor(227, 242, 253))  # 浅蓝
            elif self._is_hovered:
                painter.fillRect(self.rect(), QColor(245, 245, 245))  # 浅灰
            # 底部细线(分隔)
            painter.setPen(QColor(230, 230, 230))
            painter.drawLine(0, self.height() - 1, self.width(), self.height() - 1)
        finally:
            painter.end()
        super().paintEvent(event)


# ---------------------------------------------------------------------
# 分体式启动按钮 - 左侧启动 + 右侧下拉箭头
# ---------------------------------------------------------------------
class InstanceLaunchButton(QWidget):
    """分体式启动游戏按钮:
    ┌─────────────────────────┬──┐
    │  🚀 启动游戏            │ ▲ │  ← 整体视觉是一个圆角按钮
    │     {当前实例名}         │   │
    └─────────────────────────┴──┘
    左侧: 点击启动当前实例
    右侧: 点击展开下拉列表(可切换实例)

    设计原则:
    - 单个圆角视觉,中间有 1px 分隔
    - 不使用 QGraphicsEffect(避免闪烁)
    - 主题色与背景融合
    """

    launch_requested = Signal()  # 点击左侧(启动)
    instance_switch_requested = Signal(object)  # 切换实例(传入 GameInstance)

    def __init__(self, parent=None):
        super().__init__(parent)
        self._current_instance = None
        self._instances = []
        self._menu_items = []  # 防止被 GC

        # ===== 启动按钮(左侧主区域) =====
        self.launch_btn = QPushButton()
        self.launch_btn.setCursor(QCursor(Qt.PointingHandCursor))
        self.launch_btn.setMinimumHeight(60)
        self.launch_btn.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Preferred)
        self.launch_btn.setToolTip("点击启动当前游戏实例")
        self.launch_btn.clicked.connect(self._on_launch_clicked)
        self._apply_launch_btn_style()

        # ===== 箭头按钮(右侧) =====
        self.arrow_btn = QToolButton()
        self.arrow_btn.setText("▲")
        self.arrow_btn.setCursor(QCursor(Qt.PointingHandCursor))
        self.arrow_btn.setFixedWidth(36)
        self.arrow_btn.setMinimumHeight(60)
        self.arrow_btn.setToolTip("展开实例列表 - 切换游戏实例")
        self.arrow_btn.setPopupMode(QToolButton.InstantPopup)
        self._apply_arrow_btn_style()

        # ===== 下拉菜单 =====
        self.menu = QMenu(self)
        self.menu.setWindowFlags(self.menu.windowFlags() | Qt.FramelessWindowHint)
        self.menu.setAttribute(Qt.WA_TranslucentBackground, True)
        # 自定义 QSS:无边框,圆角,阴影(用 QSS box-shadow 而非 effect)
        self.menu.setStyleSheet("""
            QMenu {
                background: white;
                border: 1px solid #d0d0d0;
                border-radius: 8px;
                padding: 6px 0;
            }
            QMenu::item {
                background: transparent;
                padding: 0;
            }
        """)
        self.arrow_btn.setMenu(self.menu)
        # 当菜单关闭时,恢复箭头向上(避免持续高亮)
        self.menu.aboutToHide.connect(self._on_menu_hide)

        # ===== 主布局 =====
        main_layout = QHBoxLayout(self)
        main_layout.setContentsMargins(0, 0, 0, 0)
        main_layout.setSpacing(0)
        main_layout.addWidget(self.launch_btn)
        main_layout.addWidget(self.arrow_btn)

        # 整体圆角通过子按钮的 border-radius 实现
        self.setMinimumHeight(60)

        # 初始化按钮显示文本(显示未选择实例状态)
        self._update_launch_btn_text()

    # ---------- 样式 ----------
    def _apply_launch_btn_style(self):
        """启动按钮的 QSS(左侧,圆角左侧)"""
        self.launch_btn.setStyleSheet("""
            QPushButton {
                background-color: #4CAF50;
                color: white;
                border: none;
                border-top-left-radius: 10px;
                border-bottom-left-radius: 10px;
                padding: 8px 16px;
                font-size: 14px;
                font-weight: bold;
                text-align: left;
            }
            QPushButton:hover {
                background-color: #43A047;
            }
            QPushButton:pressed {
                background-color: #388E3C;
            }
        """)

    def _apply_arrow_btn_style(self):
        """箭头按钮的 QSS(右侧,圆角右侧,颜色稍深)"""
        self.arrow_btn.setStyleSheet("""
            QToolButton {
                background-color: #388E3C;
                color: white;
                border: none;
                border-top-right-radius: 10px;
                border-bottom-right-radius: 10px;
                font-size: 11px;
                padding: 0;
            }
            QToolButton:hover {
                background-color: #2E7D32;
            }
            QToolButton:pressed {
                background-color: #1B5E20;
            }
            QToolButton::menu-indicator {
                image: none;
            }
        """)

    # ---------- 公共 API ----------
    def set_instances(self, instances, current_instance=None):
        """设置实例列表和当前选中实例
        instances: List[GameInstance]
        current_instance: GameInstance | None
        """
        self._instances = list(instances) if instances else []
        # 推断 current_instance(若未提供)
        if current_instance is None and self._instances:
            # 找 id 匹配;否则用第一个
            for inst in self._instances:
                if self._current_instance and inst.id == self._current_instance.id:
                    current_instance = inst
                    break
            if current_instance is None:
                current_instance = self._instances[0]
        self._current_instance = current_instance
        self._rebuild_menu()
        self._update_launch_btn_text()

    def set_current_instance(self, current_instance):
        """更新当前实例(不重建菜单)"""
        self._current_instance = current_instance
        self._update_launch_btn_text()

    def get_current_instance(self):
        return self._current_instance

    # ---------- 内部 ----------
    def _update_launch_btn_text(self):
        """更新启动按钮显示的实例名"""
        if self._current_instance:
            name = self._current_instance.name
            # 截断过长名称
            display_name = name if len(name) <= 18 else name[:17] + "…"
            self.launch_btn.setText(f"🚀  启动游戏\n   {display_name}")
            self.launch_btn.setToolTip(f"启动游戏: {name}")
        else:
            self.launch_btn.setText("🚀  启动游戏\n   (未选择实例)")
            self.launch_btn.setToolTip("请先添加并选择游戏实例")

    def _rebuild_menu(self):
        """重建下拉菜单"""
        self.menu.clear()
        self._menu_items.clear()

        if not self._instances:
            # 无实例: 显示提示项
            placeholder = QAction("暂无游戏实例,请先在实例管理中添加", self.menu)
            placeholder.setEnabled(False)
            self.menu.addAction(placeholder)
            return

        # 添加标题(不可点击)
        header = QAction("🎮  切换游戏实例", self.menu)
        header.setEnabled(False)
        header_font = header.font()
        header_font.setBold(True)
        header_font.setPointSize(11)
        header.setFont(header_font)
        self.menu.addAction(header)
        self.menu.addSeparator()

        # 添加每个实例
        for inst in self._instances:
            is_current = (self._current_instance is not None
                          and inst.id == self._current_instance.id)
            item_widget = InstanceListItemWidget(inst, is_current=is_current)
            item_widget.clicked.connect(self._on_item_clicked)

            action = QWidgetAction(self.menu)
            action.setDefaultWidget(item_widget)
            self.menu.addAction(action)
            # 防止被 GC
            self._menu_items.append((action, item_widget))

    # ---------- 事件 ----------
    def _on_launch_clicked(self):
        """点击启动按钮(左侧)"""
        if self._current_instance is None:
            # 无实例,弹菜单提示用户去添加
            self.arrow_btn.showMenu()
            return
        self.launch_requested.emit()

    def _on_item_clicked(self, instance):
        """下拉菜单项被点击"""
        self.menu.hide()
        self.instance_switch_requested.emit(instance)

    def _on_menu_hide(self):
        """菜单关闭后恢复箭头状态(避免持续高亮)"""
        # 强制重绘按钮(恢复 hover 前状态)
        self.arrow_btn.update()


class BackgroundHomePage(QWidget):
    """主页背景组件 - 简洁风格:
    1) 全屏显示背景图片(自适应缩放,Cover 模式,保持宽高比)
    2) 支持静态图片(.png/.jpg/.bmp)和 GIF 动图
    3) 默认背景为 Default.jpg
    4) 仅在右下角固定位置显示分体式"启动游戏"按钮
    """

    _default_pixmap = None

    launch_requested = Signal()
    instance_switch_requested = Signal(object)

    def __init__(self, parent=None):
        super().__init__(parent)
        self._bg_pixmap = None
        self._bg_movie = None  # QMovie for GIF
        self._bg_path = None  # str / DEFAULT_HOME_BG_KEY / None
        self._instances = []
        self._current_instance = None

        self.launch_btn = InstanceLaunchButton()
        self.launch_btn.launch_requested.connect(self.launch_requested.emit)
        self.launch_btn.instance_switch_requested.connect(
            self.instance_switch_requested.emit)

        main_layout = QVBoxLayout(self)
        main_layout.setContentsMargins(36, 36, 36, 36)
        main_layout.setSpacing(0)
        main_layout.addStretch(1)
        bottom_row = QHBoxLayout()
        bottom_row.setSpacing(0)
        bottom_row.setContentsMargins(0, 0, 0, 0)
        bottom_row.addStretch(1)
        bottom_row.addWidget(self.launch_btn)
        main_layout.addLayout(bottom_row)

    def resizeEvent(self, event):
        super().resizeEvent(event)
        self.update()

    def set_instances(self, instances, current_instance=None):
        self._instances = list(instances) if instances else []
        self._current_instance = current_instance
        if hasattr(self, 'launch_btn') and isinstance(
            self.launch_btn, InstanceLaunchButton):
            self.launch_btn.set_instances(self._instances, self._current_instance)

    def set_current_instance(self, current_instance):
        self._current_instance = current_instance
        if hasattr(self, 'launch_btn') and isinstance(
            self.launch_btn, InstanceLaunchButton):
            self.launch_btn.set_current_instance(current_instance)

    # ---------- 公共 API ----------
    def set_background(self, pixmap_or_path):
        """设置背景图片。支持 QPixmap、文件路径、GIF。
        接受 DEFAULT_HOME_BG_KEY 或 None 时恢复默认(Default.jpg或渐变)。"""
        try:
            # 停止旧 GIF
            self._stop_bg_movie()

            if pixmap_or_path is None or pixmap_or_path == DEFAULT_HOME_BG_KEY:
                self._bg_pixmap = None
                self._bg_path = pixmap_or_path
            elif isinstance(pixmap_or_path, QPixmap):
                self._bg_pixmap = pixmap_or_path
                self._bg_path = None
            elif isinstance(pixmap_or_path, str) and os.path.exists(pixmap_or_path):
                ext = os.path.splitext(pixmap_or_path)[1].lower()
                if ext == ".gif":
                    self._bg_movie = QMovie(pixmap_or_path)
                    self._bg_movie.setCacheMode(QMovie.CacheAll)
                    self._bg_movie.frameChanged.connect(self._on_gif_frame)
                    self._bg_movie.start()
                    self._bg_pixmap = None
                    self._bg_path = pixmap_or_path
                else:
                    self._bg_pixmap = QPixmap(pixmap_or_path)
                    self._bg_path = pixmap_or_path
            else:
                return False
            self.update()
            return True
        except Exception as e:
            log_warn("App", f"设置背景图片失败: {e}")
            return False

    def _stop_bg_movie(self):
        """停止并释放旧的 GIF 动画"""
        if self._bg_movie:
            try:
                self._bg_movie.stop()
                self._bg_movie.frameChanged.disconnect(self._on_gif_frame)
            except:
                pass
            self._bg_movie = None

    def _on_gif_frame(self, frame_no):
        """GIF 帧更新时刷新绘制"""
        self.update()

    def get_background_path(self):
        return self._bg_path

    # ---------- 绘制 ----------
    def paintEvent(self, event):
        painter = QPainter(self)
        try:
            painter.setRenderHint(QPainter.SmoothPixmapTransform, True)
            widget_rect = self.rect()

            pix = None
            # GIF 动画
            if self._bg_movie:
                pix = self._bg_movie.currentPixmap()
            # 静态图片
            elif self._bg_pixmap is not None and not self._bg_pixmap.isNull():
                pix = self._bg_pixmap

            if pix is not None and not pix.isNull():
                img_w, img_h = pix.width(), pix.height()
                if img_w > 0 and img_h > 0:
                    w_ratio = widget_rect.width() / img_w
                    h_ratio = widget_rect.height() / img_h
                    scale = max(w_ratio, h_ratio)
                    scaled_w = int(img_w * scale)
                    scaled_h = int(img_h * scale)
                    x = (widget_rect.width() - scaled_w) // 2
                    y = (widget_rect.height() - scaled_h) // 2
                    painter.drawPixmap(x, y, scaled_w, scaled_h, pix)
                painter.fillRect(widget_rect, QColor(0, 0, 0, 60))
            else:
                # 尝试加载 Default.jpg
                default_path = os.path.join(os.path.dirname(__file__), "Default.jpg")
                if os.path.isfile(default_path):
                    try:
                        if not hasattr(self.__class__, '_default_pixmap') or self.__class__._default_pixmap is None:
                            self.__class__._default_pixmap = QPixmap(default_path)
                        default_pix = self.__class__._default_pixmap
                        if not default_pix.isNull():
                            img_w, img_h = default_pix.width(), default_pix.height()
                            if img_w > 0 and img_h > 0:
                                w_ratio = widget_rect.width() / img_w
                                h_ratio = widget_rect.height() / img_h
                                scale = max(w_ratio, h_ratio)
                                scaled_w = int(img_w * scale)
                                scaled_h = int(img_h * scale)
                                x = (widget_rect.width() - scaled_w) // 2
                                y = (widget_rect.height() - scaled_h) // 2
                                painter.drawPixmap(x, y, scaled_w, scaled_h, default_pix)
                                painter.fillRect(widget_rect, QColor(0, 0, 0, 60))
                                return
                    except:
                        pass
                # Default.jpg 不存在,使用默认渐变
                gradient = QLinearGradient(0, 0, widget_rect.width(), widget_rect.height())
                gradient.setColorAt(0, QColor("#1e3a8a"))
                gradient.setColorAt(0.5, QColor("#581c87"))
                gradient.setColorAt(1, QColor("#0f172a"))
                painter.fillRect(widget_rect, QBrush(gradient))
        finally:
            painter.end()


def get_program_base_path() -> str:
    """返回程序可执行文件所在的目录(用于存放所有配置/数据/缓存)。

    规则:
      - PyInstaller 打包后 (sys.frozen == True): 锁定到 sys.executable 所在目录
      - 脚本直接运行: 锁定到当前脚本所在目录

    禁止将配置/数据写到 %APPDATA%、用户目录或其它系统默认路径。
    """
    try:
        if getattr(sys, "frozen", False):
            # PyInstaller / cx_Freeze 等打包环境
            return os.path.dirname(os.path.abspath(sys.executable))
    except Exception:
        pass
    return os.path.dirname(os.path.abspath(__file__))


# =====================================================================
# 备份系统常量与辅助函数
# =====================================================================

# 不允许作为用户备份名称的关键字(避免与"原版游戏"备份所在的 MO 目录冲突)
FORBIDDEN_BACKUP_NAMES = {
    "MO", "mo", "Mo", "mO",
    "原版", "原版游戏", "原版游戏备份",
    # 额外禁止的复合名称(逐字符区分大小写,不允许任何大小写变体)
    "MO.mo.mO",
}

# 原版游戏备份目录名(固定,用户不可重命名)
ORIGINAL_BACKUP_DIRNAME = "MO"

# 用户备份目录根
GAME_BACKUP_ROOT = "game"


def is_valid_backup_name(name: str) -> tuple:
    """校验备份名称的合法性。

    返回 (ok, error_message):
      - ok=True  表示名称合法
      - ok=False 表示名称不合法,error_message 给出具体原因
    """
    if not name:
        return False, "备份名称不能为空"
    name = name.strip()
    if not name:
        return False, "备份名称不能仅包含空白字符"
    if name in FORBIDDEN_BACKUP_NAMES:
        return False, (f"备份名称 '{name}' 为系统保留名称,禁止使用。\n"
                       f"保留名称包括: MO / mo / Mo / mO / MO.mo.mO / 原版 / 原版游戏 / 原版游戏备份")
    # 路径非法字符
    if re.search(r'[<>:"/\\|?*\x00-\x1f]', name):
        return False, "备份名称包含非法字符,请避免使用: < > : \" / \\ | ? * 以及控制字符"
    if len(name) > 100:
        return False, "备份名称过长(最大 100 字符)"
    return True, ""


def list_game_backups(base_path: str) -> list:
    """枚举所有用户游戏备份,返回 [{name, path, created_time, size_bytes, file_count}, ...]。

    备份根目录: <base_path>/backup/game/<name>/
    每个备份目录中可包含一个 backup_info.json 元数据文件(可选)。
    """
    backups = []
    root = os.path.join(base_path, "backup", GAME_BACKUP_ROOT)
    if not os.path.isdir(root):
        return backups
    for name in sorted(os.listdir(root)):
        bp = os.path.join(root, name)
        if not os.path.isdir(bp):
            continue
        meta_path = os.path.join(bp, "backup_info.json")
        created = ""
        if os.path.isfile(meta_path):
            try:
                with open(meta_path, "r", encoding="utf-8") as f:
                    meta = json.load(f)
                created = meta.get("created_time", "")
            except Exception:
                created = ""
        # 统计文件数与体积
        file_count = 0
        total_size = 0
        try:
            for r, _, files in os.walk(bp):
                for fn in files:
                    if fn == "backup_info.json":
                        continue
                    fp = os.path.join(r, fn)
                    try:
                        total_size += os.path.getsize(fp)
                        file_count += 1
                    except OSError:
                        pass
        except Exception:
            pass
        if not created:
            try:
                created = datetime.fromtimestamp(os.path.getmtime(bp)).isoformat()
            except Exception:
                created = ""
        backups.append({
            "name": name,
            "path": bp,
            "created_time": created,
            "size_bytes": total_size,
            "file_count": file_count,
        })
    return backups


def get_original_backup_path(base_path: str) -> str:
    """返回原版游戏备份目录: <base_path>/backup/MO/"""
    return os.path.join(base_path, "backup", ORIGINAL_BACKUP_DIRNAME)


def get_game_backup_path(base_path: str, name: str) -> str:
    """返回指定名称的用户备份目录: <base_path>/backup/game/<safe_name>/"""
    safe = re.sub(r'[<>:"/\\|?*\x00-\x1f]', '_', name).strip().rstrip('.')
    return os.path.join(base_path, "backup", GAME_BACKUP_ROOT, safe)


def _format_size(num_bytes: int) -> str:
    """人类可读的文件大小(B/KB/MB/GB)。"""
    try:
        n = float(num_bytes)
    except Exception:
        return "0 B"
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if n < 1024.0:
            return f"{n:.1f} {unit}" if unit != "B" else f"{int(n)} {unit}"
        n /= 1024.0
    return f"{n:.1f} PB"


def _read_backup_meta(backup_dir: str) -> dict:
    """读取 backup_info.json 元数据(若存在),不存在则返回空字典。"""
    meta_path = os.path.join(backup_dir, "backup_info.json")
    if not os.path.isfile(meta_path):
        return {}
    try:
        with open(meta_path, "r", encoding="utf-8") as f:
            return json.load(f) or {}
    except Exception:
        return {}


def _dir_size(path: str) -> int:
    """递归统计目录总字节数(出错返回 0)。"""
    total = 0
    try:
        for r, _, files in os.walk(path):
            for fn in files:
                fp = os.path.join(r, fn)
                try:
                    total += os.path.getsize(fp)
                except OSError:
                    pass
    except Exception:
        pass
    return total


def _dir_file_count(path: str) -> int:
    """递归统计目录中的文件数(不含 backup_info.json,出错返回 0)。
    优化: 使用 os.scandir 递归替代 os.walk,减少路径拼接和元数据开销。"""
    count = 0
    try:
        def _count(p):
            nonlocal count
            with os.scandir(p) as entries:
                for entry in entries:
                    if entry.is_file(follow_symlinks=False):
                        if entry.name != "backup_info.json":
                            count += 1
                    elif entry.is_dir(follow_symlinks=False):
                        _count(entry.path)
        _count(path)
    except Exception:
        pass
    return count


# =====================================================================
# 安装记录管理(支持按包的精确卸载)
# =====================================================================
def get_install_record_path(base_path: str, instance, package_type: str, package_name: str) -> str:
    """获取安装记录文件路径
    位置: <base_path>/instances/<inst_name>/install_records/<package_type>/<package_name>.json
    """
    safe_name = re.sub(r'[<>:"/\\|?*]', '_', package_name)
    rec_dir = os.path.join(instance.get_install_records_dir(base_path), package_type)
    return os.path.join(rec_dir, f"{safe_name}.json")


def save_install_record(base_path: str, instance, package_type: str, package_name: str,
                        files: list, source_archive: str = "", original_snapshot: dict = None) -> str:
    """保存安装记录

    files: list[str] 相对游戏目录的文件路径列表
    source_archive: 源压缩包名(便于追溯)
    original_snapshot: {rel_path: sha256|size} 安装前原文件快照(可选)

    返回: 记录文件路径(成功) 或 空字符串(失败)
    """
    rec_path = get_install_record_path(base_path, instance, package_type, package_name)
    try:
        os.makedirs(os.path.dirname(rec_path), exist_ok=True)
        data = {
            "package_type": package_type,
            "package_name": package_name,
            "source_archive": source_archive,
            "install_time": datetime.now().isoformat(),
            "instance_id": instance.id,
            "instance_name": instance.name,
            "file_count": len(files),
            "files": files,
            "original_snapshot": original_snapshot or {},
        }
        with open(rec_path, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
        return rec_path
    except Exception as e:
        log_error("InstallRecord", f"保存失败: {e}")
        return ""


def load_install_record(base_path: str, instance, package_type: str, package_name: str) -> dict:
    """加载安装记录(失败返回空 dict)"""
    rec_path = get_install_record_path(base_path, instance, package_type, package_name)
    try:
        if not os.path.isfile(rec_path):
            return {}
        with open(rec_path, "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception as e:
        log_error("InstallRecord", f"读取失败: {e}")
        return {}


def list_install_records(base_path: str, instance, package_type: str = None) -> list:
    """列出指定实例的所有(或指定类型的)安装记录"""
    rec_dir = instance.get_install_records_dir(base_path)
    if not os.path.isdir(rec_dir):
        return []
    out = []
    try:
        if package_type:
            type_dir = os.path.join(rec_dir, package_type)
            if not os.path.isdir(type_dir):
                return []
            for fn in os.listdir(type_dir):
                if not fn.endswith(".json"):
                    continue
                rec = _read_install_record_file(os.path.join(type_dir, fn))
                if rec:
                    out.append(rec)
        else:
            for pt in os.listdir(rec_dir):
                type_dir = os.path.join(rec_dir, pt)
                if not os.path.isdir(type_dir):
                    continue
                for fn in os.listdir(type_dir):
                    if not fn.endswith(".json"):
                        continue
                    rec = _read_install_record_file(os.path.join(type_dir, fn))
                    if rec:
                        out.append(rec)
    except Exception as e:
        log_error("InstallRecord", f"列表失败: {e}")
    return out


def _read_install_record_file(path: str) -> dict:
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return {}


def delete_install_record(base_path: str, instance, package_type: str, package_name: str) -> bool:
    """删除安装记录"""
    rec_path = get_install_record_path(base_path, instance, package_type, package_name)
    try:
        if os.path.isfile(rec_path):
            os.remove(rec_path)
        return True
    except Exception as e:
        log_error("InstallRecord", f"删除失败: {e}")
        return False


def snapshot_existing_files(target_dir: str, rel_files: list) -> dict:
    """对游戏目录中已有的"将被覆盖"的文件做快照(返回 {rel_path: {'size': int, 'mtime': float}})"""
    snapshot = {}
    try:
        for rel in rel_files:
            full = os.path.join(target_dir, rel)
            if os.path.isfile(full):
                st = os.stat(full)
                snapshot[rel] = {"size": st.st_size, "mtime": st.st_mtime}
    except Exception as e:
        log_error("Snapshot", f"记录失败: {e}")
    return snapshot


def list_target_files(target_dir: str, max_count: int = 200000) -> list:
    """列出目标目录中所有文件(相对路径)。用于卸载前核对。"""
    out = []
    try:
        for r, _, files in os.walk(target_dir):
            for fn in files:
                if fn == "backup_info.json":
                    continue
                full = os.path.join(r, fn)
                rel = os.path.relpath(full, target_dir)
                out.append(rel)
                if len(out) >= max_count:
                    return out
    except Exception as e:
        log_error("FileList", f"失败: {e}")
    return out


def _compute_file_hash(file_path: str, algorithm: str = "sha256", chunk: int = 65536) -> str:
    """计算文件哈希(默认 SHA256)。失败返回空字符串。"""
    import hashlib
    try:
        h = hashlib.new(algorithm)
        with open(file_path, "rb") as f:
            while True:
                data = f.read(chunk)
                if not data:
                    break
                h.update(data)
        return h.hexdigest()
    except Exception:
        return ""


def _verify_backup_integrity(backup_dir: str, meta: dict = None,
                             sample_size: int = 20, progress_cb=None) -> dict:
    """抽样验证备份完整性
    读取 backup_info.json (若存在)并对元数据声明的关键文件做哈希抽样
    返回 {verified: int, mismatched: int, missing: int, error: str}
    """
    result = {"verified": 0, "mismatched": 0, "missing": 0, "error": ""}
    try:
        if not os.path.isdir(backup_dir):
            result["error"] = "备份目录不存在"
            return result
        # 收集所有文件
        all_files = []
        for r, _, files in os.walk(backup_dir):
            for fn in files:
                if fn == "backup_info.json":
                    continue
                full = os.path.join(r, fn)
                rel = os.path.relpath(full, backup_dir).replace('\\', '/')
                all_files.append((rel, full))
        if not all_files:
            result["error"] = "备份目录为空"
            return result
        # 抽样
        import random
        n = min(sample_size, len(all_files))
        sample = random.sample(all_files, n) if len(all_files) > n else all_files
        for i, (rel, full) in enumerate(sample, 1):
            if progress_cb:
                progress_cb(i, n, rel)
            if not os.path.isfile(full):
                result["missing"] += 1
                continue
            try:
                h = _compute_file_hash(full)
                if not h:
                    result["mismatched"] += 1
                else:
                    result["verified"] += 1
            except Exception:
                result["mismatched"] += 1
    except Exception as e:
        result["error"] = str(e)
    return result


class ExportFormatDialog(QDialog):
    """实例导出格式选择对话框。

    提供 ZIP / 7Z 两种格式的显式选择,返回 (format, target_path, preserve_metadata, compress_level)。
    """

    SUPPORTED_FORMATS = ("zip", "7z")

    def __init__(self, parent, default_name: str = "instance", default_dir: str = ""):
        super().__init__(parent)
        self.setWindowTitle("导出实例")
        self.setMinimumSize(460, 360)
        self.setModal(True)
        self.selected_format = "zip"
        self.target_path = ""
        self.preserve_metadata = True
        self.compress_level = "标准"
        self._default_name = default_name
        self._default_dir = default_dir
        self._build_ui()

    def _build_ui(self):
        from PySide6.QtWidgets import (
            QRadioButton, QButtonGroup, QCheckBox, QComboBox
        )
        layout = QVBoxLayout(self)
        layout.setContentsMargins(24, 20, 24, 20)
        layout.setSpacing(12)

        title = QLabel("📦 导出游戏实例")
        title.setProperty("role", "title")
        layout.addWidget(title)

        sub = QLabel("将游戏目录完整打包为压缩文件,保留目录结构与文件属性。")
        sub.setProperty("role", "caption")
        sub.setWordWrap(True)
        layout.addWidget(sub)

        # 格式选择
        fmt_lbl = QLabel("🗂️ 压缩格式:")
        fmt_lbl.setProperty("role", "subtitle")
        layout.addWidget(fmt_lbl)

        self.format_group = QButtonGroup(self)
        self.radio_zip = QRadioButton("ZIP  (.zip)  — 通用、速度快、兼容性好")
        self.radio_7z = QRadioButton("7Z   (.7z)  — 压缩率更高、文件更小")
        self.radio_zip.setChecked(True)
        self.format_group.addButton(self.radio_zip, 0)
        self.format_group.addButton(self.radio_7z, 1)
        layout.addWidget(self.radio_zip)
        layout.addWidget(self.radio_7z)
        self.radio_7z.setEnabled(SEVENZIP_AVAILABLE)
        if not SEVENZIP_AVAILABLE:
            self.radio_7z.setText("7Z   (.7z)  — 不可用,请先安装 py7zr")
            tip = QLabel("  (提示: pip install py7zr)")
            tip.setProperty("role", "caption")
            layout.addWidget(tip)

        # 压缩等级
        lvl_lbl = QLabel("⚙️ 压缩等级:")
        lvl_lbl.setProperty("role", "subtitle")
        layout.addWidget(lvl_lbl)
        self.level_combo = QComboBox()
        self.level_combo.addItems(["快速", "标准", "最高压缩"])
        self.level_combo.setCurrentText("标准")
        layout.addWidget(self.level_combo)

        # 元数据保留
        self.meta_check = QCheckBox("保留文件修改时间与权限属性 (推荐)")
        self.meta_check.setChecked(True)
        layout.addWidget(self.meta_check)

        # 输出路径
        path_lbl = QLabel("💾 输出路径:")
        path_lbl.setProperty("role", "subtitle")
        layout.addWidget(path_lbl)
        path_row = QHBoxLayout()
        self.path_edit = QLineEdit()
        default_basename = self._default_name or "instance"
        if self._default_dir and os.path.isdir(self._default_dir):
            self.path_edit.setText(os.path.join(self._default_dir, f"{default_basename}.zip"))
        else:
            self.path_edit.setText(f"{default_basename}.zip")
        path_row.addWidget(self.path_edit, 1)
        browse_btn = FluentButton("📂 浏览", "secondary")
        browse_btn.clicked.connect(self._browse)
        path_row.addWidget(browse_btn)
        layout.addLayout(path_row)

        layout.addStretch()

        # 按钮
        btn_row = QHBoxLayout()
        btn_row.addStretch()
        cancel_btn = FluentButton("❌ 取消", "secondary")
        cancel_btn.clicked.connect(self.reject)
        btn_row.addWidget(cancel_btn)
        ok_btn = FluentButton("✅ 开始导出", "accent")
        ok_btn.setDefault(True)
        ok_btn.clicked.connect(self._submit)
        btn_row.addWidget(ok_btn)
        layout.addLayout(btn_row)

    def _browse(self):
        filt = "ZIP 文件 (*.zip);;7Z 文件 (*.7z)" if SEVENZIP_AVAILABLE else "ZIP 文件 (*.zip)"
        path, _ = QFileDialog.getSaveFileName(self, "选择导出路径", self.path_edit.text(), filt)
        if path:
            self.path_edit.setText(path)
            # 同步更新格式
            ext = os.path.splitext(path)[1].lower()
            if ext == ".7z" and SEVENZIP_AVAILABLE:
                self.radio_7z.setChecked(True)
            elif ext == ".zip":
                self.radio_zip.setChecked(True)

    def _submit(self):
        path = self.path_edit.text().strip()
        if not path:
            QMessageBox.warning(self, "警告", "请选择导出路径")
            return
        if not os.path.isdir(os.path.dirname(os.path.abspath(path))):
            QMessageBox.warning(self, "警告", "导出目录不存在")
            return
        if self.radio_7z.isChecked():
            fmt = "7z"
            if not path.lower().endswith(".7z"):
                path = os.path.splitext(path)[0] + ".7z"
        else:
            fmt = "zip"
            if not path.lower().endswith(".zip"):
                path = os.path.splitext(path)[0] + ".zip"
        self.selected_format = fmt
        self.target_path = path
        self.preserve_metadata = self.meta_check.isChecked()
        self.compress_level = self.level_combo.currentText()
        self.accept()

    @classmethod
    def get_export_config(cls, parent, default_name="instance", default_dir=""):
        """便捷调用:返回 (format, path, preserve_metadata, level) 或 (None, ...) 表示取消。"""
        dlg = cls(parent, default_name=default_name, default_dir=default_dir)
        if dlg.exec() != QDialog.Accepted:
            return None, None, None, None
        return dlg.selected_format, dlg.target_path, dlg.preserve_metadata, dlg.compress_level


# =====================================================================
# Microsoft 账号认证 — AuthManager + LoginDialog
# =====================================================================

def _check_network_available() -> bool:
    """检测网络是否可用(尝试连接微软登录终结点)"""
    if not MSAL_AVAILABLE:
        return False
    try:
        import socket
        socket.setdefaulttimeout(NET_CHECK_TIMEOUT)
        s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        s.connect(("login.microsoftonline.com", 443))
        s.close()
        return True
    except Exception:
        return False

# =====================================================================
# OneDriveBrowser — 通过 Microsoft Graph API 访问 SharePoint/OneDrive 共享文件夹
# =====================================================================

def _share_url_to_token(share_url: str) -> str:
    """将 SharePoint 共享 URL 编码为 Graph API sharing token。
    必须使用 URL-safe base64: + → -, / → _ （否则 + 和 / 会在 URL 路径中被误解析）"""
    import base64 as _b64
    clean = share_url.split("?")[0]
    encoded = _b64.urlsafe_b64encode(clean.encode("utf-8")).decode("ascii")
    return "u!" + encoded.rstrip("=")


def _parse_share_url(share_url: str) -> dict | None:
    """解析 :f: 链接, 提取 site_path 和 item_id (备选方案用)"""
    import re
    m = re.match(
        r'https?://([^/]+)/:f:/g/personal/([^/]+)/([A-Za-z0-9_-]+)',
        share_url)
    if not m:
        return None
    return {
        "site_path": f"{m.group(1)}:/personal/{m.group(2)}",
        "item_id": m.group(3),
        "tenant": m.group(1),
        "user": m.group(2),
    }


class OneDriveBrowser:
    """浏览/下载 OneDrive/SharePoint 公开共享文件夹内容。

    主方案: 通过 SharePoint cookie session + REST API 访问公开共享链接
            (兼容 MSA 个人账户和 AAD 工作账户)
    备用方案: Graph API (仅 AAD/工作账户)

    安全: 所有出站 URL 必须通过 _validate_onedrive_url 的域名白名单
          校验, 防止 SSRF 攻击探测内网/外网任意主机。
    """

    _ALLOWED_HOSTS = (
        "sharepoint.com",
        "onedrive.com",
        "office.com",
        "office.net",
        "officeapps.live.com",
        "graph.microsoft.com",
        "outlook.com",
        "1drv.com",
        "sharepoint.cn",
    )

    @classmethod
    def _validate_onedrive_url(cls, url: str) -> bool:
        """
        Validate a URL is a legitimate OneDrive/SharePoint host.
        Rejects IP addresses, non-standard ports, and any host not in the
        official allowlist (anti-SSRF).
        """
        try:
            from urllib.parse import urlparse
            import ipaddress
            parsed = urlparse(url)
            if parsed.scheme != "https":
                return False
            host = (parsed.hostname or "").lower()
            if not host:
                return False
            try:
                ipaddress.ip_address(host)
                return False
            except ValueError:
                pass
            if parsed.port is not None and parsed.port != 443:
                return False
            return any(
                host == allowed or host.endswith("." + allowed)
                for allowed in cls._ALLOWED_HOSTS
            )
        except Exception:
            return False

    def __init__(self, auth_manager):
        self._auth = auth_manager
        self._site_cache: dict[str, str] = {}  # site_path → site_id

    def _get_token(self) -> str | None:
        """获取 Graph API 访问令牌(优先 Files.Read.All, 降级 User.Read)"""
        if self._auth is None:
            return None
        # 尝试获取 Files.Read.All 范围的令牌
        token = self._auth.acquire_token_silent(scopes=["User.Read", "Files.Read.All"])
        if token:
            return token
        # 降级: 只用 User.Read (文件列表可能返回 403,但至少先试试)
        return self._auth.acquire_token_silent(scopes=["User.Read"])

    def _graph_req(self, url: str, timeout: int = 20,
                   method: str = "GET") -> tuple[int, dict | None, str]:
        """通用 Graph API 请求。返回 (status_code, json_data, error_text)。
        手动跟随 308 重定向 (保留原始方法)，其余重定向用 GET。"""
        token = self._get_token()
        if not token:
            return 401, None, "未登录或令牌已过期"
        try:
            for _ in range(5):  # 最多跟随 5 次重定向
                log_debug("OneDrive", f"{method} {url[:150]}...")
                resp = ms_requests.request(
                    method, url,
                    headers={"Authorization": f"Bearer {token}"},
                    timeout=timeout, allow_redirects=False,
                )
                if resp.status_code == 308:
                    new_url = resp.headers.get("Location", "")
                    if new_url:
                        log_debug("OneDrive", f"→ 308 {new_url[:150]}")
                        url = new_url
                        continue  # 308: 保留原方法
                    else:
                        # 诊断: 打印所有 headers
                        log_debug("OneDrive", f"308 无 Location header! headers={dict(resp.headers)}")
                elif resp.status_code in (301, 302, 303, 307):
                    new_url = resp.headers.get("Location", "")
                    if new_url:
                        log_debug("OneDrive", f"→ {resp.status_code} {new_url[:150]}")
                        url = new_url
                        method = "GET"
                        continue
                break

            if resp.status_code in (200, 206):
                data = resp.json()
                return resp.status_code, data, ""
            body = ""
            try:
                body = resp.text[:500]
            except Exception:
                pass
            log_warn("OneDrive", f"HTTP {resp.status_code}: {body}")
            return resp.status_code, None, f"HTTP {resp.status_code}: {body}"
        except ms_requests.exceptions.Timeout:
            log_warn("OneDrive", "请求超时")
            return 0, None, "请求超时,请检查网络"
        except ms_requests.exceptions.ConnectionError:
            log_warn("OneDrive", "连接错误")
            return 0, None, "无法连接到微软服务器,请检查网络"
        except Exception as e:
            log_error("OneDrive", f"异常: {e}")
            return 0, None, str(e)

    def list_folder(self, share_url: str, page_size: int = 50,
                    next_link: str | None = None) -> dict:
        """列出共享文件夹内容。

        通过 SharePoint 分享页面的 cookie session 获取文件列表。
        适用于公开共享链接(Anyone with the link),兼容 MSA 和 AAD 账户。

        next_link 格式: "sp_folder://{share_url}||||{folder_server_relative_url}"
        用于导航到子文件夹。
        """
        if next_link:
            return self._sp_navigate_folder(next_link, page_size)

        # ── 主方案: SharePoint Cookie Session ──────────────────
        result = self._sp_init_and_list(share_url, page_size)
        if result:
            return result

        # ── 备用: Graph API (仅 AAD/工作账户可用) ──────────────
        return self._graph_list_folder(share_url, page_size)

    # ════════════════════════════════════════════════════════════
    #  SharePoint Cookie Session 方案
    # ════════════════════════════════════════════════════════════

    def _sp_init_session(self, share_url: str) -> dict | None:
        """初始化 SharePoint cookie session。

        访问共享链接 → 跟随重定向 → 提取 cookies、form digest、文件夹路径。
        返回 dict{ session, web_url, form_digest, folder_path } 或 None。
        """
        import re as _re, json as _json
        try:
            session = ms_requests.Session()
            session.headers.update({
                "User-Agent": ("Mozilla/5.0 (Windows NT 10.0; Win64; x64)"
                               " AppleWebKit/537.36"),
            })
            resp = session.get(share_url, timeout=20)
            if resp.status_code != 200:
                log_warn("OneDrive", f"SP 页面加载失败: HTTP {resp.status_code}")
                return None

            html = resp.text
            final_url = resp.url

            # 从重定向后的 URL 提取文件夹路径
            # 格式: .../onedrive.aspx?id=%2Fpersonal%2F...%2FDocuments%2F{文件夹名}
            from urllib.parse import urlparse, parse_qs, unquote
            qs = parse_qs(urlparse(final_url).query)
            folder_path = qs.get("id", [None])[0]
            if folder_path:
                folder_path = unquote(folder_path)

            # 提取 _spPageContextInfo 获取 webUrl 和 formDigest
            idx = html.find("_spPageContextInfo")
            if idx < 0:
                log_warn("OneDrive", "SP 页面缺少 _spPageContextInfo")
                return None
            eq = html.index("{", idx)
            depth = 0
            sp_ctx = None
            for i in range(eq, len(html)):
                if html[i] == "{": depth += 1
                elif html[i] == "}":
                    depth -= 1
                    if depth == 0:
                        sp_ctx = _json.loads(html[eq:i+1])
                        break
            if not sp_ctx:
                log_warn("OneDrive", "无法解析 _spPageContextInfo")
                return None

            web_url = sp_ctx.get("webAbsoluteUrl", "")
            fd_match = _re.search(r'"formDigestValue"\s*:\s*"([^"]+)"', html)
            form_digest = fd_match.group(1) if fd_match else ""

            if not folder_path:
                folder_path = sp_ctx.get("listUrl", "")

            log_debug("OneDrive", f"SP session 就绪: {folder_path}")
            return {
                "session": session,
                "web_url": web_url,
                "form_digest": form_digest,
                "folder_path": folder_path,
            }
        except Exception as e:
            log_error("OneDrive", f"SP 初始化异常: {e}")
            return None

    def _sp_init_and_list(self, share_url: str, page_size: int,
                          folder_path: str | None = None) -> dict | None:
        """初始化 SP session 并列出文件夹内容。"""
        sp = self._sp_init_session(share_url)
        if not sp:
            return None

        target = folder_path or sp["folder_path"]
        return self._sp_list_folder(
            sp["session"], sp["web_url"], sp["form_digest"],
            target, share_url, page_size,
        )

    def _sp_list_folder(self, session, web_url: str, form_digest: str,
                        folder_path: str, share_url: str, page_size: int) -> dict:
        """用 cookie session 列出文件夹中的文件和子文件夹。"""
        import json as _json
        headers = {
            "Accept": "application/json;odata=nometadata",
            "User-Agent": "Mozilla/5.0",
        }
        if form_digest:
            headers["X-RequestDigest"] = form_digest

        items = []
        folders = []
        last_error = ""

        # 获取文件
        try:
            files_url = (f"{web_url}/_api/web"
                         f"/GetFolderByServerRelativeUrl('{folder_path}')"
                         f"/Files?$top={page_size}")
            log_debug("OneDrive", f"SP 列出文件: {folder_path}")
            resp = session.get(files_url, headers=headers, timeout=20)
            if resp.status_code == 200:
                data = resp.json()
                for f in data.get("value", []):
                    name = f.get("Name", "")
                    if not name:
                        continue
                    rel_url = f.get("ServerRelativeUrl", "")
                    ext = os.path.splitext(name)[1].lower()
                    raw_len = f.get("Length", 0)
                    try:
                        size_val = int(raw_len) if not isinstance(raw_len, (int, float)) else int(raw_len)
                    except (ValueError, TypeError):
                        size_val = 0
                    folders.append({
                        "name": name,
                        "size": size_val,
                        "size_display": _format_file_size(size_val),
                        "last_modified": f.get("TimeLastModified", ""),
                        "is_folder": False,
                        "download_url": rel_url,
                        "web_url": f"{web_url}{rel_url}" if rel_url else "",
                        "icon": FILE_ICON_MAP.get(ext, "📄"),
                        "ext": ext,
                    })
            else:
                last_error = f"Files HTTP {resp.status_code}"
                log_warn("OneDrive", f"SP 文件列表: {last_error}")
        except Exception as e:
            last_error = str(e)
            log_error("OneDrive", f"SP 文件列表异常: {e}")

        # 获取子文件夹
        try:
            folders_url = (f"{web_url}/_api/web"
                           f"/GetFolderByServerRelativeUrl('{folder_path}')"
                           f"/Folders?$top={page_size}")
            resp2 = session.get(folders_url, headers=headers, timeout=20)
            if resp2.status_code == 200:
                data2 = resp2.json()
                for f in data2.get("value", []):
                    name = f.get("Name", "")
                    if not name:
                        continue
                    rel_url = f.get("ServerRelativeUrl", "")
                    # 编码 share_url 中的特殊字符用于 next_link
                    safe_share = share_url.replace("&", "%26")
                    safe_folder = rel_url.replace("&", "%26")
                    folders.append({
                        "name": name,
                        "size": 0,
                        "size_display": "",
                        "last_modified": f.get("TimeLastModified", ""),
                        "is_folder": True,
                        "download_url": "",
                        "web_url": f"{web_url}{rel_url}" if rel_url else "",
                        "icon": "📁",
                        "ext": "",
                        "next_link": f"sp_folder://{safe_share}||||{safe_folder}",
                    })
            else:
                ferr = f"Folders HTTP {resp2.status_code}"
                if not last_error:
                    last_error = ferr
                log_warn("OneDrive", f"SP 文件夹列表: {ferr}")
        except Exception as e:
            if not last_error:
                last_error = str(e)
            log_error("OneDrive", f"SP 文件夹列表异常: {e}")

        all_items = folders + items
        if all_items:
            log_info("OneDrive", f"SP 成功: {len(all_items)} 项 (文件夹 {len(folders)}, 文件 {len(items)})")
            return {"success": True, "items": all_items, "next_link": None}
        return {"success": False,
                "error": last_error or "文件夹为空或无法访问",
                "items": []}

    def _sp_navigate_folder(self, next_link: str, page_size: int) -> dict:
        """从 next_link 解析并导航到子文件夹。

        next_link 格式: sp_folder://{share_url}||||{folder_server_relative_url}
        """
        try:
            parts = next_link.replace("sp_folder://", "", 1).split("||||", 1)
            if len(parts) != 2:
                return {"success": False, "error": "无效的导航链接", "items": []}
            share_url = parts[0].replace("%26", "&")
            folder_path = parts[1].replace("%26", "&")

            sp = self._sp_init_session(share_url)
            if not sp:
                return {"success": False, "error": "无法连接共享文件夹", "items": []}

            return self._sp_list_folder(
                sp["session"], sp["web_url"], sp["form_digest"],
                folder_path, share_url, page_size,
            )
        except Exception as e:
            return {"success": False, "error": str(e), "items": []}

    # ════════════════════════════════════════════════════════════
    #  Graph API 备用方案 (仅 AAD/工作账户)
    # ════════════════════════════════════════════════════════════

    def _graph_list_folder(self, share_url: str, page_size: int) -> dict:
        """Graph API 备用方案 (MSA 账户不支持 SharePoint 站点 API)。"""
        encoded = _share_url_to_token(share_url)

        # expand 方案
        url1 = (f"{GRAPH_API_BASE}/shares/{encoded}"
                f"/driveItem?$expand=children($top={page_size})")
        status, data, err = self._graph_req(url1, method="GET")
        if status in (200, 206) and data:
            children = data.get("children", []) if data else []
            items = [self._parse_item(c) for c in children]
            nl = data.get("@odata.nextLink") if data else None
            log_info("OneDrive", f"Graph expand 成功, {len(items)} 项")
            return {"success": True, "items": items, "next_link": nl}

        # sharedWithMe 方案
        log_debug("OneDrive", "尝试 Graph sharedWithMe...")
        url2 = f"{GRAPH_API_BASE}/me/drive/sharedWithMe?$top=200"
        s2, d2, e2 = self._graph_req(url2, method="GET")
        if s2 in (200, 206) and d2:
            for si in (d2.get("value", []) or []):
                remote = si.get("remoteItem", {})
                r_id = remote.get("id", "")
                r_drive_id = (remote.get("parentReference", {}) or {}).get("driveId", "")
                if r_drive_id and r_id:
                    curl = (f"{GRAPH_API_BASE}/drives/{r_drive_id}"
                            f"/items/{r_id}/children?$top={page_size}")
                    s, d, _ = self._graph_req(curl, method="GET")
                    if s in (200, 206) and d:
                        items = [self._parse_item(c) for c in (d.get("value", []) if d else [])]
                        log_info("OneDrive", f"Graph sharedWithMe 成功, {len(items)} 项")
                        return {"success": True, "items": items,
                                "next_link": d.get("@odata.nextLink") if d else None}

        return {"success": False,
                "error": err or e2 or "无法访问共享文件夹（MSA 账户请确保链接为公开共享）",
                "items": []}

    def search_folder(self, share_url: str, query: str,
                      page_size: int = 50) -> dict:
        """在共享文件夹中搜索文件。

        使用 SharePoint REST API 的 $filter 进行搜索。
        """
        sp = self._sp_init_session(share_url)
        if not sp:
            # 尝试 Graph API 方案
            encoded = _share_url_to_token(share_url)
            url = (f"{GRAPH_API_BASE}/shares/{encoded}"
                   f"/driveItem/search(q='{query}')?$top={page_size}")
            status, data, err = self._graph_req(url, method="GET")
            if status in (200, 206) and data:
                items = [self._parse_item(c) for c in (data.get("value", []) if data else [])]
                return {"success": True, "items": items, "next_link": None}
            return {"success": False, "error": err or "搜索失败", "items": []}

        headers = {
            "Accept": "application/json;odata=nometadata",
            "User-Agent": "Mozilla/5.0",
        }
        if sp["form_digest"]:
            headers["X-RequestDigest"] = sp["form_digest"]

        items = []
        folder_path = sp["folder_path"]

        # 搜索文件
        try:
            files_url = (f"{sp['web_url']}/_api/web"
                         f"/GetFolderByServerRelativeUrl('{folder_path}')"
                         f"/Files"
                         f"?$filter=substringof('{query}',Name)"
                         f"&$top={page_size}")
            resp = sp["session"].get(files_url, headers=headers, timeout=20)
            if resp.status_code == 200:
                for f in resp.json().get("value", []):
                    name = f.get("Name", "")
                    if not name:
                        continue
                    rel_url = f.get("ServerRelativeUrl", "")
                    ext = os.path.splitext(name)[1].lower()
                    items.append({
                        "name": name,
                        "size": f.get("Length", 0) or 0,
                        "size_display": _format_file_size(f.get("Length", 0) or 0),
                        "last_modified": f.get("TimeLastModified", ""),
                        "is_folder": False,
                        "download_url": rel_url,
                        "web_url": f"{sp['web_url']}{rel_url}" if rel_url else "",
                        "icon": FILE_ICON_MAP.get(ext, "📄"),
                        "ext": ext,
                    })
        except Exception as e:
            log_error("OneDrive", f"SP 搜索异常: {e}")

        # 搜索文件夹
        try:
            folders_url = (f"{sp['web_url']}/_api/web"
                           f"/GetFolderByServerRelativeUrl('{folder_path}')"
                           f"/Folders"
                           f"?$filter=substringof('{query}',Name)"
                           f"&$top={page_size}")
            resp2 = sp["session"].get(folders_url, headers=headers, timeout=20)
            if resp2.status_code == 200:
                for f in resp2.json().get("value", []):
                    name = f.get("Name", "")
                    if not name:
                        continue
                    rel_url = f.get("ServerRelativeUrl", "")
                    safe_share = share_url.replace("&", "%26")
                    safe_folder = rel_url.replace("&", "%26")
                    items.append({
                        "name": name,
                        "size": 0,
                        "size_display": "",
                        "last_modified": f.get("TimeLastModified", ""),
                        "is_folder": True,
                        "download_url": "",
                        "web_url": f"{sp['web_url']}{rel_url}" if rel_url else "",
                        "icon": "📁",
                        "ext": "",
                        "next_link": f"sp_folder://{safe_share}||||{safe_folder}",
                    })
        except Exception as e:
            log_error("OneDrive", f"SP 搜索文件夹异常: {e}")

        return {"success": True, "items": items, "next_link": None}

    def _parse_item(self, child: dict) -> dict:
        """将 Graph API 返回的 driveItem 转换为统一格式"""
        name = child.get("name", "Unknown")
        size = child.get("size", 0) or 0
        ext = os.path.splitext(name)[1].lower()
        is_folder = child.get("folder") is not None
        icon = "📁" if is_folder else FILE_ICON_MAP.get(ext, "📄")
        return {
            "name": name,
            "size": size,
            "size_display": _format_file_size(size) if not is_folder else "",
            "last_modified": child.get("lastModifiedDateTime",
                                       child.get("createdDateTime", "")),
            "is_folder": is_folder,
            "download_url": child.get("@microsoft.graph.downloadUrl", ""),
            "web_url": child.get("webUrl", "") or child.get("@microsoft.graph.downloadUrl", ""),
            "icon": icon,
            "ext": ext,
        }

    def download_file(self, download_url: str, dest_path: str,
                      progress_callback=None, share_url: str = "") -> bool:
        """流式下载文件到本地,支持进度回调。

        支持两种下载方式:
        1. SharePoint server-relative URL (以 /personal/ 或 / 开头)
           → 使用 cookie session
        2. Graph API download URL (https://...)
           → 使用 Bearer token

        progress_callback(downloaded_bytes, total_bytes)
        """
        # 判断下载 URL 类型
        if download_url.startswith("/"):
            # SharePoint server-relative URL: 需要 cookie session
            return self._sp_download_file(
                download_url, dest_path, progress_callback, share_url)

        # Graph API download URL: 需要 Bearer token
        token = self._get_token()
        if not token:
            return False
        try:
            resp = ms_requests.get(
                download_url,
                headers={"Authorization": f"Bearer {token}"},
                stream=True,
                timeout=(15, 120),
            )
            resp.raise_for_status()
            total = int(resp.headers.get("Content-Length", 0))
            downloaded = 0
            with open(dest_path, "wb") as f:
                for chunk in resp.iter_content(chunk_size=1024 * 1024):
                    if chunk:
                        f.write(chunk)
                        downloaded += len(chunk)
                        if progress_callback:
                            progress_callback(downloaded, total)
            return True
        except Exception as e:
            log_error("OneDrive", f"下载失败: {e}")
            return False

    def _sp_download_file(self, server_rel_url: str, dest_path: str,
                          progress_callback=None, share_url: str = "") -> bool:
        """使用 SharePoint cookie session 下载文件。"""
        sp = self._sp_init_session(share_url) if share_url else None
        if not sp:
            return False

        # 构造下载 URL
        # SharePoint 支持直接访问 + ?download=1 参数
        from urllib.parse import quote
        download_url = f"{sp['web_url']}/_layouts/15/download.aspx?SourceUrl={quote(server_rel_url)}"

        headers = {
            "User-Agent": "Mozilla/5.0",
        }
        if sp["form_digest"]:
            headers["X-RequestDigest"] = sp["form_digest"]

        try:
            resp = sp["session"].get(download_url, headers=headers,
                                     stream=True, timeout=(15, 120),
                                     allow_redirects=True)
            if resp.status_code != 200:
                # 备选: 直接访问文件 URL
                direct_url = f"{sp['web_url']}{server_rel_url}"
                resp = sp["session"].get(direct_url, headers=headers,
                                         stream=True, timeout=(15, 120),
                                         allow_redirects=True)
            resp.raise_for_status()
            total = int(resp.headers.get("Content-Length", 0))
            downloaded = 0
            with open(dest_path, "wb") as f:
                for chunk in resp.iter_content(chunk_size=1024 * 1024):
                    if chunk:
                        f.write(chunk)
                        downloaded += len(chunk)
                        if progress_callback:
                            progress_callback(downloaded, total)
            return True
        except Exception as e:
            log_error("OneDrive", f"SP 下载失败: {e}")
            return False


def _format_file_size(size: int) -> str:
    """格式化文件大小"""
    if not isinstance(size, (int, float)):
        try:
            size = int(size)
        except (ValueError, TypeError):
            return "0 B"
    if size < 1024:
        return f"{size} B"
    elif size < 1024 * 1024:
        return f"{size / 1024:.1f} KB"
    elif size < 1024 * 1024 * 1024:
        return f"{size / (1024 * 1024):.1f} MB"
    else:
        return f"{size / (1024 * 1024 * 1024):.2f} GB"


class AuthManager:
    """微软账号认证管理器 — 基于 MSAL Python,支持交互式登录 + 静默令牌刷新"""

    def __init__(self, base_path: str):
        self._client_id = MSAL_CLIENT_ID
        self._authority = MSAL_AUTHORITY
        self._scopes = MSAL_SCOPES
        self._cache_path = os.path.join(base_path, MSAL_CACHE_FILE)
        self._app = None
        self._account = None
        self._init_app()

    def _init_app(self):
        """初始化 MSAL PublicClientApplication"""
        if not MSAL_AVAILABLE:
            return
        cache = msal.SerializableTokenCache()
        if os.path.exists(self._cache_path):
            try:
                plaintext = None
                with open(self._cache_path, "rb") as f:
                    blob = f.read()
                if CRYPTO_AVAILABLE and blob[:1] == b'\x01':
                    # 加密格式: 尝试用机器码派生密钥解密
                    try:
                        master_key = get_master_key()
                        plaintext = decrypt(blob, master_key, associated_data=b"msal_cache")
                    except Exception:
                        # 旧版本/不同机器生成的密文, 静默回退到空缓存
                        plaintext = None
                else:
                    # 兼容旧版明文缓存 (一次性自动升级)
                    plaintext = blob.decode("utf-8", errors="ignore")
                if plaintext:
                    cache.deserialize(plaintext)
            except Exception:
                pass
        try:
            self._app = msal.PublicClientApplication(
                self._client_id,
                authority=self._authority,
                token_cache=cache,
            )
        except Exception as e:
            # 网络不可达时 MSAL tenant discovery 会抛 ConnectionError,
            # 此时标记为未初始化, 后续所有操作将优雅降级
            log_warn("App", f"MSAL 初始化失败 (无网络?): {e}")
            self._app = None

    def _save_cache(self):
        """持久化令牌缓存到文件 (AES-256-GCM 加密, 拒绝明文降级)"""
        if not CRYPTO_AVAILABLE:
            log_warn("OneDriveAuth", "cryptography library unavailable, "
                     "MSAL token cache NOT saved (refuse plaintext fallback)")
            return
        if self._app and self._app.token_cache:
            try:
                plaintext = self._app.token_cache.serialize()
                master_key = get_master_key()
                ciphertext = encrypt(plaintext.encode("utf-8"), master_key,
                                     associated_data=b"msal_cache")
                with open(self._cache_path, "wb") as f:
                    f.write(ciphertext)
                try:
                    os.chmod(self._cache_path, 0o600)
                except Exception:
                    pass
            except Exception as e:
                log_warn("OneDriveAuth", f"Token cache save failed: {e}")

    def login(self) -> dict | None:
        """设备代码流登录: 显示代码,用户在浏览器中输入完成授权。
        无需本地 HTTP 服务器,避免端口防火墙问题。
        返回 account dict 或 None(用户取消 / 失败)
        """
        if not self._app:
            return None
        try:
            flow = self._app.initiate_device_flow(scopes=self._scopes)
            if "user_code" not in flow:
                error = flow.get("error_description", flow.get("error", "无法启动设备代码流"))
                return {"success": False, "error": error}
            # 返回 flow 信息供 UI 显示
            result = self._app.acquire_token_by_device_flow(flow)
            if "access_token" in result:
                self._account = result.get("id_token_claims", {})
                self._save_cache()
                return {"success": True, "account": self._account}
            error = result.get("error_description", result.get("error", "授权失败"))
            return {"success": False, "error": error}
        except Exception as e:
            return {"success": False, "error": str(e)}

    def start_device_flow(self) -> dict | None:
        """启动设备代码流,返回包含 user_code / verification_uri 的 dict"""
        if not self._app:
            return None
        try:
            flow = self._app.initiate_device_flow(scopes=self._scopes)
            if "user_code" in flow:
                return {
                    "success": True,
                    "user_code": flow["user_code"],
                    "verification_uri": flow["verification_uri"],
                    "message": flow.get("message", ""),
                    "interval": flow.get("interval", 5),
                    "device_code": flow["device_code"],
                }
            return {"success": False, "error": flow.get("error_description", "无法启动")}
        except Exception as e:
            return {"success": False, "error": str(e)}

    def poll_device_flow(self, flow_info: dict) -> dict | None:
        """轮询设备代码流是否完成,返回结果或 None(继续等待)"""
        if not self._app:
            return None
        try:
            result = self._app.acquire_token_by_device_flow(
                {"device_code": flow_info["device_code"],
                 "interval": flow_info.get("interval", 5)})
            if "access_token" in result:
                self._account = result.get("id_token_claims", {})
                self._save_cache()
                return {"success": True, "account": self._account}
            error = result.get("error")
            if error == "authorization_pending":
                return None  # 用户还没完成,继续等
            if error == "slow_down":
                return None  # 轮询太快,继续等
            if error in ("authorization_expired", "expired_token"):
                return {"success": False, "expired": True,
                        "error": "设备代码已过期,请点击\"刷新代码\"获取新代码"}
            return {"success": False, "error": result.get("error_description", error or "授权失败")}
        except Exception as e:
            return {"success": False, "error": str(e)}

    def has_cache_account(self) -> bool:
        """快速检查 MSAL 缓存中是否有账号(不发起网络请求)"""
        if not self._app:
            return False
        return len(self._app.get_accounts()) > 0

    def acquire_token_silent(self, scopes: list = None) -> str | None:
        """尝试静默获取访问令牌(使用缓存中的 refresh token)。
        可选指定额外 scopes，会合并到基础 scopes 中。"""
        if not self._app:
            return None
        _scopes = self._scopes if scopes is None else list(set(self._scopes + scopes))
        accounts = self._app.get_accounts()
        if accounts:
            try:
                result = self._app.acquire_token_silent(
                    _scopes, account=accounts[0])
                if "access_token" in result:
                    self._save_cache()
                    return result["access_token"]
            except Exception:
                pass
        return None

    def is_logged_in(self) -> bool:
        """检查是否有缓存的登录状态"""
        if not self._app:
            return False
        token = self.acquire_token_silent()
        return token is not None

    def logout(self):
        """清除所有缓存的令牌和账户信息"""
        accounts = self._app.get_accounts() if self._app else []
        for acc in accounts:
            try:
                self._app.remove_account(acc)
            except Exception:
                pass
        self._account = None
        if os.path.exists(self._cache_path):
            try:
                os.remove(self._cache_path)
            except Exception:
                pass
        # 重新初始化空缓存
        self._init_app()

    # ---- Microsoft Graph API 调用 ----

    def _graph_get(self, endpoint: str, token: str) -> dict | None:
        """通用 Graph API GET 请求"""
        url = f"{GRAPH_API_BASE}/{endpoint}"
        try:
            resp = ms_requests.get(
                url,
                headers={"Authorization": f"Bearer {token}"},
                timeout=15,
            )
            resp.raise_for_status()
            return resp.json()
        except Exception as e:
            log_error("Graph", f"GET {endpoint} 失败: {e}")
            return None

    def _graph_get_bytes(self, endpoint: str, token: str) -> bytes | None:
        """通用 Graph API GET 请求(返回二进制)"""
        url = f"{GRAPH_API_BASE}/{endpoint}"
        try:
            resp = ms_requests.get(
                url,
                headers={"Authorization": f"Bearer {token}"},
                timeout=15,
            )
            resp.raise_for_status()
            return resp.content
        except Exception as e:
            log_error("Graph", f"GET {endpoint} 失败: {e}")
            return None

    def get_user_info(self) -> dict:
        """从 Microsoft Graph 获取用户基本信息"""
        token = self.acquire_token_silent(scopes=["User.Read"])
        if not token:
            return {"error": "未登录或令牌已过期"}

        data = self._graph_get(
            "me?$select=displayName,userPrincipalName,mail,id", token)
        if data:
            return {
                "display_name": data.get("displayName", ""),
                "email": data.get("mail") or data.get("userPrincipalName", ""),
                "user_id": data.get("id", ""),
                "upn": data.get("userPrincipalName", ""),
            }
        return {"error": "获取用户信息失败"}

    def get_cached_user_info(self) -> dict:
        """从 MSAL 缓存中提取用户信息(离线可用, 不发起网络请求)"""
        if not self._app:
            return {"error": "未初始化"}
        accounts = self._app.get_accounts()
        if not accounts:
            return {"error": "无缓存账号"}
        acct = accounts[0]
        claims = acct.get("id_token_claims", {})
        if claims:
            return {
                "display_name": claims.get("name", acct.get("username", "未知用户")),
                "email": claims.get("preferred_username", "") or claims.get("email", ""),
                "user_id": claims.get("sub") or acct.get("local_account_id", ""),
                "upn": claims.get("preferred_username", ""),
            }
        return {
            "display_name": acct.get("username", "未知用户"),
            "email": acct.get("username", ""),
            "user_id": acct.get("local_account_id", ""),
            "upn": acct.get("username", ""),
        }

    def get_user_photo(self) -> bytes | None:
        """从 Microsoft Graph 获取用户头像(返回图片字节)"""
        token = self.acquire_token_silent(scopes=["User.Read"])
        if not token:
            return None
        # 尝试获取高清头像 (648x648)
        photo_data = self._graph_get_bytes(
            "me/photos('648x648')/$value", token)
        if photo_data:
            return photo_data
        # 降级: 获取默认尺寸头像
        return self._graph_get_bytes("me/photo/$value", token)

    def _acquire_xbox_token(self) -> str | None:
        """获取用于 Xbox Live 认证的专用令牌。
        尝试多种方式: 1) XboxLive.signin 范围 2) user.auth.xboxlive.com 资源
        3) 降级使用 Graph 令牌
        """
        accounts = self._app.get_accounts()
        if not accounts:
            return None
        account = accounts[0]
        # 方式 1: 静默获取 XboxLive.signin 令牌 (用户可能已在浏览器中同意)
        try:
            result = self._app.acquire_token_silent(
                scopes=["XboxLive.signin"],
                account=account,
            )
            if "access_token" in result:
                return result["access_token"]
        except Exception:
            pass
        # 方式 2: 请求 user.auth.xboxlive.com 资源令牌
        try:
            result = self._app.acquire_token_silent(
                scopes=["https://user.auth.xboxlive.com/.default"],
                account=account,
            )
            if "access_token" in result:
                return result["access_token"]
        except Exception:
            pass
        # 方式 3: 降级用 Graph 令牌
        try:
            result = self._app.acquire_token_silent(
                scopes=MSAL_SCOPES, account=account)
            if "access_token" in result:
                return result["access_token"]
        except Exception:
            pass
        return None

    def _get_xsts_credentials(self) -> dict | None:
        """执行 XAS + XSTS 认证链, 返回 {'uhs': str, 'token': str} 或 None。
        此方法被 get_xbox_gamertag 和 get_xbox_friends 共享复用。
        """
        msal_token = self._acquire_xbox_token()
        if not msal_token:
            return None
        try:
            # Step 1: XAS — 用 MSAL 令牌换取 Xbox Live 用户令牌
            xas_resp = ms_requests.post(
                "https://user.auth.xboxlive.com/user/authenticate",
                json={
                    "Properties": {
                        "AuthMethod": "RPS",
                        "SiteName": "user.auth.xboxlive.com",
                        "RpsTicket": f"d={msal_token}",
                    },
                    "RelyingParty": "http://auth.xboxlive.com",
                    "TokenType": "JWT",
                },
                headers={"x-xbl-contract-version": "1"},
                timeout=15,
            )
            xas_resp.raise_for_status()
            xbl_token = xas_resp.json()["Token"]

            # Step 2: XSTS — 用 Xbox 用户令牌换取 XSTS 令牌
            xsts_resp = ms_requests.post(
                "https://xsts.auth.xboxlive.com/xsts/authorize",
                json={
                    "Properties": {
                        "SandboxId": "RETAIL",
                        "UserTokens": [xbl_token],
                    },
                    "RelyingParty": "http://xboxlive.com",
                    "TokenType": "JWT",
                },
                headers={"x-xbl-contract-version": "1"},
                timeout=15,
            )
            xsts_resp.raise_for_status()
            xsts_data = xsts_resp.json()
            return {
                "uhs": xsts_data["DisplayClaims"]["xui"][0]["uhs"],
                "token": xsts_data["Token"],
            }
        except Exception as e:
            log_error("Xbox", f"XSTS 认证失败: {e}")
            return None

    def get_xbox_gamertag(self) -> dict:
        """获取 Xbox 玩家代号。

        通过 Xbox Live 认证链:
          1. 用令牌向 user.auth.xboxlive.com 换取 Xbox 用户令牌
          2. 用 Xbox 用户令牌向 xsts.auth.xboxlive.com 换取 XSTS 令牌
          3. 用 XSTS 令牌调用 profile.xboxlive.com 获取 Gamertag
        """
        creds = self._get_xsts_credentials()
        if not creds:
            return {"error": "未登录或令牌已过期", "success": False}

        try:
            profile_resp = ms_requests.get(
                "https://profile.xboxlive.com/users/me/settings",
                params={"settings": "Gamertag"},
                headers={
                    "Authorization": f"XBL3.0 x={creds['uhs']};{creds['token']}",
                    "x-xbl-contract-version": "3",
                },
                timeout=10,
            )
            profile_resp.raise_for_status()
            data = profile_resp.json()
            settings = data.get("profileUsers", [{}])[0].get("settings", [])
            for s in settings:
                if s.get("id") == "Gamertag":
                    return {"gamertag": s.get("value", ""), "success": True}
            return {"gamertag": "", "success": False, "reason": "该微软账号未关联 Xbox 个人资料",
                    "no_xbox_profile": True}

        except ms_requests.exceptions.HTTPError as e:
            body = ""
            try:
                body = e.response.text[:500]
            except Exception:
                pass
            return {"gamertag": "", "success": False,
                    "reason": f"Xbox API 错误 ({e.response.status_code})", "http_body": body}
        except Exception as e:
            return {"gamertag": "", "success": False, "reason": str(e)}

    def get_xbox_friends(self, max_items: int = 25, skip_items: int = 0) -> dict:
        """获取 Xbox 好友列表。

        调用 peoplehub.xboxlive.com 社交 API, 返回格式:
          {
            'success': bool,
            'friends': [{'xuid', 'gamertag', 'display_name', 'display_pic',
                         'is_favorite', 'online_state', 'presence_text'}, ...],
            'total': int,
            'reason': str (失败时)
          }
        """
        creds = self._get_xsts_credentials()
        if not creds:
            return {"success": False, "reason": "Xbox Live 认证失败", "friends": [], "total": 0}

        try:
            resp = ms_requests.get(
                "https://peoplehub.xboxlive.com/users/me/people/social/decoration/favorite,detail",
                headers={
                    "Authorization": f"XBL3.0 x={creds['uhs']};{creds['token']}",
                    "x-xbl-contract-version": "5",
                    "Accept-Language": "en-US",
                },
                params={"maxItems": max_items, "skipItems": skip_items},
                timeout=20,
            )
            resp.raise_for_status()
            data = resp.json()

            friends = []
            for person in data.get("people", []):
                presence = person.get("presenceDetail", {})
                friends.append({
                    "xuid": person.get("xuid", ""),
                    "gamertag": person.get("gamertag", ""),
                    "display_name": person.get("displayName", ""),
                    "display_pic": person.get("displayPicRaw", ""),
                    "is_favorite": person.get("isFavorite", False),
                    "online_state": presence.get("state", "Offline"),
                    "presence_text": presence.get("text", ""),
                })
            return {
                "success": True,
                "friends": friends,
                "total": data.get("totalCount", len(friends)),
            }
        except ms_requests.exceptions.HTTPError as e:
            body = ""
            try:
                body = e.response.text[:300]
            except Exception:
                pass
            return {"success": False,
                    "reason": f"Xbox 好友 API 错误 ({e.response.status_code}): {body}",
                    "friends": [], "total": 0}
        except Exception as e:
            return {"success": False, "reason": str(e), "friends": [], "total": 0}


class LoginDialog(QDialog):
    """登录对话框 — 设备代码流 / 离线跳过 / 跳过登录"""

    def __init__(self, auth: AuthManager, parent=None):
        super().__init__(parent)
        self.auth = auth
        self._result = None  # "login" / "offline" / "cancel"
        self._poll_timer = None
        self._flow_info = None
        self._is_online = False  # 默认离线,异步检测后再更新
        self._build_ui()
        # 异步检测网络,不阻塞 UI
        self._start_network_check()

    def _build_ui(self):
        self.setWindowTitle("Hello Mental Omega Launcher — 登录")
        self.setFixedSize(480, 560)
        self.setWindowFlags(
            self.windowFlags() & ~Qt.WindowContextHelpButtonHint)
        layout = QVBoxLayout(self)
        layout.setContentsMargins(32, 32, 32, 24)
        layout.setSpacing(12)

        # 标题
        title = QLabel("🎮 Hello Mental Omega Launcher (HMOL)")
        title.setAlignment(Qt.AlignCenter)
        title.setStyleSheet("font-size: 18pt; font-weight: 700;")
        layout.addWidget(title)

        subtitle = QLabel("请使用微软账号登录以使用完整功能")
        subtitle.setAlignment(Qt.AlignCenter)
        subtitle.setStyleSheet("font-size: 10pt; color: rgba(0,0,0,140);")
        layout.addWidget(subtitle)

        # ---- 设备代码显示区 (初始隐藏) ----
        self.code_area = QFrame()
        self.code_area.setVisible(False)
        self.code_area.setStyleSheet(
            "QFrame { background-color: #f5f5f5; border-radius: 8px; "
            "border: 2px dashed #ddd; }")
        cv = QVBoxLayout(self.code_area)
        cv.setSpacing(6)

        code_hint = QLabel("在浏览器中打开以下网址并输入代码:")
        code_hint.setAlignment(Qt.AlignCenter)
        code_hint.setStyleSheet("font-size: 10pt; color: #666;")
        cv.addWidget(code_hint)

        self.code_link = QLabel()
        self.code_link.setAlignment(Qt.AlignCenter)
        self.code_link.setOpenExternalLinks(True)
        self.code_link.setStyleSheet(
            "font-size: 11pt; color: #0078d4; font-weight: 600;")
        cv.addWidget(self.code_link)

        self.code_value = QLabel()
        self.code_value.setAlignment(Qt.AlignCenter)
        self.code_value.setTextInteractionFlags(Qt.TextSelectableByMouse)
        self.code_value.setStyleSheet(
            "font-size: 22pt; font-weight: 700; letter-spacing: 8px; "
            "padding: 8px; color: #333; font-family: Consolas, monospace;")
        cv.addWidget(self.code_value)

        copy_btn = QPushButton("📋 复制代码")
        copy_btn.setMaximumWidth(140)
        copy_btn.setCursor(QCursor(Qt.PointingHandCursor))
        copy_btn.setStyleSheet(
            "QPushButton { background: transparent; border: 1px solid #ccc; "
            "border-radius: 4px; padding: 4px; font-size: 9pt; }")
        copy_btn.clicked.connect(self._copy_code)
        cv.addWidget(copy_btn, 0, Qt.AlignCenter)

        self.refresh_btn = QPushButton("🔄 刷新代码")
        self.refresh_btn.setMaximumWidth(140)
        self.refresh_btn.setCursor(QCursor(Qt.PointingHandCursor))
        self.refresh_btn.setStyleSheet(
            "QPushButton { background: #0078d4; color: white; "
            "border: none; border-radius: 4px; padding: 6px; font-size: 9pt; font-weight: 600; }"
            "QPushButton:hover { background: #106ebe; }")
        self.refresh_btn.clicked.connect(self._do_login)
        self.refresh_btn.setVisible(False)  # 初始隐藏,代码过期后显示
        cv.addWidget(self.refresh_btn, 0, Qt.AlignCenter)

        layout.addWidget(self.code_area)

        # 状态图标
        self.status_icon = QLabel()
        self.status_icon.setAlignment(Qt.AlignCenter)
        self.status_icon.setStyleSheet("font-size: 40pt;")
        layout.addWidget(self.status_icon)

        # 状态文本
        self.status_label = QLabel()
        self.status_label.setAlignment(Qt.AlignCenter)
        self.status_label.setWordWrap(True)
        self.status_label.setStyleSheet("font-size: 10pt; padding: 4px;")
        layout.addWidget(self.status_label)

        layout.addStretch()

        # 按钮区域
        btn_layout = QVBoxLayout()
        btn_layout.setSpacing(8)

        self.login_btn = QPushButton("🔐 使用微软账号登录")
        self.login_btn.setCursor(QCursor(Qt.PointingHandCursor))
        self.login_btn.setMinimumHeight(44)
        self.login_btn.setStyleSheet("""
            QPushButton {
                background-color: #0078D4; color: white;
                border: none; border-radius: 8px;
                font-size: 12pt; font-weight: 600;
            }
            QPushButton:hover { background-color: #106EBE; }
            QPushButton:disabled { background-color: #a0c4e8; }
        """)
        self.login_btn.clicked.connect(self._do_login)
        btn_layout.addWidget(self.login_btn)

        # 跳过登录按钮 (联网时可选跳过,无需登录直接使用基础功能)
        self.skip_btn = QPushButton("⏭️ 跳过登录 (基础功能)")
        self.skip_btn.setCursor(QCursor(Qt.PointingHandCursor))
        self.skip_btn.setMinimumHeight(40)
        self.skip_btn.setStyleSheet("""
            QPushButton {
                background-color: transparent; color: #555;
                border: 1px dashed #bbb; border-radius: 8px;
                font-size: 10pt;
            }
            QPushButton:hover { background-color: rgba(0,0,0,10); border-color: #999; }
        """)
        self.skip_btn.clicked.connect(self._do_offline)
        btn_layout.addWidget(self.skip_btn)

        self.offline_btn = QPushButton("📡 离线跳过 (受限功能)")
        self.offline_btn.setCursor(QCursor(Qt.PointingHandCursor))
        self.offline_btn.setMinimumHeight(38)
        self.offline_btn.setStyleSheet("""
            QPushButton {
                background-color: transparent; color: #666;
                border: 1px solid #ccc; border-radius: 8px;
                font-size: 10pt;
            }
            QPushButton:hover { background-color: rgba(0,0,0,10); }
        """)
        self.offline_btn.clicked.connect(self._do_offline)
        btn_layout.addWidget(self.offline_btn)

        self.cancel_btn = QPushButton("取消")
        self.cancel_btn.setCursor(QCursor(Qt.PointingHandCursor))
        self.cancel_btn.setFlat(True)
        self.cancel_btn.setStyleSheet(
            "QPushButton { color: #999; font-size: 9pt; }")
        self.cancel_btn.clicked.connect(self.reject)
        btn_layout.addWidget(self.cancel_btn)

        layout.addLayout(btn_layout)

        # 初始化状态
        if not MSAL_AVAILABLE:
            self.status_icon.setText("⚠️")
            self.status_label.setText(
                "缺少认证组件 (msal)\n请在命令行运行:\npip install msal requests")
            self.login_btn.setEnabled(False)
            self.skip_btn.setVisible(False)
            self.offline_btn.setVisible(True)
            self.offline_btn.setText("📡 离线模式 (功能受限)")
        else:
            # 初始显示检测中, _start_network_check 完成后更新实际状态
            self.status_icon.setText("🔍")
            self.status_label.setText("正在检测网络连接...")
            self.login_btn.setEnabled(True)  # 默认可点击, 离线检测完成后禁用
            self.skip_btn.setVisible(True)   # 联网时允许跳过登录
            self.offline_btn.setVisible(False)

    def _do_login(self):
        """启动设备代码流"""
        # 停止旧轮询
        if self._poll_timer:
            self._poll_timer.stop()
        self._poll_timer = None
        self.login_btn.setEnabled(False)
        self.login_btn.setText("⏳ 正在连接...")
        self.skip_btn.setVisible(False)
        self.status_label.setText("正在与微软服务器通信...")
        QApplication.processEvents()

        flow = self.auth.start_device_flow()
        if not flow or not flow.get("success"):
            error = (flow or {}).get("error", "无法启动设备代码流")
            self.status_label.setText(f"❌ {error}")
            self.login_btn.setEnabled(True)
            self.login_btn.setText("🔐 使用微软账号登录")
            self.skip_btn.setVisible(True)
            if not self.offline_btn.isVisible():
                self.offline_btn.setVisible(True)
            return

        self._flow_info = flow

        # 显示设备代码
        self.status_icon.setText("📱")
        self.status_label.setText(
            "请在浏览器中打开下方网址\n输入显示的代码完成授权\n\n授权完成后将自动进入程序")
        self.code_link.setText(
            f"<a href='{flow['verification_uri']}' "
            f"style='color:#0078d4;'>{flow['verification_uri']}</a>")
        self.code_value.setText(flow["user_code"])
        self.code_area.setVisible(True)
        self.refresh_btn.setVisible(False)  # 新代码生成后隐藏刷新按钮
        self.login_btn.setVisible(False)
        self.skip_btn.setVisible(False)

        # 自动打开浏览器
        try:
            webbrowser.open(flow["verification_uri"])
        except Exception:
            pass

        # 开始轮询
        self._poll_timer = QTimer(self)
        self._poll_timer.timeout.connect(self._poll_flow)
        self._poll_timer.start(flow.get("interval", 5) * 1000)

    def _poll_flow(self):
        """轮询设备代码授权结果"""
        if not self._flow_info:
            return
        result = self.auth.poll_device_flow(self._flow_info)
        if result is None:
            return  # 还在等待
        # 有结果了
        self._poll_timer.stop()
        if result.get("success"):
            self._result = "login"
            self.accept()
        elif result.get("expired"):
            # 代码过期 —— 显示提示和刷新按钮
            self.status_label.setText(f"⌛ {result.get('error', '代码已过期')}")
            self.refresh_btn.setVisible(True)
            self.login_btn.setVisible(False)
            self.skip_btn.setVisible(True)
            self.status_icon.setText("⌛")
        else:
            self.status_label.setText(f"❌ {result.get('error', '授权失败')}")
            self.login_btn.setVisible(True)
            self.login_btn.setEnabled(True)
            self.login_btn.setText("🔐 重试登录")
            self.skip_btn.setVisible(True)
            self.code_area.setVisible(False)
            self.refresh_btn.setVisible(False)
            if not self.offline_btn.isVisible():
                self.offline_btn.setVisible(True)

    def _copy_code(self):
        """复制设备代码到剪贴板"""
        code = self.code_value.text()
        if code:
            QApplication.clipboard().setText(code)
            self.status_label.setText("✅ 代码已复制到剪贴板!")

    def _do_offline(self):
        """离线模式"""
        self._result = "offline"
        self.accept()

    def _start_network_check(self):
        """后台检测网络,完成后更新 UI 状态"""
        def do_check():
            online = _check_network_available()
            def update_ui():
                self._is_online = online
                if not MSAL_AVAILABLE:
                    return
                if online:
                    self.status_icon.setText("🔐")
                    self.status_label.setText(
                        "点击下方按钮,在浏览器中输入代码完成授权")
                    self.login_btn.setEnabled(True)
                    self.skip_btn.setVisible(True)
                    self.offline_btn.setVisible(False)
                else:
                    self.status_icon.setText("📡")
                    self.status_label.setText(
                        "未检测到网络连接\n可以选择离线模式进入程序\n(笨蛋广场和账户功能将不可用)")
                    self.login_btn.setEnabled(False)
                    self.skip_btn.setVisible(False)
                    self.offline_btn.setVisible(True)
            QTimer.singleShot(0, update_ui)
        threading.Thread(target=do_check, daemon=True).start()

    def get_result(self) -> str:
        """返回 'login' / 'offline' / 'cancel'"""
        return self._result or "cancel"


class MainWindow(QMainWindow):
    """主窗口 - PySide6 现代化 UI"""

    # ── OneDrive 跨线程信号 ──
    _od_list_signal = Signal(str, dict)
    _od_more_signal = Signal(str, dict)
    _od_search_signal = Signal(str, dict)
    _account_signal = Signal(dict, object, dict)
    _xbox_friends_signal = Signal(dict)  # Xbox 好友列表跨线程信号
    _friend_avatar_signal = Signal(str, object)  # 好友头像: (xuid, bytes|None)
    _qq_shout_signal = Signal(bool, str)         # 联机喊话结果: (success, message)

    def __init__(self, auth_manager=None, is_offline=False):
        super().__init__()
        self.base_path = get_program_base_path()
        # Microsoft 账号认证状态
        self.auth_manager = auth_manager
        self.od_browser = OneDriveBrowser(auth_manager) if auth_manager else None  # OneDrive 文件浏览器
        self._onedrive_loaded = set()  # 跟踪已懒加载的 OneDrive 页面
        # 跨线程信号连接（确保从 worker 线程安全投递到主线程）
        self._od_list_signal.connect(self._od_on_list_loaded)
        self._od_more_signal.connect(self._od_on_more_loaded)
        self._od_search_signal.connect(self._od_on_search_result)
        self._account_signal.connect(self._on_account_loaded)
        self._xbox_friends_signal.connect(self._on_xbox_friends_loaded)
        self._friend_avatar_signal.connect(self._on_friend_avatar_loaded)
        self.is_offline = is_offline  # 离线模式标志(笨蛋广场禁用)
        # ── Xbox 好友列表状态 ──
        self._xbox_friends_all: list = []       # 已加载的全部好友
        self._xbox_friends_skip = 0             # 分页偏移量
        self._xbox_friends_total = 0            # 好友总数
        self._xbox_friends_sort = "default"     # 排序方式: default / online / name
        self._xbox_friends_loading = False       # 是否正在加载
        self._xbox_friends_appending = False     # 追加模式标志
        self._xbox_friends_filter = ""           # 搜索过滤文本
        # ── ──
        self.config_file = os.path.join(self.base_path, "HMOL_config.json")
        # 兼容旧版 mo_manager_config.json: 若 HMOL_config.json 不存在但旧文件存在,自动迁移
        old_config_file = os.path.join(self.base_path, "mo_manager_config.json")
        if not os.path.exists(self.config_file) and os.path.exists(old_config_file):
            try:
                os.rename(old_config_file, self.config_file)
                log_info("Migration", f"已将旧配置 {old_config_file} 重命名为 {self.config_file}")
            except Exception as ex:
                log_warn("Migration", f"重命名旧配置失败,回退读取: {ex}")
                self.config_file = old_config_file
        self.config = self._load_config()
        self.theme = self._resolve_theme()
        self.theme_mode = self.config.get("theme_mode", "跟随系统")
        # 主题色方案(用于渐变 UI)
        saved_gradient = self.config.get("gradient_theme", DEFAULT_GRADIENT_THEME)
        self.gradient_theme = saved_gradient if saved_gradient in GRADIENT_THEMES else DEFAULT_GRADIENT_THEME

        # 业务对象
        self.file_op_thread = FileOperationThread()
        self.file_op_thread.start()
        self.file_op_thread.task_completed.connect(self._on_file_op_completed)

        self.instance_manager = InstanceManager(self, self.base_path)
        self.instance_manager.load_instances()
        # 恢复上次选择的实例
        last_id = self.config.get("last_instance_id")
        if last_id and last_id in self.instance_manager.instances:
            self.instance_manager.set_current_instance(last_id)
        self.instance_manager.instances_changed.connect(self.update_instance_combo)

        # 安装包定义 (从 instance 管理,此处仅作为初始化备份)
        self.package_configs = self._get_package_configs()
        self.package_dirs = self._get_package_dirs()

        # 当前选中的 tab
        self.package_tabs = {}

        self._build_ui()
        self._apply_theme()
        self.update_instance_combo()
        # 启动时加载用户保存的主页背景图片
        try:
            self._load_saved_home_background()
        except Exception as e:
            log_warn("App", f"加载主页背景失败: {e}")

        # 启动时扫描 DLC 文件夹中未处理的压缩包
        QTimer.singleShot(500, self._dlc_scan_pending_archives)

        # 离线模式: 禁用笨蛋广场按钮与账户入口
        if self.is_offline:
            for btn in (self.resource_dl_btn, self.runtime_env_btn, self.program_extend_btn, self.upload_res_btn):
                btn.setEnabled(False)
                btn.setToolTip("离线模式不可用 — 请重启程序并登录微软账号")
            # 禁用账户导航按钮
            if hasattr(self, 'nav_buttons') and "account" in self.nav_buttons:
                self.nav_buttons["account"].setEnabled(False)
                self.nav_buttons["account"].setToolTip("离线模式不可用 — 请重启程序并登录微软账号")

    # ---------- 工具方法 ----------
    def _load_config(self) -> dict:
        if os.path.exists(self.config_file):
            try:
                with open(self.config_file, "r", encoding="utf-8") as f:
                    data = json.load(f)
                # 兼容老配置: 若有 installed_packages, 丢弃(由 instance manager 管理)
                data.pop("installed_packages", None)
                # 兼容老配置: 补全缺失字段
                data.setdefault("home_background_path", None)
                return data
            except (json.JSONDecodeError, OSError) as e:
                log_error("App", f"加载配置失败: {e},使用默认配置")
        return {
            "version": "2.2",
            "theme_mode": "跟随系统",
            "auto_detect_path": True,
            "gradient_theme": DEFAULT_GRADIENT_THEME,
            "home_background_path": None,
        }

    def save_config(self, immediate=False):
        """保存应用配置(不含 per-instance 数据)。内置防抖: 连续调用仅最后一次写入。"""
        if immediate:
            self._save_config_pending = False
            self._do_save_config()
            return
        if getattr(self, '_save_config_pending', False):
            return
        self._save_config_pending = True
        QTimer.singleShot(300, self._do_save_config)

    def _do_save_config(self):
        self._save_config_pending = False
        try:
            # 仅持久化全局设置;installed_packages 是 dead code,不再写入
            safe_config = {
                "version": self.config.get("version", "2.2"),
                "theme_mode": self.config.get("theme_mode", "跟随系统"),
                "gradient_theme": self.config.get("gradient_theme", DEFAULT_GRADIENT_THEME),
                "auto_detect_path": self.config.get("auto_detect_path", True),
                "last_instance_id": (
                    self.instance_manager.current_instance.id
                    if self.instance_manager.current_instance else None
                ),
                # 主页背景图片路径(用户自定义)
                "home_background_path": self.config.get("home_background_path"),
                # EULA 接受状态 (GDPR / 法律合规)
                "eula_accepted": self.config.get("eula_accepted", False),
                "eula_accepted_version": self.config.get("eula_accepted_version", ""),
            }
            with open(self.config_file, "w", encoding="utf-8") as f:
                json.dump(safe_config, f, ensure_ascii=False, indent=4)
        except Exception as e:
            log_error("App", f"保存配置失败: {e}")

    def closeEvent(self, event):
        """关闭窗口时自动保存所有状态"""
        try:
            self.save_config(immediate=True)
            # 保存当前实例下所有实例的 config
            if hasattr(self, 'instance_manager'):
                for inst in self.instance_manager.instances.values():
                    try:
                        self.instance_manager._save_instance_config(inst)
                    except Exception as e:
                        # 关键: 不再静默吞掉,至少打印日志方便排查
                        log_warn("App", f"保存实例配置失败 ({inst.name if hasattr(inst, 'name') else '?'}): {e}")
            # 停止后台线程
            if hasattr(self, 'file_op_thread'):
                self.file_op_thread.stop()
        except Exception as e:
            log_error("App", f"关闭异常: {e}")
        super().closeEvent(event)

    def _resolve_theme(self) -> dict:
        mode = self.config.get("theme_mode", "跟随系统")
        if mode == "跟随系统":
            return DARK if detect_system_theme() == "深色模式" else LIGHT
        return DARK if mode == "深色模式" else LIGHT

    def _get_package_configs(self) -> dict:
        # 默认扩展名(包含 .rar,需 rarfile 库)
        _arc_exts = [".zip", ".7z"]
        if RARFILE_AVAILABLE:
            _arc_exts.append(".rar")
        return {
            "ini": {"name": "INI", "extensions": _arc_exts, "icon": "📀"},
            "map": {"name": "地图", "extensions": [".map"] + _arc_exts, "icon": "🗺️"},
            "mission": {"name": "任务", "extensions": _arc_exts, "icon": "🎯"},
            "voice": {"name": "语音", "extensions": _arc_exts + [".mp3", ".ogg", ".wav"], "icon": "🎤"},
            "plugin": {"name": "插件", "extensions": _arc_exts + [".dll"], "icon": "🔌"},
            "beautification": {"name": "美化", "extensions": _arc_exts, "icon": "✨"},
            "music": {"name": "音乐", "extensions": [".mp3", ".ogg", ".wav"] + _arc_exts, "icon": "🎵"},
        }

    def _get_package_dirs(self) -> dict:
        # 旧版本 INI 包目录名为 "mod",已重命名为 "ini"。
        # 兼容:若 packages/mod 仍存在且 packages/ini 不存在,自动迁移
        old_mod_dir = os.path.join(self.base_path, "packages", "mod")
        new_ini_dir = os.path.join(self.base_path, "packages", "ini")
        if os.path.isdir(old_mod_dir) and not os.path.isdir(new_ini_dir):
            try:
                os.rename(old_mod_dir, new_ini_dir)
                log_info("Migration", f"已将 INI 包目录 {old_mod_dir} 重命名为 {new_ini_dir}")
            except Exception as e:
                log_error("Migration", f"重命名 INI 包目录失败: {e}")
        dirs = {}
        for t in self.package_configs:
            dirs[t] = self.get_package_dir(t)
            os.makedirs(dirs[t], exist_ok=True)
        return dirs

    def get_package_dir(self, package_type: str) -> str:
        return os.path.join(self.base_path, "packages", package_type)

    def is_mo_directory(self, path: str) -> bool:
        """检测是否为有效的心灵终结目录

        判定规则(满足任一即为有效):
          1. 目录下存在 Mental_Omega 子目录
          2. 路径本身就是 Mental_Omega 目录
          3. 目录下存在 MentalOmegaClient.exe 或 Mental Omega.exe
          4. 父目录下存在 MentalOmegaClient.exe 或 Mental Omega.exe
             (允许用户选中安装根的子目录时也能正确识别)
        """
        if not path or not os.path.exists(path):
            return False
        try:
            path = os.path.abspath(path)
        except Exception:
            return False

        # 1) 存在 Mental_Omega 子目录
        mo_path = os.path.join(path, "Mental_Omega")
        if os.path.isdir(mo_path):
            return True

        # 2) 路径本身即为 Mental_Omega
        if os.path.basename(path) == "Mental_Omega" and os.path.isdir(path):
            return True

        # 3) 目录下存在特征可执行文件
        exe_markers = ("MentalOmegaClient.exe", "Mental Omega.exe")
        for marker in exe_markers:
            if os.path.isfile(os.path.join(path, marker)):
                return True

        # 4) 父目录中存在特征可执行文件(用户可能选中了子目录)
        parent = os.path.dirname(path)
        if parent and os.path.isdir(parent):
            for marker in exe_markers:
                if os.path.isfile(os.path.join(parent, marker)):
                    return True

        return False

    def copy_files(self, source_dir, target_dir, progress_callback=None,
                   conflict_policy="overwrite_all"):
        """跨平台同步复制(不依赖 xcopy/cp,使用 Python 原生实现)。
        返回 (success, total, failed) 元组。
        progress_callback: 可选回调函数 (current, total, current_file) 用于进度反馈。
        conflict_policy: 文件级冲突处理策略
            - "overwrite_all": 覆盖目标目录中的所有现有文件
            - "skip_existing": 跳过目标目录中已存在的文件
            - "abort":        检测到任何冲突立即中止(实际上层会先扫描)
        性能优化: 使用 os.scandir 单次遍历预收集文件列表,避免双重 os.walk。
        """
        if not os.path.exists(source_dir):
            return (False, 0, 0)
        # 单次扫描: 收集所有待复制文件的 (src, dst) 对
        file_pairs = []

        def _collect_files(path):
            try:
                with os.scandir(path) as entries:
                    for entry in entries:
                        if entry.is_file(follow_symlinks=False):
                            file_pairs.append(entry.path)
                        elif entry.is_dir(follow_symlinks=False):
                            _collect_files(entry.path)
            except OSError as e:
                log_error("Scan", f"扫描目录失败: {e}")

        _collect_files(source_dir)

        total = len(file_pairs)
        if total == 0:
            return (True, 0, 0)

        # 实际复制(仅一次遍历)
        processed = 0
        failed = 0
        last_pct = [-1]
        try:
            os.makedirs(target_dir, exist_ok=True)
            for src in file_pairs:
                rel = os.path.relpath(src, source_dir)
                dst = os.path.normpath(os.path.join(target_dir, rel))
                dst_dir = os.path.dirname(dst)
                if dst_dir:
                    os.makedirs(dst_dir, exist_ok=True)
                # 文件级冲突处理
                if os.path.exists(dst) and conflict_policy == "skip_existing":
                    processed += 1
                    if progress_callback:
                        try:
                            progress_callback(processed, total, src)
                        except Exception:
                            pass
                    continue
                try:
                    shutil.copy2(src, dst)  # copy2 保留 mtime/权限
                except (OSError, PermissionError) as e:
                    failed += 1
                    log_error("App", f"复制文件失败 {src}: {e}")
                processed += 1
                # 节流回调: 仅每 1% 或每 50 个文件回调一次
                if progress_callback:
                    try:
                        pct = int((processed / total) * 100) if total > 0 else processed
                        if pct != last_pct[0]:
                            last_pct[0] = pct
                            progress_callback(processed, total, src)
                    except Exception:
                        pass
            success = failed < total
            return (success, total, failed)
        except Exception as e:
            log_error("App", f"复制目录失败: {e}")
            return (False, processed, failed)

    def scan_install_conflicts(self, src_to_copy, target):
        """扫描安装时的文件级冲突。

        返回:
            conflicts: 冲突文件相对路径列表(已截断到最多 50 个)
            total: 冲突文件总数
        优化: 使用 os.scandir 递归替代 os.walk，减少无谓的路径拼接开销。
        """
        conflicts = []
        total = 0
        if not os.path.isdir(src_to_copy) or not os.path.isdir(target):
            return conflicts, 0

        def _scan(path):
            nonlocal total
            try:
                with os.scandir(path) as entries:
                    for entry in entries:
                        src_entry = entry.path
                        rel = os.path.relpath(src_entry, src_to_copy)
                        dst_file = os.path.join(target, rel)
                        if entry.is_file(follow_symlinks=False):
                            if os.path.exists(dst_file):
                                total += 1
                                if len(conflicts) < 50:
                                    conflicts.append(rel)
                        elif entry.is_dir(follow_symlinks=False):
                            _scan(src_entry)
            except OSError:
                pass

        _scan(src_to_copy)
        return conflicts, total

    # ---------- 主题 ----------
    # QSS 缓存 key
    _qss_cache = {}

    def _apply_theme(self):
        g = self._current_gradient()
        # 主题与渐变均未变化时跳过
        cache_key = (self.theme.get("name", ""), g.get("name", ""))
        if cache_key == getattr(self, '_last_theme_key', None):
            return
        self._last_theme_key = cache_key
        # 缓存: 同一主题+渐变组合不再重建 QSS
        if cache_key in self._qss_cache:
            qss = self._qss_cache[cache_key]
        else:
            qss = build_qss(self.theme, gradient=g)
            self._qss_cache[cache_key] = qss
        # 强制全局字体(避免不同控件/平台字体不一致)
        base_font = QFont("Microsoft YaHei UI", 10)
        base_font.setStyleStrategy(QFont.PreferAntialias)
        self.setFont(base_font)
        self.setStyleSheet(qss)
        # 通知子组件
        for tab in self.package_tabs.values():
            if hasattr(tab, 'apply_theme'):
                tab.apply_theme(self.theme)
        # 刷新 nav 按钮样式(主题色变更后保持 active 高亮)
        active_key = getattr(self, '_active_page_key', None)
        for k, btn in self.nav_buttons.items():
            btn.setStyleSheet(self._nav_btn_style(k == active_key))
        # 刷新非导航 sidebar 按钮 (不在 nav_buttons 中的 secondary/danger)
        for btn in getattr(self, '_sidebar_extra_btns', []):
            btn.setStyleSheet(self._nav_btn_style(False))
        # 刷新 danger 按钮
        for btn in getattr(self, '_sidebar_danger_btns', []):
            btn.setStyleSheet(f"""
                QPushButton {{
                    background-color: {self.theme['error']};
                    color: {self.theme['text_inverse']};
                    border: none;
                    border-radius: 6px;
                    padding: 8px 12px;
                    text-align: left;
                }}
                QPushButton:hover {{
                    background-color: {self._lighten(self.theme['error'])};
                }}
                QPushButton:pressed {{
                    background-color: {self._darken(self.theme['error'])};
                }}
            """)
        # 刷新主页当前实例标签
        if hasattr(self, 'home_instance_label'):
            self.home_instance_label.setStyleSheet(
                f"color: {self.theme.get('text_secondary', '#6c757d')}; "
                f"font-size: 10pt; padding: 4px 0;"
            )
        # 刷新 caption 标签 (使用存储的引用列表,避免 findChildren 树遍历)
        for lbl in getattr(self, '_caption_labels', []):
            lbl.setStyleSheet(
                f"color: {self.theme.get('text_secondary', '#6c757d')}; "
                f"font-size: 9pt;"
            )
        # 应用渐变色方案
        self._apply_gradient_theme()
        self._update_status_theme()

    def _update_status_theme(self):
        if hasattr(self, 'status_label') and self.status_label:
            self.status_label.setStyleSheet(f"color: {self.theme['text_secondary']};")

    @staticmethod
    def _build_theme_card(theme: dict) -> QFrame:
        """构建一个带主题样式的卡片 QFrame"""
        card = QFrame()
        card.setProperty("role", "card")
        return card

    def apply_theme_mode(self, mode: str):
        self.config["theme_mode"] = mode
        self.theme_mode = mode
        if mode == "跟随系统":
            self.theme = DARK if detect_system_theme() == "深色模式" else LIGHT
        else:
            self.theme = DARK if mode == "深色模式" else LIGHT
        self._apply_theme()

    # ---------- UI 构建 ----------
    def _build_ui(self):
        self.setWindowTitle("Hello Mental Omega Launcher - v2.2")
        # 响应式最小尺寸: 既能适应低分辨率(老旧 1366×768 笔记本),也保留宽屏扩展空间
        self.setMinimumSize(640, 480)
        self.setWindowIcon(QIcon(get_app_icon_pixmap()))

        # 初始大小: 按当前屏幕的 75% 居中显示,最小不低于最小尺寸
        try:
            screen = QGuiApplication.primaryScreen()
            if screen:
                avail = screen.availableGeometry()
                init_w = max(self.minimumWidth(), int(avail.width() * 0.75))
                init_h = max(self.minimumHeight(), int(avail.height() * 0.75))
                # 上限: 不超过屏幕工作区,避免窗口超出可见范围
                init_w = min(init_w, avail.width())
                init_h = min(init_h, avail.height())
                self.resize(init_w, init_h)
                # 居中
                x = avail.x() + (avail.width() - self.width()) // 2
                y = avail.y() + (avail.height() - self.height()) // 2
                self.move(max(avail.x(), x), max(avail.y(), y))
            else:
                self.resize(1100, 720)
        except Exception:
            self.resize(1100, 720)

        # 中央 widget
        central = QWidget()
        self.setCentralWidget(central)
        root = QHBoxLayout(central)
        root.setContentsMargins(0, 0, 0, 0)
        root.setSpacing(0)

        # 侧边栏
        self.sidebar = self._build_sidebar()
        root.addWidget(self.sidebar)

        # 主内容区
        self.content_stack = QStackedWidget()
        self.content_stack.setObjectName("contentStack")
        root.addWidget(self.content_stack, 1)

        # 主页(欢迎/概览)
        self.content_stack.addWidget(self._build_home_page())
        # 包管理页
        self.content_stack.addWidget(self._build_package_page())
        # 实例管理页
        self.content_stack.addWidget(self._build_instance_page())
        # 设置页
        self.content_stack.addWidget(self._build_settings_page())
        # 关于页
        self.content_stack.addWidget(self._build_about_page())
        # ===== 子页(内嵌到主窗口,带"返回"按钮) =====

        self.sub_tutorial = self._wrap_subpage(
            "📖 使用教程", self._build_tutorial_subpage())
        self.content_stack.addWidget(self.sub_tutorial)
        self.sub_feedback = self._wrap_subpage(
            "💬 用户反馈", self._build_feedback_subpage())
        self.content_stack.addWidget(self.sub_feedback)
        self.sub_add_instance = self._wrap_subpage(
            "➕ 添加实例", self._build_add_instance_subpage())
        self.content_stack.addWidget(self.sub_add_instance)
        self.sub_rename_instance = self._wrap_subpage(
            "✏️ 重命名实例", self._build_rename_instance_subpage())
        self.content_stack.addWidget(self.sub_rename_instance)
        # ===== 笨蛋广场子页 (延迟加载) =====
        self._src = ONEDRIVE_SOURCES
        QTimer.singleShot(100, self._build_od_subpages)
        # ===== 账户页 =====
        self.sub_account = self._build_account_page()
        self.content_stack.addWidget(self.sub_account)
        # ===== 程序 DLC =====
        self.sub_dlc = self._build_dlc_page()
        self.content_stack.addWidget(self.sub_dlc)
        # 状态栏
        self._build_statusbar()
        # 为所有内容页设置 role="content_page"(透明背景, 让 contentStack 渐变可见)
        for i in range(self.content_stack.count()):
            w = self.content_stack.widget(i)
            if w:
                w.setProperty("role", "content_page")
        # 初始化 active page
        self._active_page_key = "home"
        # 触发一次响应式布局
        QTimer.singleShot(0, lambda: self.resizeEvent(None))

    def _build_od_subpages(self):
        """延迟构建笨蛋广场 OneDrive 子页（避免阻塞 UI 启动）"""
        src = self._src
        self.sub_resource_download = self._wrap_subpage(
            "🎮 游戏资源下载",
            self._build_onedrive_page("game_resources", src["game_resources"]["url"]))
        self.content_stack.addWidget(self.sub_resource_download)
        self.sub_runtime_env = self._wrap_subpage(
            "⚙️ 运行环境",
            self._build_onedrive_page("runtime_env", src["runtime_env"]["url"]))
        self.content_stack.addWidget(self.sub_runtime_env)
        self.sub_program_extend = self._wrap_subpage(
            "🧩 程序DLC下载",
            self._build_onedrive_page("program_extend", src["program_extend"]["url"]))
        self.content_stack.addWidget(self.sub_program_extend)
        # 标记延迟加载页的透明背景 role
        for w in (self.sub_resource_download, self.sub_runtime_env, self.sub_program_extend):
            if w:
                w.setProperty("role", "content_page")

    def _build_sidebar(self) -> QFrame:
        sidebar = QFrame()
        sidebar.setProperty("role", "sidebar")
        sidebar.setFixedWidth(220)
        sidebar.setMinimumWidth(96)  # 紧凑模式最小宽度(放得下 emoji 按钮)
        sidebar.setMaximumWidth(280)
        layout = QVBoxLayout(sidebar)
        layout.setContentsMargins(8, 16, 8, 16)
        layout.setSpacing(6)

        # 用于紧凑模式隐藏
        self.sidebar_labels = []

        # Logo 区域: 启动器图标 + HMOL v版本号
        logo_row = QHBoxLayout()
        logo_row.setContentsMargins(4, 0, 4, 4)
        logo_row.setSpacing(10)

        # 启动器图标(纯视觉, 无交互)
        self.launcher_icon = QLabel()
        self.launcher_icon.setFixedSize(40, 40)
        app_icon = get_app_icon_pixmap()
        if not app_icon.isNull():
            scaled = app_icon.scaled(40, 40, Qt.KeepAspectRatio, Qt.SmoothTransformation)
            self.launcher_icon.setPixmap(scaled)
        else:
            self.launcher_icon.setText("🚀")
            self.launcher_icon.setStyleSheet("font-size: 22px; border: none; background: transparent;")
        self.launcher_icon.setAlignment(Qt.AlignCenter)
        logo_row.addWidget(self.launcher_icon)

        # HMOL 版本标签
        ver = self.config.get("version", "2.2")
        logo_label = QLabel(f"HMOL v{ver}")
        logo_label.setProperty("role", "title")
        logo_label.setStyleSheet("font-size: 14pt; font-weight: 700; padding: 0;")
        logo_row.addWidget(logo_label)
        logo_row.addStretch()

        logo_widget = QWidget()
        logo_widget.setLayout(logo_row)
        layout.addWidget(logo_widget)
        self.sidebar_labels.append(logo_label)

        layout.addSpacing(20)

        # ===== 主导航 =====
        self.nav_buttons = {}
        nav_items = [
            ("home", "🏠 主页", 0),
            ("account", "👤 账户与联机", 9),
        ]
        for key, text, idx in nav_items:
            btn = QPushButton(text)
            btn.setCursor(QCursor(Qt.PointingHandCursor))
            btn.setMinimumHeight(42)
            btn.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Fixed)
            btn.setStyleSheet(self._nav_btn_style(False))
            btn.setProperty("full_text", text)
            btn.setProperty("icon_only", extract_emoji_icon(text) or text)
            btn.clicked.connect(lambda _, i=idx, k=key: self._switch_page(i, k))
            self.nav_buttons[key] = btn
            layout.addWidget(btn)

        layout.addSpacing(16)

        # ===== 心灵终结设置 =====
        mo_section = QLabel("🎮 心灵终结设置")
        mo_section.setProperty("role", "caption")
        mo_section.setStyleSheet("font-size: 10pt; font-weight: 600; padding: 4px 4px 2px; margin-top: 8px;")
        layout.addWidget(mo_section)
        self.sidebar_labels.append(mo_section)

        instance_btn = QPushButton("🎮 实例管理")
        instance_btn.setCursor(QCursor(Qt.PointingHandCursor))
        instance_btn.setMinimumHeight(42)
        instance_btn.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Fixed)
        instance_btn.setStyleSheet(self._nav_btn_style(False))
        instance_btn.setProperty("full_text", "🎮 实例管理")
        instance_btn.setProperty("icon_only", "🎮")
        instance_btn.clicked.connect(lambda: self._switch_page(2, "instance"))
        self.nav_buttons["instance"] = instance_btn
        layout.addWidget(instance_btn)

        package_btn = QPushButton("📦 包管理")
        package_btn.setCursor(QCursor(Qt.PointingHandCursor))
        package_btn.setMinimumHeight(42)
        package_btn.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Fixed)
        package_btn.setStyleSheet(self._nav_btn_style(False))
        package_btn.setProperty("full_text", "📦 包管理")
        package_btn.setProperty("icon_only", "📦")
        package_btn.clicked.connect(lambda: self._switch_page(1, "package"))
        self.nav_buttons["package"] = package_btn
        layout.addWidget(package_btn)

        layout.addSpacing(16)

        # ===== 笨蛋广场 =====
        silly_section = QLabel("🤡 笨蛋广场")
        silly_section.setProperty("role", "caption")
        silly_section.setStyleSheet("font-size: 10pt; font-weight: 600; padding: 4px 4px 2px; margin-top: 8px;")
        layout.addWidget(silly_section)
        self.sidebar_labels.append(silly_section)

        self.resource_dl_btn = self._make_sidebar_btn(
            "🎮 游戏资源下载", "secondary",
            lambda: self._switch_to_subpage(self.sub_resource_download, "od_resource"))
        layout.addWidget(self.resource_dl_btn)
        self.nav_buttons["od_resource"] = self.resource_dl_btn

        self.runtime_env_btn = self._make_sidebar_btn(
            "⚙️ 运行环境", "secondary",
            lambda: self._switch_to_subpage(self.sub_runtime_env, "od_runtime"))
        layout.addWidget(self.runtime_env_btn)
        self.nav_buttons["od_runtime"] = self.runtime_env_btn

        self.program_extend_btn = self._make_sidebar_btn(
            "🧩 程序DLC下载", "secondary",
            lambda: self._switch_to_subpage(self.sub_program_extend, "od_extend"))
        layout.addWidget(self.program_extend_btn)
        self.nav_buttons["od_extend"] = self.program_extend_btn

        # 上传资源按钮
        self.upload_res_btn = self._make_sidebar_btn(
            "📤 上传资源", "secondary",
            self._show_upload_dialog)
        layout.addWidget(self.upload_res_btn)

        layout.addSpacing(12)

        # ===== 程序DLC =====
        dlc_btn = QPushButton("🧩 程序DLC")
        dlc_btn.setCursor(QCursor(Qt.PointingHandCursor))
        dlc_btn.setMinimumHeight(42)
        dlc_btn.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Fixed)
        dlc_btn.setStyleSheet(self._nav_btn_style(False))
        dlc_btn.setProperty("full_text", "🧩 程序DLC")
        dlc_btn.setProperty("icon_only", "🧩")
        dlc_btn.clicked.connect(lambda: self._switch_page(10, "dlc"))
        self.nav_buttons["dlc"] = dlc_btn
        layout.addWidget(dlc_btn)

        layout.addSpacing(8)

        # ===== 设置 =====
        self.settings_btn = self._make_sidebar_btn("⚙️ 设置", "secondary",
            lambda: self._switch_page(3, "settings"))
        layout.addWidget(self.settings_btn)

        layout.addSpacing(8)

        self.tutorial_btn = self._make_sidebar_btn("📖 使用教程", "secondary", self._open_tutorial)
        layout.addWidget(self.tutorial_btn)
        self.feedback_btn = self._make_sidebar_btn("💬 用户反馈", "secondary", self._open_feedback)
        layout.addWidget(self.feedback_btn)

        layout.addSpacing(8)

        self.about_btn = self._make_sidebar_btn("ℹ️ 关于", "secondary",
            lambda: self._switch_page(4, "about"))
        layout.addWidget(self.about_btn)

        layout.addStretch()

        # 退出
        exit_btn = self._make_sidebar_btn("❌ 退出", "danger", self.close)
        layout.addWidget(exit_btn)

        return sidebar

    _nav_inactive_style_cache = None

    def _nav_btn_style(self, active: bool) -> str:
        if active:
            self.__class__._nav_inactive_style_cache = None  # invalidate on theme change signal via active
            # active 状态: 渐变背景(主题色方案)+ 纯白文字 + 左侧高亮条
            g = self._current_gradient()
            return f"""
                QPushButton {{
                    background: qlineargradient(x1:0, y1:0, x2:1, y2:0,
                        stop:0 {g['primary']}, stop:1 {g['secondary']});
                    color: #ffffff;
                    border: none;
                    border-left: 4px solid {g['accent_hover']};
                    border-radius: 8px;
                    padding: 10px 14px;
                    text-align: left;
                    font-weight: 600;
                }}
                QPushButton:hover {{
                    background: qlineargradient(x1:0, y1:0, x2:1, y2:0,
                        stop:0 {g['accent']}, stop:1 {g['accent_hover']});
                }}
            """
        if self.__class__._nav_inactive_style_cache is not None:
            return self.__class__._nav_inactive_style_cache
        style = f"""
            QPushButton {{
                background-color: transparent;
                color: {self.theme['text']};
                border: none;
                border-radius: 8px;
                padding: 10px 14px;
                text-align: left;
            }}
            QPushButton:hover {{
                background-color: {self.theme['surface_alt']};
            }}
            QPushButton:pressed {{
                background-color: {self.theme['border']};
            }}
        """
        self.__class__._nav_inactive_style_cache = style
        return style

    def _current_gradient(self) -> dict:
        """获取当前渐变主题(用户可切换的 6 套之一)"""
        return GRADIENT_THEMES.get(
            getattr(self, 'gradient_theme', DEFAULT_GRADIENT_THEME),
            GRADIENT_THEMES[DEFAULT_GRADIENT_THEME])

    def _apply_gradient_theme(self):
        """应用当前渐变主题(刷新所有使用渐变的 widget)"""
        g = self._current_gradient()
        # 1. 教程 Hero
        if hasattr(self, 'tutorial_hero'):
            self.tutorial_hero.setStyleSheet(self._tutorial_hero_style())
        # 3. 主页 Hero (如果有)
        if hasattr(self, 'home_hero'):
            self.home_hero.setStyleSheet(
                f"QFrame#home_hero {{ background: qlineargradient(x1:0, y1:0, x2:1, y2:0, "
                f"stop:0 {g['primary']}, stop:1 {g['secondary']}); border: none; }}"
                f"QFrame#home_hero QLabel {{ color: white; background: transparent; }}"
            )
        # 4. 包管理 Hero (如果有)
        if hasattr(self, 'package_hero'):
            self.package_hero.setStyleSheet(
                f"QFrame#package_hero {{ background: qlineargradient(x1:0, y1:0, x2:1, y2:0, "
                f"stop:0 {g['primary']}, stop:1 {g['secondary']}); border: none; }}"
                f"QFrame#package_hero QLabel {{ color: white; background: transparent; }}"
            )
        # 5. 侧边栏激活态(通过 _apply_theme 中的 _refresh_sidebar_btn_styles 间接刷新)

    def set_gradient_theme(self, name: str):
        """切换渐变主题(从设置页调用)"""
        if name not in GRADIENT_THEMES:
            return
        self.gradient_theme = name
        self.config["gradient_theme"] = name
        # 刷新渐变按钮 active 状态
        if hasattr(self, '_gradient_buttons'):
            for n, btn in self._gradient_buttons:
                g_data = GRADIENT_THEMES[n]
                btn.setStyleSheet(
                    self._gradient_btn_style(g_data, active=(n == name)))
                btn.setChecked(n == name)
        # 立即刷新所有渐变相关 widget
        self._apply_gradient_theme()
        # 刷新 nav 按钮 active 态
        if hasattr(self, '_apply_theme'):
            self._apply_theme()
        # 状态栏提示
        if hasattr(self, 'statusBar'):
            self.statusBar().showMessage(f"✅ 已切换到 {GRADIENT_THEMES[name]['name']}", 3000)
        # 更新设置页中的提示
        if hasattr(self, '_update_gradient_hint'):
            self._update_gradient_hint()
        # 持久化到磁盘
        self.save_config()

    def _gradient_btn_style(self, g: dict, active: bool = False) -> str:
        """渐变方案选择按钮的样式(实时预览)"""
        border = f"3px solid {g['accent']}" if active else "1px solid rgba(0,0,0,15)"
        return (
            f"QPushButton {{ "
            f"background: qlineargradient(x1:0, y1:0, x2:1, y2:0, "
            f"stop:0 {g['primary']}, stop:1 {g['secondary']}); "
            f"color: white; border: {border}; border-radius: 8px; "
            f"padding: 8px 12px; font-weight: 600; font-size: 10pt; "
            f"text-align: center; }} "
            f"QPushButton:hover {{ "
            f"background: qlineargradient(x1:0, y1:0, x2:1, y2:0, "
            f"stop:0 {g['accent']}, stop:1 {g['accent_hover']}); "
            f"border: 2px solid rgba(255,255,255,80); }}"
        )

    def _tcss(self, key: str, fallback: str = "") -> str:
        """主题颜色快捷访问 — 优先梯度方案, 再回到基础主题"""
        g = self._current_gradient()
        gmap = {
            "accent": g.get("accent", self.theme.get("accent", fallback)),
            "accent_hover": g.get("accent_hover", self.theme.get("accent_hover", fallback)),
            "primary": g.get("primary", self.theme.get("primary", fallback)),
            "secondary": g.get("secondary", self.theme.get("secondary", fallback)),
            "text_secondary": self.theme.get("text_secondary", "#888"),
            "text": self.theme.get("text", "#2c3e50"),
            "border": self.theme.get("border", "#ccc"),
            "surface_alt": self.theme.get("surface_alt", "#f1f3f5"),
            "surface": self.theme.get("surface", "#fff"),
            "bg": self.theme.get("bg", "#f8f9fa"),
            "success": self.theme.get("success", "#4caf50"),
            "error": self.theme.get("error", "#f44336"),
            "warning": self.theme.get("warning", "#f39c12"),
            "text_inverse": self.theme.get("text_inverse", "#fff"),
        }
        return gmap.get(key, fallback)

    def _make_sidebar_btn(self, text, role, slot):
        btn = FluentButton(text, role)
        btn.setMinimumHeight(36)
        btn.setProperty("full_text", text)
        btn.setProperty("icon_only", extract_emoji_icon(text) or text)
        if role == "danger":
            btn.setStyleSheet(f"""
                QPushButton {{
                    background-color: {self.theme['error']};
                    color: {self.theme['text_inverse']};
                    border: none;
                    border-radius: 6px;
                    padding: 8px 12px;
                    text-align: left;
                }}
                QPushButton:hover {{
                    background-color: {self._lighten(self.theme['error'])};
                }}
                QPushButton:pressed {{
                    background-color: {self._darken(self.theme['error'])};
                }}
            """)
        else:
            btn.setStyleSheet(f"""
                QPushButton {{
                    background-color: transparent;
                    color: {self.theme['text_secondary']};
                    border: none;
                    border-radius: 6px;
                    padding: 8px 12px;
                    text-align: left;
                }}
                QPushButton:hover {{
                    background-color: {self.theme['surface_alt']};
                    color: {self.theme['text']};
                }}
                QPushButton:pressed {{
                    background-color: {self.theme['border']};
                }}
            """)
        btn.clicked.connect(slot)
        return btn

    @staticmethod
    def _lighten(hex_color: str, amount: float = 0.15) -> str:
        """颜色加亮(返回 hex 字符串)"""
        try:
            c = QColor(hex_color)
            h, s, l, a = c.getHslF()
            l = min(1.0, (l or 0.5) + amount)
            c.setHslF(h, s, l, a)
            return c.name()
        except Exception:
            return hex_color

    @staticmethod
    def _darken(hex_color: str, amount: float = 0.15) -> str:
        """颜色变暗(返回 hex 字符串)"""
        try:
            c = QColor(hex_color)
            h, s, l, a = c.getHslF()
            l = max(0.0, (l or 0.5) - amount)
            c.setHslF(h, s, l, a)
            return c.name()
        except Exception:
            return hex_color

    def _switch_page(self, idx, key=None):
        """切换页面: 含淡入淡出过渡动画(防竞态 + 首屏不闪烁)

        :param idx: content_stack 中的目标索引
        :param key: 侧边栏导航 key(可选, 用于按钮高亮; 子页面切换可不传)
        """
        if idx < 0 or idx >= self.content_stack.count():
            return
        # 记录当前 active 页面 key(子页面切换时保持上一个 key 不变)
        if key is not None:
            self._active_page_key = key
        # 更新 nav 按钮高亮(用 _active_page_key)
        active = self._active_page_key if hasattr(self, '_active_page_key') else None
        for k, btn in self.nav_buttons.items():
            btn.setStyleSheet(self._nav_btn_style(k == active))
        # 已经在该页则直接返回
        if self.content_stack.currentIndex() == idx:
            return
        # 首屏或首次切换: 直接切,不动画(避免闪烁)
        if not hasattr(self, '_first_switch_done') or not self._first_switch_done:
            self.content_stack.setCurrentIndex(idx)
            self._first_switch_done = True
            return
        # 停止任何进行中的动画,避免竞态
        if hasattr(self, '_fade_anim') and self._fade_anim:
            for anim in self._fade_anim:
                try:
                    if anim and anim.state() == QAbstractAnimation.Running:
                        anim.stop()
                except Exception:
                    pass
        current_widget = self.content_stack.currentWidget()
        new_widget = self.content_stack.widget(idx)
        if current_widget is None or new_widget is None:
            self.content_stack.setCurrentIndex(idx)
            return
        # 给两个 widget 装上 opacity effect (仅一次)
        for w in (current_widget, new_widget):
            eff = w.graphicsEffect()
            if not isinstance(eff, QGraphicsOpacityEffect):
                eff = QGraphicsOpacityEffect(w)
                w.setGraphicsEffect(eff)
        # 新页初始透明度 0
        new_widget.graphicsEffect().setOpacity(0.0)

        fade_out = QPropertyAnimation(current_widget.graphicsEffect(), b"opacity", self)
        fade_out.setDuration(120)
        fade_out.setStartValue(1.0)
        fade_out.setEndValue(0.0)
        fade_out.setEasingCurve(QEasingCurve.OutCubic)

        fade_in = QPropertyAnimation(new_widget.graphicsEffect(), b"opacity", self)
        fade_in.setDuration(180)
        fade_in.setStartValue(0.0)
        fade_in.setEndValue(1.0)
        fade_in.setEasingCurve(QEasingCurve.OutCubic)

        def do_switch():
            self.content_stack.setCurrentIndex(idx)

        def on_fade_in_finished():
            self._fade_anim = None
            # 清理离屏渲染开销
            current_widget.setGraphicsEffect(None)
            new_widget.setGraphicsEffect(None)

        fade_out.finished.connect(do_switch)
        fade_out.finished.connect(fade_in.start)
        fade_in.finished.connect(on_fade_in_finished)
        fade_out.start()
        self._fade_anim = (fade_out, fade_in)

    def _switch_to_subpage(self, subpage_widget, key=None):
        """切换到子页面(记录当前主页索引,方便返回)。
        首次导航到 OneDrive 子页时触发懒加载。"""
        current_idx = self.content_stack.currentIndex()
        # 只在当前是主页面时记录(bound 安全)
        if current_idx in (0, 1, 2, 3, 4, 9, 10):
            self._last_main_index = current_idx
        idx = self.content_stack.indexOf(subpage_widget)
        if idx >= 0:
            self._switch_page(idx, key)
            # 懒加载 OneDrive 页面数据
            self._od_lazy_load_if_needed(subpage_widget)

    def _build_home_page(self) -> QWidget:
        """主页: 简洁风格 - 全屏背景 + 右下角启动游戏按钮
        设计原则:
          1) 主页仅展示背景图 + 一个"启动游戏"按钮
          2) 按钮固定在右下角
          3) 背景图自适应屏幕尺寸(Cover 模式)
          4) 主页不展示额外信息卡/快捷入口/更换背景按钮(简洁)
        保留 home_instance_label / home_instance_combo / home_grid 等属性,
        以兼容 _apply_theme / update_instance_combo 等其他模块对其的引用
        (设置时通过 hasattr 守护,实际不显示)。
        """
        # 主页 = 背景主页组件
        self.home_page = BackgroundHomePage()
        page = self.home_page
        # 连接信号: 启动按钮
        self.home_page.launch_requested.connect(self._launch_game)
        # 连接信号: 切换实例
        self.home_page.instance_switch_requested.connect(
            self._on_home_instance_switched
        )
        # 注: 主页不显示"更换背景"按钮,此功能仅在设置页可用

        # 兼容属性: 其他模块可能更新这些属性
        # 由于新主页不再显示实例卡,这里创建空 widget 作为占位
        self.home_instance_card = QFrame()  # 占位,无视觉显示
        self.home_instance_card.hide()
        self.home_instance_label = QLabel("")  # 占位
        self.home_instance_combo = QComboBox()  # 占位
        self.home_instance_combo.hide()
        self.home_grid = QGridLayout()  # 占位
        self._home_cards = []

        # 标记 home 已创建
        self._home_page = page
        return page

    def _on_home_instance_changed(self, idx):
        """主页切换实例: 同步主 instance_combo"""
        if idx < 0 or not hasattr(self, 'instance_combo'):
            return
        # 同步主页 combobox 到主 combobox
        if idx != self.instance_combo.currentIndex():
            self.instance_combo.setCurrentIndex(idx)
        # 触发主逻辑
        self._on_instance_changed(idx)

    def _on_home_instance_switched(self, instance):
        """主页启动按钮下拉列表中点击了某个实例 -> 切换到该实例
        切换后:
        1) 更新 instance_manager.current_instance
        2) 同步主页按钮显示
        3) 同步其他 UI(combobox, instance_info_label)
        4) 刷新包管理页签
        5) 保存到配置
        """
        if instance is None:
            return
        try:
            # 1. 切换实例
            ok = self.instance_manager.set_current_instance(instance.id)
            if not ok:
                QMessageBox.warning(self, "警告", f"无法切换到实例: {instance.name}")
                return
            # 2. 同步主页按钮显示
            if hasattr(self, 'home_page') and self.home_page is not None:
                self.home_page.set_current_instance(instance)
            # 3. 同步 combobox
            self.update_instance_combo()
            # 4. 刷新包管理页签
            if hasattr(self, '_refresh_all_package_tabs'):
                self._refresh_all_package_tabs()
            # 5. 状态反馈
            self.statusBar().showMessage(
                f"✅ 已切换到实例: {instance.name}", 5000
            )
        except Exception as e:
            QMessageBox.critical(self, "错误", f"切换实例失败: {e}")
            log_error("App", f"切换实例异常: {e}")

    def _build_package_page(self) -> QWidget:
        """包管理页: 实例选择卡片 + 7 个包类型 Tab"""
        page = QWidget()
        v = QVBoxLayout(page)
        v.setContentsMargins(16, 16, 16, 16)
        v.setSpacing(10)

        # 标题
        title = QLabel("📦 包管理")
        title.setProperty("role", "title")
        v.addWidget(title)

        # === 实例选择卡片(更醒目,避免用户漏选实例) ===
        self.pkg_instance_card = QFrame()
        self.pkg_instance_card.setObjectName("pkgInstanceCard")
        self.pkg_instance_card.setProperty("role", "card")
        p = self._tcss("primary"); s = self._tcss("secondary"); a = self._tcss("accent")
        self.pkg_instance_card.setStyleSheet(f"""
            QFrame#pkgInstanceCard {{
                background: qlineargradient(x1:0, y1:0, x2:1, y2:0,
                    stop:0 rgba({_hex_to_rgba(p, 0.18)}),
                    stop:1 rgba({_hex_to_rgba(s, 0.18)}));
                border: 1px solid rgba({_hex_to_rgba(a, 0.45)});
                border-radius: 10px;
                padding: 10px;
            }}
        """)
        card_layout = QHBoxLayout(self.pkg_instance_card)
        card_layout.setContentsMargins(14, 8, 14, 8)
        card_layout.setSpacing(10)

        # 左侧: 当前实例信息
        info_box = QVBoxLayout()
        info_box.setSpacing(2)
        self.pkg_instance_title = QLabel()
        self.pkg_instance_title.setProperty("role", "subtitle")
        self.pkg_instance_path = QLabel()
        self.pkg_instance_path.setProperty("role", "caption")
        self.pkg_instance_path.setWordWrap(True)
        info_box.addWidget(self.pkg_instance_title)
        info_box.addWidget(self.pkg_instance_path)
        card_layout.addLayout(info_box, 1)

        # 中间: 切换实例的 combobox
        switch_box = QHBoxLayout()
        switch_box.setSpacing(6)
        switch_label = QLabel("切换实例:")
        switch_label.setProperty("role", "caption")
        switch_box.addWidget(switch_label)
        self.instance_combo = QComboBox()
        self.instance_combo.setMinimumWidth(200)
        self.instance_combo.setMinimumHeight(32)
        self.instance_combo.currentIndexChanged.connect(self._on_instance_changed)
        switch_box.addWidget(self.instance_combo)
        card_layout.addLayout(switch_box)

        # 右侧: 管理实例 / 快速操作
        self.pkg_manage_btn = FluentButton("⚙️ 管理实例", "primary")
        self.pkg_manage_btn.setMinimumHeight(32)
        self.pkg_manage_btn.clicked.connect(self._open_instance_management)
        card_layout.addWidget(self.pkg_manage_btn)

        v.addWidget(self.pkg_instance_card)

        # 状态提示条(无实例时显示警告)
        self.pkg_warning_label = QLabel()
        self.pkg_warning_label.setProperty("role", "warning")
        self.pkg_warning_label.setWordWrap(True)
        self.pkg_warning_label.hide()
        v.addWidget(self.pkg_warning_label)

        # 提示条
        tip = QLabel(
            "💡 提示: 左侧列表显示可用包(双击直接安装);右侧显示当前实例已安装的包;顶部可切换实例。"
        )
        tip.setProperty("role", "caption")
        tip.setWordWrap(True)
        v.addWidget(tip)

        # 7 个包类型 Tab
        self.package_tab_widget = QTabWidget()
        for pkg_type, cfg in self.package_configs.items():
            tab = PackageManagerTab(self, pkg_type, cfg)
            tab.install_requested.connect(self._install_package)
            tab.uninstall_requested.connect(self._uninstall_package)
            tab.remove_requested.connect(self._remove_package)
            tab.import_requested.connect(self._import_package)
            # download_requested → OneDrive game_resources page
            tab.open_dir_requested.connect(self._open_package_dir)
            self.package_tabs[pkg_type] = tab
            self.package_tab_widget.addTab(tab, f"{ICONS.get(pkg_type, '📦')} {cfg['name']}包")
        v.addWidget(self.package_tab_widget, 1)

        # 首次构建后刷新实例卡片
        self._refresh_pkg_instance_card()
        return page

    def _refresh_pkg_instance_card(self):
        """刷新包管理页顶部的实例选择卡片。"""
        if not hasattr(self, 'pkg_instance_title'):
            return
        current = self.instance_manager.get_current_instance()
        ts = self._tcss("text_secondary")
        if current:
            path = current.path or ""
            short = path if len(path) <= 80 else path[:77] + "..."
            self.pkg_instance_title.setText(f"🎯 当前实例: {current.name}  ✅")
            self.pkg_instance_path.setText(f"📁 {short}")
            self.pkg_instance_path.setStyleSheet(f"color: {ts};")
            self.pkg_warning_label.hide()
        else:
            self.pkg_instance_title.setText("⚠️ 未选择实例")
            self.pkg_instance_title.setStyleSheet(f"color: {self._tcss('warning')}; font-weight: bold;")
            self.pkg_instance_path.setText("请先添加并选择一个游戏实例,然后才能安装/卸载包。")
            self.pkg_instance_path.setStyleSheet(f"color: {self._tcss('error')};")
            self.pkg_warning_label.setText(
                "❌ 没有可用的实例!点击 [⚙️ 管理实例] 添加,或在 [🎮 实例管理] 中添加。"
            )
            self.pkg_warning_label.show()

    def _build_instance_page(self) -> QWidget:
        """内嵌式实例管理页(完整功能,无弹窗)"""
        page = QWidget()
        v = QVBoxLayout(page)
        v.setContentsMargins(16, 16, 16, 16)
        v.setSpacing(10)

        # 标题行
        title_row = QHBoxLayout()
        title = QLabel("🎮 游戏实例管理")
        title.setProperty("role", "title")
        title_row.addWidget(title)
        title_row.addStretch()

        for label, slot in [
            ("➕ 添加", self._inline_add_instance),
            ("✏️ 重命名", self._inline_rename_instance),
            ("🗑️ 删除", self._inline_delete_instance),
            ("📤 导出", self._inline_export_instance),
            ("📋 预览导出配置", self._inline_preview_export_config),
            ("📥 导入", self._inline_import_instance),
            ("🔄 刷新", self._inline_refresh_instance_list),
        ]:
            btn = FluentButton(label, "secondary")
            btn.clicked.connect(slot)
            title_row.addWidget(btn)
        v.addLayout(title_row)

        # 列表
        self.inline_instance_list = QListWidget()
        # 双击: 设为当前实例(选中)
        self.inline_instance_list.itemDoubleClicked.connect(self._inline_select_instance)
        self.inline_instance_list.itemSelectionChanged.connect(self._inline_update_size)
        v.addWidget(self.inline_instance_list, 1)

        # 底部
        bottom = QHBoxLayout()
        self.inline_size_label = QLabel("")
        self.inline_size_label.setProperty("role", "caption")
        bottom.addWidget(self.inline_size_label)
        bottom.addStretch()

        # 备份与恢复
        backup_btn = FluentButton("💾 备份游戏", "secondary")
        backup_btn.clicked.connect(self._backup_game)
        bottom.addWidget(backup_btn)
        backup_orig_btn = FluentButton("🗂️ 备份原版", "secondary")
        backup_orig_btn.clicked.connect(self._backup_original_game)
        bottom.addWidget(backup_orig_btn)
        restore_btn = FluentButton("♻️ 恢复游戏", "secondary")
        restore_btn.clicked.connect(self._restore_game)
        bottom.addWidget(restore_btn)

        bottom.addSpacing(8)
        open_dir_btn = FluentButton("📁 打开目录", "secondary")
        open_dir_btn.clicked.connect(self._inline_open_dir)
        bottom.addWidget(open_dir_btn)
        v.addLayout(bottom)

        self._inline_refresh_instance_list()
        return page

    def _inline_refresh_instance_list(self):
        if not hasattr(self, 'inline_instance_list'):
            return
        self.inline_instance_list.clear()
        instances = self.instance_manager.get_instance_list()
        if not instances:
            it = QListWidgetItem("暂无游戏实例,请点击 ➕ 添加 新实例")
            it.setFlags(Qt.NoItemFlags)
            self.inline_instance_list.addItem(it)
            self.inline_size_label.setText("")
            return
        current = self.instance_manager.get_current_instance()
        for inst in instances:
            is_current = current and current.id == inst.id
            mark = "✅ " if is_current else "    "
            item = QListWidgetItem(f"{mark}{inst.name}  ({inst.path})")
            item.setData(Qt.UserRole, inst.id)
            self.inline_instance_list.addItem(item)

    def _inline_get_selected(self):
        item = self.inline_instance_list.currentItem()
        if not item:
            return None
        inst_id = item.data(Qt.UserRole)
        if not inst_id:
            return None
        return self.instance_manager.instances.get(inst_id)

    def _inline_update_size(self):
        inst = self._inline_get_selected()
        if inst:
            size = self.instance_manager.get_instance_size(inst.id)
            self.inline_size_label.setText(f"占用空间: {self.instance_manager.format_size(size)}")
        else:
            self.inline_size_label.setText("")

    def _inline_select_instance(self, item=None):
        """双击某个实例: 设为当前实例,并在所有 combobox 中同步。

        单击只会高亮/更新占用空间,不会切换;切换由双击触发,
        符合"双击 = 选完"的交互约定,避免误触。
        """
        inst = self._inline_get_selected()
        if not inst:
            return
        # 若已是当前实例,直接给出反馈
        current = self.instance_manager.get_current_instance()
        if current and current.id == inst.id:
            self.statusBar().showMessage(
                f"🎯 当前实例已是: {inst.name}", 3000
            )
            return
        # 统一切换流程(同步 combobox、刷新主页、刷新包管理页、刷新 ✅ 标记)
        self._on_instance_changed_external(inst.id)
        # 保持刚才那一行仍处于选中态
        for i in range(self.inline_instance_list.count()):
            it = self.inline_instance_list.item(i)
            if it and it.data(Qt.UserRole) == inst.id:
                self.inline_instance_list.setCurrentItem(it)
                break
        self.statusBar().showMessage(
            f"🎯 已选择当前实例: {inst.name}", 3000
        )

    def _inline_add_instance(self):
        dlg = AddInstanceDialog(self, self)
        if dlg.exec() == QDialog.Accepted:
            self._inline_refresh_instance_list()
            self._refresh_all_package_tabs()

    def _inline_rename_instance(self):
        """编辑实例(重命名 + 重新选择游戏路径)。"""
        inst = self._inline_get_selected()
        if not inst:
            QMessageBox.warning(self, "警告", "请先选择一个实例")
            return
        dlg = AddInstanceDialog(self, self, edit_instance=inst)
        if dlg.exec() == QDialog.Accepted:
            self._inline_refresh_instance_list()
            self.update_instance_combo()

    def _inline_delete_instance(self):
        inst = self._inline_get_selected()
        if not inst:
            QMessageBox.warning(self, "警告", "请先选择一个实例")
            return
        ret = QMessageBox.question(
            self, "确认删除",
            f"确定要删除实例 '{inst.name}' 吗?\n\n"
            f"注意:这将删除实例的所有数据,包括:\n"
            f"• 安装包\n• 备份文件\n• 配置文件\n\n"
            f"此操作不可撤销!",
            QMessageBox.Yes | QMessageBox.No, QMessageBox.No
        )
        if ret != QMessageBox.Yes:
            return
        success, message = self.instance_manager.remove_instance(inst.id)
        if success:
            QMessageBox.information(self, "成功", message)
            self._inline_refresh_instance_list()
            self.update_instance_combo()
        else:
            QMessageBox.critical(self, "错误", message)

    def _inline_preview_export_config(self):
        """设置页: 预览导出配置"""
        self._preview_export_config_inline()

    def _preview_export_config_inline(self):
        """预览当前实例的导出配置文件内容 (使用 inline 列表)"""
        inst = self._inline_get_selected()
        if not inst:
            QMessageBox.warning(self, "警告", "请先选择一个实例")
            return

        files_to_zip = []
        total_size = 0
        scan_errors = 0

        def _scan_dir(path):
            nonlocal scan_errors, total_size
            try:
                with os.scandir(path) as it:
                    for entry in it:
                        if entry.is_dir():
                            _scan_dir(entry.path)
                        elif entry.is_file():
                            try:
                                file_size = entry.stat().st_size
                            except OSError:
                                scan_errors += 1
                                continue
                            rel_path = os.path.relpath(entry.path, inst.path)
                            files_to_zip.append((entry.path, rel_path, file_size))
                            total_size += file_size
            except OSError:
                pass

        _scan_dir(inst.path)

        installed_packages = {}
        for package_type, packages in inst.installed_packages.items():
            if packages:
                installed_packages[package_type] = list(packages)

        export_info = {
            "name": inst.name,
            "original_path": inst.path,
            "export_date": datetime.now().isoformat(),
            "manager_version": "2.2",
            "game_files_count": len(files_to_zip),
            "total_size_bytes": total_size,
            "total_size_readable": _format_size(total_size),
            "instance_id": inst.id,
            "created_time": inst.created_time.isoformat(),
            "installed_packages": installed_packages,
        }
        self._show_preview_dialog(inst, export_info, files_to_zip, total_size, scan_errors, installed_packages)

    def _show_preview_dialog(self, inst, export_info, files_to_zip, total_size, scan_errors, installed_packages):
        """显示导出配置预览对话框 (公用)"""
        info_json = json.dumps(export_info, ensure_ascii=False, indent=2)

        preview_lines = [
            f"📋 导出配置文件预览",
            f"",
            f"实例名称: {inst.name}",
            f"实例 ID:   {inst.id}",
            f"游戏路径:  {inst.path}",
            f"创建时间:  {inst.created_time.isoformat()}",
            f"",
            f"──────────────────────────────",
            f"  扫描结果",
            f"──────────────────────────────",
            f"文件数量:  {len(files_to_zip)} 个",
            f"总大小:    {_format_size(total_size)}",
        ]
        if scan_errors:
            preview_lines.append(f"读取失败:  {scan_errors} 个文件(将被跳过)")
        if installed_packages:
            pkg_count = sum(len(v) for v in installed_packages.values())
            preview_lines.extend([
                f"",
                f"──────────────────────────────",
                f"  已安装程序包 ({pkg_count} 个)",
                f"──────────────────────────────",
            ])
            for pkg_type, pkgs in installed_packages.items():
                for pkg in pkgs:
                    preview_lines.append(f"  • [{pkg_type}] {pkg}")
        preview_lines.extend([
            f"",
            f"──────────────────────────────",
            f"  完整的 export_info.json",
            f"──────────────────────────────",
            info_json,
        ])
        preview_text = "\n".join(preview_lines)

        dlg = QDialog(self)
        dlg.setWindowTitle(f"导出配置预览 — {inst.name}")
        dlg.setMinimumSize(600, 500)
        dlv = QVBoxLayout(dlg)
        dlv.setContentsMargins(16, 16, 16, 16)
        dlv.setSpacing(12)
        header = QLabel(f"📋 {inst.name} — 导出配置预览")
        header.setStyleSheet("font-size: 13pt; font-weight: 700;")
        dlv.addWidget(header)
        text_edit = QTextEdit()
        text_edit.setReadOnly(True)
        font = QFont("Consolas", 10)
        font.setStyleHint(QFont.Monospace)
        text_edit.setFont(font)
        text_edit.setPlainText(preview_text)
        dlv.addWidget(text_edit, 1)
        btn_row = QHBoxLayout()
        btn_row.setSpacing(8)
        copy_btn = FluentButton("📋 复制 JSON", "secondary")
        copy_btn.clicked.connect(lambda: (QApplication.clipboard().setText(info_json),
                                           self.statusBar().showMessage("✅ export_info.json 已复制到剪贴板", 3000)))
        btn_row.addWidget(copy_btn)
        btn_row.addStretch()
        close_btn = FluentButton("关闭", "secondary")
        close_btn.clicked.connect(dlg.accept)
        btn_row.addWidget(close_btn)
        dlv.addLayout(btn_row)
        dlg.exec()

    def _inline_export_instance(self):
        inst = self._inline_get_selected()
        if not inst:
            QMessageBox.warning(self, "警告", "请先选择一个实例")
            return
        # 默认输出到实例的父目录
        default_dir = os.path.dirname(inst.path) if inst.path and os.path.isdir(inst.path) else ""
        default_path = os.path.join(default_dir, f"{inst.name}.zip") if default_dir else f"{inst.name}.zip"
        # 仅展示 zip / 7z 两种压缩格式(按用户要求)
        filt = "压缩文件 (*.zip *.7z);;ZIP 文件 (*.zip);;7Z 文件 (*.7z)"
        path, sel_filter = QFileDialog.getSaveFileName(
            self, "导出游戏实例", default_path, filt
        )
        if not path:
            return
        ext = os.path.splitext(path)[1].lower()
        if ext not in (".zip", ".7z"):
            if "7z" in (sel_filter or "").lower() and SEVENZIP_AVAILABLE:
                path = path + ".7z"
            else:
                path = path + ".zip"
        # 目标已存在则要求确认
        if os.path.exists(path):
            ret = QMessageBox.question(
                self, "目标已存在",
                f"文件已存在:\n{path}\n\n是否覆盖?",
                QMessageBox.Yes | QMessageBox.No, QMessageBox.No
            )
            if ret != QMessageBox.Yes:
                return
            try:
                os.remove(path)
            except Exception as e:
                QMessageBox.critical(self, "错误", f"无法删除已存在文件: {e}")
                return
        # 进度对话框
        progress = QProgressDialog("正在准备导出...", "取消", 0, 100, self)
        progress.setWindowTitle("导出实例")
        progress.setWindowModality(Qt.WindowModal)
        progress.setMinimumDuration(0)
        progress.setValue(0)
        progress.setLabelText(f"正在导出 {inst.name} ...")
        inst_id = inst.id
        self.statusBar().showMessage(f"正在导出实例: {inst.name}...")

        def progress_cb(cur, total, msg):
            if progress.wasCanceled():
                return
            progress.setValue(cur)
            progress.setLabelText(msg or f"导出中... {cur}%")
            QApplication.processEvents()

        # 用 QTimer 投递到主线程执行,避免工作线程直接操作 GUI 导致崩溃
        def do_export():
            try:
                success, message = self.instance_manager.export_instance(
                    inst_id, path, progress_cb,
                    compress_level="标准", preserve_metadata=False
                )
                progress.close()
                if progress.wasCanceled():
                    self.statusBar().showMessage("⚠️ 导出已取消", 5000)
                    return
                if success:
                    QMessageBox.information(self, "导出成功", message)
                    self.statusBar().showMessage(
                        f"✅ 实例已导出: {path}", 5000
                    )
                else:
                    QMessageBox.critical(self, "错误", message)
                    self.statusBar().showMessage(f"❌ 导出失败: {message}", 5000)
            except Exception as e:
                progress.close()
                QMessageBox.critical(self, "错误", f"导出失败: {e}")
                self.statusBar().showMessage(f"❌ 导出失败: {e}", 5000)
        QTimer.singleShot(50, do_export)

    def _inline_import_instance(self):
        path, _ = QFileDialog.getOpenFileName(
            self, "导入实例", "", "压缩文件 (*.zip *.7z *.rar)"
        )
        if not path:
            return
        progress = QProgressDialog("正在准备导入...", "取消", 0, 100, self)
        progress.setWindowTitle("导入实例")
        progress.setWindowModality(Qt.WindowModal)
        progress.setMinimumDuration(0)
        progress.setValue(0)
        self.statusBar().showMessage("正在导入实例...")

        def progress_cb(cur, total, msg):
            if progress.wasCanceled():
                return
            progress.setValue(cur)
            progress.setLabelText(msg or f"导入中... {cur}%")
            QApplication.processEvents()

        # 用 QTimer 投递到主线程执行,避免工作线程直接操作 GUI 导致崩溃
        def do_import():
            try:
                success, message, _ = self.instance_manager.import_instance(
                    path, progress_cb
                )
                progress.close()
                if progress.wasCanceled():
                    self.statusBar().showMessage("⚠️ 导入已取消", 5000)
                    return
                if success:
                    QMessageBox.information(self, "成功", message)
                    self._inline_refresh_instance_list()
                    self.update_instance_combo()
                    self.statusBar().showMessage(
                        f"✅ 实例导入成功: {message}", 5000
                    )
                else:
                    QMessageBox.critical(self, "错误", message)
                    self.statusBar().showMessage(f"❌ 导入失败: {message}", 5000)
            except Exception as e:
                progress.close()
                QMessageBox.critical(self, "错误", f"导入失败: {e}")
                self.statusBar().showMessage(f"❌ 导入失败: {e}", 5000)
        QTimer.singleShot(50, do_import)

    def _inline_open_dir(self):
        inst = self._inline_get_selected()
        if not inst:
            QMessageBox.warning(self, "警告", "请先选择一个实例")
            return
        success, message = self.instance_manager.open_instance_directory(inst.id)
        if not success:
            QMessageBox.information(self, "目录", message)

    def _build_settings_page(self) -> QWidget:
        """内嵌式设置页(完整功能,无弹窗)"""
        page = QWidget()
        outer = QVBoxLayout(page)
        outer.setContentsMargins(0, 0, 0, 0)
        outer.setSpacing(0)

        # 顶部标题
        title_bar = QWidget()
        title_layout = QHBoxLayout(title_bar)
        title_layout.setContentsMargins(16, 16, 16, 8)
        title = QLabel("⚙️ 设置")
        title.setProperty("role", "title")
        title_layout.addWidget(title)
        title_layout.addStretch()
        save_btn = FluentButton("💾 保存设置", "accent")
        save_btn.clicked.connect(self._inline_save_settings)
        title_layout.addWidget(save_btn)
        cancel_btn = FluentButton("↩️ 还原", "secondary")
        cancel_btn.clicked.connect(self._inline_load_settings)
        title_layout.addWidget(cancel_btn)
        outer.addWidget(title_bar)

        # 滚动区域(以兼容小屏)
        scroll = QScrollArea()
        scroll.setWidgetResizable(True)
        scroll.setFrameShape(QFrame.NoFrame)
        container = QWidget()
        cv = QVBoxLayout(container)
        cv.setContentsMargins(16, 8, 16, 16)
        cv.setSpacing(12)

        # === 主题设置 ===
        theme_card = QFrame()
        theme_card.setProperty("role", "card")
        tcv = QVBoxLayout(theme_card)
        tcv.setContentsMargins(16, 12, 16, 12)
        tcv.setSpacing(8)
        theme_title = QLabel("🎨 主题设置")
        theme_title.setProperty("role", "subtitle")
        tcv.addWidget(theme_title)
        row = QHBoxLayout()
        row.addWidget(QLabel("主题模式:"))
        self.inline_theme_combo = QComboBox()
        self.inline_theme_combo.addItems(["浅色模式", "深色模式", "跟随系统"])
        self.inline_theme_combo.setCurrentText(self.config.get("theme_mode", "跟随系统"))
        self.inline_theme_combo.currentTextChanged.connect(self._inline_preview_theme)
        row.addWidget(self.inline_theme_combo)
        row.addStretch()
        tcv.addLayout(row)
        self.inline_theme_status = QLabel("")
        self.inline_theme_status.setProperty("role", "caption")
        tcv.addWidget(self.inline_theme_status)
        self._inline_update_theme_status()
        cv.addWidget(theme_card)

        # === 主题色方案(渐变) ===
        gradient_card = QFrame()
        gradient_card.setProperty("role", "card")
        gcv = QVBoxLayout(gradient_card)
        gcv.setContentsMargins(16, 12, 16, 12)
        gcv.setSpacing(8)
        g_title = QLabel("🌈 主题色方案(渐变)")
        g_title.setProperty("role", "subtitle")
        gcv.addWidget(g_title)
        g_desc = QLabel("选择你喜欢的渐变配色,立即生效")
        g_desc.setProperty("role", "caption")
        gcv.addWidget(g_desc)
        # 11 个渐变方案按钮(4 列 × 3 行)
        self._gradient_buttons = []
        grid = QGridLayout()
        grid.setSpacing(10)
        names = list(GRADIENT_THEMES.keys())
        COLS = 4
        for i, name in enumerate(names):
            g_data = GRADIENT_THEMES[name]
            btn = QPushButton(g_data["name"])
            btn.setCursor(QCursor(Qt.PointingHandCursor))
            btn.setMinimumHeight(64)
            btn.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Fixed)
            btn.setCheckable(True)
            btn.setStyleSheet(self._gradient_btn_style(g_data, active=(name == self.gradient_theme)))
            btn.clicked.connect(lambda _, n=name: self.set_gradient_theme(n))
            grid.addWidget(btn, i // COLS, i % COLS)
            self._gradient_buttons.append((name, btn))
        gcv.addLayout(grid)
        # 当前选中提示
        self._gradient_hint = QLabel("")
        self._gradient_hint.setProperty("role", "caption")
        self._gradient_hint.setStyleSheet("font-size: 9pt; margin-top: 4px;")
        self._update_gradient_hint()
        gcv.addWidget(self._gradient_hint)
        cv.addWidget(gradient_card)

        # === 主页背景图片(自定义) ===
        bg_card = QFrame()
        bg_card.setProperty("role", "card")
        bcv = QVBoxLayout(bg_card)
        bcv.setContentsMargins(16, 12, 16, 12)
        bcv.setSpacing(8)
        bg_title = QLabel("🖼️ 主页背景图片")
        bg_title.setProperty("role", "subtitle")
        bcv.addWidget(bg_title)
        bg_desc = QLabel(
            "为主页选择一张自定义背景图片(支持 PNG / JPG / WEBP)。\n"
            "主页采用简洁设计:仅显示背景 + 右下角启动游戏按钮。"
        )
        bg_desc.setProperty("role", "caption")
        bg_desc.setWordWrap(True)
        bcv.addWidget(bg_desc)
        # 当前背景信息
        current_bg_path = self.config.get("home_background_path")
        if current_bg_path and os.path.isfile(current_bg_path):
            bg_name = os.path.basename(current_bg_path)
            bg_status_text = f"当前: {bg_name}"
        else:
            bg_status_text = "当前: 默认渐变背景"
        self.inline_bg_status_label = QLabel(bg_status_text)
        self.inline_bg_status_label.setProperty("role", "caption")
        bcv.addWidget(self.inline_bg_status_label)
        # 操作按钮
        bg_btn_row = QHBoxLayout()
        choose_bg_btn = FluentButton("📁 选择图片...", "accent")
        choose_bg_btn.clicked.connect(self._inline_choose_background)
        bg_btn_row.addWidget(choose_bg_btn)
        reset_bg_btn = FluentButton("♻️ 恢复默认", "secondary")
        reset_bg_btn.clicked.connect(self._inline_reset_background)
        bg_btn_row.addWidget(reset_bg_btn)
        open_bg_dir_btn = FluentButton("📂 打开背景目录", "secondary")
        open_bg_dir_btn.clicked.connect(self._open_background_dir)
        bg_btn_row.addWidget(open_bg_dir_btn)
        bg_btn_row.addStretch()
        bcv.addLayout(bg_btn_row)
        cv.addWidget(bg_card)

        # 启动参数功能已移除
        self.inline_auto_detect_check = QCheckBox("启动时自动检测游戏路径")
        self.inline_auto_detect_check.setChecked(self.config.get("auto_detect_path", True))
        cv.addWidget(self.inline_auto_detect_check)

        # ---- 程序日志卡片 ----
        log_card = self._build_log_viewer_card()
        cv.addWidget(log_card)

        cv.addStretch()
        scroll.setWidget(container)
        outer.addWidget(scroll, 1)
        return page

    def _inline_update_theme_status(self):
        if not hasattr(self, 'inline_theme_combo'):
            return
        mode = self.inline_theme_combo.currentText()
        status = {
            "浅色模式": "☀️ 使用浅色主题界面",
            "深色模式": "🌙 使用深色主题界面",
            "跟随系统": "🖥️ 根据系统设置自动切换主题",
        }
        self.inline_theme_status.setText(status.get(mode, ""))

    def _inline_preview_theme(self, mode):
        """实时预览主题(不立即保存到磁盘)"""
        self._inline_update_theme_status()
        self.apply_theme_mode(mode)

    def _inline_browse_music(self):
        # 背景音乐功能已移除,保留空方法以防旧引用
        pass

    def _update_gradient_hint(self):
        """更新渐变方案描述提示"""
        if not hasattr(self, 'gradient_theme') or not hasattr(self, '_gradient_hint'):
            return
        g = GRADIENT_THEMES.get(self.gradient_theme, {})
        if g:
            self._gradient_hint.setText(f"当前: {g['name']} — {g['desc']}")

    def _inline_load_settings(self):
        """从 config 重新填充 UI(还原操作)"""
        if not hasattr(self, 'inline_theme_combo'):
            return
        self.inline_theme_combo.setCurrentText(self.config.get("theme_mode", "跟随系统"))
        self.inline_auto_detect_check.setChecked(self.config.get("auto_detect_path", True))
        self._inline_update_theme_status()

    def _inline_save_settings(self):
        if not hasattr(self, 'inline_theme_combo'):
            return
        self.config["theme_mode"] = self.inline_theme_combo.currentText()
        self.config["auto_detect_path"] = self.inline_auto_detect_check.isChecked()
        self.save_config()
        self.apply_theme_mode(self.inline_theme_combo.currentText())
        self.statusBar().showMessage("✅ 设置已保存", 3000)
        QMessageBox.information(self, "成功", "设置已保存")

    # ---------- 设置页内嵌的背景管理 ----------
    def _inline_choose_background(self):
        """设置页 - 选择背景图片"""
        self._choose_home_background()
        # 刷新状态文本
        self._update_inline_bg_status()

    def _inline_reset_background(self):
        """设置页 - 恢复默认背景"""
        self._reset_home_background()
        self._update_inline_bg_status()

    def _update_inline_bg_status(self):
        """刷新设置页背景状态显示"""
        if not hasattr(self, 'inline_bg_status_label'):
            return
        current_bg_path = self.config.get("home_background_path")
        if current_bg_path and os.path.isfile(current_bg_path):
            bg_name = os.path.basename(current_bg_path)
            self.inline_bg_status_label.setText(f"当前: {bg_name}")
        else:
            self.inline_bg_status_label.setText("当前: 默认渐变背景")

    def _open_background_dir(self):
        """打开主页背景存储目录"""
        bg_dir = self._get_home_background_dir()
        try:
            if sys.platform == "win32":
                os.startfile(bg_dir)
            elif sys.platform == "darwin":
                subprocess.Popen(["open", bg_dir])
            else:
                subprocess.Popen(["xdg-open", bg_dir])
        except Exception as e:
            QMessageBox.information(self, "目录", bg_dir + f"\n(打开失败: {e})")

    # ---------- 程序日志查看器 ----------

    def _build_log_viewer_card(self) -> QFrame:
        """构建设置页中的日志查看器卡片"""
        card = QFrame()
        card.setProperty("role", "card")
        cv = QVBoxLayout(card)
        cv.setContentsMargins(16, 12, 16, 12)
        cv.setSpacing(8)

        title_row = QHBoxLayout()
        title = QLabel("📋 程序日志")
        title.setProperty("role", "subtitle")
        title_row.addWidget(title)
        title_row.addStretch()
        # 导出按钮
        export_txt_btn = FluentButton("⬇ TXT", "secondary")
        export_txt_btn.setFixedHeight(28)
        export_txt_btn.clicked.connect(self._log_export_txt)
        title_row.addWidget(export_txt_btn)
        export_csv_btn = FluentButton("⬇ CSV", "secondary")
        export_csv_btn.setFixedHeight(28)
        export_csv_btn.clicked.connect(self._log_export_csv)
        title_row.addWidget(export_csv_btn)
        # 清理按钮
        clean_btn = FluentButton("🧹 清理旧日志", "secondary")
        clean_btn.setFixedHeight(28)
        clean_btn.clicked.connect(self._log_cleanup)
        title_row.addWidget(clean_btn)
        cv.addLayout(title_row)

        # 筛选栏
        filter_row = QHBoxLayout()
        filter_row.setSpacing(8)
        filter_row.addWidget(QLabel("级别:"))
        self._log_level_combo = QComboBox()
        self._log_level_combo.addItems(["全部", "INFO", "WARN", "ERROR", "DEBUG"])
        self._log_level_combo.setMaximumWidth(80)
        self._log_level_combo.currentTextChanged.connect(self._log_refresh_view)
        filter_row.addWidget(self._log_level_combo)

        filter_row.addWidget(QLabel("搜索:"))
        self._log_keyword_edit = QLineEdit()
        self._log_keyword_edit.setPlaceholderText("关键词过滤...")
        self._log_keyword_edit.setClearButtonEnabled(True)
        self._log_keyword_edit.setMaximumWidth(180)
        self._log_keyword_edit.textChanged.connect(self._log_refresh_view)
        filter_row.addWidget(self._log_keyword_edit)

        filter_row.addWidget(QLabel("日期:"))
        self._log_date_from = QLineEdit()
        self._log_date_from.setPlaceholderText("起始 YYYY-MM-DD")
        self._log_date_from.setMaximumWidth(120)
        self._log_date_from.textChanged.connect(self._log_refresh_view)
        filter_row.addWidget(self._log_date_from)
        filter_row.addWidget(QLabel("→"))
        self._log_date_to = QLineEdit()
        self._log_date_to.setPlaceholderText("截止 YYYY-MM-DD")
        self._log_date_to.setMaximumWidth(120)
        self._log_date_to.textChanged.connect(self._log_refresh_view)
        filter_row.addWidget(self._log_date_to)
        filter_row.addStretch()

        # 刷新
        refresh_btn = FluentButton("🔄 刷新", "accent")
        refresh_btn.setFixedHeight(28)
        refresh_btn.clicked.connect(self._log_refresh_view)
        filter_row.addWidget(refresh_btn)
        cv.addLayout(filter_row)

        # 日志显示区
        self._log_viewer = QPlainTextEdit()
        self._log_viewer.setReadOnly(True)
        self._log_viewer.setMaximumBlockCount(5000)
        f = self._log_viewer.font()
        f.setFamily("Consolas, Courier New, Microsoft YaHei")
        f.setPointSize(9)
        self._log_viewer.setFont(f)
        self._log_viewer.setMinimumHeight(200)
        self._log_viewer.setStyleSheet(
            f"QPlainTextEdit {{ background-color: {self._tcss('surface')}; "
            f"color: {self._tcss('text')}; border: 1px solid {self._tcss('border')}; "
            f"border-radius: 6px; padding: 8px; font-size: 9pt; }}")
        cv.addWidget(self._log_viewer)

        # 状态栏
        self._log_status_label = QLabel("共 0 条日志")
        self._log_status_label.setProperty("role", "caption")
        cv.addWidget(self._log_status_label)

        # 初始加载
        self._log_refresh_view()
        return card

    def _log_refresh_view(self):
        """刷新日志显示"""
        level_text = self._log_level_combo.currentText()
        level_filter = "" if level_text == "全部" else level_text
        keyword = self._log_keyword_edit.text().strip()
        date_from = self._log_date_from.text().strip()
        date_to = self._log_date_to.text().strip()

        entries = get_logs(level_filter=level_filter, keyword=keyword,
                           date_from=date_from, date_to=date_to, limit=2000)

        # 渲染为纯文本(带颜色区分级别)
        import html
        lines = []
        for e in entries:
            msg = html.escape(e["message"])[:300]
            lines.append(f"[{e['timestamp']}] [{e['level']}] [{e['module']}] {msg}")
        self._log_viewer.setPlainText("\n".join(lines))
        self._log_status_label.setText(f"共 {len(entries)} 条日志 (显示最近 2000 条)")

    def _log_export_txt(self):
        """导出日志为 TXT"""
        entries = get_logs()
        if not entries:
            QMessageBox.information(self, "导出", "暂无日志可导出")
            return
        path, _ = QFileDialog.getSaveFileName(
            self, "导出日志 (TXT)", f"HMOL_log_{datetime.now().strftime('%Y%m%d_%H%M%S')}.txt",
            "文本文件 (*.txt)")
        if path:
            try:
                export_logs_txt(path, entries)
                QMessageBox.information(self, "导出成功", f"已导出 {len(entries)} 条日志到:\n{path}")
            except Exception as e:
                QMessageBox.critical(self, "导出失败", str(e))

    def _log_export_csv(self):
        """导出日志为 CSV"""
        entries = get_logs()
        if not entries:
            QMessageBox.information(self, "导出", "暂无日志可导出")
            return
        path, _ = QFileDialog.getSaveFileName(
            self, "导出日志 (CSV)", f"HMOL_log_{datetime.now().strftime('%Y%m%d_%H%M%S')}.csv",
            "CSV 文件 (*.csv)")
        if path:
            try:
                export_logs_csv(path, entries)
                QMessageBox.information(self, "导出成功", f"已导出 {len(entries)} 条日志到:\n{path}")
            except Exception as e:
                QMessageBox.critical(self, "导出失败", str(e))

    def _log_cleanup(self):
        """清理过期日志文件"""
        reply = QMessageBox.question(
            self, "清理旧日志",
            f"将删除 {_LOG_MAX_DAYS} 天前的日志文件。\n当前日志目录: {_LOG_FILE_PATH}\n\n确定继续?",
            QMessageBox.Yes | QMessageBox.No, QMessageBox.No)
        if reply == QMessageBox.Yes:
            cleanup_old_logs()
            QMessageBox.information(self, "清理完成", "旧日志已清理。")

    # ---------------------------------------------------------

    def _build_placeholder_page(self, title: str, message: str) -> QWidget:
        """构建占位页面（功能尚未实现时的提示页）"""
        page = QWidget()
        v = QVBoxLayout(page)
        v.setAlignment(Qt.AlignCenter)
        icon = QLabel(title.split(" ", 1)[0] if " " in title else title)
        icon.setStyleSheet("font-size: 48pt;")
        icon.setAlignment(Qt.AlignCenter)
        v.addWidget(icon)
        msg = QLabel(message)
        msg.setProperty("role", "caption")
        msg.setAlignment(Qt.AlignCenter)
        msg.setStyleSheet("font-size: 14pt; padding: 16px;")
        v.addWidget(msg)
        return page

    # ============== 笨蛋广场 OneDrive 文件浏览器 ==============

    def _build_onedrive_page(self, source_key: str, share_url: str) -> QWidget:
        """构建 OneDrive 文件浏览器页面。

        source_key: ONEDRIVE_SOURCES 中的键
        share_url: SharePoint 共享文件夹 URL
        """
        src = ONEDRIVE_SOURCES.get(source_key, {})
        page = QWidget()
        root = QVBoxLayout(page)
        root.setContentsMargins(0, 0, 0, 0)
        root.setSpacing(0)

        # ---- 顶部信息栏 ----
        hero = QFrame()
        hero.setStyleSheet(f"""
            QFrame {{
                background: qlineargradient(x1:0, y1:0, x2:1, y2:0,
                    stop:0 #0f4c75, stop:1 #1a5a8a);
            }}
        """)
        hero.setFixedHeight(100)
        hv = QVBoxLayout(hero)
        hv.setContentsMargins(20, 14, 20, 14)
        hv.setSpacing(2)
        hero_title = QLabel(f"{src.get('icon', '📁')} {src.get('name', source_key)}")
        hero_title.setStyleSheet(
            "font-size: 18pt; font-weight: 700; color: white; "
            "background: transparent; padding: 0;")
        hv.addWidget(hero_title)
        hero_desc = QLabel(src.get("description", "OneDrive 共享文件夹"))
        hero_desc.setStyleSheet(
            "font-size: 10pt; color: rgba(255,255,255,220); "
            "background: transparent; padding: 0;")
        hv.addWidget(hero_desc)
        root.addWidget(hero)

        # ---- 搜索栏 + 返回按钮 + 浏览器按钮 ----
        toolbar = QFrame()
        toolbar.setStyleSheet(f"QFrame {{ background-color: transparent; "
                              f"border-bottom: 1px solid {self._tcss('border')}; }}")
        tl = QHBoxLayout(toolbar)
        tl.setContentsMargins(12, 8, 12, 8)
        tl.setSpacing(8)

        ac = self._tcss("accent"); ac_rgba = _hex_to_rgba(ac, 0.12)
        # 返回上级目录按钮（初始隐藏）
        _back_btn = QPushButton("⬅ 返回上级")
        _back_btn.setCursor(QCursor(Qt.PointingHandCursor))
        _back_btn.setMaximumWidth(110)
        _back_btn.setStyleSheet(f"""
            QPushButton {{
                background-color: transparent; color: {ac};
                border: 1px solid {ac}; border-radius: 6px;
                padding: 6px 10px; font-size: 9pt; font-weight: 600;
            }}
            QPushButton:hover {{ background-color: rgba({ac_rgba}); }}
        """)
        _back_btn.setVisible(False)
        tl.addWidget(_back_btn)

        _search_input = QLineEdit()
        _search_input.setPlaceholderText("🔍 搜索文件... (按回车搜索)")
        _search_input.setClearButtonEnabled(True)
        _search_input.setMaximumWidth(300)
        _search_input.setStyleSheet(
            f"QLineEdit {{ padding: 6px 10px; border-radius: 6px; "
            f"border: 1px solid {self._tcss('border')}; font-size: 10pt; }}"
            f"QLineEdit:focus {{ border-color: {ac}; }}")
        tl.addWidget(_search_input)
        # 刷新按钮
        _refresh_btn = QPushButton("🔄 刷新")
        _refresh_btn.setCursor(QCursor(Qt.PointingHandCursor))
        _refresh_btn.setMaximumWidth(100)
        _refresh_btn.setStyleSheet(f"""
            QPushButton {{
                background-color: transparent; color: {ac};
                border: 1px solid {ac}; border-radius: 6px;
                padding: 6px 12px; font-size: 9pt; font-weight: 600;
            }}
            QPushButton:hover {{ background-color: rgba({ac_rgba}); }}
        """)
        _refresh_btn.clicked.connect(lambda checked=False: self._od_refresh_page(pg))
        tl.addWidget(_refresh_btn)

        tl.addStretch()

        # 用浏览器查看按钮
        _od_browser_btn = QPushButton("🌐 在浏览器中查看")
        _od_browser_btn.setCursor(QCursor(Qt.PointingHandCursor))
        _od_browser_btn.setMaximumWidth(160)
        _od_browser_btn.setStyleSheet(f"""
            QPushButton {{
                background-color: transparent; color: {ac};
                border: 1px solid {ac}; border-radius: 6px;
                padding: 6px 12px; font-size: 9pt;
            }}
            QPushButton:hover {{ background-color: rgba({ac_rgba}); }}
        """)
        _od_browser_btn.clicked.connect(lambda checked=False, u=share_url: webbrowser.open(u))
        tl.addWidget(_od_browser_btn)
        root.addWidget(toolbar)

        # ---- 状态文本 ----
        _status = QLabel("⏳ 正在加载文件列表...")
        _status.setAlignment(Qt.AlignCenter)
        _status.setStyleSheet(
            f"font-size: 11pt; padding: 20px; color: {self._tcss('text_secondary')};")
        root.addWidget(_status)

        # ---- 文件列表滚动区 ----
        _scroll = QScrollArea()
        _scroll.setWidgetResizable(True)
        _scroll.setFrameShape(QFrame.NoFrame)
        _list_container = QWidget()
        _list_layout = QVBoxLayout(_list_container)
        _list_layout.setContentsMargins(8, 4, 8, 8)
        _list_layout.setSpacing(2)
        # 表头
        header_row = self._make_od_header_row()
        _list_layout.addWidget(header_row)
        # 文件项容器
        _items_layout = QVBoxLayout()
        _items_layout.setSpacing(1)
        _list_layout.addLayout(_items_layout)
        _list_layout.addStretch()
        _scroll.setWidget(_list_container)
        root.addWidget(_scroll, 1)

        # ---- 底部状态栏 ----
        sa_rgba = _hex_to_rgba(self._tcss("surface_alt"), 0.12)
        bottom_bar = QFrame()
        bottom_bar.setFixedHeight(40)
        bottom_bar.setStyleSheet(
            f"QFrame {{ background-color: rgba({sa_rgba}); "
            f"border-top: 1px solid {self._tcss('border')}; }}")
        bl = QHBoxLayout(bottom_bar)
        bl.setContentsMargins(12, 4, 12, 4)
        bl.setSpacing(8)
        _count_label = QLabel("")
        _count_label.setStyleSheet(f"font-size: 9pt; color: {self._tcss('text_secondary')};")
        bl.addWidget(_count_label)
        bl.addStretch()
        _load_more_btn = QPushButton("📥 加载更多...")
        _load_more_btn.setCursor(QCursor(Qt.PointingHandCursor))
        _load_more_btn.setMaximumWidth(140)
        _load_more_btn.setStyleSheet(f"""
            QPushButton {{ background: transparent; border: 1px solid {self.theme['border']};
                color: {self.theme['text_secondary']};
                border-radius: 4px; padding: 4px 10px; font-size: 9pt; }}
            QPushButton:hover {{ background: {self.theme['surface_alt']}; }}
        """)
        _load_more_btn.clicked.connect(self._od_load_more)
        _load_more_btn.setVisible(False)
        bl.addWidget(_load_more_btn)
        root.addWidget(bottom_bar)

        # 存储状态 — 每个 source_key 独立持有自己的 widget 引用
        if not hasattr(self, '_onedrive_pages'):
            self._onedrive_pages = {}
        self._onedrive_pages[source_key] = {
            "share_url": share_url,
            "next_link": None,
            "current_search": "",
            "breadcrumb": [],  # [{name, folder_path}, ...] 导航栈（保存父级路径用于返回）
            "folder_path": "",  # 当前文件夹 server-relative 路径
            "back_btn": _back_btn,
            "status": _status,
            "count_label": _count_label,
            "load_more_btn": _load_more_btn,
            "items_layout": _items_layout,
            "search_input": _search_input,
        }

        # 搜索信号
        _search_input.returnPressed.connect(
            lambda sk=source_key: self._od_do_search(sk))
        # 返回按钮
        _back_btn.clicked.connect(lambda checked=False, sk=source_key: self._od_go_back(sk))

        # 不在此处预加载 — 由 _switch_to_subpage 触发懒加载
        return page

    def _make_od_header_row(self) -> QFrame:
        """文件列表表头"""
        row = QFrame()
        row.setStyleSheet(
            f"QFrame {{ background-color: rgba({_hex_to_rgba(self._tcss('surface_alt'), 0.08)}); "
            f"border-radius: 6px; }}")
        hl = QHBoxLayout(row)
        hl.setContentsMargins(12, 6, 24, 6)
        hl.setSpacing(8)
        hl.addWidget(QLabel("  文件名称"), 3)
        size_lbl = QLabel("大小")
        size_lbl.setAlignment(Qt.AlignRight)
        size_lbl.setFixedWidth(80)
        hl.addWidget(size_lbl)
        date_lbl = QLabel("修改日期")
        date_lbl.setFixedWidth(140)
        hl.addWidget(date_lbl)
        hl.addWidget(QLabel(""), 0)  # 操作按钮占位 (约 160px)
        return row

    def _make_od_item_row(self, item: dict) -> QFrame:
        """创建单个文件行"""
        ac = self._tcss("accent"); ah = self._tcss("accent_hover")
        ts = self._tcss("text_secondary"); bd = self._tcss("border")
        sa = _hex_to_rgba(self._tcss("surface_alt"), 0.08)
        row = QFrame()
        row.setCursor(QCursor(Qt.PointingHandCursor))
        row.setStyleSheet(f"""
            QFrame {{ background-color: transparent; border-radius: 4px; }}
            QFrame:hover {{ background-color: rgba({sa}); }}
        """)
        hl = QHBoxLayout(row)
        hl.setContentsMargins(12, 5, 12, 5)
        hl.setSpacing(8)

        is_folder = item.get("is_folder", False)

        # 图标 + 文件名
        name_widget = QWidget()
        nvl = QHBoxLayout(name_widget)
        nvl.setContentsMargins(0, 0, 0, 0)
        nvl.setSpacing(6)
        icon_label = QLabel(item.get("icon", "📄"))
        icon_label.setFixedWidth(24)
        icon_label.setAlignment(Qt.AlignCenter)
        nvl.addWidget(icon_label)
        name_label = QLabel(item["name"])
        name_label.setStyleSheet("font-size: 10pt;" + (
            f" color: {ac}; font-weight: 600;" if is_folder else ""))
        name_label.setWordWrap(False)
        nvl.addWidget(name_label, 1)
        hl.addWidget(name_widget, 3)

        # 大小
        size_txt = item.get("size_display", "")
        if is_folder:
            size_txt = "—"
        size_label = QLabel(size_txt)
        size_label.setAlignment(Qt.AlignRight | Qt.AlignVCenter)
        size_label.setFixedWidth(80)
        size_label.setStyleSheet(f"font-size: 9pt; color: {ts};")
        hl.addWidget(size_label)

        # 日期
        date_str = item.get("last_modified", "")
        if date_str:
            try:
                dt = datetime.fromisoformat(date_str.replace("Z", "+00:00"))
                date_str = dt.strftime("%Y-%m-%d %H:%M")
            except Exception:
                pass
        date_label = QLabel(date_str)
        date_label.setFixedWidth(140)
        date_label.setStyleSheet(f"font-size: 9pt; color: {ts};")
        hl.addWidget(date_label)

        # 操作按钮
        btn_widget = QWidget()
        bvl = QHBoxLayout(btn_widget)
        bvl.setContentsMargins(0, 0, 0, 0)
        bvl.setSpacing(4)

        if is_folder:
            open_btn = QPushButton("📂 打开")
            open_btn.setCursor(QCursor(Qt.PointingHandCursor))
            open_btn.setMaximumWidth(70)
            open_btn.setStyleSheet(f"""
                QPushButton {{
                    background-color: {ac}; color: white;
                    border: none; border-radius: 4px;
                    padding: 3px 8px; font-size: 9pt;
                }}
                QPushButton:hover {{ background-color: {ah}; }}
            """)
            open_btn.clicked.connect(
                lambda checked, i=item: self._od_open_item(i))
            bvl.addWidget(open_btn)
        else:
            dl_btn = QPushButton("⬇ 下载")
            dl_btn.setCursor(QCursor(Qt.PointingHandCursor))
            dl_btn.setMaximumWidth(70)
            dl_btn.setStyleSheet(f"""
                QPushButton {{
                    background-color: {ac}; color: white;
                    border: none; border-radius: 4px;
                    padding: 3px 8px; font-size: 9pt;
                }}
                QPushButton:hover {{ background-color: {ah}; }}
            """)
            dl_btn.clicked.connect(
                lambda checked, i=item: self._od_download_file(i))
            bvl.addWidget(dl_btn)

        # 属性按钮
        prop_btn = QPushButton("ℹ")
        prop_btn.setToolTip("查看属性")
        prop_btn.setCursor(QCursor(Qt.PointingHandCursor))
        prop_btn.setMaximumWidth(32)
        prop_btn.setStyleSheet(f"""
            QPushButton {{
                background-color: transparent; color: {ts};
                border: 1px solid {bd}; border-radius: 4px;
                padding: 3px 6px; font-size: 9pt;
            }}
            QPushButton:hover {{ background-color: rgba({sa}); }}
        """)
        prop_btn.clicked.connect(lambda checked, i=item: self._od_show_properties(i))
        bvl.addWidget(prop_btn)

        hl.addWidget(btn_widget)

        # 文件夹双击打开
        if is_folder:
            row.mouseDoubleClickEvent = lambda e, i=item: self._od_open_item(i)

        return row

    def _od_open_item(self, item: dict):
        """打开文件夹或下载文件"""
        if item.get("is_folder"):
            next_link = item.get("next_link", "")
            if not next_link:
                QMessageBox.warning(self, "无法打开",
                                    f"文件夹 {item['name']} 缺少导航链接")
                return
            sk = item.get("_source_key", "")
            self._od_navigate_to_folder(sk, next_link, item["name"])
        else:
            self._od_download_file(item)

    def _od_navigate_to_folder(self, source_key: str, next_link: str, folder_name: str):
        """导航到子文件夹"""
        pg = self._onedrive_pages.get(source_key)
        if pg is None or self.od_browser is None:
            return
        # 解析目标文件夹路径
        target_path = ""
        share_url = pg["share_url"]
        if next_link.startswith("sp_folder://"):
            parts = next_link.replace("sp_folder://", "", 1).split("||||", 1)
            if len(parts) == 2:
                share_url = parts[0].replace("%26", "&")
                target_path = parts[1].replace("%26", "&")
        # 将当前路径压入面包屑（用于返回）
        current_path = pg.get("folder_path", "")
        pg["breadcrumb"].append({
            "name": folder_name,
            "folder_path": current_path,
        })
        pg["folder_path"] = target_path
        pg["back_btn"].setVisible(True)
        # 加载子文件夹
        pg["status"].setText(f"⏳ 正在打开 {folder_name}...")
        pg["status"].setVisible(True)

        def do_load():
            result = self.od_browser.list_folder(
                share_url="", next_link=next_link)
            self._od_list_signal.emit(source_key, result)
        threading.Thread(target=do_load, daemon=True).start()

    def _od_go_back(self, source_key: str):
        """返回上级目录"""
        pg = self._onedrive_pages.get(source_key)
        if pg is None or self.od_browser is None:
            return
        if not pg["breadcrumb"]:
            return
        # 弹出上级目录信息
        parent = pg["breadcrumb"].pop()
        parent_path = parent.get("folder_path", "")
        pg["folder_path"] = parent_path
        if not pg["breadcrumb"]:
            pg["back_btn"].setVisible(False)
        pg["status"].setText("⏳ 正在返回上级...")
        pg["status"].setVisible(True)

        def do_load():
            if parent_path:
                # 构造 sp_folder:// 链接导航回上级
                safe_share = pg["share_url"].replace("&", "%26")
                safe_folder = parent_path.replace("&", "%26")
                back_link = f"sp_folder://{safe_share}||||{safe_folder}"
                result = self.od_browser.list_folder(
                    share_url="", next_link=back_link)
            else:
                # 返回根目录
                result = self.od_browser.list_folder(
                    share_url=pg["share_url"])
            self._od_list_signal.emit(source_key, result)
        threading.Thread(target=do_load, daemon=True).start()

    def _od_show_properties(self, item: dict):
        """显示文件/文件夹属性对话框"""
        is_folder = item.get("is_folder", False)
        type_name = "文件夹" if is_folder else "文件"
        ext = item.get("ext", "")
        if ext:
            type_name += f" ({ext.upper()})"
        elif is_folder:
            type_name = "文件夹"

        size_val = item.get("size", 0)
        if isinstance(size_val, str):
            try:
                size_val = int(size_val)
            except (ValueError, TypeError):
                size_val = 0
        size_display = _format_file_size(size_val) if size_val > 0 and not is_folder else ("—" if is_folder else "0 B")

        date_str = item.get("last_modified", "未知")
        if date_str and date_str != "未知":
            try:
                dt = datetime.fromisoformat(date_str.replace("Z", "+00:00"))
                date_str = dt.strftime("%Y-%m-%d %H:%M:%S")
            except Exception:
                pass

        lines = [
            f"📛 名称: {item.get('name', '未知')}",
            f"📁 类型: {type_name}",
            f"📏 大小: {size_display}",
            f"🕐 修改时间: {date_str}",
        ]
        web_url = item.get("web_url", "")
        if web_url:
            lines.append(f"🔗 路径: {web_url}")

        QMessageBox.information(self, f"属性 - {item.get('name', '')}", "\n".join(lines))

    def _od_lazy_load_if_needed(self, subpage_widget):
        """首次导航到 OneDrive 子页时触发数据加载，仅加载一次"""
        if not hasattr(self, '_onedrive_loaded') or not self.od_browser:
            return
        # 建立子页 widget → source_key 映射
        mapping = {
            id(self.sub_resource_download): "game_resources",
            id(self.sub_runtime_env): "runtime_env",
            id(self.sub_program_extend): "program_extend",
        }
        sk = mapping.get(id(subpage_widget))
        if sk is None or sk in self._onedrive_loaded:
            return
        self._onedrive_loaded.add(sk)
        # 延迟一帧加载，让页面切换动画先完成
        QTimer.singleShot(150, lambda: self._od_load_list(sk))

    # ---- OneDrive 网络请求方法 ----

    def _od_refresh_page(self, pg: dict):
        """刷新当前 OneDrive 页面的文件列表"""
        source_key = pg.get("source_key", "")
        if not source_key:
            return
        # 重置为根目录
        pg["breadcrumb"] = []
        self._od_load_list(source_key)

    def _od_load_list(self, source_key: str):
        """异步加载文件列表（新鲜加载，清除导航栈）"""
        pg = self._onedrive_pages.get(source_key)
        if pg is None:
            return
        if self.od_browser is None:
            pg["status"].setText(
                "❌ OneDrive 功能需要登录微软账号\n请重启程序完成登录")
            return
        # 清除面包屑导航栈（回到根目录）
        pg["breadcrumb"] = []
        pg["folder_path"] = ""
        pg["back_btn"].setVisible(False)

        def do_load():
            result = self.od_browser.list_folder(pg["share_url"])
            self._od_list_signal.emit(source_key, result)
        threading.Thread(target=do_load, daemon=True).start()

    def _od_on_list_loaded(self, source_key: str, result: dict):
        """文件列表加载完成回调(主线程)"""
        pg = self._onedrive_pages.get(source_key)
        if pg is None:
            return
        pg["status"].setVisible(not result.get("success"))
        if not result.get("success"):
            err = result.get("error", "未知错误")
            pg["status"].setText(f"❌ {err}")
            pg["count_label"].setText("加载失败")
            return

        items = result.get("items", [])
        pg["next_link"] = result.get("next_link")
        self._od_render_items(source_key, items, clear_existing=True)
        pg["back_btn"].setVisible(bool(pg.get("breadcrumb")))
        pg["count_label"].setText(
            f"共 {len(items)} 个文件{' (还有更多)' if pg['next_link'] else ''}")
        pg["load_more_btn"].setVisible(bool(pg["next_link"]))

    def _od_render_items(self, source_key: str, items: list, clear_existing: bool = False):
        """渲染文件列表"""
        pg = self._onedrive_pages.get(source_key)
        if pg is None:
            return
        items_layout = pg["items_layout"]
        if clear_existing:
            while items_layout.count():
                child = items_layout.takeAt(0)
                if child.widget():
                    child.widget().deleteLater()
        for item in items:
            item["_source_key"] = source_key
            row = self._make_od_item_row(item)
            items_layout.addWidget(row)

    def _od_load_more(self):
        """加载更多文件 — 从当前显示的页面推断 source_key"""
        # 查找当前活跃的页面
        active_key = None
        for sk, pg in self._onedrive_pages.items():
            if pg["load_more_btn"].isVisible():
                active_key = sk
                break
        if not active_key:
            # 回退到第一个有 next_link 的页面
            for sk, pg in self._onedrive_pages.items():
                if pg.get("next_link"):
                    active_key = sk
                    break
        if not active_key:
            return
        pg = self._onedrive_pages.get(active_key)
        if pg is None or not pg["next_link"] or not self.od_browser:
            return
        pg["load_more_btn"].setText("⏳ 加载中...")
        pg["load_more_btn"].setEnabled(False)
        QApplication.processEvents()

        def do_load():
            result = self.od_browser.list_folder(
                pg["share_url"], next_link=pg["next_link"])
            self._od_more_signal.emit(active_key, result)
        threading.Thread(target=do_load, daemon=True).start()

    def _od_on_more_loaded(self, source_key: str, result: dict):
        """加载更多回调"""
        pg = self._onedrive_pages.get(source_key)
        if pg is None:
            return
        pg["load_more_btn"].setText("📥 加载更多...")
        pg["load_more_btn"].setEnabled(True)
        if result.get("success"):
            items = result.get("items", [])
            pg["next_link"] = result.get("next_link")
            self._od_render_items(source_key, items)
            total = pg["items_layout"].count()
            pg["count_label"].setText(
                f"已加载 {total} 个文件{' (还有更多)' if pg['next_link'] else ''}")
            pg["load_more_btn"].setVisible(bool(pg["next_link"]))
        else:
            QMessageBox.warning(self, "加载失败",
                                f"无法加载更多文件: {result.get('error', '未知错误')}")

    def _od_do_search(self, source_key: str):
        """搜索文件"""
        pg = self._onedrive_pages.get(source_key)
        if pg is None:
            return
        query = pg["search_input"].text().strip()
        if not query:
            pg["status"].setText("⏳ 正在加载文件列表...")
            pg["status"].setVisible(True)
            self._od_load_list(source_key)
            return
        if not self.od_browser:
            return
        pg["status"].setText(f"⏳ 正在搜索 \"{query}\"...")
        pg["status"].setVisible(True)
        pg["current_search"] = query
        QApplication.processEvents()

        def do_search():
            result = self.od_browser.search_folder(pg["share_url"], query)
            self._od_search_signal.emit(source_key, result)
        threading.Thread(target=do_search, daemon=True).start()

    def _od_on_search_result(self, source_key: str, result: dict):
        """搜索结果回调"""
        pg = self._onedrive_pages.get(source_key)
        if pg is None:
            return
        pg["status"].setVisible(not result.get("success"))
        if not result.get("success"):
            pg["status"].setText(
                f"❌ 搜索失败: {result.get('error', '未知错误')}")
            pg["count_label"].setText("搜索失败")
            return
        items = result.get("items", [])
        pg["next_link"] = result.get("next_link")
        self._od_render_items(source_key, items, clear_existing=True)
        q = pg["current_search"]
        pg["status"].setVisible(False)
        pg["count_label"].setText(
            f"搜索 \"{q}\" 找到 {len(items)} 个结果")
        pg["load_more_btn"].setVisible(bool(pg["next_link"]))

    def _od_download_file(self, item: dict):
        """下载单个文件。若来源为 program_extend，强制保存到 DLC 文件夹并自动解压。"""
        if self.is_offline or self.od_browser is None:
            QMessageBox.warning(self, "无法下载",
                                "离线模式下无法下载文件,请重启程序并登录")
            return

        name = item.get("name", "unknown")
        url = item.get("download_url", "")
        if not url:
            QMessageBox.warning(self, "无法下载",
                                f"文件 {name} 没有可用下载链接")
            return

        sk = item.get("_source_key", "")
        is_dlc_download = (sk == "program_extend")

        if is_dlc_download:
            # 强制保存到 DLC 文件夹
            dlc_dir = os.path.join(self.base_path, "DLC")
            os.makedirs(dlc_dir, exist_ok=True)
            dest = os.path.join(dlc_dir, name)
            # 若文件已存在，询问
            if os.path.exists(dest):
                reply = QMessageBox.question(self, "文件已存在",
                    f"DLC 文件夹中已存在 {name}，是否覆盖?\n\n路径: {dest}",
                    QMessageBox.Yes | QMessageBox.No, QMessageBox.No)
                if reply != QMessageBox.Yes:
                    return
        else:
            default_name = name
            dest, _ = QFileDialog.getSaveFileName(
                self, f"下载 {name}",
                os.path.join(self.base_path, "downloads", default_name))
            if not dest:
                return

        # 确保目录存在
        os.makedirs(os.path.dirname(dest) if os.path.dirname(dest) else self.base_path,
                    exist_ok=True)

        # 下载进度对话框
        progress = QProgressDialog(
            f"正在下载 {name}...", "取消", 0, 100, self)
        title = "DLC 拓展包下载" if is_dlc_download else "OneDrive 下载"
        progress.setWindowTitle(title)
        progress.setWindowModality(Qt.WindowModal)
        progress.setMinimumDuration(0)
        progress.setValue(0)
        progress.show()
        QApplication.processEvents()

        _dl_state = {"last_time": time.time(), "last_bytes": 0}
        def on_dl(downloaded, total):
            if progress.wasCanceled():
                return
            now = time.time()
            elapsed = now - _dl_state["last_time"]
            if elapsed < 0.15 and total > 0:
                return
            _dl_state["last_time"] = now

            delta_bytes = downloaded - _dl_state["last_bytes"]
            delta_time = elapsed if elapsed > 0 else 0.001
            speed_mbs = (delta_bytes / delta_time) / (1024 * 1024)
            _dl_state["last_bytes"] = downloaded

            if total > 0:
                pct = int(downloaded / total * 100)
                progress.setValue(pct)
                dl_mb = downloaded / (1024 * 1024)
                total_mb = total / (1024 * 1024)
                progress.setLabelText(
                    f"下载中: {dl_mb:.1f} / {total_mb:.1f} MB  ({speed_mbs:.1f} MB/s)")
            else:
                progress.setValue(0)
                dl_mb = downloaded / (1024 * 1024)
                progress.setLabelText(f"下载中: {dl_mb:.1f} MB  ({speed_mbs:.1f} MB/s)")
            QApplication.processEvents()
            if progress.wasCanceled():
                return

        def do_dl():
            share_url = ONEDRIVE_SOURCES.get(sk, {}).get("url", "")
            success = self.od_browser.download_file(url, dest, on_dl, share_url)
            def finish_ui():
                progress.close()
                if success:
                    self.statusBar().showMessage(f"✅ {name} 下载完成 → {dest}", 8000)
                    if is_dlc_download:
                        # DLC 下载完成后自动解压
                        extracted = self._dlc_extract_after_download(dest, os.path.join(self.base_path, "DLC"))
                        if extracted:
                            self._dlc_refresh()  # 刷新 DLC 列表
                            # 提示用户重启程序
                            ret = QMessageBox.question(
                                self, "DLC 安装完成",
                                "DLC 拓展包已下载并安装完成，需要重启程序以加载新内容。\n\n是否立即重启？",
                                QMessageBox.Yes | QMessageBox.No, QMessageBox.Yes)
                            if ret == QMessageBox.Yes:
                                self._restart_application()
                    else:
                        QMessageBox.information(
                            self, "下载完成", f"{name}\n已保存到:\n{dest}")
                else:
                    if os.path.exists(dest):
                        try:
                            os.remove(dest)
                        except Exception:
                            pass
                    self.statusBar().showMessage(f"❌ {name} 下载失败", 5000)
                    QMessageBox.critical(
                        self, "下载失败", f"文件 {name} 下载失败,请检查网络连接后重试")
            QTimer.singleShot(0, finish_ui)

        threading.Thread(target=do_dl, daemon=True).start()

    # ============== DLC 自动化处理流程 ==============

    def _dlc_log(self, msg: str, level: str = "INFO"):
        """DLC 操作日志记录"""
        log_dir = self._dlc_folder_path()
        log_file = os.path.join(log_dir, "dlc_operations.log")
        timestamp = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        line = f"[{timestamp}] [{level}] {msg}"
        try:
            os.makedirs(log_dir, exist_ok=True)
            with open(log_file, "a", encoding="utf-8") as f:
                f.write(line + "\n")
        except Exception as e:
            log_error("DLC", f"无法写入日志: {e}")
        print(line)

    def _dlc_verify_archive(self, archive_path: str) -> tuple:
        """验证压缩包完整性。返回 (is_valid, file_count, error_msg)。
        验证失败不会阻止解压，仅作日志记录用途。"""
        name = os.path.basename(archive_path)
        lower = name.lower()
        file_count = 0
        try:
            if lower.endswith(".zip"):
                with zipfile.ZipFile(archive_path, "r") as zf:
                    bad = zf.testzip()
                    if bad:
                        return (False, 0, f"ZIP 文件损坏，问题文件: {bad}")
                    file_count = len(zf.namelist())
                return (True, file_count, "")
            elif lower.endswith(".7z"):
                if not SEVENZIP_AVAILABLE:
                    return (False, 0, "7z 支持库 (py7zr) 不可用")
                import py7zr
                with py7zr.SevenZipFile(archive_path, "r") as szf:
                    names = szf.getnames()
                    file_count = len(names)
                    # testzip 返回 None 表示全部正常，否则返回第一个损坏文件名
                    bad = szf.testzip() if hasattr(szf, 'testzip') else None
                    if bad:
                        return (False, file_count, f"7z 文件部分损坏: {bad}")
                return (True, file_count, "")
            elif lower.endswith(".rar"):
                if not RARFILE_AVAILABLE:
                    return (False, 0, "RAR 支持库 (rarfile) 不可用")
                with rarfile.RarFile(archive_path) as rf:
                    file_count = len(rf.namelist())
                return (True, file_count, "")
            else:
                return (False, 0, f"不支持的压缩格式: {name}")
        except Exception as e:
            # 验证异常不阻断流程，返回中立结果让解压继续尝试
            return (True, 0, "")

    def _dlc_extract_after_download(self, archive_path: str, dlc_dir: str = None, silent: bool = False) -> bool:
        """DLC 压缩包自动化处理流程: 验证→解压→验证→删除→日志。

        Args:
            archive_path: 压缩包路径
            dlc_dir: DLC 目标文件夹，默认取 self._dlc_folder_path()
            silent: 是否静默模式(不弹消息框，仅状态栏+日志)

        Returns:
            是否成功处理
        """
        if dlc_dir is None:
            dlc_dir = self._dlc_folder_path()

        name = os.path.basename(archive_path)
        lower = name.lower()

        self._dlc_log(f"========== 开始处理: {name} ==========")
        self._dlc_log(f"源文件: {archive_path}")
        self._dlc_log(f"目标目录: {dlc_dir}")

        # ===== 步骤1: 格式检查 =====
        if not (lower.endswith(".zip") or lower.endswith(".7z") or lower.endswith(".rar")):
            msg = f"{name} 不是压缩包格式，跳过自动处理"
            self._dlc_log(msg, "WARN")
            if not silent:
                QMessageBox.information(self, "处理完成",
                    f"✅ {name} 已保存到 DLC 文件夹\n\n提示: 文件不是压缩包，请手动配置 DLC.json。")
            return False

        # ===== 步骤2: 完整性验证(非阻塞) =====
        self._dlc_log("正在验证压缩包完整性...")
        self.statusBar().showMessage(f"🔍 正在验证 {name}...", 3000)
        QApplication.processEvents()

        is_valid, file_count, verify_error = self._dlc_verify_archive(archive_path)
        if not is_valid and verify_error:
            # 验证发现问题但继续解压（因为验证可能因库版本差异误报）
            self._dlc_log(f"验证警告: {verify_error}，将继续解压", "WARN")
        else:
            self._dlc_log(f"验证通过 — 包含 {file_count} 个文件")

        # ===== 步骤3: 确定解压目标目录 =====
        base_name = os.path.splitext(name)[0]
        if base_name.lower().endswith(".tar"):
            base_name = os.path.splitext(base_name)[0]
        extract_to = os.path.join(dlc_dir, base_name)

        # 目标已存在则添加后缀
        counter = 1
        orig_target = extract_to
        while os.path.exists(extract_to):
            extract_to = f"{orig_target}_{counter}"
            counter += 1
        if extract_to != orig_target:
            self._dlc_log(f"目标目录已存在，使用: {os.path.basename(extract_to)}")

        # ===== 步骤4: 解压 =====
        self._dlc_log(f"正在解压到: {extract_to}")
        self.statusBar().showMessage(f"📦 正在解压 {name}...", 0)
        QApplication.processEvents()

        try:
            os.makedirs(extract_to, exist_ok=True)

            if lower.endswith(".zip"):
                with zipfile.ZipFile(archive_path, "r") as zf:
                    zf.extractall(extract_to)
            elif lower.endswith(".7z"):
                if not SEVENZIP_AVAILABLE:
                    self._dlc_log("7z 支持库不可用", "ERROR")
                    if not silent:
                        QMessageBox.warning(self, "解压失败",
                            f"{name} 是 7z 格式，但 7z 支持库 (py7zr) 不可用。")
                    self.statusBar().showMessage(f"❌ {name} 解压失败: 7z 库不可用", 5000)
                    return False
                import py7zr
                with py7zr.SevenZipFile(archive_path, "r") as szf:
                    szf.extractall(extract_to)
            elif lower.endswith(".rar"):
                if not RARFILE_AVAILABLE:
                    self._dlc_log("RAR 支持库不可用", "ERROR")
                    if not silent:
                        QMessageBox.warning(self, "解压失败",
                            f"{name} 是 RAR 格式，但支持库 (rarfile) 不可用。")
                    self.statusBar().showMessage(f"❌ {name} 解压失败: RAR 库不可用", 5000)
                    return False
                with rarfile.RarFile(archive_path) as rf:
                    rf.extractall(extract_to)

            self._dlc_log("解压完成")
        except Exception as e:
            self._dlc_log(f"解压异常: {str(e)[:500]}", "ERROR")
            if not silent:
                QMessageBox.critical(self, "解压失败",
                    f"解压 {name} 时发生错误:\n\n{str(e)[:500]}")
            self.statusBar().showMessage(f"❌ {name} 解压失败", 5000)
            # 清理失败的解压目录
            import shutil
            try:
                if os.path.exists(extract_to):
                    shutil.rmtree(extract_to, ignore_errors=True)
            except Exception:
                pass
            return False

        # ===== 步骤5: 解压后验证 =====
        self._dlc_log("正在验证解压结果...")
        extracted_files = []
        for root, dirs, files in os.walk(extract_to):
            for f in files:
                extracted_files.append(os.path.join(root, f))

        has_dlc_json = any(os.path.basename(f) == "DLC.json" for f in extracted_files)
        actual_count = len(extracted_files)

        self._dlc_log(f"解压后文件数: {actual_count} (预期: {file_count})")
        self._dlc_log(f"DLC.json: {'已找到' if has_dlc_json else '未找到'}")

        if actual_count == 0:
            self._dlc_log("解压结果为空", "ERROR")
            if not silent:
                QMessageBox.warning(self, "解压异常",
                    f"{name} 解压后目录为空。\n压缩包已保留，请手动检查。")
            self.statusBar().showMessage(f"⚠️ {name} 解压结果为空", 5000)
            return False

        # ===== 步骤6: 删除原始压缩包 =====
        try:
            os.remove(archive_path)
            self._dlc_log(f"已删除原始压缩包: {name}")
        except Exception as e:
            self._dlc_log(f"删除压缩包失败: {str(e)[:200]}", "WARN")

        # ===== 步骤7: 输出结果 =====
        if not has_dlc_json:
            self._dlc_log("⚠️ 未找到 DLC.json，请手动创建配置文件", "WARN")

        self._dlc_log(f"========== 处理完成: {name} ✓ ==========")
        self.statusBar().showMessage(f"✅ {name} 处理完成", 5000)

        if not silent:
            msg = (f"✅ {name} 已解压到:\n{extract_to}\n\n"
                   f"解压结果: 成功 ({actual_count} 个文件)")
            if not has_dlc_json:
                msg += ("\n\n⚠️ 未在解压目录中找到 DLC.json 配置文件。\n"
                        "请确保解压后的文件夹直接包含 DLC.json（而非嵌套在子目录中）。")
            msg += ("\n\n提示: 程序会自动扫描 DLC 文件夹并加载项目。"
                    if has_dlc_json else
                    "\n\n提示: 可手动创建 DLC.json 配置文件后刷新列表。")
            QMessageBox.information(self, "解压完成", msg)

        return True

    def _dlc_scan_pending_archives(self):
        """启动时扫描 DLC 文件夹中的未处理压缩包并自动处理"""
        dlc_dir = self._dlc_folder_path()
        if not os.path.isdir(dlc_dir):
            return

        self._dlc_log("========== 启动扫描: 检查未处理压缩包 ==========")
        pending = []
        try:
            for entry in sorted(os.listdir(dlc_dir)):
                entry_path = os.path.join(dlc_dir, entry)
                if os.path.isfile(entry_path):
                    lower = entry.lower()
                    if lower.endswith((".zip", ".7z", ".rar")):
                        pending.append(entry_path)
        except Exception as e:
            self._dlc_log(f"扫描目录失败: {str(e)[:200]}", "ERROR")
            return

        if not pending:
            self._dlc_log("未发现待处理的压缩包")
            return

        self._dlc_log(f"发现 {len(pending)} 个待处理压缩包")
        for i, archive_path in enumerate(pending, 1):
            name = os.path.basename(archive_path)
            self._dlc_log(f"[{i}/{len(pending)}] 自动处理: {name}")
            self._dlc_extract_after_download(archive_path, dlc_dir, silent=True)

        self._dlc_log(f"========== 启动扫描完成: 处理了 {len(pending)} 个文件 ==========")

    def _restart_application(self):
        """重启应用程序"""
        try:
            subprocess.Popen([sys.executable] + sys.argv)
        except Exception as e:
            QMessageBox.critical(self, "错误", f"重启失败: {e}\n请手动重启程序。")
            return
        QApplication.quit()

    # ============== 程序DLC 管理 ==============

    def _build_dlc_page(self) -> QWidget:
        """构建独立的程序DLC管理页面（含左侧目录导航）"""
        page = QWidget()
        outer = QVBoxLayout(page)
        outer.setContentsMargins(0, 0, 0, 0)
        outer.setSpacing(0)

        # 顶部标题栏
        title_bar = QWidget()
        title_layout = QHBoxLayout(title_bar)
        title_layout.setContentsMargins(16, 16, 16, 8)
        title = QLabel("🧩 程序DLC")
        title.setProperty("role", "title")
        title_layout.addWidget(title)
        title_layout.addStretch()
        refresh_btn = QPushButton("🔄 刷新列表")
        refresh_btn.setCursor(QCursor(Qt.PointingHandCursor))
        refresh_btn.setMinimumHeight(32)
        refresh_btn.setStyleSheet(self._dlc_btn_style())
        refresh_btn.clicked.connect(self._dlc_refresh)
        title_layout.addWidget(refresh_btn)
        open_folder_btn = QPushButton("📂 打开 DLC 文件夹")
        open_folder_btn.setCursor(QCursor(Qt.PointingHandCursor))
        open_folder_btn.setMinimumHeight(32)
        open_folder_btn.setStyleSheet(self._dlc_btn_style())
        open_folder_btn.clicked.connect(self._dlc_open_folder)
        title_layout.addWidget(open_folder_btn)
        spec_btn = FluentButton("📋 查看 DLC.json 规范", "secondary")
        spec_btn.clicked.connect(self._show_dlc_json_spec)
        title_layout.addWidget(spec_btn)
        outer.addWidget(title_bar)

        # 描述文本
        desc = QLabel("将含 DLC.json 的文件夹放入程序根目录下 DLC 文件夹中，自动加载。")
        desc.setStyleSheet(f"font-size: 9.5pt; color: {self._tcss('text_secondary')}; padding: 0 16px 4px;")
        desc.setWordWrap(True)
        outer.addWidget(desc)

        # 主内容区：左右分栏
        splitter = QSplitter(Qt.Horizontal)

        # ===== 左侧目录导航 =====
        left_panel = QFrame()
        left_panel.setMinimumWidth(160)
        left_panel.setMaximumWidth(280)
        left_panel.setStyleSheet(f"QFrame {{ border-right: 1px solid {self._tcss('border')}; }}")
        lv = QVBoxLayout(left_panel)
        lv.setContentsMargins(8, 8, 8, 8)
        lv.setSpacing(6)

        toc_title = QLabel("📑 目录")
        toc_title.setStyleSheet("font-size: 11pt; font-weight: 700; padding: 4px 8px;")
        lv.addWidget(toc_title)

        dlc_ac = self._tcss("accent"); dlc_sa = _hex_to_rgba(self._tcss("surface_alt"), 0.08)
        dlc_ac_rgba = _hex_to_rgba(self._tcss("accent"), 0.15)
        self._dlc_toc_list = QListWidget()
        self._dlc_toc_list.setFrameShape(QFrame.NoFrame)
        self._dlc_toc_list.setStyleSheet(f"""
            QListWidget {{ background-color: transparent; }}
            QListWidget::item {{ padding: 8px 10px; border-radius: 6px; font-size: 10pt; }}
            QListWidget::item:hover {{ background-color: rgba({dlc_sa}); }}
            QListWidget::item:selected {{ background-color: rgba({dlc_ac_rgba}); color: {dlc_ac}; font-weight: 600; }}
        """)
        self._dlc_toc_list.currentRowChanged.connect(self._dlc_toc_navigate)
        lv.addWidget(self._dlc_toc_list, 1)
        splitter.addWidget(left_panel)

        # ===== 右侧 DLC 内容区 =====
        right_panel = QFrame()
        rv = QVBoxLayout(right_panel)
        rv.setContentsMargins(12, 8, 12, 8)
        rv.setSpacing(0)

        # DLC 列表滚动区
        self._dlc_page_scroll = QScrollArea()
        self._dlc_page_scroll.setWidgetResizable(True)
        self._dlc_page_scroll.setFrameShape(QFrame.NoFrame)
        self._dlc_page_scroll.setStyleSheet("QScrollArea { background-color: transparent; }")

        self._dlc_page_list_container = QWidget()
        self._dlc_page_list_layout = QVBoxLayout(self._dlc_page_list_container)
        self._dlc_page_list_layout.setSpacing(10)
        self._dlc_page_list_layout.setContentsMargins(4, 4, 4, 4)
        self._dlc_page_list_layout.setAlignment(Qt.AlignTop)
        self._dlc_page_scroll.setWidget(self._dlc_page_list_container)
        rv.addWidget(self._dlc_page_scroll, 1)

        # 状态标签
        self._dlc_page_status_label = QLabel("")
        self._dlc_page_status_label.setStyleSheet("font-size: 9pt; color: #888; padding: 6px 12px;")
        rv.addWidget(self._dlc_page_status_label)

        splitter.addWidget(right_panel)
        splitter.setSizes([220, 500])
        outer.addWidget(splitter, 1)

        # 保持与旧代码兼容：_dlc_list 和 _dlc_list_container 指向页面控件
        self._dlc_list = self._dlc_page_list_layout
        self._dlc_list_container = self._dlc_page_list_container
        self._dlc_status_label = self._dlc_page_status_label
        self._dlc_items = []

        # 启动文件监控
        self._dlc_watcher = QFileSystemWatcher(self)
        self._dlc_watcher.directoryChanged.connect(self._dlc_on_folder_changed)
        self._dlc_setup_watcher()

        # 初始扫描
        QTimer.singleShot(100, self._dlc_refresh)

        return page

    def _dlc_toc_navigate(self, row: int):
        """目录导航：点击目录项滚动到对应 DLC 卡片位置"""
        if row < 0 or row >= len(self._dlc_items):
            return
        # 计算目标 widget 在 scroll area 中的位置
        target_widget = self._dlc_page_list_layout.itemAt(row)
        if target_widget and target_widget.widget():
            self._dlc_page_scroll.ensureWidgetVisible(target_widget.widget(), 0, 50)

    def _build_dlc_settings_card(self) -> QFrame:
        """构建设置页内的 DLC 程序拓展卡片"""
        t = self.theme
        dark = self.config.get("theme_mode", "跟随系统") == "深色模式"
        fc = t.get("text", "#e0e0e0" if dark else "#333")
        bd = t.get("border", "#555" if dark else "#e0e0e0")

        card = QFrame()
        card.setProperty("role", "card")
        cv = QVBoxLayout(card)
        cv.setContentsMargins(16, 12, 16, 12)
        cv.setSpacing(8)

        # 标题行
        header_row = QHBoxLayout()
        title = QLabel("🧩 程序拓展（DLC）")
        title.setStyleSheet(f"font-size: 12pt; font-weight: 700; color: {fc};")
        header_row.addWidget(title)
        header_row.addStretch()

        # DLC.json 规范按钮
        spec_btn = FluentButton("📋 查看 DLC.json 规范", "secondary")
        spec_btn.clicked.connect(self._show_dlc_json_spec)
        header_row.addWidget(spec_btn)
        cv.addLayout(header_row)

        desc = QLabel("将含 DLC.json 的文件夹放入程序根目录下 DLC 文件夹中，自动加载。")
        fg = t.get("text_secondary", "#aaa" if dark else "#888")
        desc.setStyleSheet(f"font-size: 9.5pt; color: {fg};")
        desc.setWordWrap(True)
        cv.addWidget(desc)

        # 工具栏
        toolbar = QHBoxLayout()
        toolbar.setSpacing(8)

        refresh_btn = QPushButton("🔄 刷新列表")
        refresh_btn.setCursor(QCursor(Qt.PointingHandCursor))
        refresh_btn.setMinimumHeight(32)
        refresh_btn.setStyleSheet(self._dlc_btn_style())
        refresh_btn.clicked.connect(self._dlc_refresh)
        toolbar.addWidget(refresh_btn)

        open_folder_btn = QPushButton("📂 打开 DLC 文件夹")
        open_folder_btn.setCursor(QCursor(Qt.PointingHandCursor))
        open_folder_btn.setMinimumHeight(32)
        open_folder_btn.setStyleSheet(self._dlc_btn_style())
        open_folder_btn.clicked.connect(self._dlc_open_folder)
        toolbar.addWidget(open_folder_btn)

        toolbar.addStretch()
        cv.addLayout(toolbar)

        # DLC 列表 (可滚动，限高)
        self._dlc_scroll = QScrollArea()
        self._dlc_scroll.setWidgetResizable(True)
        self._dlc_scroll.setFrameShape(QFrame.NoFrame)
        self._dlc_scroll.setMaximumHeight(320)
        self._dlc_scroll.setStyleSheet(
            "QScrollArea { background-color: transparent; "
            f"border-radius: 8px; border: 1px solid {bd}; }}")
        self._dlc_list_container = QWidget()
        self._dlc_list = QVBoxLayout(self._dlc_list_container)
        self._dlc_list.setSpacing(8)
        self._dlc_list.setContentsMargins(6, 6, 6, 6)
        self._dlc_list.setAlignment(Qt.AlignTop)
        self._dlc_scroll.setWidget(self._dlc_list_container)
        cv.addWidget(self._dlc_scroll)

        # 状态标签
        self._dlc_status_label = QLabel("")
        self._dlc_status_label.setStyleSheet(f"font-size: 9pt; color: {fg};")
        cv.addWidget(self._dlc_status_label)

        # 启动文件监控
        self._dlc_items = []  # DLC 项目列表
        self._dlc_watcher = QFileSystemWatcher(self)
        self._dlc_watcher.directoryChanged.connect(self._dlc_on_folder_changed)
        self._dlc_setup_watcher()

        # 初始扫描
        QTimer.singleShot(100, self._dlc_refresh)
        return card

    def _show_dlc_json_spec(self):
        """显示 DLC.json 规范说明"""
        spec = (
            "📋 DLC.json 配置文件规范\n\n"
            "每个 DLC 文件夹根目录下必须包含一个 DLC.json 文件，\n"
            "编码格式为 UTF-8，内容为标准 JSON 对象格式。\n\n"
            "──────────────────────────\n"
            "  必填字段\n"
            "──────────────────────────\n\n"
            '  "name"        :  DLC 名称（字符串）\n'
            '  "version"     :  DLC 版本号（字符串，如 "1.0.0"）\n'
            '  "description" :  DLC 介绍说明（字符串）\n'
            '  "author"      :  DLC 作者名称（字符串）\n'
            '  "contact"     :  作者联系方式（字符串，QQ/邮箱/网站）\n'
            '  "main"        :  主程序路径（字符串，相对于 DLC 文件夹的 .exe 路径）\n\n'
            "──────────────────────────\n"
            "  完整示例\n"
            "──────────────────────────\n\n"
            "  {\n"
            '      "name": "示例DLC",\n'
            '      "version": "1.0.0",\n'
            '      "description": "这是一个示例DLC，提供额外游戏功能。",\n'
            '      "author": "作者名",\n'
            '      "contact": "qq:123456789 或 email@example.com",\n'
            '      "main": "bin/game.exe"\n'
            "  }\n\n"
            "──────────────────────────\n"
            "  注意事项\n"
            "──────────────────────────\n\n"
            "  • 文件必须是标准 JSON 格式（不支持注释）\n"
            "  • 编码必须为 UTF-8\n"
            "  • main 路径相对于 DLC 文件夹根目录\n"
            "  • 所有字段缺一不可\n"
            "  • 字段值不能为空字符串"
        )
        msg = QMessageBox(self)
        msg.setWindowTitle("DLC.json 规范")
        msg.setText(spec)
        msg.setIcon(QMessageBox.Information)
        msg.setStandardButtons(QMessageBox.Ok)
        # 设置固定宽度使文本更易读
        msg.setStyleSheet("QLabel { font-family: 'Consolas', 'Courier New', monospace; "
                         "font-size: 11pt; min-width: 520px; }")
        msg.exec()


    def _dlc_btn_style(self) -> str:
        dark = self.config.get("theme_mode", "跟随系统") == "深色模式"
        bg = "#3a3a48" if dark else "#e8e8ec"
        fc = "#e0e0e0" if dark else "#333"
        hover_bg = "#4a4a58" if dark else "#ddd"
        return (
            f"QPushButton {{ background-color: {bg}; color: {fc}; "
            f"border: 1px solid #ccc; border-radius: 6px; "
            f"padding: 6px 14px; font-size: 10pt; }} "
            f"QPushButton:hover {{ background-color: {hover_bg}; }}"
        )

    def _dlc_folder_path(self) -> str:
        return os.path.join(self.base_path, "DLC")

    def _dlc_setup_watcher(self):
        """设置 DLC 文件夹监控"""
        dlc_dir = self._dlc_folder_path()
        try:
            os.makedirs(dlc_dir, exist_ok=True)
            # 移除旧路径，添加新路径
            dirs = self._dlc_watcher.directories()
            if dirs:
                self._dlc_watcher.removePaths(dirs)
            self._dlc_watcher.addPath(dlc_dir)
        except Exception as e:
            log_error("DLC", f"设置文件监控失败: {e}")

    def _dlc_on_folder_changed(self, path: str):
        """DLC 文件夹内容变化时触发"""
        QTimer.singleShot(500, self._dlc_refresh)  # 防抖

    def _dlc_refresh(self):
        """扫描并刷新 DLC 列表"""
        # 清空列表
        while self._dlc_list.count():
            item = self._dlc_list.takeAt(0)
            if item.widget():
                item.widget().deleteLater()

        # 清空目录导航
        if hasattr(self, '_dlc_toc_list') and self._dlc_toc_list is not None:
            self._dlc_toc_list.blockSignals(True)
            self._dlc_toc_list.clear()

        dlc_dir = self._dlc_folder_path()
        if not os.path.isdir(dlc_dir):
            try:
                os.makedirs(dlc_dir, exist_ok=True)
            except Exception as e:
                self._dlc_status_label.setText(f"⚠️ 无法创建 DLC 文件夹: {e}")
                return

        self._dlc_items = self._scan_dlc_folder(dlc_dir)

        if not self._dlc_items:
            empty_label = QLabel("📭 暂无 DLC 项目\n\n将包含 DLC.json 的文件夹放入 DLC 目录即可自动加载(下载DLC包后须重启程序)")
            empty_label.setAlignment(Qt.AlignCenter)
            empty_label.setStyleSheet("color: #999; font-size: 11pt; padding: 40px;")
            self._dlc_list.addWidget(empty_label)
            self._dlc_status_label.setText("未检测到 DLC 项目")
        else:
            ok_count = sum(1 for d in self._dlc_items if d.get("status") == "ok")
            err_count = len(self._dlc_items) - ok_count
            for dlc in self._dlc_items:
                self._dlc_list.addWidget(self._make_dlc_card(dlc))
            status_text = f"共 {len(self._dlc_items)} 个 DLC 项目"
            if ok_count:
                status_text += f"  ✅ {ok_count} 个可用"
            if err_count:
                status_text += f"  ⚠️ {err_count} 个异常"
            self._dlc_status_label.setText(status_text)

            # 更新目录导航
            if hasattr(self, '_dlc_toc_list') and self._dlc_toc_list is not None:
                for i, dlc in enumerate(self._dlc_items):
                    status_icon = "✅" if dlc.get("status") == "ok" else "⚠️"
                    item = QListWidgetItem(f"{status_icon}  {dlc['name']}")
                    item.setData(Qt.UserRole, i)
                    self._dlc_toc_list.addItem(item)
                if self._dlc_toc_list.count() > 0:
                    self._dlc_toc_list.setCurrentRow(0)
            self._dlc_toc_list.blockSignals(False)

    def _scan_dlc_folder(self, dlc_dir: str) -> list:
        """扫描 DLC 文件夹，返回 DLC 项目列表"""
        items = []
        try:
            for entry in sorted(os.listdir(dlc_dir)):
                entry_path = os.path.join(dlc_dir, entry)
                if not os.path.isdir(entry_path):
                    continue
                dlc_json_path = os.path.join(entry_path, "DLC.json")
                if not os.path.isfile(dlc_json_path):
                    items.append({
                        "name": entry,
                        "version": "—",
                        "description": "",
                        "author": "",
                        "contact": "",
                        "main": "",
                        "folder": entry_path,
                        "status": "no_config",
                        "status_text": "⚠️ 缺少 DLC.json",
                        "error_detail": f"文件夹内未找到 DLC.json 配置文件。\n"
                                       f"请在 {entry_path} 中创建 DLC.json，格式：\n"
                                       '{"name":"...","version":"...","description":"...","author":"...","contact":"...","main":"xxx.exe"}'
                    })
                    continue
                # 读取并解析 DLC.json
                result = self._parse_dlc_json(dlc_json_path, entry_path, entry)
                items.append(result)
        except PermissionError:
            items.append({
                "name": "权限错误",
                "version": "—",
                "description": "",
                "author": "",
                "contact": "",
                "main": "",
                "folder": dlc_dir,
                "status": "perm_error",
                "status_text": "🚫 权限不足",
                "error_detail": f"无法访问 DLC 文件夹: {dlc_dir}\n请检查文件夹权限设置。"
            })
        except Exception as e:
            items.append({
                "name": "扫描异常",
                "version": "—",
                "description": "",
                "author": "",
                "contact": "",
                "main": "",
                "folder": dlc_dir,
                "status": "scan_error",
                "status_text": f"❌ 扫描错误",
                "error_detail": f"扫描 DLC 文件夹时发生错误:\n{str(e)}"
            })
        return items

    def _parse_dlc_json(self, json_path: str, folder_path: str, folder_name: str) -> dict:
        """解析 DLC.json 并验证字段"""
        base = {
            "name": folder_name,
            "version": "—",
            "description": "",
            "author": "",
            "contact": "",
            "main": "",
            "folder": folder_path,
            "status": "ok",
            "status_text": "✅ 正常",
            "error_detail": ""
        }

        # 读取文件
        try:
            with open(json_path, "r", encoding="utf-8") as f:
                raw = f.read()
        except PermissionError:
            base["status"] = "perm_error"
            base["status_text"] = "🚫 权限不足"
            base["error_detail"] = f"无法读取 DLC.json:\n{json_path}\n请检查文件权限。"
            return base
        except UnicodeDecodeError:
            base["status"] = "bad_encoding"
            base["status_text"] = "⚠️ 编码错误"
            base["error_detail"] = f"DLC.json 编码格式不正确，必须使用 UTF-8 编码:\n{json_path}"
            return base
        except Exception as e:
            base["status"] = "read_error"
            base["status_text"] = "⚠️ 读取失败"
            base["error_detail"] = f"读取 DLC.json 失败:\n{json_path}\n错误: {e}"
            return base

        if not raw.strip():
            base["status"] = "empty_config"
            base["status_text"] = "⚠️ 空配置文件"
            base["error_detail"] = f"DLC.json 文件为空:\n{json_path}"
            return base

        # 解析 JSON
        try:
            config = json.loads(raw)
        except json.JSONDecodeError as e:
            base["status"] = "bad_json"
            base["status_text"] = "⚠️ JSON 格式错误"
            base["error_detail"] = (f"DLC.json 不是有效的 JSON 格式:\n"
                                   f"{json_path}\n错误: {e.msg} (第 {e.lineno} 行, 第 {e.colno} 列)")
            return base

        if not isinstance(config, dict):
            base["status"] = "bad_format"
            base["status_text"] = "⚠️ 格式错误"
            base["error_detail"] = f"DLC.json 内容必须是一个 JSON 对象 ({{}}):\n{json_path}"
            return base

        # 验证必填字段
        required_fields = {
            "name": "DLC名称",
            "version": "DLC版本",
            "description": "DLC介绍",
            "author": "作者",
            "contact": "联系方式",
            "main": "DLC主程序路径"
        }
        missing = []
        for field, label in required_fields.items():
            if field not in config or not str(config[field]).strip():
                missing.append(f"{label} ({field})")
        if missing:
            base["status"] = "missing_fields"
            base["status_text"] = "⚠️ 字段缺失"
            base["error_detail"] = (f"DLC.json 缺少必填字段:\n"
                                   f"{json_path}\n"
                                   f"缺失: {', '.join(missing)}")
            return base

        # 填充字段
        base["name"] = str(config["name"]).strip()
        base["version"] = str(config["version"]).strip()
        base["description"] = str(config["description"]).strip()
        base["author"] = str(config["author"]).strip()
        base["contact"] = str(config["contact"]).strip()
        base["main"] = str(config["main"]).strip()

        # 检查主程序是否存在
        main_path = os.path.join(folder_path, base["main"])
        if not os.path.isfile(main_path):
            base["status"] = "exe_missing"
            base["status_text"] = "⚠️ 主程序缺失"
            base["error_detail"] = (f"DLC 主程序文件不存在:\n{main_path}\n"
                                   f"请检查 DLC.json 中 main 字段指向的路径是否正确。")
        else:
            # 尝试打开以检查权限
            try:
                with open(main_path, "rb") as _:
                    pass
            except PermissionError:
                base["status"] = "perm_error"
                base["status_text"] = "🚫 权限不足"
                base["error_detail"] = f"无法访问 DLC 主程序:\n{main_path}\n请检查文件权限。"

        return base

    def _make_dlc_card(self, dlc: dict) -> QFrame:
        """创建单个 DLC 项目的卡片"""
        card = QFrame()
        card.setProperty("role", "card")
        card.setObjectName("dlc_card")
        dark = self.config.get("theme_mode", "跟随系统") == "深色模式"
        fc = self.theme.get("text", "#e0e0e0" if dark else "#333")
        card.setStyleSheet(
            f"QFrame#dlc_card {{ color: {fc}; }}")
        cv = QVBoxLayout(card)
        cv.setSpacing(6)
        cv.setContentsMargins(14, 12, 14, 12)

        # 第一行: 状态 + 名称 + 版本
        top_row = QHBoxLayout()
        top_row.setSpacing(8)

        status_icon = QLabel(dlc.get("status_text", "⚠️"))
        status_icon.setStyleSheet("font-size: 10pt; font-weight: 600;")
        top_row.addWidget(status_icon)

        name_label = QLabel(dlc["name"])
        name_label.setStyleSheet("font-size: 11pt; font-weight: 700;")
        name_label.setWordWrap(True)
        top_row.addWidget(name_label, 1)

        ver_label = QLabel(f"v{dlc.get('version', '—')}")
        ver_label.setStyleSheet(f"font-size: 9pt; color: {'#aaa' if dark else '#888'};")
        top_row.addWidget(ver_label)

        cv.addLayout(top_row)

        # 作者与联系方式
        author = dlc.get("author", "") or ""
        contact = dlc.get("contact", "") or ""
        if author or contact:
            info_row = QHBoxLayout()
            info_row.setSpacing(12)
            if author:
                au = QLabel(f"👤 {author}")
                au.setStyleSheet(f"font-size: 8.5pt; color: {'#aaa' if dark else '#888'};")
                info_row.addWidget(au)
            if contact:
                ct = QLabel(f"📞 {contact}")
                ct.setStyleSheet(f"font-size: 8.5pt; color: {'#aaa' if dark else '#888'};")
                info_row.addWidget(ct)
            info_row.addStretch()
            cv.addLayout(info_row)

        # 描述(悬停提示)
        desc = dlc.get("description", "")
        if desc:
            desc_label = QLabel(desc)
            fg = "#aaa" if dark else "#777"
            desc_label.setStyleSheet(f"font-size: 9pt; color: {fg};")
            desc_label.setWordWrap(True)
            desc_label.setMaximumHeight(40)
            cv.addWidget(desc_label)

        # 错误详情(异常状态时显示)
        error_detail = dlc.get("error_detail", "")
        if error_detail and dlc.get("status") != "ok":
            err_label = QLabel(error_detail)
            err_label.setStyleSheet("font-size: 8.5pt; color: #e74c3c; "
                                   "background-color: rgba(231,76,60,8); "
                                   "border-radius: 4px; padding: 6px 8px;")
            err_label.setWordWrap(True)
            cv.addWidget(err_label)

        # 按钮行
        btn_row = QHBoxLayout()
        btn_row.setSpacing(8)

        if dlc.get("status") == "ok":
            launch_btn = QPushButton("▶ 启动")
            launch_btn.setCursor(QCursor(Qt.PointingHandCursor))
            launch_btn.setStyleSheet(
                f"QPushButton {{ background-color: {self.theme['success']}; color: white; "
                "border: none; border-radius: 6px; padding: 6px 18px; "
                "font-size: 10pt; font-weight: 600; }"
                "QPushButton:hover { background-color: #219a52; }")
            launch_btn.clicked.connect(lambda checked, d=dlc: self._dlc_launch(d))
            btn_row.addWidget(launch_btn)

        if dlc.get("status") != "ok" and error_detail:
            detail_btn = QPushButton("📋 查看详情")
            detail_btn.setCursor(QCursor(Qt.PointingHandCursor))
            detail_btn.setStyleSheet(
                "QPushButton { background-color: transparent; color: #e74c3c; "
                "border: 1px solid #e74c3c; border-radius: 6px; "
                "padding: 6px 14px; font-size: 10pt; }"
                "QPushButton:hover { background-color: rgba(231,76,60,15); }")
            detail_btn.clicked.connect(lambda checked, d=dlc: QMessageBox.warning(
                self, f"DLC 异常: {d['name']}", d.get("error_detail", "未知错误")))
            btn_row.addWidget(detail_btn)

        btn_row.addStretch()
        cv.addLayout(btn_row)

        return card

    def _dlc_launch(self, dlc: dict):
        """启动 DLC 主程序"""
        main_path = os.path.join(dlc["folder"], dlc["main"])

        if not os.path.isfile(main_path):
            QMessageBox.warning(self, "启动失败",
                f"主程序文件不存在:\n{main_path}\n\n"
                f"建议: 请检查 DLC.json 中的 main 字段指向的路径是否正确。")
            return

        # 权限检查
        if not os.access(main_path, os.X_OK):
            reply = QMessageBox.question(self, "权限不足",
                f"主程序文件没有执行权限:\n{main_path}\n\n是否仍要尝试启动?",
                QMessageBox.Yes | QMessageBox.No, QMessageBox.No)
            if reply != QMessageBox.Yes:
                return

        try:
            self.statusBar().showMessage(f"正在启动: {dlc['name']}...", 5000)
            subprocess.Popen(
                [main_path],
                cwd=os.path.dirname(main_path),
                creationflags=subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
            )
            QMessageBox.information(self, "启动成功",
                f"✅ {dlc['name']} 已启动\n\n"
                f"提示: 程序已在后台运行，请查看任务栏。")
        except PermissionError:
            QMessageBox.critical(self, "权限错误",
                f"无法启动 DLC 程序 (权限不足):\n{main_path}\n\n"
                f"建议: 请以管理员身份运行本程序，或检查文件权限设置。")
        except FileNotFoundError:
            QMessageBox.warning(self, "文件未找到",
                f"无法启动 DLC 程序:\n{main_path}\n\n"
                f"建议: 请检查 DLC.json 中 main 字段指向的路径是否正确。")
        except Exception as e:
            QMessageBox.critical(self, "启动失败",
                f"启动 DLC 程序时发生错误:\n{main_path}\n\n错误: {str(e)[:500]}")

    def _dlc_open_folder(self):
        """在资源管理器中打开 DLC 文件夹"""
        dlc_dir = self._dlc_folder_path()
        try:
            os.makedirs(dlc_dir, exist_ok=True)
        except Exception:
            pass
        if sys.platform == "win32":
            os.startfile(dlc_dir)
        else:
            webbrowser.open(dlc_dir)

    def _build_account_page(self) -> QWidget:
        """构建账户页面 — 微软账号 + 联机功能 (卡片式布局)"""
        page = QWidget()
        page.setObjectName("accountPage")
        outer = QVBoxLayout(page)
        outer.setContentsMargins(24, 20, 24, 24)
        outer.setSpacing(20)

        # 标题
        title = QLabel("👤 账户与联机")
        title.setStyleSheet("font-size: 20pt; font-weight: 700;")
        outer.addWidget(title)

        # 离线 / 未登录 状态
        logged_in = not self.is_offline and self.auth_manager is not None and self.auth_manager.has_cache_account()
        if not logged_in:
            offline_widget = QWidget()
            ov = QVBoxLayout(offline_widget)
            ov.setAlignment(Qt.AlignCenter)
            ov.addSpacing(60)
            icon = QLabel("👤")
            icon.setStyleSheet("font-size: 56pt;")
            icon.setAlignment(Qt.AlignCenter)
            ov.addWidget(icon)
            msg = QLabel("尚未登录微软账号")
            msg.setAlignment(Qt.AlignCenter)
            msg.setStyleSheet("font-size: 14pt; padding: 16px; color: #888;")
            ov.addWidget(msg)
            hint = QLabel("重新启动程序后可登录" if self.is_offline else "请重启程序完成登录")
            hint.setAlignment(Qt.AlignCenter)
            hint.setStyleSheet("font-size: 10pt; padding: 4px; color: #aaa;")
            ov.addWidget(hint)
            ov.addStretch()
            outer.addWidget(offline_widget, 1)
            return page

        # ===== 上方双列: 账户信息 | Xbox 玩家代号 =====
        top_row = QHBoxLayout()
        top_row.setSpacing(20)

        # --- 左列: 账户信息 ---
        card1 = self._build_theme_card(self.theme)
        cv1 = QVBoxLayout(card1)
        cv1.setContentsMargins(24, 20, 24, 20)
        cv1.setSpacing(10)
        cv1.addWidget(QLabel("🔑 微软账户"))
        cv1.itemAt(cv1.count() - 1).widget().setStyleSheet("font-size: 12pt; font-weight: 600;")

        self._avatar_label = QLabel()
        self._avatar_label.setFixedSize(72, 72)
        self._avatar_label.setAlignment(Qt.AlignCenter)
        self._avatar_label.setStyleSheet(
            "QLabel { border-radius: 36px; background-color: #e0e0e0; font-size: 28pt; }")
        self._avatar_label.setText("👤")

        self._account_name_label = QLabel("加载中...")
        self._account_name_label.setStyleSheet("font-size: 14pt; font-weight: 700;")
        self._account_name_label.setWordWrap(True)
        self._account_email_label = QLabel("")
        self._account_email_label.setStyleSheet(f"font-size: 10pt; color: {self._tcss('text_secondary')};")
        self._account_email_label.setWordWrap(True)

        av_row = QHBoxLayout()
        av_row.setSpacing(14)
        av_row.addWidget(self._avatar_label)
        av_info = QVBoxLayout()
        av_info.setSpacing(3)
        av_info.addStretch()
        av_info.addWidget(self._account_name_label)
        av_info.addWidget(self._account_email_label)
        av_info.addStretch()
        av_row.addLayout(av_info, 1)
        cv1.addLayout(av_row)
        top_row.addWidget(card1, 1)

        # --- 右列: Xbox 玩家代号 ---
        card2 = self._build_theme_card(self.theme)
        cv2 = QVBoxLayout(card2)
        cv2.setContentsMargins(24, 20, 24, 20)
        cv2.setSpacing(10)
        cv2.addWidget(QLabel("🎮 Xbox 玩家代号"))
        cv2.itemAt(cv2.count() - 1).widget().setStyleSheet("font-size: 12pt; font-weight: 600;")

        self._xbox_auto_label = QLabel("正在获取...")
        self._xbox_auto_label.setStyleSheet("font-size: 13pt; font-weight: 600; padding: 4px 0;")
        cv2.addWidget(self._xbox_auto_label)

        ac_success = self._tcss("success"); ac_border = self._tcss("border")
        self._xbox_edit_widget = QWidget()
        self._xbox_edit_widget.setVisible(False)
        xev = QHBoxLayout(self._xbox_edit_widget)
        xev.setContentsMargins(0, 4, 0, 0)
        xev.setSpacing(8)
        self._xbox_edit = QLineEdit()
        self._xbox_edit.setPlaceholderText("手动输入 Xbox 玩家代号")
        self._xbox_edit.setMaximumWidth(220)
        self._xbox_edit.setStyleSheet(
            f"QLineEdit {{ padding: 6px 10px; border-radius: 4px; "
            f"border: 1px solid {ac_border}; font-size: 11pt; }}")
        xev.addWidget(self._xbox_edit)
        save_gamer_btn = QPushButton("💾 保存")
        save_gamer_btn.setMaximumWidth(80)
        save_gamer_btn.setCursor(QCursor(Qt.PointingHandCursor))
        save_gamer_btn.setStyleSheet(
            f"QPushButton {{ background: {ac_success}; color: white; border: none; "
            f"border-radius: 4px; padding: 6px 12px; font-size: 10pt; }}"
            f"QPushButton:hover {{ background: {self._darken(ac_success)}; }}")
        save_gamer_btn.clicked.connect(self._save_manual_gamertag)
        xev.addWidget(save_gamer_btn)
        xev.addStretch()
        cv2.addWidget(self._xbox_edit_widget)
        cv2.addStretch()
        top_row.addWidget(card2, 1)

        outer.addLayout(top_row)

        # ===== 卡片: Xbox 好友 (占据主要空间) =====
        card3 = self._build_theme_card(self.theme)
        cv3 = QVBoxLayout(card3)
        cv3.setContentsMargins(24, 20, 24, 16)
        cv3.setSpacing(10)

        friends_title = QLabel("👥 Xbox 好友")
        friends_title.setStyleSheet("font-size: 13pt; font-weight: 600;")
        cv3.addWidget(friends_title)

        # 搜索 + 排序 + 刷新 栏 (合并为一行)
        toolbar = QHBoxLayout()
        toolbar.setSpacing(8)
        self._friends_search = QLineEdit()
        self._friends_search.setPlaceholderText("🔍 搜索好友...")
        self._friends_search.setClearButtonEnabled(True)
        self._friends_search.setMinimumWidth(180)
        self._friends_search.setStyleSheet(
            f"QLineEdit {{ padding: 6px 10px; border-radius: 4px; "
            f"border: 1px solid {ac_border}; font-size: 10pt; }}")
        self._friends_search.textChanged.connect(self._on_friends_filter_changed)
        toolbar.addWidget(self._friends_search)
        sort_lbl = QLabel("排序")
        sort_lbl.setStyleSheet(f"font-size: 10pt; color: {self._tcss('text_secondary')};")
        toolbar.addWidget(sort_lbl)
        self._friends_sort_combo = QComboBox()
        self._friends_sort_combo.addItems(["默认", "在线优先", "A-Z"])
        self._friends_sort_combo.setMaximumWidth(90)
        self._friends_sort_combo.setStyleSheet(
            f"QComboBox {{ padding: 3px 6px; border-radius: 3px; "
            f"border: 1px solid {ac_border}; font-size: 10pt; }}")
        self._friends_sort_combo.currentIndexChanged.connect(self._on_friends_sort_changed)
        toolbar.addWidget(self._friends_sort_combo)
        toolbar.addStretch()
        self._friends_count_label = QLabel("")
        self._friends_count_label.setStyleSheet(f"font-size: 10pt; color: {self._tcss('text_secondary')};")
        toolbar.addWidget(self._friends_count_label)
        self._friends_refresh_btn = QPushButton("🔄 刷新")
        self._friends_refresh_btn.setMaximumWidth(80)
        self._friends_refresh_btn.setCursor(QCursor(Qt.PointingHandCursor))
        ac_primary = self._tcss("primary")
        self._friends_refresh_btn.setStyleSheet(
            f"QPushButton {{ background: {ac_primary}; color: white; border: none; "
            f"border-radius: 4px; padding: 5px 10px; font-size: 10pt; }}"
            f"QPushButton:hover {{ background: {self._darken(ac_primary)}; }}")
        self._friends_refresh_btn.clicked.connect(self._refresh_xbox_friends)
        toolbar.addWidget(self._friends_refresh_btn)
        cv3.addLayout(toolbar)

        # 加载指示器
        self._friends_loading_label = QLabel("⏳ 正在加载好友列表...")
        self._friends_loading_label.setAlignment(Qt.AlignCenter)
        self._friends_loading_label.setStyleSheet("font-size: 11pt; color: #888; padding: 20px;")
        self._friends_loading_label.setVisible(False)
        cv3.addWidget(self._friends_loading_label)

        # 错误/空状态提示
        self._friends_status_label = QLabel("")
        self._friends_status_label.setAlignment(Qt.AlignCenter)
        self._friends_status_label.setWordWrap(True)
        self._friends_status_label.setStyleSheet("font-size: 11pt; color: #888; padding: 20px;")
        self._friends_status_label.setVisible(False)
        cv3.addWidget(self._friends_status_label)

        # 好友列表滚动区域 — 无最大高度限制, 自然撑满
        self._friends_scroll = QScrollArea()
        self._friends_scroll.setWidgetResizable(True)
        self._friends_scroll.setFrameShape(QFrame.NoFrame)
        self._friends_scroll.setStyleSheet(
            "QScrollArea { border: none; background: transparent; }")
        self._friends_list_widget = QWidget()
        self._friends_list_widget.setObjectName("friendsListWidget")
        self._friends_list_layout = QVBoxLayout(self._friends_list_widget)
        self._friends_list_layout.setContentsMargins(0, 0, 0, 0)
        self._friends_list_layout.setSpacing(6)
        self._friends_list_layout.addStretch()
        self._friends_scroll.setWidget(self._friends_list_widget)
        cv3.addWidget(self._friends_scroll, 1)  # stretch=1 让它占据剩余空间

        # 加载更多按钮
        self._friends_load_more_btn = QPushButton("📥 加载更多好友")
        self._friends_load_more_btn.setCursor(QCursor(Qt.PointingHandCursor))
        self._friends_load_more_btn.setMaximumWidth(200)
        self._friends_load_more_btn.setStyleSheet(
            "QPushButton { background: transparent; color: #2196f3; "
            "border: 1px solid #2196f3; border-radius: 4px; "
            "padding: 5px 12px; font-size: 10pt; }"
            "QPushButton:hover { background: rgba(33,150,243,0.1); }")
        self._friends_load_more_btn.clicked.connect(self._load_more_friends)
        self._friends_load_more_btn.setVisible(False)
        cv3.addWidget(self._friends_load_more_btn, alignment=Qt.AlignLeft)
        outer.addWidget(card3, 1)  # stretch=1 让好友卡片占据主要空间

        # ===== 底部操作栏 =====
        bottom_row = QHBoxLayout()
        bottom_row.setSpacing(12)

        shout_btn = QPushButton("📢 联机喊话 — 发送房间信息到 QQ 频道")
        shout_btn.setCursor(QCursor(Qt.PointingHandCursor))
        shout_btn.setMinimumHeight(44)
        shout_btn.setStyleSheet("""
            QPushButton {
                background: qlineargradient(x1:0, y1:0, x2:1, y2:0,
                    stop:0 #667eea, stop:1 #764ba2);
                color: white; border: none; border-radius: 8px;
                padding: 8px 20px; font-size: 11pt; font-weight: 600;
            }
            QPushButton:hover {
                background: qlineargradient(x1:0, y1:0, x2:1, y2:0,
                    stop:0 #5a6fd6, stop:1 #6a4196);
            }
        """)
        shout_btn.clicked.connect(self._open_shout)
        bottom_row.addWidget(shout_btn, 1)

        logout_btn = QPushButton("🚪 退出登录")
        logout_btn.setCursor(QCursor(Qt.PointingHandCursor))
        logout_btn.setMaximumWidth(140)
        logout_btn.setMinimumHeight(44)
        logout_btn.setStyleSheet("""
            QPushButton {
                background-color: transparent; color: #f44336;
                border: 1px solid #f44336; border-radius: 8px;
                padding: 8px 16px; font-size: 10pt;
            }
            QPushButton:hover { background-color: rgba(244,67,54,0.1); }
        """)
        logout_btn.clicked.connect(self._do_logout)
        bottom_row.addWidget(logout_btn)
        outer.addLayout(bottom_row)

        # ---- 异步加载 ----
        QTimer.singleShot(50, self._async_load_account)
        return page

    def _async_load_account(self):
        """后台线程加载账户信息,完成后更新 UI"""
        if self.auth_manager is None:
            return
        def do_load():
            # 先从缓存获取基本信息(离线可用)
            cached = self.auth_manager.get_cached_user_info() if self.auth_manager else {}
            # 尝试 Graph API 获取最新信息
            info = None
            avatar_data = None
            xbox_result = {"success": False, "reason": "未尝试"}
            try:
                info = self.auth_manager.get_user_info()
                avatar_data = self.auth_manager.get_user_photo()
                xbox_result = self.auth_manager.get_xbox_gamertag()
            except Exception as e:
                log_error("Account", f"加载异常: {e}")
            # 如果 Graph API 失败, 使用缓存信息
            if not info or info.get("error"):
                log_error("Account", "Graph API 获取失败: {info.get('error', '') if info else 'None'}，使用缓存信息")
                info = cached
            if not avatar_data:
                log_error("Account", "头像获取失败(可能未设置头像)")
            self._account_signal.emit(info, avatar_data, xbox_result)
        threading.Thread(target=do_load, daemon=True).start()
        # 同时触发异步加载好友列表
        QTimer.singleShot(200, self._async_load_xbox_friends)

    def _on_account_loaded(self, info: dict, avatar_data, xbox_result: dict):
        """账户信息加载完成(主线程)"""
        # 用户名 + 邮箱
        if info and not info.get("error"):
            self._account_name_label.setText(info.get("display_name", "未知用户"))
            self._account_email_label.setText(
                info.get("email") or info.get("upn", ""))
        else:
            self._account_name_label.setText("⚠️ 获取失败")
        # 头像
        if avatar_data:
            pix = QPixmap()
            pix.loadFromData(avatar_data)
            if not pix.isNull():
                scaled = pix.scaled(90, 90, Qt.KeepAspectRatio, Qt.SmoothTransformation)
                # 画到 90x90 圆形裁剪画布上, 避免非方形 pixmap 与方形 mask 尺寸不匹配
                rounded = QPixmap(90, 90)
                rounded.fill(Qt.transparent)
                rp = QPainter(rounded)
                rp.setRenderHint(QPainter.Antialiasing)
                clip = QPainterPath()
                clip.addEllipse(0, 0, 90, 90)
                rp.setClipPath(clip)
                ox = (90 - scaled.width()) // 2
                oy = (90 - scaled.height()) // 2
                rp.drawPixmap(ox, oy, scaled)
                rp.end()
                self._avatar_label.setPixmap(rounded)
                self._avatar_label.setText("")
        # Xbox —— 优先使用手动保存的代号
        saved_gamertag = self.config.get("xbox_gamertag", "")
        if saved_gamertag:
            self._xbox_auto_label.setText(f"✅ {saved_gamertag} (手动设置)")
            self._xbox_edit.setText(saved_gamertag)
            self._xbox_edit_widget.setVisible(True)
        elif xbox_result.get("success"):
            self._xbox_auto_label.setText(f"🎮 {xbox_result['gamertag']}")
        elif xbox_result.get("no_xbox_profile"):
            self._xbox_auto_label.setText(
                "⚠️ 该微软账号未关联 Xbox 个人资料\n请在下方手动输入你的 Xbox 玩家代号:")
            self._xbox_edit_widget.setVisible(True)
        else:
            reason = xbox_result.get("reason", "未知错误")
            http_body = xbox_result.get("http_body", "")
            detail = f" ({http_body[:120]})" if http_body else ""
            self._xbox_auto_label.setText(
                f"⚠️ 无法自动获取: {reason}{detail}\n请在下方手动输入你的 Xbox 玩家代号:")
            self._xbox_edit_widget.setVisible(True)

    def _save_manual_gamertag(self):
        """保存手动输入的 Xbox 玩家代号"""
        gamertag = self._xbox_edit.text().strip()
        if gamertag:
            self.config["xbox_gamertag"] = gamertag
            self.save_config()
            self._xbox_auto_label.setText(f"✅ {gamertag} (手动设置)")
            QMessageBox.information(self, "已保存", f"Xbox 玩家代号已设置为: {gamertag}")
        else:
            QMessageBox.warning(self, "输入为空", "请输入 Xbox 玩家代号")

    # ── Xbox 好友列表 ──

    _ONLINE_STATE_MAP = {
        "Online": ("🟢 在线", "#4caf50"),
        "Away": ("🟡 离开", "#ff9800"),
        "Offline": ("⚫ 离线", "#9e9e9e"),
        "InGame": ("🎮 游戏中", "#2196f3"),
        "InMultiplayerSession": ("🎮 多人游戏中", "#2196f3"),
        "InParty": ("🎉 队伍中", "#9c27b0"),
    }

    @staticmethod
    def _online_state_display(state: str) -> tuple:
        """将 Xbox 在线状态转为 (文本, 颜色)"""
        return MainWindow._ONLINE_STATE_MAP.get(state, (f"⚫ {state}", "#9e9e9e"))

    def _async_load_xbox_friends(self):
        """后台线程加载 Xbox 好友列表第一页。
        先从缓存加载即时显示,再后台刷新最新数据。
        """
        if self.auth_manager is None or self._xbox_friends_loading:
            return
        # 先尝试从缓存加载(即时显示)
        cache_path = os.path.join(self.base_path, "_xbox_friends_cache.json")
        if not self._xbox_friends_all:
            try:
                if os.path.exists(cache_path):
                    with open(cache_path, "r", encoding="utf-8") as f:
                        cached = json.load(f)
                    if isinstance(cached, list) and cached:
                        self._xbox_friends_all = cached
                        self._xbox_friends_skip = len(cached)
                        self._xbox_friends_total = len(cached)
                        self._render_friends_list()
            except Exception:
                pass
        if hasattr(self, '_friends_loading_label'):
            self._friends_loading_label.setVisible(True)
            self._friends_status_label.setVisible(False)
        self._xbox_friends_loading = True
        def do_load():
            result = self.auth_manager.get_xbox_friends(max_items=25, skip_items=0)
            self._xbox_friends_signal.emit(result)
        threading.Thread(target=do_load, daemon=True).start()

    def _on_xbox_friends_loaded(self, result: dict):
        """好友列表加载完成 (主线程)"""
        self._xbox_friends_loading = False
        if hasattr(self, '_friends_loading_label'):
            self._friends_loading_label.setVisible(False)
        if not result.get("success"):
            reason = result.get("reason", "未知错误")
            log_error("XboxFriends", f"加载失败: {reason}")
            if hasattr(self, '_friends_status_label'):
                self._friends_status_label.setText(f"⚠️ 无法加载好友列表: {reason}")
                self._friends_status_label.setVisible(True)
            if hasattr(self, '_friends_load_more_btn'):
                self._friends_load_more_btn.setVisible(False)
                self._friends_load_more_btn.setEnabled(True)
                self._friends_load_more_btn.setText("📥 加载更多好友")
            return
        friends = result.get("friends", [])
        total = result.get("total", 0)
        # 根据是否追加模式决定合并还是替换
        if getattr(self, '_xbox_friends_appending', False):
            self._xbox_friends_all.extend(friends)
            self._xbox_friends_appending = False
        else:
            self._xbox_friends_all = friends
        self._xbox_friends_total = total
        self._xbox_friends_skip = len(self._xbox_friends_all)
        # 保存缓存(非追加模式时替换缓存)
        if not getattr(self, '_xbox_friends_appending', False):
            try:
                cache_path = os.path.join(self.base_path, "_xbox_friends_cache.json")
                with open(cache_path, "w", encoding="utf-8") as f:
                    json.dump(self._xbox_friends_all, f, ensure_ascii=False)
            except Exception:
                pass
        if not self._xbox_friends_all:
            if hasattr(self, '_friends_status_label'):
                self._friends_status_label.setText("📭 你的好友列表为空")
                self._friends_status_label.setVisible(True)
            if hasattr(self, '_friends_load_more_btn'):
                self._friends_load_more_btn.setVisible(False)
                self._friends_load_more_btn.setEnabled(True)
        else:
            if hasattr(self, '_friends_status_label'):
                self._friends_status_label.setVisible(False)
            if hasattr(self, '_friends_load_more_btn'):
                has_more = self._xbox_friends_skip < self._xbox_friends_total
                self._friends_load_more_btn.setVisible(has_more)
                self._friends_load_more_btn.setEnabled(True)
                if has_more:
                    remaining = self._xbox_friends_total - self._xbox_friends_skip
                    self._friends_load_more_btn.setText(f"📥 加载更多好友 ({remaining} 位未显示)")
                else:
                    self._friends_load_more_btn.setText("📥 加载更多好友")
        self._render_friends_list()

    def _on_friends_sort_changed(self, index: int):
        """排序方式改变"""
        sort_keys = ["default", "online", "name"]
        if 0 <= index < len(sort_keys):
            self._xbox_friends_sort = sort_keys[index]
        self._render_friends_list()

    def _on_friends_filter_changed(self, text: str):
        """搜索过滤文本改变"""
        self._xbox_friends_filter = text.strip().lower()
        self._render_friends_list()

    def _refresh_xbox_friends(self):
        """刷新好友列表 (重新加载第一页)"""
        self._xbox_friends_all = []
        self._xbox_friends_skip = 0
        self._xbox_friends_total = 0
        self._async_load_xbox_friends()

    def _load_more_friends(self):
        """加载下一页好友"""
        if self.auth_manager is None or self._xbox_friends_loading:
            return
        skip = self._xbox_friends_skip
        self._xbox_friends_loading = True
        self._xbox_friends_appending = True  # 标记为追加模式
        if hasattr(self, '_friends_load_more_btn'):
            self._friends_load_more_btn.setText("⏳ 加载中...")
            self._friends_load_more_btn.setEnabled(False)
        def do_load():
            result = self.auth_manager.get_xbox_friends(max_items=25, skip_items=skip)
            self._xbox_friends_signal.emit(result)
        threading.Thread(target=do_load, daemon=True).start()

    def _on_friend_avatar_loaded(self, xuid: str, image_data):
        """好友头像加载完成 (主线程) — 图片数据由后台线程下载,信号投递到主线程"""
        if image_data is None:
            return
        # 查找对应的头像 QLabel
        layout = self._friends_list_layout
        for i in range(layout.count()):
            item = layout.itemAt(i)
            widget = item.widget()
            if widget and isinstance(widget, QWidget):
                avatar_lbl = widget.findChild(QLabel, f"avatar_{xuid}")
                if avatar_lbl:
                    try:
                        pix = QPixmap()
                        pix.loadFromData(image_data)
                        if not pix.isNull():
                            scaled = pix.scaled(38, 38, Qt.KeepAspectRatio, Qt.SmoothTransformation)
                            rounded = QPixmap(38, 38)
                            rounded.fill(Qt.transparent)
                            rp = QPainter(rounded)
                            rp.setRenderHint(QPainter.Antialiasing)
                            clip = QPainterPath()
                            clip.addEllipse(0, 0, 38, 38)
                            rp.setClipPath(clip)
                            ox = (38 - scaled.width()) // 2
                            oy = (38 - scaled.height()) // 2
                            rp.drawPixmap(ox, oy, scaled)
                            rp.end()
                            avatar_lbl.setPixmap(rounded)
                            avatar_lbl.setText("")
                            avatar_lbl.setStyleSheet("border-radius: 20px;")
                    except Exception:
                        pass
                    break

    def _render_friends_list(self):
        """渲染好友列表到滚动区域"""
        if not hasattr(self, '_friends_list_layout'):
            return
        # 清空现有卡片
        layout = self._friends_list_layout
        while layout.count() > 0:
            item = layout.takeAt(0)
            widget = item.widget()
            if widget:
                widget.deleteLater()

        friends = list(self._xbox_friends_all)
        # 搜索过滤
        filter_text = self._xbox_friends_filter
        if filter_text:
            friends = [
                f for f in friends
                if filter_text in (f.get("gamertag", "") + f.get("display_name", "")).lower()
            ]
        # 排序
        if self._xbox_friends_sort == "online":
            state_order = {
                "Online": 0, "InGame": 1, "InMultiplayerSession": 2,
                "InParty": 3, "Away": 4, "Offline": 5,
            }
            friends.sort(key=lambda f: state_order.get(f.get("online_state", "Offline"), 99))
        elif self._xbox_friends_sort == "name":
            friends.sort(key=lambda f: (f.get("gamertag", "") or f.get("display_name", "")).lower())

        # 过滤结果提示
        if filter_text and not friends and self._xbox_friends_all:
            no_result = QLabel(f'🔍 没有找到 "{filter_text}" 相关的好友')
            no_result.setAlignment(Qt.AlignCenter)
            no_result.setStyleSheet("font-size: 10pt; color: #888; padding: 12px;")
            layout.addWidget(no_result)

        for friend in friends:
            card = self._make_friend_card(friend)
            layout.addWidget(card)
        layout.addStretch()

        # 更新好友计数
        total = len(self._xbox_friends_all)
        shown = len(friends)
        if hasattr(self, '_friends_count_label') and self._friends_count_label:
            if filter_text:
                self._friends_count_label.setText(f"{shown}/{total} 人")
            else:
                self._friends_count_label.setText(f"{total} 人")

    def _make_friend_card(self, friend: dict) -> QWidget:
        """创建单个好友卡片 — 大尺寸头像 + 清晰信息"""
        card = QWidget()
        card.setStyleSheet(
            "QWidget#friendCard { background: rgba(128,128,128,0.08); "
            "border-radius: 10px; padding: 4px; }"
            "QWidget#friendCard:hover { background: rgba(128,128,128,0.16); }")
        card.setObjectName("friendCard")
        row = QHBoxLayout(card)
        row.setContentsMargins(10, 8, 10, 8)
        row.setSpacing(12)

        # 头像 (52x52 → 更大)
        avatar = QLabel()
        avatar.setObjectName(f"avatar_{friend.get('xuid', 'unknown')}")
        avatar.setFixedSize(52, 52)
        avatar.setAlignment(Qt.AlignCenter)
        state = friend.get("online_state", "Offline")
        if state in ("Online", "InGame", "InMultiplayerSession"):
            avatar_icon = "🟢"
        elif state == "Away":
            avatar_icon = "🟡"
        elif state == "InParty":
            avatar_icon = "🎉"
        else:
            avatar_icon = "🎮"
        avatar.setText(avatar_icon)
        avatar.setStyleSheet(
            "font-size: 22pt; background: rgba(128,128,128,0.15); border-radius: 26px;")
        row.addWidget(avatar)

        # 异步加载真实头像
        xuid = friend.get("xuid", "")
        display_pic = friend.get("display_pic", "")
        if xuid and display_pic:
            signal = self._friend_avatar_signal
            def _fetch_avatar(x=xuid, url=display_pic, sig=signal):
                try:
                    data = ms_requests.get(url, timeout=10).content
                    sig.emit(x, data)
                except Exception:
                    sig.emit(x, None)
            threading.Thread(target=_fetch_avatar, daemon=True).start()

        # 信息区
        info_layout = QVBoxLayout()
        info_layout.setSpacing(3)
        name = friend.get("gamertag") or friend.get("display_name", "未知")
        name_label = QLabel(name)
        name_label.setStyleSheet("font-size: 12pt; font-weight: 600;")
        info_layout.addWidget(name_label)

        state_text, state_color = self._online_state_display(state)
        presence_text = friend.get("presence_text", "")
        detail_parts = [f"{state_text}"]
        if presence_text:
            detail_parts.append(presence_text)
        detail_label = QLabel(" · ".join(detail_parts))
        detail_label.setStyleSheet(f"font-size: 10pt; color: {state_color};")
        detail_label.setWordWrap(True)
        info_layout.addWidget(detail_label)

        # 游戏信息 (如果有)
        title_history = friend.get("title_history")
        if title_history and isinstance(title_history, list):
            for entry in title_history[:1]:  # 只显示第一个 (最近)
                game_name = entry.get("name", "")
                if game_name:
                    game_label = QLabel(f"🎮 {game_name}")
                    game_label.setStyleSheet(f"font-size: 9pt; color: {self._tcss('text_secondary')};")
                    info_layout.addWidget(game_label)

        row.addLayout(info_layout, 1)

        # 收藏标记
        if friend.get("is_favorite"):
            fav = QLabel("⭐")
            fav.setStyleSheet("font-size: 14pt;")
            row.addWidget(fav)

        return card

    # ── 退出登录 ──

    def _do_logout(self):
        """退出登录"""
        reply = QMessageBox.question(
            self, "确认退出",
            "退出登录后需要重新启动才能再次登录。\n确定要退出吗?",
            QMessageBox.Yes | QMessageBox.No, QMessageBox.No)
        if reply == QMessageBox.Yes:
            self.auth_manager.logout()
            QMessageBox.information(self, "已退出",
                "已清除登录状态。\n重新启动程序可再次登录。")
            self.close()

    def _build_about_page(self) -> QWidget:
        """关于页面(内嵌,直接显示 AboutDialog 的完整内容,不再弹新窗口)"""
        page = QWidget()
        v = QVBoxLayout(page)
        v.setContentsMargins(0, 0, 0, 0)
        v.setSpacing(0)
        # 直接复用 AboutDialog.build_widget() 创建的内容(完整 v2.2 更新 + QQ 群)
        about_widget = AboutDialog.build_widget(self, page, in_dialog=False)
        v.addWidget(about_widget, 1)
        return page

    # ============== 子页通用包装 ==============
    def _wrap_subpage(self, title: str, body) -> QWidget:
        """把任意 widget 包成带"返回"按钮的子页(完全内嵌)"""
        page = QWidget()
        v = QVBoxLayout(page)
        v.setContentsMargins(0, 0, 0, 0)
        v.setSpacing(0)
        top = QFrame()
        top.setObjectName("sub_topbar")
        th = QHBoxLayout(top)
        th.setContentsMargins(12, 6, 12, 6)
        th.setSpacing(8)
        title_lbl = QLabel(title)
        title_lbl.setStyleSheet("font-size: 12pt; font-weight: 600; padding: 4px 6px;")
        th.addWidget(title_lbl)
        th.addStretch()
        back_btn = FluentButton("← 返回", "secondary")
        back_btn.setFixedHeight(34)
        back_btn.setMinimumWidth(80)
        back_btn.clicked.connect(self._back_to_main)
        th.addWidget(back_btn)
        v.addWidget(top)
        v.addWidget(body, 1)
        return page

    def _back_to_main(self):
        """返回上一个主页面"""
        idx = getattr(self, '_last_main_index', 0)
        # 同步主侧边栏按钮的 active 高亮
        key_map = {0: "home", 1: "package", 2: "instance", 3: "settings", 4: "about", 9: "account", 10: "dlc"}
        self._switch_page(idx, key_map.get(idx))

    _TUTORIAL_SECTIONS = [
        {
            "key": "welcome", "icon": "👋", "title": "欢迎使用",
            "blocks": [
                {"type": "heading", "text": "Hello Mental Omega Launcher 是什么?"},
                {"type": "text", "text":
                    "这是一个帮你管理《心灵终结》游戏资源的小工具。"
                    "你可以用它来切换不同的游戏版本、添加各种 插件、"
                    "安装新的地图和任务、修改游戏音效和外观,所有操作都能通过鼠标点几下完成。"},
                {"type": "heading", "text": "这个程序能做什么?"},
                {"type": "list", "items": [
                    "📦 一键安装 / 卸载 / 删除资源包",
                    "🎮 同时管理多个游戏版本",
                    "🗺️ 安装地图、任务、语音、美化等各类资源",
                    "💾 把游戏配置备份下来,以后可以还原",
                    "🎨 切换深色 / 浅色主题,看哪个舒服用哪个",
                ]},
                {"type": "heading", "text": "界面上有什么?"},
                {"type": "list", "items": [
                    "左侧深色竖条 = 导航栏,点击切换不同功能",
                    "中间大区域 = 当前功能的内容(主页/包管理/实例等)",
                    "右侧如果出现滚动条 = 内容多,可以滚动查看",
                ]},
            ]
        },
        {
            "key": "first_use", "icon": "🚀", "title": "第一次使用",
            "blocks": [
                {"type": "heading", "text": "第 1 步:添加你的游戏"},
                {"type": "text", "text":
                    "程序第一次打开时,里面还没有任何游戏,"
                    "你需要先告诉程序你的心灵终结安装在哪个文件夹。"},
                {"type": "list", "items": [
                    "1. 点击下方的「备份原版游戏文件」按钮，备份原版游戏文件",
                    "2. 点击左侧的「🎮 实例管理」按钮",
                    "3. 找到「➕ 添加实例」按钮,点一下",
                    "4. 给你这个游戏起个名字(比如「我的原版」)",
                    "5. 点击「📁 浏览」选择游戏文件夹",
                    "    (文件夹里要能看到 mentalOmega.exe)",
                    "6. 点击「✅ 添加」完成",
                ]},
                {"type": "tip", "text":
                    "💡 小提示: 游戏文件夹的名字最好用英文,比如 Mental Omega。"
                    "如果有中文路径,游戏可能会打不开。"},
                {"type": "heading", "text": "第 2 步:看看效果"},
                {"type": "text", "text":
                    "添加完成后,你会看到游戏出现在了实例列表里。"
                    "点击「▶️ 启动游戏」按钮,程序就会帮你打开游戏啦。"},
                {"type": "heading", "text": "第 3 步:换个喜欢的主题"},
                {"type": "list", "items": [
                    "1. 点击左侧的「⚙️ 设置」按钮",
                    "2. 在「主题」那一栏,选你喜欢的模式:",
                    "    · 浅色: 白色背景",
                    "    · 深色: 深色背景",
                    "    · 跟随系统: 自动跟随你电脑的设置",
                    "3. 还可以自定义游戏的背景图！",
                ]},
            ]
        },
        {
            "key": "package", "icon": "📦", "title": "怎么安装资源包",
            "blocks": [
                {"type": "heading", "text": "资源包是什么?"},
                {"type": "text", "text":
                    "资源包就是一个压缩文件(像 .zip 那样),里面装着要装到游戏里的文件。"
                    "比如添加新战役的  任务包、新地图的 Map 包。"},
                {"type": "heading", "text": "方式 1:从网上下载（HMOL内置笨蛋广场）并安装"},
                {"type": "list", "items": [
                    "1. 点击左侧的「📦 包管理」按钮",
                    "2. 顶部有 7 个分类(INI / 地图 / 任务 等),选你需要的",
                    "3. 点击「⬇️ 下载」按钮,会打开笨蛋广场",
                    "4. 找到你要的包,点击「下载」",
                    "5. 下载下来的压缩文件先保存到一个容易找的地方",
                    "6. 回到包管理页,点击「📥 导入」选择刚才下载的文件",
                    "7. 选中刚导入的包,点击「⬇️ 安装」就装好了",
                ]},
                {"type": "heading", "text": "方式 2:本地已有的文件"},
                {"type": "text", "text":
                    "如果朋友给了你一个资源包文件,直接用「📥 导入」加载,然后「⬇️ 安装」即可。"},
                {"type": "heading", "text": "怎么卸载?"},
                {"type": "text", "text":
                    "在「已安装」列表里选中你想卸载的包,点「⬆️ 卸载」,"
                    "程序会从游戏文件夹里删除掉,以后想装回来再装一次就行。"},
                {"type": "tip", "text": "💡 没有备份原版文件是无法卸载的！！！"},
            ]
        },
        {
            "key": "instance", "icon": "🎮", "title": "怎么管理游戏",
            "blocks": [
                {"type": "heading", "text": "我有好几个游戏版本怎么办?"},
                {"type": "text", "text":
                    "程序支持同时管理多个游戏!比如你同时安装了原版 3.3.6 和Apra2（为什么是Apra2？因为mmm经常玩Apra2）,"
                    "可以把它们都加进来,在「实例管理」里随时切换。"},
                {"type": "heading", "text": "切换当前游戏"},
                {"type": "text", "text":
                    "在主页或包管理页顶部的下拉框里,选你要用的游戏,点一下就切换了。"
                    "切换后,所有的包管理都会针对这个游戏显示。"},
                {"type": "heading", "text": "重命名 / 删除"},
                {"type": "list", "items": [
                    "重命名: 实例管理里点「✏️ 重命名」,改个好记的名字",
                    "删除: 实例管理里点「🗑️ 删除」,可以选择只删除记录(游戏文件还在),"
                    "    或者连游戏文件一起删(小心!无法恢复！！！)",
                ]},
                {"type": "heading", "text": "备份和还原"},
                {"type": "text", "text":
                    "导出游戏实例: 点击「📤 导出」,会把当前游戏的配置、已安装的包等打包成一个文件,可以分享给朋友"
                    "建议定期备份,这样万一游戏出问题了可以恢复。"},
                {"type": "text", "text":
                    "导入游戏实例: 在另一台电脑或重装系统后（萌新不会安装各种任务包的话）,点击「📥 导入」,"
                    "选择之前的备份文件,所有配置和包就都回来了。"},
                {"type": "tip", "text": "💡 这个功能很实用吧！快夸我！（去QQ群拍拍群主）"},
            ]
        },
        {
            "key": "settings", "icon": "🎨", "title": "个性化设置",
            "blocks": [
                {"type": "heading", "text": "主题(深色 / 浅色)"},
                {"type": "text", "text":
                    "点击左侧的「⚙️ 设置」按钮,顶部就是主题切换。"
                    "选完即时生效,不用重启。"},
            ]
        },
        {
            "key": "feedback", "icon": "💬", "title": "遇到问题?",
            "blocks": [
                {"type": "heading", "text": "程序打不开?"},
                {"type": "list", "items": [
                    "把完整的报错窗口截图发到QQ群里面",
                    "把报错提示复制一份发给群主",
                    "可以在QQ群或者QQ频道发帖求助",
                    "可以去Github报告漏洞！",
                ]},
                {"type": "heading", "text": "下载按钮没反应?"},
                {"type": "text", "text":
                    "检查你的电脑有没有正确连接网络。"
                    "检查你有没有登录微软账号"
                    "可以试试在浏览器里打开。"},
                {"type": "heading", "text": "装包时提示「不是有效的 MO 实例」?"},
                {"type": "text", "text":
                    "通常是没选对当前游戏。"
                    "先在主页顶部下拉框里选好你的游戏,再回来安装。"},
                {"type": "heading", "text": "想反馈建议或报告 Bug?"},
                {"type": "list", "items": [
                    "点击左侧的「💬 反馈」",
                    "选 QQ 群和 QQ 频道(推荐,有人在线解答)",
                    "或者 GitHub(适合正式 Bug 报告)",
                ]},
                {"type": "tip", "text": "💡 反馈前先看看你的问题是不是在 FAQ 里,能省不少时间。"},
            ]
        },
    ]

    def _build_tutorial_subpage(self) -> QWidget:
        """内嵌使用教程(左侧章节导航 + 右侧内容滚动,主题感知)"""
        page = QWidget()
        outer = QVBoxLayout(page)
        outer.setContentsMargins(0, 0, 0, 0)
        outer.setSpacing(0)

        # Hero 顶栏(与资源中心/包管理一致的蓝紫渐变)
        self.tutorial_hero = QFrame()
        self.tutorial_hero.setObjectName("tutorial_hero")
        self.tutorial_hero.setStyleSheet(self._tutorial_hero_style())
        self.tutorial_hero.setFixedHeight(80)
        hv = QVBoxLayout(self.tutorial_hero)
        hv.setContentsMargins(20, 10, 20, 10)
        hv.setSpacing(0)
        h_title = QLabel("📖 使用教程")
        h_title.setStyleSheet(
            "font-size: 18pt; font-weight: 700; color: white; "
            "background: transparent; padding: 0;"
        )
        hv.addWidget(h_title)
        h_sub = QLabel("Hello Mental Omega Launcher v2.2 · 完整使用指南")
        h_sub.setStyleSheet(
            "font-size: 9pt; color: rgba(255,255,255,210); "
            "background: transparent; padding: 0;"
        )
        hv.addWidget(h_sub)
        outer.addWidget(self.tutorial_hero)

        # 主体:左侧导航 + 右侧内容
        body = QWidget()
        bl = QHBoxLayout(body)
        bl.setContentsMargins(0, 0, 0, 0)
        bl.setSpacing(0)
        # 左侧章节列表(响应式 - 窄屏自动缩窄)
        self.tutorial_nav = QListWidget()
        self.tutorial_nav.setStyleSheet(self._tutorial_nav_style())
        self.tutorial_nav.setMinimumWidth(140)
        self.tutorial_nav.setMaximumWidth(220)
        self.tutorial_nav.setFrameShape(QFrame.NoFrame)
        for sec in self._TUTORIAL_SECTIONS:
            item = QListWidgetItem(f"{sec['icon']}  {sec['title']}")
            item.setData(Qt.UserRole, sec["key"])
            self.tutorial_nav.addItem(item)
        self.tutorial_nav.currentRowChanged.connect(self._on_tutorial_section_changed)
        bl.addWidget(self.tutorial_nav)

        # 右侧内容滚动区
        self.tutorial_scroll = QScrollArea()
        self.tutorial_scroll.setWidgetResizable(True)
        self.tutorial_scroll.setFrameShape(QFrame.NoFrame)
        self.tutorial_content = QWidget()
        self.tutorial_content_layout = QVBoxLayout(self.tutorial_content)
        self.tutorial_content_layout.setContentsMargins(24, 16, 24, 16)
        self.tutorial_content_layout.setSpacing(10)
        self.tutorial_content_layout.addStretch()
        self.tutorial_scroll.setWidget(self.tutorial_content)
        bl.addWidget(self.tutorial_scroll, 1)

        outer.addWidget(body, 1)

        # 默认选中第一章节
        self.tutorial_nav.setCurrentRow(0)
        return page

    def _tutorial_hero_style(self) -> str:
        """教程 Hero 样式(根据当前主题色方案动态生成)"""
        g = self._current_gradient()
        return (
            "QFrame#tutorial_hero { "
            "background: qlineargradient(x1:0, y1:0, x2:1, y2:0, "
            f"stop:0 {g['primary']}, stop:1 {g['secondary']}); "
            "border: none; }"
            "QFrame#tutorial_hero QLabel { color: white; background: transparent; }"
        )

    def _tutorial_nav_style(self) -> str:
        is_dark = self.theme is DARK
        bg = self.theme.get("bg_sidebar", self.theme.get("surface", "#eef1f5"))
        text = self.theme.get("text", "#2c3e50")
        sel_bg = self.theme.get("accent", "#0f4c75")
        sel_text = self.theme.get("text_inverse", "#ffffff")
        hover_bg = self.theme.get("surface_alt", self.theme.get("bg_widget", "#f1f3f5"))
        return (
            f"QListWidget {{ background-color: {bg}; color: {text}; "
            f"border: none; border-right: 1px solid {self.theme.get('border', self.theme.get('bg_sidebar', '#d0d7de'))}; "
            f"outline: 0px; padding: 8px 0px; }} "
            f"QListWidget::item {{ padding: 12px 16px; "
            f"border: none; border-bottom: 1px solid {self.theme.get('border', self.theme.get('bg_sidebar', '#d0d7de'))}; "
            f"font-size: 10pt; }} "
            f"QListWidget::item:hover {{ background-color: {hover_bg}; }} "
            f"QListWidget::item:selected {{ "
            f"background-color: {sel_bg}; color: {sel_text}; "
            f"border-left: 4px solid {self.theme.get('accent_hover', self.theme.get('accent', '#1a5a8a'))}; }}"
        )

    def _on_tutorial_section_changed(self, row: int):
        """切换章节内容"""
        if row < 0 or row >= len(self._TUTORIAL_SECTIONS):
            return
        sec = self._TUTORIAL_SECTIONS[row]
        # 清空旧内容(stretch 保留)
        while self.tutorial_content_layout.count() > 1:
            it = self.tutorial_content_layout.takeAt(0)
            w = it.widget()
            if w is not None:
                w.deleteLater()
        # 标题
        title_lbl = QLabel(f"{sec['icon']}  {sec['title']}")
        title_lbl.setStyleSheet(
            f"font-size: 16pt; font-weight: 700; "
            f"color: {self.theme.get('primary', '#1a1a2e')}; "
            f"padding: 6px 0 12px 0; background: transparent; "
            f"border-bottom: 2px solid {self.theme.get('accent', '#0f4c75')};"
        )
        self.tutorial_content_layout.insertWidget(
            self.tutorial_content_layout.count() - 1, title_lbl)
        # 渲染 blocks
        for block in sec.get("blocks", []):
            w = self._render_tutorial_block(block)
            if w is not None:
                self.tutorial_content_layout.insertWidget(
                    self.tutorial_content_layout.count() - 1, w)
        # 滚动到顶部
        self.tutorial_scroll.verticalScrollBar().setValue(0)

    def _render_tutorial_block(self, block: dict):
        """根据 block 类型渲染不同 widget"""
        btype = block.get("type")
        text_color = self.theme.get("text", "#2c3e50")
        text_secondary = self.theme.get("text_secondary", "#6c757d")
        accent = self.theme.get("accent", "#0f4c75")

        if btype == "heading":
            lbl = QLabel(block.get("text", ""))
            lbl.setStyleSheet(
                f"font-size: 12pt; font-weight: 700; color: {accent}; "
                f"padding: 10px 0 4px 0; background: transparent;"
            )
            lbl.setWordWrap(True)
            return lbl

        if btype == "text":
            lbl = QLabel(block.get("text", ""))
            lbl.setStyleSheet(
                f"font-size: 10pt; color: {text_color}; "
                f"padding: 4px 0; background: transparent; line-height: 1.6;"
            )
            lbl.setWordWrap(True)
            return lbl

        if btype == "list":
            container = QWidget()
            cl = QVBoxLayout(container)
            cl.setContentsMargins(0, 4, 0, 4)
            cl.setSpacing(3)
            for item in block.get("items", []):
                lbl = QLabel("• " + item)
                lbl.setStyleSheet(
                    f"font-size: 10pt; color: {text_color}; "
                    f"padding: 3px 0 3px 8px; background: transparent;"
                )
                lbl.setWordWrap(True)
                cl.addWidget(lbl)
            return container

        if btype == "tip":
            box = QFrame()
            box.setStyleSheet(
                f"QFrame {{ background-color: {self.theme.get('surface_alt', '#f1f3f5')}; "
                f"border-left: 4px solid {self.theme.get('success', '#27ae60')}; "
                f"border-radius: 4px; padding: 4px; }} "
                f"QFrame QLabel {{ background: transparent; padding: 0; }}"
            )
            bl = QVBoxLayout(box)
            bl.setContentsMargins(10, 6, 10, 6)
            bl.setSpacing(2)
            lbl = QLabel(block.get("text", ""))
            lbl.setStyleSheet(
                f"color: {text_color}; font-size: 10pt; background: transparent;"
            )
            lbl.setWordWrap(True)
            bl.addWidget(lbl)
            return box

        if btype == "warn":
            box = QFrame()
            box.setStyleSheet(
                f"QFrame {{ background-color: {self.theme.get('surface_alt', '#f1f3f5')}; "
                f"border-left: 4px solid {self.theme.get('error', '#e74c3c')}; "
                f"border-radius: 4px; padding: 4px; }} "
                f"QFrame QLabel {{ background: transparent; padding: 0; }}"
            )
            bl = QVBoxLayout(box)
            bl.setContentsMargins(10, 6, 10, 6)
            bl.setSpacing(2)
            lbl = QLabel(block.get("text", ""))
            lbl.setStyleSheet(
                f"color: {text_color}; font-size: 10pt; background: transparent;"
            )
            lbl.setWordWrap(True)
            bl.addWidget(lbl)
            return box

        if btype == "code":
            box = QFrame()
            box.setStyleSheet(
                f"QFrame {{ background-color: {self.theme.get('bg_sidebar', '#eef1f5')}; "
                f"border: 1px solid {self.theme.get('border', '#d0d7de')}; "
                f"border-radius: 6px; }}"
            )
            bl = QVBoxLayout(box)
            bl.setContentsMargins(12, 8, 12, 8)
            bl.setSpacing(2)
            lbl = QLabel(block.get("text", ""))
            lbl.setStyleSheet(
                f"font-family: 'Cascadia Code', 'Consolas', monospace; "
                f"font-size: 9pt; color: {text_color}; "
                f"background: transparent; padding: 0;"
            )
            lbl.setWordWrap(True)
            lbl.setTextInteractionFlags(Qt.TextSelectableByMouse)
            bl.addWidget(lbl)
            return box

        if btype == "table":
            table = QTableWidget()
            rows = block.get("rows", [])
            if not rows:
                return None
            table.setRowCount(len(rows))
            table.setColumnCount(len(rows[0]))
            for r, row in enumerate(rows):
                for c, val in enumerate(row):
                    item = QTableWidgetItem(str(val))
                    if r == 0:
                        item.setFont(QFont("Microsoft YaHei UI", 10, QFont.Bold))
                        item.setBackground(QColor(self.theme.get("surface_alt", "#f1f3f5")))
                        item.setForeground(QColor(self.theme.get("accent", "#0f4c75")))
                    else:
                        item.setForeground(QColor(text_color))
                    table.setItem(r, c, item)
            table.horizontalHeader().setVisible(False)
            table.verticalHeader().setVisible(False)
            table.setShowGrid(True)
            table.setStyleSheet(
                f"QTableWidget {{ background-color: {self.theme.get('surface', '#ffffff')}; "
                f"gridline-color: {self.theme.get('border', '#d0d7de')}; "
                f"border: 1px solid {self.theme.get('border', '#d0d7de')}; "
                f"border-radius: 6px; font-size: 10pt; "
                f"selection-background-color: {self.theme.get('selection', '#cfe2ff')}; }} "
                f"QTableWidget::item {{ padding: 6px 10px; }}"
            )
            table.setEditTriggers(QAbstractItemView.NoEditTriggers)
            table.horizontalHeader().setSectionResizeMode(QHeaderView.Stretch)
            table.verticalHeader().setDefaultSectionSize(30)
            table.setMinimumHeight(40 + len(rows) * 30)
            return table

        return None

    def _refresh_tutorial_subpage_style(self):
        """主题切换时刷新教程子页"""
        if hasattr(self, 'tutorial_hero'):
            self.tutorial_hero.setStyleSheet(self._tutorial_hero_style())
        if hasattr(self, 'tutorial_nav'):
            self.tutorial_nav.setStyleSheet(self._tutorial_nav_style())
            # 重新渲染当前章节
            row = self.tutorial_nav.currentRow()
            if row >= 0:
                self._on_tutorial_section_changed(row)

    # ============== 反馈子页 ==============
    def _build_feedback_subpage(self) -> QWidget:
        page = QWidget()
        v = QVBoxLayout(page)
        v.setContentsMargins(20, 20, 20, 20)
        v.setSpacing(12)
        title = QLabel("💬 请选择反馈方式")
        title.setProperty("role", "title")
        title.setAlignment(Qt.AlignCenter)
        v.addWidget(title)
        info = QLabel(
            "感谢您使用本软件!\\n"
            "请选择反馈问题的渠道:\\n\\n"
            "💬 QQ 群反馈 (推荐) - 实时交流、问题解答\\n"
            "📡 QQ 频道 - 优先获取更新与公告\\n"
            "🐙 GitHub 反馈 - Bug 报告、功能建议"
        )
        info.setProperty("role", "caption")
        info.setWordWrap(True)
        info.setAlignment(Qt.AlignCenter)
        v.addWidget(info)
        v.addSpacing(8)
        qq_btn = FluentButton("💬 QQ 群反馈 (推荐)", "accent")
        qq_btn.setMinimumHeight(48)
        qq_btn.clicked.connect(
            lambda: webbrowser.open(FeedbackDialog.QQ_GROUP_URL))
        v.addWidget(qq_btn)
        qch_btn = FluentButton("📡 QQ 频道", "accent")
        qch_btn.setMinimumHeight(48)
        qch_btn.clicked.connect(
            lambda: webbrowser.open(FeedbackDialog.QQ_CHANNEL_URL))
        v.addWidget(qch_btn)
        gh_btn = FluentButton("🐙 GitHub 反馈", "secondary")
        gh_btn.setStyleSheet(
            "QPushButton { background-color: #24292e; color: white; "
            "border: none; border-radius: 8px; padding: 10px; "
            "font-size: 11pt; font-weight: 500; min-height: 48px; }"
            "QPushButton:hover { background-color: #2f363d; }"
            "QPushButton:pressed { background-color: #1b1f23; }"
        )
        gh_btn.setMinimumHeight(48)
        gh_btn.clicked.connect(
            lambda: webbrowser.open(FeedbackDialog.GITHUB_URL))
        v.addWidget(gh_btn)
        v.addStretch()
        return page

    # ============== 添加实例子页 ==============
    def _build_add_instance_subpage(self) -> QWidget:
        page = QWidget()
        v = QVBoxLayout(page)
        v.setContentsMargins(20, 16, 20, 16)
        v.setSpacing(10)
        title = QLabel("➕ 添加新游戏实例")
        title.setProperty("role", "title")
        v.addWidget(title)
        v.addWidget(QLabel("实例名称:"))
        self.add_inst_name_edit = QLineEdit()
        self.add_inst_name_edit.setPlaceholderText("例: 原版-纯净、3.3.6-联机")
        v.addWidget(self.add_inst_name_edit)
        v.addWidget(QLabel("游戏路径(需含 mentalOmega.exe):"))
        path_row = QHBoxLayout()
        self.add_inst_path_edit = QLineEdit()
        self.add_inst_path_edit.setPlaceholderText("D:\\Games\\MentalOmega")
        path_row.addWidget(self.add_inst_path_edit, 1)
        browse_btn = FluentButton("📁 浏览", "secondary")
        browse_btn.clicked.connect(self._browse_add_instance_path)
        path_row.addWidget(browse_btn)
        v.addLayout(path_row)
        tip = QLabel("💡 提示: 名称不能与现有实例重复;游戏路径不能含中文")
        tip.setProperty("role", "caption")
        tip.setWordWrap(True)
        v.addWidget(tip)
        v.addStretch()
        btn_row = QHBoxLayout()
        btn_row.addStretch()
        cancel_btn = FluentButton("取消", "secondary")
        cancel_btn.clicked.connect(self._back_to_main)
        btn_row.addWidget(cancel_btn)
        ok_btn = FluentButton("✅ 添加", "accent")
        ok_btn.clicked.connect(self._submit_add_instance)
        btn_row.addWidget(ok_btn)
        v.addLayout(btn_row)
        return page

    def _browse_add_instance_path(self):
        path = QFileDialog.getExistingDirectory(self, "选择游戏目录")
        if path:
            self.add_inst_path_edit.setText(path)

    def _submit_add_instance(self):
        name = self.add_inst_name_edit.text().strip()
        path = self.add_inst_path_edit.text().strip()
        if not name or not path:
            QMessageBox.warning(self, "警告", "请填写实例名称和游戏路径")
            return
        ok, msg = self.instance_manager.create_instance(name, path)
        if not ok:
            QMessageBox.warning(self, "错误", msg)
            return
        QMessageBox.information(self, "成功", f"实例『{name}』创建成功")
        if hasattr(self, '_inline_refresh_instance_list'):
            self._inline_refresh_instance_list()
        if hasattr(self, 'update_instance_combo'):
            self.update_instance_combo()
        if hasattr(self, '_refresh_all_package_tabs'):
            self._refresh_all_package_tabs()
        self.add_inst_name_edit.clear()
        self.add_inst_path_edit.clear()
        self._back_to_main()

    # ============== 重命名实例子页 ==============
    def _build_rename_instance_subpage(self) -> QWidget:
        page = QWidget()
        v = QVBoxLayout(page)
        v.setContentsMargins(20, 16, 20, 16)
        v.setSpacing(10)
        title = QLabel("✏️ 重命名实例")
        title.setProperty("role", "title")
        v.addWidget(title)
        current = self.instance_manager.get_current_instance()
        v.addWidget(QLabel("当前实例:"))
        cur_lbl = QLabel(current.name if current else "(无)")
        cur_lbl.setStyleSheet("font-weight: 600; color: #3498db;")
        v.addWidget(cur_lbl)
        v.addWidget(QLabel("新名称:"))
        self.rename_edit = QLineEdit(current.name if current else "")
        v.addWidget(self.rename_edit)
        v.addStretch()
        btn_row = QHBoxLayout()
        btn_row.addStretch()
        cancel_btn = FluentButton("取消", "secondary")
        cancel_btn.clicked.connect(self._back_to_main)
        btn_row.addWidget(cancel_btn)
        ok_btn = FluentButton("✅ 确认", "accent")
        ok_btn.clicked.connect(self._submit_rename)
        btn_row.addWidget(ok_btn)
        v.addLayout(btn_row)
        return page

    def _submit_rename(self):
        current = self.instance_manager.get_current_instance()
        if not current:
            QMessageBox.warning(self, "警告", "没有选中的实例")
            return
        new_name = self.rename_edit.text().strip()
        if not new_name:
            QMessageBox.warning(self, "警告", "请输入新名称")
            return
        if new_name == current.name:
            return
        success, msg = self.instance_manager.rename_instance(current.id, new_name)
        if not success:
            QMessageBox.warning(self, "错误", msg)
            return
        QMessageBox.information(self, "成功", f"已重命名: {new_name}")
        if hasattr(self, '_inline_refresh_instance_list'):
            self._inline_refresh_instance_list()
        if hasattr(self, 'update_instance_combo'):
            self.update_instance_combo()
        self._back_to_main()

    def _build_statusbar(self):
        sb = QStatusBar()
        self.setStatusBar(sb)
        # 状态栏字体略小
        sb_font = QFont("Microsoft YaHei UI", 9)
        sb.setFont(sb_font)

        self.status_label = QLabel("就绪")
        self.status_label.setFont(sb_font)
        sb.addWidget(self.status_label, 1)

        self.instance_info_label = QLabel("未选择实例")
        self.instance_info_label.setFont(sb_font)
        sb.addPermanentWidget(self.instance_info_label)

        sep = QFrame()
        sep.setFrameShape(QFrame.VLine)
        sep.setFrameShadow(QFrame.Plain)
        sb.addPermanentWidget(sep)

        self.version_label = QLabel("正式版 v2.2")
        self.version_label.setProperty("role", "caption")
        self.version_label.setFont(sb_font)
        sb.addPermanentWidget(self.version_label)

    # ---------- 导航操作 ----------
    def update_instance_combo(self):
        # 同步包管理页与主页的 combobox
        combos = [c for c in (getattr(self, 'instance_combo', None),
                              getattr(self, 'home_instance_combo', None)) if c is not None]
        for combo in combos:
            combo.blockSignals(True)
            combo.clear()
        instances = self.instance_manager.get_instance_list()
        for inst in instances:
            for combo in combos:
                combo.addItem(inst.name, inst.id)
        # 保持当前选择
        current = self.instance_manager.get_current_instance()
        if current:
            for combo in combos:
                for i in range(combo.count()):
                    if combo.itemData(i) == current.id:
                        combo.setCurrentIndex(i)
                        break
        for combo in combos:
            combo.blockSignals(False)
        # 同步主页启动按钮(下拉列表 + 当前实例显示)
        if hasattr(self, 'home_page') and self.home_page is not None:
            try:
                self.home_page.set_instances(instances, current)
            except Exception as e:
                log_warn("App", f"同步主页启动按钮失败: {e}")
        self._refresh_instance_info()

    def _refresh_instance_info(self):
        current = self.instance_manager.get_current_instance()
        if current:
            self.instance_info_label.setText(f"  🎮 {current.name}  ")
            if hasattr(self, 'home_instance_label'):
                self.home_instance_label.setText(f"📂 路径: {current.path}")
        else:
            self.instance_info_label.setText("  未选择实例  ")
            if hasattr(self, 'home_instance_label'):
                self.home_instance_label.setText("未选择实例 (请到 🎮 实例管理 添加)")

    def _on_instance_changed(self, idx):
        """切换实例(包管理页 combobox 触发)"""
        if idx < 0 or not hasattr(self, 'instance_combo') or self.instance_combo.count() == 0:
            return
        inst_id = self.instance_combo.itemData(idx)
        if not inst_id:
            return
        # 已是当前实例则跳过,避免无意义的清空 current
        if (self.instance_manager.current_instance
                and self.instance_manager.current_instance.id == inst_id):
            return
        self._on_instance_changed_external(inst_id, source_idx=idx)

    def _on_instance_changed_external(self, inst_id, source_idx=None):
        """实例切换后的统一刷新:
        - 同步所有 combobox
        - 刷新主页实例信息
        - 刷新所有包管理页
        - 刷新内嵌实例列表的 ✅ 标记

        source_idx: 触发源的 combobox 索引,用于同步其它 combo 时避开回环。
        """
        self.instance_manager.set_current_instance(inst_id)
        # 同步所有 combobox(避开触发源,避免回环触发)
        for attr, combo in (('instance_combo', getattr(self, 'instance_combo', None)),
                            ('home_instance_combo', getattr(self, 'home_instance_combo', None))):
            if combo is None:
                continue
            # 找到目标实例对应的索引
            target_idx = -1
            for i in range(combo.count()):
                if combo.itemData(i) == inst_id:
                    target_idx = i
                    break
            if target_idx < 0:
                continue
            if combo.currentIndex() == target_idx:
                continue
            combo.blockSignals(True)
            combo.setCurrentIndex(target_idx)
            combo.blockSignals(False)
        self._refresh_instance_info()
        self._refresh_all_package_tabs()
        self._inline_refresh_instance_list()
        # 刷新包管理页的实例选择卡片
        self._refresh_pkg_instance_card()

    def _on_home_instance_changed(self, idx):
        """主页 combobox 切换: 同步到主 combobox"""
        if idx < 0 or not hasattr(self, 'home_instance_combo') or self.home_instance_combo.count() == 0:
            return
        inst_id = self.home_instance_combo.itemData(idx)
        if not inst_id:
            return
        if hasattr(self, 'instance_combo') and self.instance_combo.currentIndex() != idx:
            self.instance_combo.blockSignals(True)
            self.instance_combo.setCurrentIndex(idx)
            self.instance_combo.blockSignals(False)
        self._on_instance_changed(idx)

    def _refresh_all_package_tabs(self):
        for tab in self.package_tabs.values():
            tab.refresh_lists()
        # 同步刷新内嵌实例列表
        if hasattr(self, '_inline_refresh_instance_list'):
            self._inline_refresh_instance_list()

    def resizeEvent(self, event):
        """响应式: 根据窗口宽度调整侧边栏与按钮文字(防抖 + 避免 setText 死循环)"""
        super().resizeEvent(event)
        if not hasattr(self, 'sidebar') or not hasattr(self, 'nav_buttons'):
            return
        w = self.width()
        # 三档: 窄(<960) 折叠 80px, 中(<1280) 200px, 宽(>=1280) 240px
        if w < 960:
            target_w, compact = 80, True
        elif w < 1280:
            target_w, compact = 200, False
        else:
            target_w, compact = 240, False
        # 防抖: 宽度不变时不调用 setFixedWidth (避免触发再次 resize)
        if self.sidebar.width() != target_w:
            self.sidebar.setFixedWidth(target_w)
        # 防抖: 紧凑模式状态不变时不重新 setText
        prev = getattr(self, '_last_compact', None)
        if prev == compact:
            return
        self._last_compact = compact
        # 导航按钮: 紧凑模式只显示图标
        for btn in self.nav_buttons.values():
            full = btn.property("full_text") or btn.text()
            icon = btn.property("icon_only") or (full.split(" ")[0] if " " in full else full)
            new_text = icon if compact else full
            if btn.text() != new_text:
                btn.setText(new_text)
            # 紧凑模式下不强制 tooltip,完整模式也不重复设
        # 侧边栏内的额外按钮
        for attr in ('tutorial_btn', 'feedback_btn', 'settings_btn', 'about_btn',
                     'resource_dl_btn', 'runtime_env_btn', 'program_extend_btn', 'upload_res_btn'):
            b = getattr(self, attr, None)
            if b is None:
                continue
            full = b.property("full_text") or b.text()
            icon = b.property("icon_only") or (full.split(" ")[0] if " " in full else full)
            new_text = icon if compact else full
            if b.text() != new_text:
                b.setText(new_text)
        # 紧凑模式下隐藏分组标题与状态文字
        for lbl in getattr(self, 'sidebar_labels', []):
            lbl.setVisible(not compact)
        # 主页 grid 列数: <900 1 列, <1200 2 列, 否则 3 列
        if hasattr(self, 'home_grid'):
            grid_w = max(0, w - target_w - 40)
            if grid_w < 600:
                new_cols = 1
            elif grid_w < 900:
                new_cols = 2
            else:
                new_cols = 3
            cur_cols = getattr(self, '_home_grid_cols', None)
            if cur_cols == new_cols:
                return
            self._home_grid_cols = new_cols
            cards = getattr(self, '_home_cards', [])
            # 先全部 remove
            for c in cards:
                self.home_grid.removeWidget(c)
            # 重新添加
            for i, c in enumerate(cards):
                self.home_grid.addWidget(c, i // new_cols, i % new_cols)

    # ---------- 子窗口 ----------
    def _open_instance_management(self):
        # 实例管理已内嵌到主窗口(content_stack idx 2),不再弹窗
        self._last_main_index = 2
        self._switch_page(2)

    def _open_settings(self):
        # 设置页已内嵌到主窗口(content_stack idx 3),不再弹窗
        self._last_main_index = 3
        self._switch_page(3)

    def _open_about(self):
        """关于(已内嵌)→ 切到关于页(不再弹新窗口)"""
        self._last_main_index = 4
        self._switch_page(4)

    def _open_tutorial(self):
        # 教程已内嵌到主窗口(content_stack idx 5 子页)
        self._last_main_index = 0
        self._switch_page(5)

    def _open_shout(self):
        """打开联机喊话对话框"""
        dialog = ShoutDialog(self, self)
        dialog.finished.connect(lambda _: None)  # 信号在 closeEvent 已断开
        dialog.exec()

    def _open_feedback(self):
        # 反馈已内嵌到主窗口(content_stack idx 6 子页),不再弹窗
        self._last_main_index = 0
        self._switch_page(6)

    def _show_upload_dialog(self):
        """显示上传资源选项对话框"""
        dialog = QDialog(self)
        dialog.setWindowTitle("📤 上传资源")
        dialog.setMinimumWidth(420)
        dialog.setModal(True)

        dv = QVBoxLayout(dialog)
        dv.setContentsMargins(24, 20, 24, 20)
        dv.setSpacing(16)

        title = QLabel("📤 上传资源")
        title.setStyleSheet("font-size: 14pt; font-weight: 700;")
        dv.addWidget(title)

        desc = QLabel(
            "请选择要上传的资源类型，点击按钮将在浏览器中打开对应的上传页面。")
        desc.setWordWrap(True)
        desc.setStyleSheet("font-size: 10pt; color: #666; padding: 4px 0;")
        dv.addWidget(desc)

        dv.addSpacing(8)

        # 上传社区游戏资源
        btn_game = QPushButton("📦 上传社区游戏资源\n用浏览器打开")
        btn_game.setCursor(QCursor(Qt.PointingHandCursor))
        btn_game.setMinimumHeight(60)
        btn_game.setStyleSheet("""
            QPushButton {
                background: qlineargradient(x1:0, y1:0, x2:1, y2:0,
                    stop:0 #0f4c75, stop:1 #1a5a8a);
                color: white; border: none; border-radius: 10px;
                padding: 12px 20px; font-size: 11pt; font-weight: 600;
                text-align: left;
            }
            QPushButton:hover {
                background: qlineargradient(x1:0, y1:0, x2:1, y2:0,
                    stop:0 #1a5a8a, stop:1 #2a6a9a);
            }
        """)
        btn_game.clicked.connect(lambda: self._open_upload_url(
            os.environ.get("ONEDRIVE_UPLOAD_GAME_URL", "").strip(),
            dialog))
        dv.addWidget(btn_game)

        # 上传社区DLC
        btn_dlc = QPushButton("🧩 上传社区DLC\n用浏览器打开")
        btn_dlc.setCursor(QCursor(Qt.PointingHandCursor))
        btn_dlc.setMinimumHeight(60)
        btn_dlc.setStyleSheet("""
            QPushButton {
                background: qlineargradient(x1:0, y1:0, x2:1, y2:0,
                    stop:0 #27ae60, stop:1 #2ecc71);
                color: white; border: none; border-radius: 10px;
                padding: 12px 20px; font-size: 11pt; font-weight: 600;
                text-align: left;
            }
            QPushButton:hover {
                background: qlineargradient(x1:0, y1:0, x2:1, y2:0,
                    stop:0 #219a52, stop:1 #27ae60);
            }
        """)
        btn_dlc.clicked.connect(lambda: self._open_upload_url(
            os.environ.get("ONEDRIVE_UPLOAD_DLC_URL", "").strip(),
            dialog))
        dv.addWidget(btn_dlc)

        dv.addSpacing(12)

        # 关闭按钮
        close_btn = FluentButton("取消", "secondary")
        close_btn.clicked.connect(dialog.reject)
        close_row = QHBoxLayout()
        close_row.addStretch()
        close_row.addWidget(close_btn)
        dv.addLayout(close_row)

        dialog.exec()

    def _open_upload_url(self, url: str, dialog: QDialog = None):
        """在浏览器中打开上传链接，含错误处理"""
        self.statusBar().showMessage("⏳ 正在打开浏览器...", 3000)
        QApplication.processEvents()
        try:
            success = webbrowser.open(url)
            if not success:
                QMessageBox.warning(self, "打开失败",
                    "无法打开浏览器。\n\n"
                    "建议: 请检查默认浏览器设置，或手动复制以下链接在浏览器中打开：\n\n"
                    f"{url}")
            else:
                self.statusBar().showMessage("✅ 已在浏览器中打开上传页面", 5000)
        except Exception as e:
            QMessageBox.critical(self, "错误",
                f"打开浏览器时发生错误:\n{str(e)[:300]}\n\n"
                "建议: 请手动复制以下链接在浏览器中打开：\n\n"
                f"{url}")
        # 关闭对话框
        if dialog is not None:
            QTimer.singleShot(300, dialog.accept)

    # ---------- 包操作 ----------
    def _install_package(self, name: str, package_type: str):
        """安装包处理:
        - 普通文件/目录:直接复制到实例目录
        - .zip / .7z / .rar:先解压到临时目录,再复制到实例目录
        - 全程进度反馈 + 错误计数 + 完整性校验
        - 关键 UI 刷新用 QTimer.singleShot 派发到主线程,避免跨线程操作
        """
        try:
            # 即时反馈:让用户看到按钮已被响应
            self.statusBar().showMessage(f"📦 准备安装: {name} ({package_type})")
            QApplication.processEvents()
            self._install_package_impl(name, package_type)
        except Exception as e:
            import traceback
            traceback.print_exc()
            QMessageBox.critical(self, "安装错误", f"安装过程发生异常: {e}")
            self.statusBar().showMessage(f"❌ 安装异常: {e}", 5000)

    def _install_package_impl(self, name: str, package_type: str):
        import threading
        current = self.instance_manager.get_current_instance()
        if not current:
            self._show_no_instance_warning()
            return
        # 再次校验实例目录(可能上次启动时有效,现在已被移动/卸载)
        if not os.path.isdir(current.path):
            QMessageBox.warning(
                self, "实例目录无效",
                f"当前实例的游戏目录已不存在或被移动:\n{current.path}\n\n"
                f"请检查游戏是否还在原位置,或在 [🎮 实例管理] 中更新路径。"
            )
            self.statusBar().showMessage("❌ 实例目录无效", 5000)
            return

        src = os.path.join(self.get_package_dir(package_type), name)
        if not os.path.exists(src):
            QMessageBox.warning(self, "警告", f"包文件不存在: {src}")
            return
        log_info("Install", f"源文件: {src} | 类型: {package_type} | 实例: {current.path}")

        ext = os.path.splitext(name)[1].lower()
        is_archive = os.path.isfile(src) and ext in (".zip", ".7z", ".rar")

        # 二次确认
        kind_label = "压缩包" if is_archive else "资源包"
        install_target = os.path.join(current.path, "Maps", "Custom") if package_type == "map" else current.path
        # 压缩包安装前弹出文件预览（搬运许可.jpg / 说明.txt）
        if is_archive:
            dlg = PackagePreviewDialog(self, src, name)
            dlg.exec()
        ret = QMessageBox.question(
            self, "确认安装",
            f"即将安装 {kind_label}:\n  {name}\n\n到实例:\n  {current.name}\n  {install_target}\n\n是否继续?",
            QMessageBox.Yes | QMessageBox.No, QMessageBox.Yes
        )
        if ret != QMessageBox.Yes:
            return

        # 进度对话框
        progress = QProgressDialog("正在准备安装...", "取消", 0, 100, self)
        progress.setWindowTitle("安装包")
        progress.setWindowModality(Qt.WindowModal)
        progress.setMinimumDuration(0)
        progress.setValue(0)
        self.statusBar().showMessage(f"正在安装: {name}")

        def do_install():
            extract_dir = None
            try:
                # 1) 若是压缩包,先解压到临时目录
                if is_archive:
                    if ext == ".zip":
                        try:
                            if not zipfile.is_zipfile(src):
                                QMessageBox.critical(self, "错误", f"ZIP 文件已损坏或格式错误:\n{src}")
                                self.statusBar().showMessage("❌ 安装失败: 压缩包格式错误", 5000)
                                return
                        except Exception as e:
                            QMessageBox.critical(self, "错误", f"读取 ZIP 文件失败: {e}")
                            self.statusBar().showMessage("❌ 安装失败: 读取文件错误", 5000)
                            return
                    if ext == ".7z" and not SEVENZIP_AVAILABLE:
                        QMessageBox.critical(
                            self, "错误",
                            "7z 支持不可用,请先安装 py7zr 库:\n  pip install py7zr"
                        )
                        self.statusBar().showMessage("❌ 安装失败: 缺少 py7zr", 5000)
                        return
                    if ext == ".rar" and not RARFILE_AVAILABLE:
                        QMessageBox.critical(
                            self, "错误",
                            "RAR 支持不可用,请先安装 rarfile 库:\n  pip install rarfile"
                        )
                        self.statusBar().showMessage("❌ 安装失败: 缺少 rarfile", 5000)
                        return

                    progress.setLabelText("正在解压压缩包...")
                    progress.setValue(5)
                    QApplication.processEvents()
                    if progress.wasCanceled():
                        self.statusBar().showMessage("⚠️ 安装已取消", 5000)
                        return

                    extract_dir = os.path.join(
                        self.base_path, "temp",
                        f"install_{datetime.now().strftime('%Y%m%d_%H%M%S_%f')}"
                    )
                    os.makedirs(extract_dir, exist_ok=True)
                    extracted_files = []  # 用于日志
                    try:
                        if ext == ".zip":
                            with zipfile.ZipFile(src, "r") as zf:
                                members = zf.namelist()
                                total = max(len(members), 1)
                                for i, m in enumerate(members):
                                    if progress.wasCanceled():
                                        raise InterruptedError("用户取消")
                                    try:
                                        zf.extract(m, extract_dir)
                                        extracted_files.append(m)
                                    except Exception as ee:
                                                log_warn("Install", f"解压跳过 {m}: {ee}")
                                    if (i + 1) % 50 == 0 or (i + 1) == total:
                                        pct = 5 + int((i + 1) / total * 25)
                                        progress.setValue(min(pct, 30))
                                        progress.setLabelText(f"正在解压... ({i+1}/{total})\n{m}")
                                        QApplication.processEvents()
                        elif ext == ".7z":
                            with py7zr.SevenZipFile(src, mode="r") as sz:
                                sz.extractall(path=extract_dir)
                                for root, _, files in os.walk(extract_dir):
                                    for f in files:
                                        extracted_files.append(os.path.relpath(os.path.join(root, f), extract_dir))
                                progress.setValue(30)
                                progress.setLabelText(f"解压完成,共 {len(extracted_files)} 个文件")
                                QApplication.processEvents()
                        elif ext == ".rar":
                            with rarfile.RarFile(src, "r") as rf:
                                members = rf.namelist()
                                total = max(len(members), 1)
                                for i, m in enumerate(members):
                                    if progress.wasCanceled():
                                        raise InterruptedError("用户取消")
                                    try:
                                        rf.extract(m, extract_dir)
                                        extracted_files.append(m)
                                    except Exception as ee:
                                                log_warn("Install", f"解压跳过 {m}: {ee}")
                                    if (i + 1) % 50 == 0 or (i + 1) == total:
                                        pct = 5 + int((i + 1) / total * 25)
                                        progress.setValue(min(pct, 30))
                                        progress.setLabelText(f"正在解压... ({i+1}/{total})\n{m}")
                                        QApplication.processEvents()
                    except InterruptedError:
                        if extract_dir and os.path.isdir(extract_dir):
                            shutil.rmtree(extract_dir, ignore_errors=True)
                        self.statusBar().showMessage("⚠️ 安装已取消", 5000)
                        return
                    except Exception as e:
                        if extract_dir and os.path.isdir(extract_dir):
                            shutil.rmtree(extract_dir, ignore_errors=True)
                        # 区分损坏 vs 一般错误
                        ename = type(e).__name__
                        is_corrupt = any(t in ename for t in (
                            "BadZipFile", "Bad7zFile", "BadRarFile",
                            "ZipFileError", "LZMAError", "ReadError"
                        ))
                        if is_corrupt:
                            QMessageBox.critical(
                                self, "错误",
                                f"压缩包已损坏或格式错误:\n{type(e).__name__}: {e}\n\n"
                                f"源文件: {src}\n\n"
                                f"请重新下载该压缩包后重试。"
                            )
                            self.statusBar().showMessage("❌ 安装失败: 压缩包损坏", 5000)
                        else:
                            QMessageBox.critical(self, "错误", f"解压失败: {e}")
                            self.statusBar().showMessage(f"❌ 安装失败: 解压错误 {e}", 5000)
                        return

                    # 校验解压结果
                    try:
                        all_entries = os.listdir(extract_dir)
                    except Exception as e:
                        shutil.rmtree(extract_dir, ignore_errors=True)
                        QMessageBox.critical(self, "错误", f"读取解压结果失败: {e}")
                        self.statusBar().showMessage("❌ 安装失败: 读取解压结果失败", 5000)
                        return
                    if not all_entries:
                        shutil.rmtree(extract_dir, ignore_errors=True)
                        QMessageBox.critical(self, "错误", "压缩包内容为空,未解压到任何文件")
                        self.statusBar().showMessage("❌ 安装失败: 压缩包为空", 5000)
                        return

                    log_info("Install", f"解压完成,共 {len(extracted_files)} 个文件")
                    progress.setLabelText(f"解压完成: {len(extracted_files)} 个文件,正在复制...")
                    progress.setValue(32)
                    QApplication.processEvents()

                    src_to_copy = extract_dir
                    # 智能识别: 若解压后只有一个顶层目录,直接以该目录为根
                    try:
                        if len(all_entries) == 1:
                            only = os.path.join(extract_dir, all_entries[0])
                            if os.path.isdir(only):
                                src_to_copy = only
                    except Exception:
                        pass
                else:
                    src_to_copy = src

                if progress.wasCanceled():
                    if extract_dir and os.path.isdir(extract_dir):
                        shutil.rmtree(extract_dir, ignore_errors=True)
                    self.statusBar().showMessage("⚠️ 安装已取消", 5000)
                    return

                # 2) 复制到实例目录
                #    .map 包: 安装到 <游戏目录>/Maps/Custom/
                #    压缩包(非 map): 直接解压到游戏根目录
                #    文件夹/单文件: 目标 = <current.path>/<name>
                if package_type == "map":
                    target = os.path.join(current.path, "Maps", "Custom")
                elif is_archive:
                    target = current.path
                elif os.path.isdir(src_to_copy):
                    target = os.path.join(current.path, name)
                else:
                    target = os.path.join(current.path, name)

                # 文件级冲突检测:扫描解压结果与目标之间的冲突
                # 提供 "覆盖全部 / 跳过已有 / 取消" 三选项
                # 注意: 压缩包安装到游戏根目录时, target 就是游戏目录本身,
                # 绝不能删除整个游戏目录, 必须走文件级合并
                conflict_policy = "overwrite_all"  # 默认:目标不存在时的策略
                if os.path.exists(target):
                    # 若 target 是单文件,直接提示覆盖
                    if not os.path.isdir(target):
                        ret2 = QMessageBox.question(
                            self, "目标已存在",
                            f"目标位置已存在(文件):\n{target}\n\n是否覆盖?",
                            QMessageBox.Yes | QMessageBox.No, QMessageBox.No
                        )
                        if ret2 != QMessageBox.Yes:
                            if extract_dir and os.path.isdir(extract_dir):
                                shutil.rmtree(extract_dir, ignore_errors=True)
                            self.statusBar().showMessage("⚠️ 已取消覆盖", 3000)
                            return
                        try:
                            os.remove(target)
                        except Exception as e:
                            QMessageBox.critical(self, "错误", f"清理旧目标失败: {e}")
                            if extract_dir and os.path.isdir(extract_dir):
                                shutil.rmtree(extract_dir, ignore_errors=True)
                            return
                    elif target == current.path or package_type == "map":
                        # 游戏根目录 或 Maps/Custom: 文件级合并, 同名覆盖
                        conflict_policy = "overwrite_all"
                    else:
                        # target 是目录:扫描文件级冲突
                        progress.setLabelText("正在扫描文件冲突...")
                        QApplication.processEvents()
                        # 仅当 src_to_copy 是目录时才做细粒度扫描
                        if os.path.isdir(src_to_copy):
                            conflicts, conflict_total = self.scan_install_conflicts(
                                src_to_copy, target
                            )
                        else:
                            conflicts, conflict_total = [], 0

                        if conflict_total == 0:
                            # 没有冲突,直接走"覆盖整个目标"逻辑
                            box = QMessageBox(self)
                            box.setIcon(QMessageBox.Question)
                            box.setWindowTitle("目标已存在")
                            box.setText(f"目标目录已存在:\n{target}\n\n未检测到文件级冲突,是否完全替换?")
                            box.setInformativeText(
                                "• 替换:删除原目录后重新安装\n"
                                "• 取消:中止本次安装"
                            )
                            btn_replace = box.addButton("替换", QMessageBox.AcceptRole)
                            btn_cancel = box.addButton("取消", QMessageBox.RejectRole)
                            box.setDefaultButton(btn_replace)
                            box.exec()
                            clicked = box.clickedButton()
                            if clicked is not btn_replace:
                                if extract_dir and os.path.isdir(extract_dir):
                                    shutil.rmtree(extract_dir, ignore_errors=True)
                                self.statusBar().showMessage("⚠️ 已取消安装", 3000)
                                return
                            try:
                                shutil.rmtree(target, ignore_errors=False)
                            except Exception as e:
                                QMessageBox.critical(self, "错误", f"清理旧目标失败: {e}")
                                if extract_dir and os.path.isdir(extract_dir):
                                    shutil.rmtree(extract_dir, ignore_errors=True)
                                return
                            conflict_policy = "overwrite_all"
                        else:
                            # 有冲突,弹出三选项
                            sample = "\n".join(f"  • {c}" for c in conflicts[:10])
                            if conflict_total > 10:
                                sample += f"\n  ... 还有 {conflict_total - 10} 个文件"
                            box = QMessageBox(self)
                            box.setIcon(QMessageBox.Warning)
                            box.setWindowTitle("检测到文件冲突")
                            box.setText(
                                f"目标目录已存在,且与本次安装有 {conflict_total} 个文件冲突:\n{target}\n\n请选择处理方式:"
                            )
                            box.setInformativeText(
                                f"{sample}\n\n"
                                f"• 覆盖全部:删除目标目录中所有冲突文件,然后安装新版本\n"
                                f"• 跳过已有:保留目标目录中的现有文件,只安装新文件\n"
                                f"• 取消:中止本次安装"
                            )
                            btn_overwrite = box.addButton("覆盖全部", QMessageBox.AcceptRole)
                            btn_skip = box.addButton("跳过已有", QMessageBox.RejectRole)
                            btn_cancel = box.addButton("取消", QMessageBox.DestructiveRole)
                            box.setDefaultButton(btn_overwrite)
                            box.exec()
                            clicked = box.clickedButton()
                            if clicked is btn_cancel:
                                if extract_dir and os.path.isdir(extract_dir):
                                    shutil.rmtree(extract_dir, ignore_errors=True)
                                self.statusBar().showMessage("⚠️ 已取消安装", 3000)
                                return
                            if clicked is btn_skip:
                                conflict_policy = "skip_existing"
                            else:
                                # 覆盖全部:删除整个目标目录后重新安装
                                try:
                                    shutil.rmtree(target, ignore_errors=False)
                                except Exception as e:
                                    QMessageBox.critical(self, "错误", f"清理旧目标失败: {e}")
                                    if extract_dir and os.path.isdir(extract_dir):
                                        shutil.rmtree(extract_dir, ignore_errors=True)
                                    return
                                conflict_policy = "overwrite_all"

                os.makedirs(target, exist_ok=True)
                skip_note = " (跳过已存在)" if conflict_policy == "skip_existing" else ""
                progress.setLabelText(f"正在安装到:\n{target}{skip_note}")
                last_pct = [32]
                def on_progress(cur, total, cur_file):
                    pct = 32 + int(cur / max(total, 1) * 60) if total else 32
                    if pct != last_pct[0]:
                        last_pct[0] = pct
                        progress.setValue(min(pct, 92))
                        try:
                            short_name = os.path.basename(cur_file)
                        except Exception:
                            short_name = "..."
                        progress.setLabelText(
                            f"正在复制到实例... ({cur}/{total})\n{short_name}"
                        )
                        QApplication.processEvents()
                if os.path.isfile(src_to_copy):
                    shutil.copy2(src_to_copy, target)
                    total, failed = 1, 0
                    ok = True
                else:
                    result = self.copy_files(src_to_copy, target, on_progress,
                                             conflict_policy=conflict_policy)
                    if isinstance(result, tuple) and len(result) == 3:
                        ok, total, failed = result
                    else:
                        ok, total, failed = (False, 0, 0)

                if progress.wasCanceled():
                    if extract_dir and os.path.isdir(extract_dir):
                        shutil.rmtree(extract_dir, ignore_errors=True)
                    self.statusBar().showMessage("⚠️ 安装已取消", 5000)
                    return

                if total == 0:
                    if extract_dir and os.path.isdir(extract_dir):
                        shutil.rmtree(extract_dir, ignore_errors=True)
                    QMessageBox.warning(self, "警告", "安装源中没有可复制的文件")
                    self.statusBar().showMessage("⚠️ 安装为空", 5000)
                    return
                if not ok and failed == total:
                    if extract_dir and os.path.isdir(extract_dir):
                        shutil.rmtree(extract_dir, ignore_errors=True)
                    QMessageBox.critical(
                        self, "错误",
                        f"安装失败:所有 {total} 个文件均无法复制"
                    )
                    self.statusBar().showMessage("❌ 安装失败", 5000)
                    return

                # 3) 完整性校验(简单: 目标目录非空 + 解压文件数 = 复制文件数)
                progress.setValue(95)
                progress.setLabelText("正在校验...")
                QApplication.processEvents()
                target_file_count = _dir_file_count(target)
                if target_file_count == 0:
                    if extract_dir and os.path.isdir(extract_dir):
                        shutil.rmtree(extract_dir, ignore_errors=True)
                    QMessageBox.critical(self, "错误", "安装后目标目录为空,可能复制失败")
                    self.statusBar().showMessage("❌ 安装失败: 目标为空", 5000)
                    return

                # 4) 清理临时解压目录
                if extract_dir and os.path.isdir(extract_dir):
                    shutil.rmtree(extract_dir, ignore_errors=True)

                # 5) 记录到实例已安装列表 + 持久化
                # 关键:installed_packages 是普通 dict,append 后必须 save
                if package_type not in current.installed_packages:
                    current.installed_packages[package_type] = []
                # 用去后缀名记录
                record_name = os.path.splitext(name)[0] if is_archive else name
                if record_name not in current.installed_packages[package_type]:
                    current.installed_packages[package_type].append(record_name)
                try:
                    self.instance_manager._save_instance_config(current)
                except Exception as se:
                    log_warn("Install", f"保存实例配置失败: {se}")
                    QMessageBox.warning(
                        self, "警告",
                        f"已安装到游戏目录,但保存安装记录失败:\n{se}\n\n"
                        f"重新启动程序后,'已安装'列表可能不显示该包。"
                    )

                # 5.5) 写入精确安装记录(用于按包卸载)
                try:
                    installed_files = list_target_files(target) if os.path.isdir(target) else []
                    if installed_files:
                        # 取本次安装涉及的文件 = 源目录的相对路径
                        try:
                            if os.path.isfile(src_to_copy):
                                src_files = [os.path.basename(src_to_copy)]
                            elif os.path.isdir(src_to_copy):
                                src_files = []
                                for r, _, files in os.walk(src_to_copy):
                                    for f in files:
                                        rel = os.path.relpath(os.path.join(r, f), src_to_copy)
                                        src_files.append(rel.replace('\\', '/'))
                            else:
                                src_files = []
                        except Exception:
                            src_files = []
                        # 安装记录只记录本次实际安装的源文件(而非整个目标)
                        save_install_record(
                            self.base_path, current, package_type, record_name,
                            files=src_files,
                            source_archive=name if is_archive else "",
                            original_snapshot={},
                        )
                except Exception as re:
                    log_info("Install", f"写入精确安装记录失败: {re}")

                # 6) 派发 UI 刷新到主线程(避免跨线程操作 Qt)
                def do_post_install_ui():
                    if package_type in self.package_tabs:
                        self.package_tabs[package_type].refresh_lists()
                    self.statusBar().showMessage(
                        f"✅ 安装完成 ({total} 个文件): {record_name}", 5000
                    )
                QTimer.singleShot(0, do_post_install_ui)

                # 7) 成功消息(主线程弹出)
                def show_success():
                    skipped = total - failed
                    # skipped 已含跳过存在文件的"成功"项;这里将"成功"分为"新建/覆盖"和"跳过"两类不准确,
                    # 因此只展示总数与失败数,以及策略提示。
                    policy_label = {
                        "overwrite_all": "覆盖模式",
                        "skip_existing": "跳过已有",
                    }.get(conflict_policy, "")
                    msg = f"已安装: {record_name}\n\n共处理 {total} 个文件"
                    if policy_label:
                        msg += f"\n冲突策略: {policy_label}"
                    if failed > 0:
                        msg += f"\n(其中 {failed} 个文件复制失败,已跳过)"
                    QMessageBox.information(self, "安装成功", msg)
                QTimer.singleShot(0, show_success)

                progress.setValue(100)
                QApplication.processEvents()
            except Exception as e:
                if extract_dir and os.path.isdir(extract_dir):
                    shutil.rmtree(extract_dir, ignore_errors=True)
                err_msg = f"安装失败: {type(e).__name__}: {e}"
                QMessageBox.critical(self, "错误", err_msg)
                self.statusBar().showMessage(f"❌ 安装失败: {e}", 5000)
                log_error("Install", f"异常: {traceback.format_exc()}")
            finally:
                progress.close()

        # 在主线程执行, 通过频繁的 processEvents() 保持 UI 响应
        do_install()

    def _show_no_instance_warning(self):
        """统一处理'未选择实例'的提示,同时引导用户到实例管理。"""
        reply = QMessageBox.question(
            self, "未选择实例",
            "当前没有可用的游戏实例。\n\n"
            "需要先添加至少一个游戏实例才能安装/卸载包。\n\n"
            "是否现在打开 [🎮 实例管理] 添加?",
            QMessageBox.Yes | QMessageBox.No, QMessageBox.Yes
        )
        if reply == QMessageBox.Yes:
            # 切换到实例管理页
            try:
                self._switch_page("instance")
            except Exception:
                # 回退方案:打开旧版弹窗
                self._open_instance_management()

    def _uninstall_package(self, name: str, package_type: str):
        """卸载处理:
        - 地图包(.map): 直接从 Maps/Custom 删除, 无需备份恢复
        - 其他包: 优先精确卸载(删除该包文件 + MO 备份恢复原版)
        - 无精确记录时退化为全量恢复
        """
        import threading
        current = self.instance_manager.get_current_instance()
        if not current:
            QMessageBox.warning(self, "警告", "请先选择游戏实例")
            return

        # 地图包: 专用卸载路径, 无需 MO 备份
        if package_type == "map":
            self._uninstall_map_package(current, name, package_type)
            return

        mo_backup = get_original_backup_path(self.base_path)
        if not os.path.isdir(mo_backup):
            QMessageBox.critical(
                self, "缺少原版备份",
                f"未在 MO 文件夹中找到原版游戏备份。\n\n"
                f"请先使用「备份原版游戏」功能,将原版游戏目录备份到:\n{mo_backup}\n\n"
                f"原版备份是卸载/恢复原版状态的必要条件。"
            )
            self.statusBar().showMessage("❌ 缺少原版游戏备份", 5000)
            return

        # 查找精确安装记录
        # 压缩包安装时记录名 = os.path.splitext(name)[0]
        record_name = os.path.splitext(name)[0]
        record = load_install_record(self.base_path, current, package_type, record_name)
        if not record:
            # 也尝试按原名查
            record = load_install_record(self.base_path, current, package_type, name)

        # 选择卸载模式
        if record and record.get("files"):
            # 选择性卸载
            self._uninstall_package_selective(
                current, package_type, name, record_name, record
            )
        else:
            # 全量恢复
            self._uninstall_package_full(current, package_type, name)

    def _uninstall_map_package(self, current, name, package_type):
        """卸载地图包: 直接从 <游戏目录>/Maps/Custom/ 删除 .map 文件, 无需备份恢复"""
        maps_custom = os.path.join(current.path, "Maps", "Custom")
        record_name = os.path.splitext(name)[0]
        record = load_install_record(self.base_path, current, package_type, record_name)
        if not record:
            record = load_install_record(self.base_path, current, package_type, name)

        # 确定要删除的文件列表
        files_to_delete = []
        if record and record.get("files"):
            # 精确记录: 按记录文件列表删除
            for rel in record["files"]:
                target_file = os.path.join(maps_custom, rel)
                if os.path.isfile(target_file):
                    files_to_delete.append(target_file)
        else:
            # 无精确记录: 尝试按名称匹配删除
            for ext in (".map", ".yrm", ".mpr"):
                guess = os.path.join(maps_custom, record_name + ext)
                if os.path.isfile(guess):
                    files_to_delete.append(guess)
                # 也尝试原名
                guess2 = os.path.join(maps_custom, name + ext) if not name.endswith(ext) else os.path.join(maps_custom, name)
                if os.path.isfile(guess2) and guess2 not in files_to_delete:
                    files_to_delete.append(guess2)

        if not files_to_delete:
            QMessageBox.information(
                self, "提示",
                f"在 Maps\\Custom 中未找到与「{name}」匹配的文件。\n\n"
                f"可能已被手动删除, 或包名不匹配。\n\n"
                f"将仅清除安装记录。"
            )
        else:
            # 确认删除
            file_list = "\n".join(f"  • {os.path.basename(f)}" for f in files_to_delete)
            ret = QMessageBox.question(
                self, "确认卸载地图",
                f"将删除 Maps\\Custom 中的以下文件:\n\n{file_list}\n\n确定继续?",
                QMessageBox.Yes | QMessageBox.No, QMessageBox.No
            )
            if ret != QMessageBox.Yes:
                return

            # 执行删除
            deleted = 0
            for f in files_to_delete:
                try:
                    os.remove(f)
                    deleted += 1
                    log_info("Uninstall", f"已删除地图文件: {f}")
                except Exception as e:
                    log_warn("Uninstall", f"删除地图文件失败: {f} | {e}")
            self.statusBar().showMessage(f"🗑️ 已删除 {deleted} 个地图文件", 5000)

        # 更新 installed_packages 列表
        for k in (record_name, name):
            if k in current.installed_packages.get(package_type, []):
                current.installed_packages[package_type].remove(k)

        # 删除安装记录
        for n in (record_name, name):
            delete_install_record(self.base_path, current, package_type, n)

        # 持久化
        try:
            self.instance_manager._save_instance_config(current)
        except Exception as e:
            log_warn("Uninstall", f"保存实例配置失败: {e}")

        # 刷新列表
        if package_type in self.package_tabs:
            self.package_tabs[package_type].refresh_lists()

    def _uninstall_package_selective(self, current, package_type, name, record_name, record):
        """选择性卸载:仅删除该包安装/修改的文件,MO 备份中存在的用 MO 备份还原"""
        import threading
        mo_backup = get_original_backup_path(self.base_path)
        target_path = current.path
        installed_files = record.get("files", [])
        # 二次确认
        file_count = len(installed_files)
        confirm = QMessageBox.question(
            self, "⚠️ 精确卸载确认",
            f"将基于安装记录进行精确卸载:\n\n"
            f"  包名: {name}\n"
            f"  类型: {package_type}\n"
            f"  安装时间: {record.get('install_time', '未知')}\n"
            f"  涉及文件: {file_count} 个\n\n"
            f"操作流程:\n"
            f"  1) 删除该包安装的所有文件: {target_path}\n"
            f"  2) 用 MO 原版备份还原被覆盖的原版文件\n"
            f"  3) 删除安装记录\n\n"
            f"⚠️ 此操作不可撤销!\n是否继续?",
            QMessageBox.Yes | QMessageBox.No, QMessageBox.No
        )
        if confirm != QMessageBox.Yes:
            self.statusBar().showMessage("⚠️ 已取消卸载", 3000)
            return

        # 进度对话框
        progress = QProgressDialog("正在准备精确卸载...", "取消", 0, 100, self)
        progress.setWindowTitle("精确卸载")
        progress.setWindowModality(Qt.WindowModal)
        progress.setMinimumDuration(0)
        progress.setValue(0)
        progress.setLabelText(f"正在卸载: {name}")
        self.statusBar().showMessage(f"正在精确卸载: {name}")

        def do_uninstall():
            try:
                QApplication.processEvents()
                if progress.wasCanceled():
                    self.statusBar().showMessage("⚠️ 卸载已取消", 5000)
                    return

                # 1) 删除该包安装的文件(已存在于 MO 备份的稍后还原)
                progress.setLabelText("正在删除包文件...")
                progress.setValue(5)
                QApplication.processEvents()

                deleted = 0
                delete_failed = 0
                to_restore = []  # 需要从 MO 备份还原的相对路径
                for rel in installed_files:
                    rel_norm = rel.replace('/', os.sep)
                    full = os.path.join(target_path, rel_norm)
                    # 是否存在于 MO 备份?
                    mo_file = os.path.join(mo_backup, rel_norm)
                    has_in_mo = os.path.isfile(mo_file)
                    if os.path.isfile(full):
                        try:
                            os.remove(full)
                            deleted += 1
                            if has_in_mo:
                                to_restore.append(rel_norm)
                        except (PermissionError, OSError) as e:
                            log_error("Uninstall", f"删除失败 {full}: {e}")
                            delete_failed += 1
                    elif not has_in_mo:
                        # 文件不存在(可能已经被用户删掉),且 MO 备份也没有 → 跳过
                        pass
                # 清理空目录(自下而上)
                cleaned_dirs = 0
                for rel in sorted(installed_files, key=lambda x: -x.count('/')):
                    rel_norm = rel.replace('/', os.sep)
                    parent = os.path.dirname(os.path.join(target_path, rel_norm))
                    while parent and os.path.isdir(parent) and parent != target_path:
                        try:
                            if not os.listdir(parent):
                                os.rmdir(parent)
                                cleaned_dirs += 1
                                parent = os.path.dirname(parent)
                            else:
                                break
                        except OSError:
                            break

                if progress.wasCanceled():
                    self.statusBar().showMessage("⚠️ 卸载已取消", 5000)
                    return

                # 2) 从 MO 备份还原被覆盖的原版文件
                last_pct = [40]
                def on_progress(cur, total, cur_file):
                    pct = 40 + int(cur / max(total, 1) * 50) if total else 40
                    if pct != last_pct[0]:
                        last_pct[0] = pct
                        progress.setValue(min(pct, 95))
                        try:
                            short_name = os.path.basename(cur_file)
                        except Exception:
                            short_name = "..."
                        progress.setLabelText(
                            f"正在还原原版文件... ({cur}/{total})\n{short_name}"
                        )
                        QApplication.processEvents()

                restore_failed = 0
                restored = 0
                if to_restore:
                    # 使用简单的复制(逐个)
                    for i, rel in enumerate(to_restore, 1):
                        if progress.wasCanceled():
                            break
                        src = os.path.join(mo_backup, rel)
                        dst = os.path.join(target_path, rel)
                        on_progress(i, len(to_restore), rel)
                        try:
                            os.makedirs(os.path.dirname(dst), exist_ok=True)
                            shutil.copy2(src, dst)
                            restored += 1
                        except (PermissionError, OSError) as e:
                            log_error("Uninstall", f"还原失败 {rel}: {e}")
                            restore_failed += 1

                if progress.wasCanceled():
                    self.statusBar().showMessage("⚠️ 卸载已取消", 5000)
                    return

                # 3) 更新实例已安装列表
                try:
                    for k in (record_name, name):
                        if k in current.installed_packages.get(package_type, []):
                            current.installed_packages[package_type].remove(k)
                    self.instance_manager._save_instance_config(current)
                except Exception as e:
                    log_info("Uninstall", f"更新实例配置失败: {e}")

                # 4) 删除安装记录
                try:
                    delete_install_record(self.base_path, current, package_type, record_name)
                    delete_install_record(self.base_path, current, package_type, name)
                except Exception as e:
                    log_info("Uninstall", f"删除安装记录失败: {e}")

                # 5) 校验
                progress.setValue(98)
                progress.setLabelText("正在校验...")
                QApplication.processEvents()
                if not self.is_mo_directory(target_path):
                    warn_msg = "⚠️ 警告:卸载后未能识别为有效的心灵终结游戏目录"
                else:
                    warn_msg = None

                # 6) 刷新 UI
                if hasattr(self, "package_tabs") and package_type in self.package_tabs:
                    self.package_tabs[package_type].refresh_lists()

                progress.setValue(100)
                QApplication.processEvents()

                msg = (f"精确卸载完成!\n\n"
                       f"包名: {name}\n"
                       f"删除文件: {deleted} 个(失败 {delete_failed})\n"
                       f"还原原版: {restored} 个(失败 {restore_failed})\n"
                       f"清理空目录: {cleaned_dirs} 个")
                if warn_msg:
                    msg += "\n\n" + warn_msg
                    QMessageBox.warning(self, "卸载完成(有警告)", msg)
                else:
                    QMessageBox.information(self, "卸载完成", msg)
                self.statusBar().showMessage(
                    f"✅ 精确卸载完成: {name}", 5000
                )
            except Exception as e:
                import traceback
                traceback.print_exc()
                QMessageBox.critical(self, "错误", f"卸载失败: {e}")
                self.statusBar().showMessage(f"❌ 卸载失败: {e}", 5000)
            finally:
                progress.close()

        threading.Thread(target=do_uninstall, daemon=True).start()

    def _uninstall_package_full(self, current, package_type, name):
        """全量恢复:清空目标目录 → 从 MO 备份复制(原始行为)"""
        import threading
        mo_backup = get_original_backup_path(self.base_path)
        # 二次确认(强警告)
        file_count = _dir_file_count(mo_backup)
        confirm = QMessageBox.question(
            self, "⚠️ 全量恢复确认",
            f"未找到该包的精确安装记录,将执行全量恢复(回退到原版游戏状态)。\n\n"
            f"操作流程:\n"
            f"  1) 彻底删除当前实例目录中的所有文件和子目录:\n"
            f"     {current.path}\n"
            f"  2) 从 MO 文件夹中复制原版游戏文件:\n"
            f"     {mo_backup}\n"
            f"  3) 校验文件复制结果\n\n"
            f"原版备份包含 {file_count} 个文件。\n\n"
            f"⚠️ 此操作不可撤销!\n是否继续?",
            QMessageBox.Yes | QMessageBox.No, QMessageBox.No
        )
        if confirm != QMessageBox.Yes:
            self.statusBar().showMessage("⚠️ 已取消卸载", 3000)
            return

        # 进度对话框
        progress = QProgressDialog("正在准备卸载...", "取消", 0, 100, self)
        progress.setWindowTitle("全量卸载(恢复原版)")
        progress.setWindowModality(Qt.WindowModal)
        progress.setMinimumDuration(0)
        progress.setValue(0)
        progress.setLabelText(f"即将从 MO 备份恢复:\n→ {current.path}")
        self.statusBar().showMessage("正在卸载(恢复原版)...")

        def do_uninstall():
            target_path = current.path
            try:
                os.makedirs(target_path, exist_ok=True)
                QApplication.processEvents()
                if progress.wasCanceled():
                    self.statusBar().showMessage("⚠️ 卸载已取消", 5000)
                    return

                # 1) 清理当前游戏目录
                progress.setLabelText("正在清理游戏目录...")
                progress.setValue(3)
                QApplication.processEvents()
                try:
                    for entry in os.listdir(target_path):
                        ep = os.path.join(target_path, entry)
                        try:
                            if os.path.isdir(ep) and not os.path.islink(ep):
                                shutil.rmtree(ep, ignore_errors=False)
                            else:
                                os.remove(ep)
                        except (PermissionError, OSError) as ce:
                            log_error("Uninstall", f"清理失败 {ep}: {ce}")
                except Exception as e:
                    QMessageBox.critical(self, "错误", f"清理游戏目录失败: {e}")
                    self.statusBar().showMessage("❌ 卸载失败: 清理目录失败", 5000)
                    return
                if progress.wasCanceled():
                    self.statusBar().showMessage("⚠️ 卸载已取消", 5000)
                    return

                # 2) 从 MO 备份复制
                last_pct = [5]
                def on_progress(cur, total, cur_file):
                    pct = 5 + int(cur / max(total, 1) * 90) if total else 5
                    if pct != last_pct[0]:
                        last_pct[0] = pct
                        progress.setValue(min(pct, 95))
                        try:
                            short_name = os.path.basename(cur_file)
                        except Exception:
                            short_name = "..."
                        progress.setLabelText(
                            f"正在恢复原版... ({cur}/{total})\n{short_name}"
                        )
                        QApplication.processEvents()
                result = self.copy_files(mo_backup, target_path, on_progress)
                if isinstance(result, tuple) and len(result) == 3:
                    ok, total, failed = result
                else:
                    ok, total, failed = (False, 0, 0)
                QApplication.processEvents()
                if progress.wasCanceled():
                    self.statusBar().showMessage("⚠️ 卸载已取消", 5000)
                    return
                if total == 0:
                    QMessageBox.warning(self, "警告", "原版备份为空,未恢复任何文件")
                    self.statusBar().showMessage("⚠️ 原版备份为空", 5000)
                    return
                if not ok and failed == total:
                    QMessageBox.critical(
                        self, "错误",
                        f"卸载失败:所有 {total} 个文件均无法复制"
                    )
                    self.statusBar().showMessage("❌ 卸载失败", 5000)
                    return

                # 3) 校验复制结果
                progress.setValue(98)
                progress.setLabelText("正在校验...")
                QApplication.processEvents()
                if not self.is_mo_directory(target_path):
                    warn_msg = "⚠️ 警告:恢复后未能识别为有效的心灵终结游戏目录\n请手动检查游戏文件是否完整"
                else:
                    warn_msg = None

                # 4) 清除当前实例的已安装包记录(因为是还原原版)
                try:
                    for k in list(current.installed_packages.keys()):
                        current.installed_packages[k] = []
                    self.instance_manager._save_instance_config(current)
                except Exception as e:
                    log_warn("App", f"清理已安装包记录失败: {e}")
                # 4.5) 清理所有精确安装记录
                try:
                    rec_dir = current.get_install_records_dir(self.base_path)
                    if os.path.isdir(rec_dir):
                        shutil.rmtree(rec_dir, ignore_errors=True)
                except Exception as e:
                    log_info("Uninstall", f"清理安装记录失败: {e}")

                # 5) 刷新 UI
                if hasattr(self, "package_tabs") and package_type in self.package_tabs:
                    self.package_tabs[package_type].refresh_lists()

                progress.setValue(100)
                QApplication.processEvents()

                msg = (f"卸载完成!实例已恢复为原版游戏状态。\n\n"
                       f"目标实例: {current.name}\n"
                       f"恢复文件数: {total}")
                if failed > 0:
                    msg += f"\n(其中 {failed} 个文件复制失败)"
                if warn_msg:
                    msg += "\n\n" + warn_msg
                    QMessageBox.warning(self, "卸载完成(有警告)", msg)
                else:
                    QMessageBox.information(self, "卸载完成", msg)
                self.statusBar().showMessage(
                    f"✅ 卸载完成,已恢复为原版 ({total} 个文件)", 5000
                )
            except Exception as e:
                QMessageBox.critical(self, "错误", f"卸载失败: {e}")
                self.statusBar().showMessage(f"❌ 卸载失败: {e}", 5000)
            finally:
                progress.close()

        threading.Thread(target=do_uninstall, daemon=True).start()

    def _remove_package(self, name: str, package_type: str):
        """移除包:从管理器目录中安全删除,带使用状态检查和确认机制"""
        path = os.path.join(self.get_package_dir(package_type), name)
        if not os.path.exists(path):
            QMessageBox.warning(self, "警告", f"包文件不存在: {path}")
            return
        # 1) 使用状态检查:是否有任何实例安装了该包
        in_use_by = []
        for inst in self.instance_manager.instances.values():
            try:
                installed = inst.installed_packages.get(package_type, [])
                if not isinstance(installed, list):
                    continue
                for entry in installed:
                    entry_name = entry if isinstance(entry, str) else (
                        entry.get("name", "") if isinstance(entry, dict) else ""
                    )
                    if entry_name == name:
                        in_use_by.append(inst.name)
            except Exception:
                continue
        # 2) 二次确认(根据使用情况显示不同提示)
        if in_use_by:
            warn = (f"⚠️ 该包已被以下实例安装使用:\n  "
                    f"{', '.join(in_use_by)}\n\n"
                    f"移除后这些实例的'已安装'列表中仍会保留包名(直到下次安装/卸载),"
                    f"且无法重新安装(需重启管理器)。\n\n"
                    f"是否继续删除源文件 '{name}'?\n此操作不可撤销!")
        else:
            warn = (f"确定要删除包文件 '{name}' 吗?\n\n"
                    f"此操作仅删除源文件,不影响已安装该包的实例(已复制的文件不会回滚)。\n"
                    f"此操作不可撤销!")
        ret = QMessageBox.question(
            self, "确认移除", warn,
            QMessageBox.Yes | QMessageBox.No, QMessageBox.No
        )
        if ret != QMessageBox.Yes:
            self.statusBar().showMessage("⚠️ 已取消移除", 3000)
            return
        try:
            if os.path.isfile(path):
                os.remove(path)
            elif os.path.isdir(path):
                shutil.rmtree(path)
            QMessageBox.information(self, "成功", f"已移除: {name}")
            self.statusBar().showMessage(f"✅ 已移除: {name}", 4000)
            self.package_tabs[package_type].refresh_lists()
        except (PermissionError, OSError) as e:
            QMessageBox.critical(
                self, "错误",
                f"移除失败:文件可能被占用或权限不足。\n\n{e}\n\n"
                f"建议:关闭可能正在使用该文件的程序后重试。"
            )
            self.statusBar().showMessage(f"❌ 移除失败: {e}", 5000)
        except Exception as e:
            QMessageBox.critical(self, "错误", f"移除失败: {e}")
            self.statusBar().showMessage(f"❌ 移除失败: {e}", 5000)

    def _import_package(self, package_type: str):
        """导入包:从本地选择文件,存储到管理器目录,自动解析元数据(预览)"""
        path, _ = QFileDialog.getOpenFileName(
            self, f"导入{self.package_configs[package_type]['name']}包",
            "", "压缩文件 (*.zip *.7z *.rar);;所有文件 (*.*)"
        )
        if not path:
            return
        # 校验源文件
        if not os.path.isfile(path):
            QMessageBox.warning(self, "警告", f"所选文件不存在: {path}")
            return
        # 提取基本元数据
        try:
            file_size = os.path.getsize(path)
            file_name = os.path.basename(path)
            ext = os.path.splitext(file_name)[1].lower()
        except Exception as e:
            QMessageBox.critical(self, "错误", f"读取文件信息失败: {e}")
            return
        target_dir = self.get_package_dir(package_type)
        os.makedirs(target_dir, exist_ok=True)
        target = os.path.join(target_dir, file_name)
        # 重复导入检查
        if os.path.exists(target):
            ret = QMessageBox.question(
                self, "目标已存在",
                f"目标目录已存在同名文件:\n{target}\n\n是否覆盖?",
                QMessageBox.Yes | QMessageBox.No, QMessageBox.No
            )
            if ret != QMessageBox.Yes:
                self.statusBar().showMessage("⚠️ 已取消导入", 3000)
                return
            try:
                os.remove(target)
            except Exception as e:
                QMessageBox.critical(self, "错误", f"无法覆盖已存在文件: {e}")
                return
        # 解析包内文件(如果是压缩包)用于预览
        preview_info = self._peek_package_contents(path, ext)
        preview_text = ""
        if preview_info:
            preview_text = "\n\n包内预览(前 10 项):\n" + "\n".join(
                f"  • {x}" for x in preview_info[:10]
            )
            if len(preview_info) > 10:
                preview_text += f"\n  ... (还有 {len(preview_info) - 10} 项)"
        # 弹出文件预览对话框（搬运许可.jpg / 说明.txt）
        if ext in (".zip", ".7z", ".rar"):
            dlg = PackagePreviewDialog(self, path, file_name)
            dlg.exec()
        # 确认导入
        size_str = _format_size(file_size) if '_format_size' in globals() else f"{file_size} B"
        confirm = QMessageBox.question(
            self, "确认导入",
            f"即将导入 {self.package_configs[package_type]['name']} 包:\n\n"
            f"  文件名: {file_name}\n"
            f"  大小: {size_str}\n"
            f"  格式: {ext.upper().lstrip('.') or '未知'}\n"
            f"  目标: {target}"
            f"{preview_text}\n\n是否继续?",
            QMessageBox.Yes | QMessageBox.No, QMessageBox.Yes
        )
        if confirm != QMessageBox.Yes:
            self.statusBar().showMessage("⚠️ 已取消导入", 3000)
            return
        # 执行复制(带回执)
        try:
            shutil.copy2(path, target)
            QMessageBox.information(
                self, "导入成功",
                f"已成功导入:\n{target}\n\n请在「{self.package_configs[package_type]['name']}包」标签中查看。"
            )
            self.statusBar().showMessage(f"✅ 已导入: {file_name}", 4000)
            self.package_tabs[package_type].refresh_lists()
        except (PermissionError, OSError) as e:
            QMessageBox.critical(
                self, "错误",
                f"复制文件失败(可能权限不足或源文件被占用):\n\n{e}"
            )
            self.statusBar().showMessage(f"❌ 导入失败: {e}", 5000)
        except Exception as e:
            QMessageBox.critical(self, "错误", f"导入失败: {e}")
            self.statusBar().showMessage(f"❌ 导入失败: {e}", 5000)

    @staticmethod
    def _peek_package_contents(path: str, ext: str) -> list:
        """快速预览压缩包内的文件列表(不完整解压,失败返回空列表)"""
        out = []
        try:
            if ext == ".zip":
                with zipfile.ZipFile(path, "r") as zf:
                    out = zf.namelist()[:30]
            elif ext == ".7z" and SEVENZIP_AVAILABLE:
                with py7zr.SevenZipFile(path, "r") as sz:
                    out = sz.getnames()[:30]
            elif ext == ".rar" and RARFILE_AVAILABLE:
                with rarfile.RarFile(path, "r") as rf:
                    out = rf.namelist()[:30]
        except Exception as ex:
            log_error("Preview", f"读取压缩包内容失败: {ex}")
        return out

    def _download_package(self, package_type: str):
        """包管理页"下载"按钮: 跳转到笨蛋广场 → 游戏资源下载"""
        if self.is_offline:
            QMessageBox.warning(self, "下载", "笨蛋广场功能不可用（离线模式）")
            return
        if not hasattr(self, 'sub_resource_download'):
            QMessageBox.warning(self, "下载", "笨蛋广场功能不可用")
            return
        # 切到笨蛋广场（侧栏第一项），然后切到游戏资源下载子页
        # 先切换主页面到主页/笨蛋广场
        self._last_main_index = 0
        self._switch_page(0)
        # 再切到游戏资源下载子页
        QTimer.singleShot(100, lambda: self._switch_to_subpage(self.sub_resource_download))

    def _open_package_dir(self, package_type: str):
        """查看包目录:打开内置浏览器(支持预览/基本操作)+ 系统资源管理器"""
        path = self.get_package_dir(package_type)
        if not os.path.isdir(path):
            try:
                os.makedirs(path, exist_ok=True)
            except Exception as e:
                QMessageBox.critical(self, "错误", f"无法创建/访问目录: {e}")
                return
        # 优先打开内置可视化浏览器(包含预览/重命名/删除)
        try:
            dlg = PackageDirectoryBrowserDialog(self, self.app, package_type, path)
            dlg.exec()
            self.statusBar().showMessage(
                f"已打开包目录浏览器: {path}", 4000
            )
        except Exception as e:
            # 回退:在系统资源管理器中打开
            log_error("App", f"打开可视化浏览器失败: {e}")
            try:
                if sys.platform == "win32":
                    os.startfile(path)
                else:
                    QMessageBox.information(self, "目录", path)
            except Exception as e2:
                QMessageBox.information(self, "目录", path + f"\n(打开失败: {e2})")

    # ---------- 游戏操作 ----------
    def _backup_game(self):
        """备份游戏实例流程:
        1) 选择游戏实例(支持取消)
        2) 输入备份名称(校验: 禁止 MO/mo/Mo/mO 等保留名)
        3) 后台线程复制 + 进度反馈 + 取消支持
        4) 写入 backup_info.json 元数据,用于后续识别
        """
        try:
            self._backup_game_impl()
        except Exception as e:
            import traceback
            traceback.print_exc()
            QMessageBox.critical(self, "备份错误", f"备份过程发生异常: {e}")
            self.statusBar().showMessage(f"❌ 备份异常: {e}", 5000)

    def _backup_game_impl(self):
        import threading

        # 立即给出 UI 反馈,避免用户误以为按钮无效
        self.statusBar().showMessage("🛠️ 准备备份游戏...")
        QApplication.processEvents()

        instances = self.instance_manager.instances
        if not instances:
            QMessageBox.warning(self, "警告", "暂无可用的游戏实例,请先添加实例")
            self.statusBar().showMessage("❌ 没有可备份的实例", 5000)
            return

        # 1) 实例选择
        labels = []
        inst_list = []
        for inst in instances.values():
            try:
                # 行内显示尽量包含路径,便于区分同名实例
                if inst.path and os.path.exists(inst.path):
                    labels.append(f"{inst.name}    [{inst.path}]")
                else:
                    labels.append(f"{inst.name}    [路径无效]")
            except Exception:
                labels.append(inst.name)
            inst_list.append(inst)
        # 默认选中当前实例
        current = self.instance_manager.get_current_instance()
        default_idx = 0
        if current:
            for i, inst in enumerate(inst_list):
                if inst.id == current.id:
                    default_idx = i
                    break
        sel, ok = QInputDialog.getItem(
            self, "选择游戏实例",
            "请选择要备份的实例:\n(显示: 实例名    [游戏路径])",
            labels, default_idx, False
        )
        if not ok or not sel:
            self.statusBar().showMessage("⚠️ 已取消选择实例", 3000)
            return
        target_inst = inst_list[labels.index(sel)]
        # 即时反馈:让用户看到已确认的实例
        self.statusBar().showMessage(f"已选实例: {target_inst.name} ({target_inst.path})")
        QApplication.processEvents()

        # 2) 输入备份名称(循环校验,直到合法或用户取消)
        default_name = f"{target_inst.name}_{datetime.now().strftime('%Y%m%d_%H%M%S')}"
        while True:
            name, ok = QInputDialog.getText(
                self, "输入备份名称",
                "为本次备份设置一个唯一名称:\n(禁止使用: MO / mo / Mo / mO / MO.mo.mO 等保留名)",
                QLineEdit.Normal, default_name
            )
            if not ok or not name:
                return
            valid, err = is_valid_backup_name(name)
            if not valid:
                QMessageBox.warning(self, "名称不合法", err)
                continue
            # 检查目标是否已存在 → 提供明确的"覆盖 / 重命名 / 取消"三选项
            target = get_game_backup_path(self.base_path, name)
            if os.path.exists(target):
                box = QMessageBox(self)
                box.setIcon(QMessageBox.Warning)
                box.setWindowTitle("备份已存在")
                box.setText(f"已存在同名备份:\n{target}\n\n请选择处理方式:")
                box.setInformativeText(
                    f"• 覆盖:删除原备份并替换为新备份(不可恢复)\n"
                    f"• 重命名:保留原备份,重新输入一个新名称\n"
                    f"• 取消:中止本次备份"
                )
                btn_overwrite = box.addButton("覆盖", QMessageBox.AcceptRole)
                btn_rename = box.addButton("重命名", QMessageBox.RejectRole)
                btn_cancel = box.addButton("取消", QMessageBox.DestructiveRole)
                box.setDefaultButton(btn_rename)
                box.exec()
                clicked = box.clickedButton()
                if clicked is btn_cancel:
                    self.statusBar().showMessage("⚠️ 已取消备份", 3000)
                    return
                if clicked is btn_rename:
                    continue
                # 覆盖前先清理
                try:
                    shutil.rmtree(target, ignore_errors=False)
                except Exception as e:
                    QMessageBox.critical(self, "错误", f"清理旧备份失败: {e}")
                    return
            break

        backup_path = target

        # 3) 准备进度对话框 + 后台线程
        if not os.path.isdir(target_inst.path):
            QMessageBox.warning(self, "警告", f"实例路径无效,无法备份:\n{target_inst.path}")
            return

        progress = QProgressDialog("正在准备备份...", "取消", 0, 100, self)
        progress.setWindowTitle("备份游戏")
        progress.setWindowModality(Qt.WindowModal)
        progress.setMinimumDuration(0)
        progress.setValue(0)
        progress.setLabelText(f"正在备份: {target_inst.name}\n→ {backup_path}")
        self.statusBar().showMessage("正在备份游戏...")

        def do_backup():
            try:
                os.makedirs(backup_path, exist_ok=True)
                QApplication.processEvents()
                if progress.wasCanceled():
                    return

                last_pct = [0]
                def on_progress(cur, total, cur_file):
                    pct = int(cur / max(total, 1) * 100) if total else 0
                    if pct != last_pct[0]:
                        last_pct[0] = pct
                        progress.setValue(pct)
                        try:
                            short_name = os.path.basename(cur_file)
                        except Exception:
                            short_name = "..."
                        progress.setLabelText(
                            f"正在备份 {target_inst.name}... ({cur}/{total})\n{short_name}"
                        )
                        QApplication.processEvents()

                result = self.copy_files(target_inst.path, backup_path, on_progress)
                if isinstance(result, tuple) and len(result) == 3:
                    success, total, failed = result
                else:
                    success, total, failed = (False, 0, 0)
                QApplication.processEvents()
                if progress.wasCanceled():
                    self.statusBar().showMessage("⚠️ 备份已取消", 5000)
                    return

                if total == 0:
                    QMessageBox.warning(self, "警告", "实例中没有可备份的文件")
                    self.statusBar().showMessage("⚠️ 备份为空", 5000)
                    return
                if not success and failed == total:
                    # 完全失败,清理
                    shutil.rmtree(backup_path, ignore_errors=True)
                    QMessageBox.critical(
                        self, "错误",
                        f"备份失败:所有 {total} 个文件均无法复制"
                    )
                    self.statusBar().showMessage("❌ 备份失败", 5000)
                    return

                # 4) 完整性校验:对比源目录与备份目录的文件数与总大小
                progress.setValue(97)
                progress.setLabelText("正在校验备份完整性...")
                QApplication.processEvents()
                source_size = _dir_size(target_inst.path)
                backup_size = _dir_size(backup_path)
                backup_file_count = _dir_file_count(backup_path)
                # 容许极小差异(可能在复制过程中源目录有新文件或 backup_info.json)
                size_diff = abs(source_size - backup_size)
                size_tolerance = max(1024, source_size // 1000)  # 1KB 或 0.1%
                if source_size > 0 and size_diff > size_tolerance:
                        log_warn("Backup", f"警告:源目录 {source_size}B,备份目录 {backup_size}B,差异 {size_diff}B")
                if backup_file_count == 0:
                    shutil.rmtree(backup_path, ignore_errors=True)
                    QMessageBox.critical(
                        self, "错误",
                        "备份完整性校验失败:备份目录为空"
                    )
                    self.statusBar().showMessage("❌ 备份校验失败", 5000)
                    return

                # 5) 写入元数据
                try:
                    meta = {
                        "name": name,
                        "source_instance": target_inst.name,
                        "source_instance_id": target_inst.id,
                        "source_path": target_inst.path,
                        "created_time": datetime.now().isoformat(),
                        "file_count": total,
                        "failed_count": failed,
                        "source_size_bytes": source_size,
                        "backup_size_bytes": backup_size,
                    }
                    with open(os.path.join(backup_path, "backup_info.json"), "w", encoding="utf-8") as f:
                        json.dump(meta, f, ensure_ascii=False, indent=4)
                except Exception as e:
                    log_error("Backup", f"写入元数据失败: {e}")

                progress.setValue(100)
                QApplication.processEvents()

                size_str = _format_size(backup_size)
                msg = (f"已备份到:\n{backup_path}\n\n"
                       f"共 {total} 个文件,共 {size_str}\n"
                       f"源目录: {source_size} 字节\n"
                       f"备份目录: {backup_size} 字节")
                if failed > 0:
                    msg += f"\n(其中 {failed} 个文件复制失败)"
                if size_diff > size_tolerance:
                    msg += f"\n(大小差异 {size_diff} 字节,可能源目录存在实时变化)"
                QMessageBox.information(self, "备份成功", msg)
                self.statusBar().showMessage(
                    f"✅ 备份完成 ({total} 个文件 / {size_str}): {backup_path}", 5000
                )
            except Exception as e:
                QMessageBox.critical(self, "错误", f"备份失败: {e}")
                self.statusBar().showMessage(f"❌ 备份失败: {e}", 5000)
            finally:
                progress.close()

        threading.Thread(target=do_backup, daemon=True).start()

    def _restore_game(self):
        """恢复游戏流程:
        1) 选择目标实例
        2) 列出所有可用备份(原版 MO 备份 + 用户游戏备份),含元数据
        3) 二次确认(提示会清空目标目录)
        4) 后台线程: 清理目标 → 复制备份 → 校验
        """
        import threading

        # 1) 目标实例
        instances = self.instance_manager.instances
        if not instances:
            QMessageBox.warning(self, "警告", "暂无可用的游戏实例,请先添加实例")
            return
        current = self.instance_manager.get_current_instance()
        inst_list = list(instances.values())
        labels = []
        for inst in inst_list:
            if inst.path and os.path.exists(inst.path):
                labels.append(f"{inst.name}    [{inst.path}]")
            else:
                labels.append(f"{inst.name}    [路径无效]")
        default_idx = 0
        if current:
            for i, inst in enumerate(inst_list):
                if inst.id == current.id:
                    default_idx = i
                    break
        sel, ok = QInputDialog.getItem(
            self, "选择目标实例",
            "请选择要恢复到哪个游戏实例:\n(显示: 实例名    [游戏路径])",
            labels, default_idx, False
        )
        if not ok or not sel:
            return
        target_inst = inst_list[labels.index(sel)]

        # 2) 列出所有可用备份
        candidates = []
        # 原始游戏备份 (2.1 版及更早)
        mo_backup = get_original_backup_path(self.base_path)
        if os.path.isdir(mo_backup):
            meta = _read_backup_meta(mo_backup)
            candidates.append({
                "kind": "原版游戏",
                "name": "原版游戏 (MO)",
                "path": mo_backup,
                "created_time": meta.get("created_time", ""),
                "size_bytes": _dir_size(mo_backup) - 0,  # 包含 backup_info.json 也可接受
                "file_count": _dir_file_count(mo_backup),
                "source_path": meta.get("source_path", ""),
            })
        # 2.2 用户游戏备份
        for b in list_game_backups(self.base_path):
            candidates.append({
                "kind": "用户备份",
                "name": b["name"],
                "path": b["path"],
                "created_time": b["created_time"],
                "size_bytes": b["size_bytes"],
                "file_count": b["file_count"],
                "source_path": "",
            })

        if not candidates:
            QMessageBox.warning(
                self, "无可用备份",
                f"未找到任何备份。\n请先使用「备份游戏」或「备份原版游戏」功能。\n\n"
                f"备份目录: {os.path.join(self.base_path, 'backup')}"
            )
            return

        # 构造展示列表(含元数据)
        display = []
        for i, c in enumerate(candidates):
            size_str = _format_size(c["size_bytes"])
            time_str = c["created_time"][:19].replace("T", " ") if c["created_time"] else "未知"
            display.append(
                f"[{c['kind']}] {c['name']}    | {c['file_count']} 个文件 | "
                f"{size_str} | {time_str}"
            )
        sel, ok = QInputDialog.getItem(
            self, "选择备份",
            "请选择要恢复的备份:\n(类型    | 名称    | 文件数 | 大小 | 创建时间)",
            display, 0, False
        )
        if not ok or not sel:
            return
        chosen = candidates[display.index(sel)]
        backup_path = chosen["path"]

        # 2.5) 备份完整性抽样校验(快速)
        try:
            verify_result = _verify_backup_integrity(
                backup_path, sample_size=10
            )
            if verify_result.get("error"):
                warn_text = f"⚠️ 备份校验提示: {verify_result['error']}\n"
            else:
                warn_text = (f"备份抽样校验: 已校验 {verify_result['verified']} 个文件"
                             f"{(', 缺失 ' + str(verify_result['missing'])) if verify_result['missing'] else ''}"
                             f"{(', 异常 ' + str(verify_result['mismatched'])) if verify_result['mismatched'] else ''}\n")
        except Exception as e:
            warn_text = f"⚠️ 备份校验失败: {e}\n"

        # 3) 二次确认(因会清空目标)
        confirm = QMessageBox.question(
            self, "⚠️ 二次确认",
            f"即将从备份恢复到实例:\n  {target_inst.name}  →  {target_inst.path}\n\n"
            f"备份信息:\n  类型: {chosen['kind']}\n  名称: {chosen['name']}\n  "
            f"文件数: {chosen['file_count']}    大小: {_format_size(chosen['size_bytes'])}\n\n"
            f"{warn_text}\n"
            f"⚠️ 目标目录中的所有现有文件/子目录将被彻底删除,无法恢复!\n\n"
            f"是否继续?",
            QMessageBox.Yes | QMessageBox.No, QMessageBox.No
        )
        if confirm != QMessageBox.Yes:
            return

        # 4) 后台线程执行清理 + 复制
        progress = QProgressDialog("正在准备还原...", "取消", 0, 100, self)
        progress.setWindowTitle("恢复游戏")
        progress.setWindowModality(Qt.WindowModal)
        progress.setMinimumDuration(0)
        progress.setValue(0)
        progress.setLabelText(
            f"正在从 [{chosen['kind']}] {chosen['name']} 恢复\n→ {target_inst.path}"
        )
        self.statusBar().showMessage("正在恢复游戏...")

        def do_restore():
            try:
                target_path = target_inst.path
                os.makedirs(target_path, exist_ok=True)
                QApplication.processEvents()
                if progress.wasCanceled():
                    self.statusBar().showMessage("⚠️ 恢复已取消", 5000)
                    return

                # 4.1 清理目标目录
                progress.setLabelText("正在清理目标目录...")
                progress.setValue(2)
                QApplication.processEvents()
                try:
                    for entry in os.listdir(target_path):
                        ep = os.path.join(target_path, entry)
                        try:
                            if os.path.isdir(ep) and not os.path.islink(ep):
                                shutil.rmtree(ep, ignore_errors=False)
                            else:
                                os.remove(ep)
                        except (PermissionError, OSError) as ce:
                            log_warn("Restore", f"清理失败 {ep}: {ce}")
                except Exception as e:
                    QMessageBox.critical(self, "错误", f"清理目标目录失败: {e}")
                    self.statusBar().showMessage("❌ 恢复失败: 清理目标目录失败", 5000)
                    return
                if progress.wasCanceled():
                    self.statusBar().showMessage("⚠️ 恢复已取消", 5000)
                    return

                # 4.2 复制备份到目标
                last_pct = [3]
                def on_progress(cur, total, cur_file):
                    pct = 3 + int(cur / max(total, 1) * 95) if total else 3
                    if pct != last_pct[0]:
                        last_pct[0] = pct
                        progress.setValue(pct)
                        try:
                            short_name = os.path.basename(cur_file)
                        except Exception:
                            short_name = "..."
                        progress.setLabelText(
                            f"正在恢复... ({cur}/{total})\n{short_name}"
                        )
                        QApplication.processEvents()
                result = self.copy_files(backup_path, target_path, on_progress)
                if isinstance(result, tuple) and len(result) == 3:
                    ok, total, failed = result
                else:
                    ok, total, failed = (False, 0, 0)
                QApplication.processEvents()
                if progress.wasCanceled():
                    self.statusBar().showMessage("⚠️ 恢复已取消", 5000)
                    return
                if total == 0:
                    QMessageBox.warning(self, "警告", "备份内容为空,未恢复任何文件")
                    self.statusBar().showMessage("⚠️ 备份为空", 5000)
                    return
                if not ok and failed == total:
                    QMessageBox.critical(
                        self, "错误",
                        f"恢复失败:所有 {total} 个文件均无法复制"
                    )
                    self.statusBar().showMessage("❌ 恢复失败", 5000)
                    return

                # 4.3 校验
                progress.setValue(100)
                progress.setLabelText("正在校验...")
                QApplication.processEvents()
                # 校验: 目标目录中能再次找到 is_mo_directory 标志
                if not self.is_mo_directory(target_path):
                    warn_msg = "⚠️ 警告:恢复后未能识别为有效的心灵终结游戏目录\n请手动检查游戏文件是否完整"
                else:
                    warn_msg = None
                msg = (f"恢复成功!\n\n"
                       f"目标实例: {target_inst.name}\n"
                       f"来源备份: [{chosen['kind']}] {chosen['name']}\n"
                       f"共 {total} 个文件")
                if failed > 0:
                    msg += f"\n(其中 {failed} 个文件复制失败)"
                if warn_msg:
                    msg += "\n\n" + warn_msg
                if warn_msg:
                    QMessageBox.warning(self, "恢复完成(有警告)", msg)
                else:
                    QMessageBox.information(self, "恢复成功", msg)
                self.statusBar().showMessage(
                    f"✅ 恢复完成 ({total} 个文件): {target_inst.name}", 5000
                )
            except Exception as e:
                QMessageBox.critical(self, "错误", f"恢复失败: {e}")
                self.statusBar().showMessage(f"❌ 恢复失败: {e}", 5000)
            finally:
                progress.close()
        threading.Thread(target=do_restore, daemon=True).start()

    def _backup_original_game(self):
        """备份原版游戏流程:
        1) 文件对话框选择原版游戏根目录
        2) 验证目录有效性(is_mo_directory)
        3) 后台线程复制到 <base_path>/backup/MO/
        4) 校验文件数与大小,确保备份完整
        """
        import threading
        # 1) 选择原版游戏根目录
        src = QFileDialog.getExistingDirectory(
            self, "选择原版游戏根目录",
            "", QFileDialog.ShowDirsOnly
        )
        if not src:
            return
        if not self.is_mo_directory(src):
            QMessageBox.warning(
                self, "无效的游戏目录",
                f"所选目录不是有效的心灵终结游戏目录:\n{src}\n\n"
                "有效目录需满足: 含 Mental_Omega 子目录,或目录内含 "
                "MentalOmegaClient.exe / Mental Omega.exe"
            )
            return

        backup_path = get_original_backup_path(self.base_path)
        if os.path.exists(backup_path):
            ret = QMessageBox.question(
                self, "原版备份已存在",
                f"原版游戏备份已存在:\n{backup_path}\n\n是否覆盖?",
                QMessageBox.Yes | QMessageBox.No, QMessageBox.No
            )
            if ret != QMessageBox.Yes:
                return

        try:
            os.makedirs(os.path.join(self.base_path, "backup"), exist_ok=True)
        except Exception as e:
            QMessageBox.critical(self, "错误", f"创建备份目录失败: {e}")
            return

        # 2) 准备进度对话框
        progress = QProgressDialog("准备备份原版游戏...", "取消", 0, 100, self)
        progress.setWindowTitle("备份原版游戏")
        progress.setWindowModality(Qt.WindowModal)
        progress.setMinimumDuration(0)
        progress.setValue(0)
        progress.setLabelText(f"源目录: {src}\n目标: {backup_path}")
        self.statusBar().showMessage("正在备份原版游戏...")

        def do_backup():
            temp_dir = os.path.join(self.base_path, "backup", "temp_original")
            try:
                if os.path.exists(temp_dir):
                    shutil.rmtree(temp_dir)
                os.makedirs(temp_dir, exist_ok=True)
                QApplication.processEvents()
                if progress.wasCanceled():
                    self.statusBar().showMessage("⚠️ 备份已取消", 5000)
                    return

                # 3) 复制整个目录
                last_pct = [0]
                def on_progress(cur, total, cur_file):
                    pct = int(cur / max(total, 1) * 100) if total else 0
                    if pct != last_pct[0]:
                        last_pct[0] = pct
                        progress.setValue(pct)
                        try:
                            short_name = os.path.basename(cur_file)
                        except Exception:
                            short_name = "..."
                        progress.setLabelText(
                            f"正在备份原版游戏... ({cur}/{total})\n{short_name}"
                        )
                        QApplication.processEvents()
                result = self.copy_files(src, temp_dir, on_progress)
                if isinstance(result, tuple) and len(result) == 3:
                    ok, total, failed = result
                else:
                    ok, total, failed = (False, 0, 0)
                QApplication.processEvents()
                if progress.wasCanceled():
                    shutil.rmtree(temp_dir, ignore_errors=True)
                    self.statusBar().showMessage("⚠️ 备份已取消", 5000)
                    return
                if total == 0:
                    shutil.rmtree(temp_dir, ignore_errors=True)
                    QMessageBox.warning(self, "警告", "所选目录没有可备份的文件")
                    self.statusBar().showMessage("⚠️ 备份为空", 5000)
                    return
                if not ok and failed == total:
                    shutil.rmtree(temp_dir, ignore_errors=True)
                    QMessageBox.critical(
                        self, "错误",
                        f"备份失败:所有 {total} 个文件均无法复制"
                    )
                    self.statusBar().showMessage("❌ 备份失败", 5000)
                    return

                # 4) 校验: 文件数与原始目录一致(允许 failed 跳过)
                if failed > 0:
                    log_warn("Backup", f"原版备份: 有 {failed}/{total} 个文件复制失败")

                # 原子替换最终目录
                if os.path.exists(backup_path):
                    shutil.rmtree(backup_path)
                shutil.move(temp_dir, backup_path)

                # 写入元数据
                try:
                    meta = {
                        "type": "original",
                        "source_path": src,
                        "created_time": datetime.now().isoformat(),
                        "file_count": total,
                        "failed_count": failed,
                    }
                    with open(os.path.join(backup_path, "backup_info.json"), "w", encoding="utf-8") as f:
                        json.dump(meta, f, ensure_ascii=False, indent=4)
                except Exception as e:
                    log_error("Backup", f"原版备份: 写入元数据失败: {e}")

                msg = (f"原版游戏备份创建成功!\n"
                       f"备份位置: {backup_path}\n"
                       f"共 {total} 个文件")
                if failed > 0:
                    msg += f"\n(其中 {failed} 个文件复制失败)"
                QMessageBox.information(self, "备份成功", msg)
                self.statusBar().showMessage(
                    f"✅ 原版游戏备份完成 ({total} 个文件): {backup_path}", 5000
                )
            except Exception as e:
                if os.path.exists(temp_dir):
                    shutil.rmtree(temp_dir, ignore_errors=True)
                QMessageBox.critical(self, "错误", f"备份失败: {e}")
                self.statusBar().showMessage(f"❌ 备份失败: {e}", 5000)
            finally:
                progress.close()

        threading.Thread(target=do_backup, daemon=True).start()

    def _launch_game(self):
        current = self.instance_manager.get_current_instance()
        if not current:
            QMessageBox.warning(self, "警告", "请先选择游戏实例")
            return
        # 简化: 查找 Mental_Omega_client.exe
        candidates = [
            os.path.join(current.path, "Mental_Omega_client.exe"),
            os.path.join(current.path, "MentalOmegaClient.exe"),
            os.path.join(current.path, "Mental_Omega.exe"),
        ]
        exe = None
        for c in candidates:
            if os.path.exists(c):
                exe = c
                break
        if not exe:
            QMessageBox.warning(self, "警告", "未找到游戏主程序 (Mental_Omega_client.exe)")
            return
        try:
            subprocess.Popen([exe], shell=False, cwd=os.path.dirname(exe))
            self.statusBar().showMessage(f"🚀 游戏已启动: {os.path.basename(exe)}")
        except Exception as e:
            QMessageBox.critical(self, "错误", f"启动失败: {e}")

    # ---------- 自定义主页背景 ----------
    def _get_home_background_dir(self) -> str:
        """获取主页背景图存储目录(用于复制用户选择的图片,避免路径失效)"""
        bg_dir = os.path.join(self.base_path, "home_backgrounds")
        try:
            os.makedirs(bg_dir, exist_ok=True)
        except Exception as e:
            log_warn("App", f"创建背景目录失败: {e}")
        return bg_dir

    def _choose_home_background(self):
        """打开文件选择对话框,让用户选择本地图片作为主页背景"""
        try:
            file_path, _ = QFileDialog.getOpenFileName(
                self, "选择主页背景图片", "",
                "图片文件 (*.png *.jpg *.jpeg *.bmp *.webp *.gif);;所有文件 (*.*)"
            )
            if not file_path:
                return  # 用户取消
            # 校验文件可读
            if not os.path.isfile(file_path):
                QMessageBox.warning(self, "警告", "所选文件不存在")
                return

            # 复制图片到程序目录(避免源文件被移动/删除后失效)
            try:
                bg_dir = self._get_home_background_dir()
                ext = os.path.splitext(file_path)[1] or ".png"
                # 文件名带时间戳避免冲突
                ts = datetime.now().strftime("%Y%m%d_%H%M%S")
                target_name = f"custom_bg_{ts}{ext}"
                target_path = os.path.join(bg_dir, target_name)
                shutil.copy2(file_path, target_path)
            except Exception as e:
                QMessageBox.critical(
                    self, "错误", f"保存背景图片失败: {e}"
                )
                return

            # 应用新背景
            if hasattr(self, 'home_page') and self.home_page is not None:
                ok = self.home_page.set_background(target_path)
                if not ok:
                    QMessageBox.critical(self, "错误", "无法加载该图片作为背景")
                    return

            # 持久化到配置
            self.config["home_background_path"] = target_path
            try:
                self.save_config()
            except Exception as e:
                log_warn("App", f"保存背景配置失败: {e}")

            # 状态反馈
            self.statusBar().showMessage(
                f"✅ 主页背景已更新: {os.path.basename(target_path)}", 5000
            )
        except Exception as e:
            QMessageBox.critical(self, "错误", f"更换背景失败: {e}")

    def _reset_home_background(self):
        """恢复默认渐变背景"""
        try:
            if hasattr(self, 'home_page') and self.home_page is not None:
                self.home_page.set_background(DEFAULT_HOME_BG_KEY)
            # 清除配置中的背景路径
            if "home_background_path" in self.config:
                del self.config["home_background_path"]
            try:
                self.save_config()
            except Exception as e:
                log_warn("App", f"保存背景配置失败: {e}")
            self.statusBar().showMessage("✅ 已恢复默认背景", 5000)
        except Exception as e:
            QMessageBox.critical(self, "错误", f"恢复默认背景失败: {e}")

    def _load_saved_home_background(self):
        """启动时加载用户上次保存的背景图片(若存在)"""
        if not hasattr(self, 'home_page') or self.home_page is None:
            return
        bg_path = self.config.get("home_background_path")
        if not bg_path:
            return  # 使用默认背景
        # 若文件已不存在,降级为默认背景并清理配置
        if not os.path.isfile(bg_path):
            log_info("App", f"已保存的背景图片不存在,使用默认背景: {bg_path}")
            self.config.pop("home_background_path", None)
            try:
                self.save_config()
            except Exception:
                pass
            return
        try:
            self.home_page.set_background(bg_path)
        except Exception as e:
            log_warn("App", f"加载背景图片失败: {e}")

    # ---------- 文件操作回调 ----------
    def _on_file_op_completed(self, success, args, error):
        if not success and error:
            log_error("App", f"文件操作失败: {error}")
            self.statusBar().showMessage(f"❌ 文件操作失败: {error}")


# =====================================================================
# 第十部分: 主入口
# =====================================================================

def check_dependencies():
    missing = []
    if not PIL_AVAILABLE:
        missing.append("Pillow")
    if not SEVENZIP_AVAILABLE:
        missing.append("py7zr")
    return missing


# EULA 当前版本号 (每次更新条款时递增, 触发重新确认)
EULA_VERSION = "2.2-20260717"

EULA_DISCLAIMER_KEY = (
    "This launcher is not affiliated with EA, the Red Alert 2 development team, "
    "or the Mental Omega development team."
)

# 完整协议文本 (中文版, 不展示英文版)
EULA_FULL_TEXT = """
<h2 style="color:#d33;">HMOL 启动器 使用协议</h2>
<p style="color:#888;">版本: 2.2 | 最后更新: 2026-07-17</p>

<h3>1. 重要声明</h3>
<p style="background:#fff3cd; padding:10px; border-left:4px solid #d33;">
<b>This launcher is not affiliated with EA, the Red Alert 2 development team,
or the Mental Omega development team.</b><br>
本启动器与 EA (Electronic Arts)、红色警戒 2 开发团队、心灵终结开发团队
不存在任何关联、授权、赞助或背书关系。
</p>
<ul>
<li>"Red Alert 2"、"命令与征服"是 <b>Electronic Arts Inc.</b> 的注册商标</li>
<li>"Mental Omega"是独立模组项目，与 EA 无官方关联</li>
<li>本启动器为<b>独立第三方工具</b>，由 HMOL 项目贡献者开发</li>
</ul>

<h3>2. 核心使用条款 (二次修改禁令) ⛔</h3>
<div style="background:#fee; padding:12px; border:2px solid #d33; border-radius:6px;">
<p><b>⚠️ 不允许二次修改程序</b></p>
<p><b>未经许可方书面授权, 用户严禁对本软件进行任何形式的二次修改。</b></p>
<p>具体禁止以下行为:</p>
<ul>
<li>❌ 修改源代码 (任何对 .py 文件的改动)</li>
<li>❌ 修改二进制 (反编译后修改、补丁、热更新)</li>
<li>❌ 反向工程 (反编译、反汇编、静态分析)</li>
<li>❌ 创建衍生作品 (fork、改编、翻译)</li>
<li>❌ 代码复用 (将本软件代码用于其他项目)</li>
<li>❌ 重新分发 (上传至任何代码托管平台)</li>
<li>❌ 商业使用 (用于商业产品或盈利活动)</li>
<li>❌ 安全绕过 (绕过、破解本软件的安全机制)</li>
<li>❌ 标识移除 (移除、隐藏、修改版权声明)</li>
</ul>
<p>违反上述条款, 您的使用许可将<b>自动立即终止</b>, 许可方保留依据
《中华人民共和国著作权法》《计算机软件保护条例》追究法律责任的权利。</p>
<p>允许的行为 (不构成修改): 个人备份复制 / 阅读学习源代码 / GitHub Issue 报告 bug</p>
</div>

<h3>3. 许可授予</h3>
<p>在遵守本协议的前提下，许可方授予您：</p>
<ul>
<li>✅ 个人非商业用途的使用权</li>
<li>✅ 为备份目的的复制权</li>
<li>❌ 禁止商业销售、再许可、出租</li>
<li>❌ 禁止绕过本协议明确允许或适用法律允许范围外的反向工程</li>
</ul>

<h3>4. 用户责任</h3>
<ol>
<li><b>合法使用</b>：仅用于合法目的，遵守所在司法管辖区法律</li>
<li><b>不滥用</b>：不得用于攻击、传播恶意代码、绕过第三方安全机制</li>
<li><b>账户责任</b>：对使用本软件时的 Microsoft、QQ、OneDrive 账户活动负全部责任</li>
<li><b>数据安全</b>：妥善保管登录凭据，定期更新密码</li>
<li><b>资源合规</b>：确保通过本软件获取的资源不侵犯第三方知识产权</li>
</ol>

<h3>5. 知识产权</h3>
<ul>
<li>本软件源代码、UI、文档归 HMOL 项目贡献者所有 (MIT 许可证)</li>
<li>Red Alert 2 相关资产归 <b>Electronic Arts Inc.</b> 所有</li>
<li>Mental Omega 相关资产归 <b>Mental Omega 开发团队</b> 所有</li>
<li>本软件<b>不包含</b>任何 Red Alert 2 或 Mental Omega 的游戏本体文件</li>
</ul>

<h3>6. 责任限制</h3>
<p><b>本软件按"现状"提供，不附带任何明示或暗示的保证。</b></p>
<p>在任何情况下，许可方、贡献者、作者或版权持有人均不对您或任何第三方
因使用或无法使用本软件而产生的任何直接、间接、附带、特殊、惩罚性或
后果性损害承担责任，即使已被告知此类损害的可能性。</p>

<h3>7. 隐私政策</h3>
<ul>
<li>本软件<b>默认不收集</b>任何用户个人信息</li>
<li>Microsoft 令牌使用 AES-256-GCM 加密本地存储，密钥由机器码派生</li>
<li>不上传任何数据至 HMOL 项目运营的中央服务器（<b>无中央服务器</b>）</li>
<li>使用第三方服务时，数据由相应服务提供商处理（Microsoft、腾讯）</li>
<li>本软件不使用 Cookies，不集成第三方追踪 SDK</li>
</ul>

<h3>8. 禁止行为</h3>
<ol>
<li>违法活动</li>
<li>侵犯他人知识产权、隐私权、名誉权</li>
<li>网络攻击、恶意代码、钓鱼、欺诈</li>
<li>绕过 Microsoft / Xbox Live / QQ 的安全机制</li>
<li>在游戏中使用未授权的作弊工具</li>
<li>上传、分发受版权保护的游戏本体文件</li>
<li>滥用 QQ 喊话、OneDrive 分享功能骚扰他人</li>
<li>商业牟利、出租、销售、转售</li>
<li>冒充 EA、Mental Omega 开发团队或本软件作者</li>
<li>植入后门、木马、挖矿代码或其他恶意功能</li>
</ol>

<h3>9. 终止</h3>
<ul>
<li>您可随时通过卸载本软件终止本协议</li>
<li>如您违反本协议任何条款，许可方有权立即终止</li>
<li>协议终止后，您必须停止使用并删除本软件所有副本</li>
</ul>

<h3>10. 争议解决</h3>
<p>本协议适用中华人民共和国法律。协商不成的，提交许可方所在地有管辖权的人民法院诉讼解决。</p>

<h3>11. 反馈与支持</h3>
<p>如有问题或建议，请通过 GitHub Issues 提交：<br>
<a href="https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues">
https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues</a></p>

<p style="color:#888; margin-top:20px;">
© 2026 HMOL Project Contributors. All Rights Reserved.
</p>
"""


def _show_eula_full(parent=None, as_viewer: bool = False) -> bool:
    """
    显示完整 EULA 对话框 (滚动视图)

    Args:
        parent: 父窗口
        as_viewer: True=仅查看 (用于关于页入口, 无按钮), False=首次启动确认
    Returns:
        True=用户同意 (或仅查看), False=用户拒绝
    """
    from PySide6.QtWidgets import (
        QDialog, QVBoxLayout, QTextBrowser, QDialogButtonBox, QPushButton
    )

    dlg = QDialog(parent)
    dlg.setWindowTitle("HMOL 启动器 使用协议")
    dlg.resize(720, 640)
    dlg.setMinimumSize(480, 400)
    dlg.setModal(True)

    layout = QVBoxLayout(dlg)
    layout.setContentsMargins(16, 16, 16, 16)
    layout.setSpacing(12)

    # 标题
    from PySide6.QtWidgets import QLabel
    title = QLabel("📜 HMOL 启动器 使用协议")
    title.setStyleSheet("font-size: 16pt; font-weight: bold; padding: 4px;")
    layout.addWidget(title)

    # 滚动文本
    text = QTextBrowser()
    text.setOpenExternalLinks(True)
    text.setHtml(EULA_FULL_TEXT)
    text.setStyleSheet("""
        QTextBrowser {
            border: 1px solid #ccc;
            border-radius: 6px;
            padding: 12px;
            background: #fafafa;
        }
    """)
    layout.addWidget(text, 1)  # stretch=1 让文本占满空间

    if as_viewer:
        # 关于页入口模式: 只有关闭按钮
        btn_box = QDialogButtonBox(QDialogButtonBox.StandardButton.Close)
        btn_box.rejected.connect(dlg.accept)
        layout.addWidget(btn_box)
    else:
        # 首次启动模式: 同意/不同意按钮
        btn_box = QDialogButtonBox()
        btn_disagree = QPushButton("❌ 不同意, 退出")
        btn_agree = QPushButton("✅ 我同意, 继续")
        btn_agree.setDefault(True)
        btn_agree.setStyleSheet("""
            QPushButton {
                background: #4CAF50; color: white; padding: 8px 24px;
                font-weight: bold; border-radius: 4px; min-width: 120px;
            }
            QPushButton:hover { background: #45a049; }
        """)
        btn_disagree.setStyleSheet("""
            QPushButton {
                background: #f44336; color: white; padding: 8px 24px;
                font-weight: bold; border-radius: 4px; min-width: 120px;
            }
            QPushButton:hover { background: #da190b; }
        """)
        btn_box.addButton(btn_disagree, QDialogButtonBox.ButtonRole.RejectRole)
        btn_box.addButton(btn_agree, QDialogButtonBox.ButtonRole.AcceptRole)
        layout.addWidget(btn_box)

        # 状态由 exec() 返回值决定
        dlg._user_accepted = False
        btn_agree.clicked.connect(lambda: _on_agree(dlg))
        btn_disagree.clicked.connect(lambda: _on_disagree(dlg))

    result = dlg.exec()
    if as_viewer:
        return True
    return getattr(dlg, '_user_accepted', False)


def _on_agree(dlg):
    dlg._user_accepted = True
    dlg.accept()


def _on_disagree(dlg):
    dlg._user_accepted = False
    dlg.reject()


def _check_eula_accepted() -> bool:
    """
    检查 EULA 是否已被当前版本接受
    首次启动或协议升级时弹出确认对话框
    Returns: True=已接受, False=未接受 (用户拒绝)
    """
    config_path = os.path.join(get_program_base_path(), "HMOL_config.json")
    accepted = False
    accepted_version = ""
    if os.path.exists(config_path):
        try:
            with open(config_path, "r", encoding="utf-8") as f:
                cfg = json.load(f)
            accepted = bool(cfg.get("eula_accepted", False))
            accepted_version = cfg.get("eula_accepted_version", "")
        except Exception:
            pass
    if accepted and accepted_version == EULA_VERSION:
        return True

    # 弹出完整协议对话框
    try:
        user_accepted = _show_eula_full(parent=None, as_viewer=False)
        if not user_accepted:
            log_info("EULA", "用户未同意 EULA, 程序立即退出, 不保留任何操作记录")
            return False
    except Exception as e:
        log_warn("EULA", f"无法弹出 EULA 对话框: {e}")
        return True  # GUI 不可用时放行

    # 持久化接受状态
    try:
        cfg = {}
        if os.path.exists(config_path):
            try:
                with open(config_path, "r", encoding="utf-8") as f:
                    cfg = json.load(f)
            except Exception:
                pass
        cfg["eula_accepted"] = True
        cfg["eula_accepted_version"] = EULA_VERSION
        with open(config_path, "w", encoding="utf-8") as f:
            json.dump(cfg, f, ensure_ascii=False, indent=4)
        log_info("EULA", f"用户已接受 EULA v{EULA_VERSION}")
    except Exception as e:
        log_warn("EULA", f"保存 EULA 状态失败: {e}")
    return True


def show_eula_viewer(parent=None):
    """关于页入口: 显示完整协议 (仅查看, 无按钮)"""
    _show_eula_full(parent=parent, as_viewer=True)


def main():
    cleanup_old_logs()
    log_info("Startup", "HMOL v2.2 启动中...")

    # 启动时完整性自检 (反调试 + 沙箱检测)
    if ANTI_DEBUG_AVAILABLE:
        try:
            # 非严格模式: 仅日志告警, 不阻止启动 (避免影响普通用户)
            verify_runtime_integrity(strict=False)
            self_hash = get_self_hash()
            if self_hash:
                log_info("Security", f"启动器 SHA256: {self_hash[:16]}...")
        except Exception as e:
            log_warn("Security", f"自检异常: {e}")

    missing = check_dependencies()
    app = QApplication(sys.argv)
    app.setApplicationName("MO 资源管理器")
    app.setOrganizationName("mmm")

    # EULA 首次启动确认 (符合 GDPR / 个保法要求)
    if not _check_eula_accepted():
        log_info("EULA", "用户未接受 EULA, 立即清理所有临时数据后退出")
        # 拒绝时: 清理本次启动产生的所有用户数据, 真正零操作记录
        try:
            # 1. 清理 HMOL_config.json (可能已被部分写入)
            config_path = os.path.join(get_program_base_path(), "HMOL_config.json")
            if os.path.exists(config_path):
                os.remove(config_path)
            # 2. 清理 MSAL token 缓存
            cache_path = os.path.join(get_program_base_path(), MSAL_CACHE_FILE)
            if os.path.exists(cache_path):
                try:
                    os.remove(cache_path)
                except Exception:
                    pass
        except Exception as cleanup_err:
            log_warn("EULA", f"清理临时数据时出错: {cleanup_err}")
        sys.exit(0)

    # ---- 微软账号强制登录 ----
    base_path = get_program_base_path()

    # 先检测网络可用性 — MSAL 初始化自身就会联网(tenant discovery),
    # 无网络时创建 PublicClientApplication 直接抛 ConnectionError
    # 因此必须在 AuthManager 构造之前完成网络检测
    network_available = _check_network_available()

    if not network_available:
        # 无网络环境: 跳过 MSAL 初始化,直接进入离线模式
        auth = None
        is_offline = True
    else:
        try:
            auth = AuthManager(base_path)
        except Exception as e:
            # AuthManager 初始化失败(如 MSAL tenant discovery 报错),
            # 降级为离线模式继续运行
            log_warn("App", f"AuthManager 初始化失败: {e}, 降级为离线模式")
            auth = None
            is_offline = True
        else:
            # 尝试静默登录(使用缓存的 refresh token)
            logged_in = auth.is_logged_in()

            if not logged_in:
                # 未登录 → 显示登录对话框
                login_dlg = LoginDialog(auth)
                login_dlg.exec()
                login_result = login_dlg.get_result()
                login_dlg.deleteLater()

                if login_result == "cancel":
                    sys.exit(0)  # 用户取消,退出程序
                elif login_result == "offline":
                    is_offline = True
                elif login_result == "login":
                    is_offline = False
                else:
                    is_offline = False  # 兜底
            else:
                # 已有有效令牌(上次登录的缓存),直接进入
                is_offline = False

    # 检查依赖
    if missing:
        dlg = DependencyWarningDialog(None, missing)
        dlg.exec()

    window = MainWindow(auth_manager=auth, is_offline=is_offline)
    # 存储全局引用,供 QQ Bot 模块级函数使用
    import __main__
    __main__._app_instance = window
    window.show()
    log_info("Startup", "HMOL 就绪")
    sys.exit(app.exec())


if __name__ == "__main__":
    main()