# ==============================================================================
# HMOL Launcher — Hello Mental Omega Launcher
# Copyright (c) 2026 HMOL Contributors. All Rights Reserved.
#
# 本文件受 HMOL Non-Commercial, No-Modification Source-Available License v2.2 保护。
# 严禁任何形式的二次修改、二次开发、二次封装、二次分发。
# 完整许可条款请参见项目根目录的 LICENSE 文件。
#
# This file is protected by the HMOL Non-Commercial, No-Modification
# Source-Available License v2.2. Any form of modification, derivative work,
# repackaging, or redistribution is strictly prohibited.
# For full license terms, see the LICENSE file in the project root.
# ==============================================================================
"""
Hello Mental Omega Launcher v2.2 (wine)
==========================================================

设计哲学: 反调试本质是猫鼠游戏, 真正的逆向大佬手里有无限时间,
而我们只有有限的开发周期. 所以这里的策略是"提高成本, 不追求绝对安全"——
让脚本小子觉得麻烦, 大佬觉得不值. 这其实是个性价比问题.
如果哪天有人非要和这份代码过不去, 那说明这份代码已经成功了.
"""

__version__ = "2.2"
__app_name__ = "Hello Mental Omega Launcher"


# =====================================================================
# 开启奇妙之旅！
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
import random
import hashlib
import socket
import csv
from datetime import datetime, timedelta
from io import BytesIO
from pathlib import Path
from urllib.parse import urlparse, parse_qs, unquote, quote
import colorsys

# 类型标注支持
try:
    from typing import Optional  # noqa: F401
except ImportError:
    Optional = None  # type: ignore

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

# RAR 支持(可选,需要安装 rarfile 库)
try:
    import rarfile
    RARFILE_AVAILABLE = True
except ImportError:
    RARFILE_AVAILABLE = False
    rarfile = None

# Microsoft 账号认证
try:
    import msal
    import requests as ms_requests
    MSAL_AVAILABLE = True
except ImportError:
    MSAL_AVAILABLE = False
    msal = None
    ms_requests = None

# tkinter
import tkinter as tk
from tkinter import ttk
from tkinter import messagebox
from tkinter import filedialog
from tkinter import simpledialog


import threading as _threading


def log_info(module: str, message: str):
    try:
        ts = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        print(f"[{ts}] [INFO] [{module}] {message}", flush=True)
    except Exception:
        pass


def log_warn(module: str, message: str):
    try:
        ts = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        print(f"[{ts}] [WARN] [{module}] {message}", flush=True)
    except Exception:
        pass


def log_error(module: str, message: str):
    """早期 log_error — 文件加载阶段使用。后续定义会覆盖。"""
    try:
        ts = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        print(f"[{ts}] [ERROR] [{module}] {message}", flush=True)
    except Exception:
        try:
            print(f"[ERROR] [{module}] {message}", flush=True)
        except Exception:
            pass


def log_debug(module: str, message: str):
    try:
        ts = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        print(f"[{ts}] [DEBUG] [{module}] {message}", flush=True)
    except Exception:
        pass


class _LazySecret:
    """惰性求值的加密凭据(线程安全)。

    用法:
        s = _LazySecret(lambda: get_secret())
        s()  # 返回解密的明文

    优势:
      - 首次访问时才执行昂贵的解密操作
      - 后续访问走内存缓存(纳秒级)
      - 线程安全(双重检查锁)
      - 解密失败时返回空字符串(避免异常中断主流程)
    """

    def __init__(self, getter, name: str = "secret"):
        self._getter = getter
        self._name = name
        self._value = None  # type: str
        self._loaded = False
        self._lock = _threading.Lock()

    def __call__(self) -> str:
        """惰性求值,首次调用时解密。"""
        if self._loaded:
            return self._value
        with self._lock:
            if self._loaded:
                return self._value
            try:
                self._value = self._getter()
            except Exception as e:
                try:
                    log_error("App", f"加载加密凭据 '{self._name}' 失败: {e}")
                except NameError:
                    import sys
                    print(
                        f"[ERROR] [App] 加载加密凭据 '{self._name}' 失败: {e}",
                        file=sys.stderr, flush=True,
                    )
                self._value = ""
            self._loaded = True
            return self._value

    def __str__(self) -> str:
        return self()

    def __repr__(self) -> str:
        v = self()
        if v and len(v) > 8:
            return f"<LazySecret '{self._name}'={v[:4]}...>"
        return f"<LazySecret '{self._name}'={v!r}>"


def _resolver_get_msal_client_id() -> str:
    from HMOL_secret_resolver import get_msal_client_id as _r
    return _r()


def _resolver_get_qq_bot_appid() -> str:
    from HMOL_secret_resolver import get_qq_bot_appid as _r
    return _r()


def _resolver_get_qq_bot_appsecret() -> str:
    from HMOL_secret_resolver import get_qq_bot_appsecret as _r
    return _r()


MSAL_CLIENT_ID = _LazySecret(_resolver_get_msal_client_id, "MSAL_CLIENT_ID")

MSAL_AUTHORITY = "https://login.microsoftonline.com/consumers"
MSAL_CACHE_FILE = "msal_token_cache.bin"
GRAPH_API_BASE = "https://graph.microsoft.com/v1.0"
MSAL_SCOPES = ["User.Read", "Files.Read.All"]
NET_CHECK_TIMEOUT = 5
MSAL_LOGIN_TIMEOUT = 120


QQ_BOT_TOKEN_URL = "https://bots.qq.com/app/getAppAccessToken"
QQ_BOT_MSG_URL = "https://api.sgroup.qq.com/channels/{}/messages"
QQ_BOT_GROUP_MSG_URL = "https://api.sgroup.qq.com/v2/groups/{}/messages"
QQ_BOT_MSG_MAX_LENGTH = 500
QQ_BOT_TOKEN_CACHE = {}

QQ_BOT_APPID = _LazySecret(_resolver_get_qq_bot_appid, "QQ_BOT_APPID")
QQ_BOT_APPSECRET = _LazySecret(_resolver_get_qq_bot_appsecret, "QQ_BOT_APPSECRET")


def _get_qq_bot_token() -> str:
    """获取 QQ Bot Access Token (带缓存)"""
    now = time.time()
    if QQ_BOT_TOKEN_CACHE.get("token") and float(QQ_BOT_TOKEN_CACHE.get("expiry", 0)) > now + 60:
        return QQ_BOT_TOKEN_CACHE["token"]
    try:
        resp = ms_requests.post(
            QQ_BOT_TOKEN_URL,
            json={"appId": QQ_BOT_APPID(), "clientSecret": QQ_BOT_APPSECRET()},
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
    except Exception as e:
        log_error("QQBot", f"Token 请求异常: {e}")
    return ""


def _get_qq_channel_id() -> str:
    """返回 QQ 频道子频道 ID (从加密 seal 模块解密)"""
    from HMOL_secret_resolver import get_qq_channel_id as _r
    return _r()


def _get_qq_group_id() -> str:
    """返回 QQ 群号 (从加密 seal 模块解密)"""
    from HMOL_secret_resolver import get_qq_group_id as _r
    return _r()

try:
    QQ_BOT_AVAILABLE = bool(
        QQ_BOT_APPID() and QQ_BOT_APPSECRET() and
        (_get_qq_channel_id() or _get_qq_group_id())
    )
except Exception as e:
    log_error("App", f"检查 QQ Bot 可用性失败: {e}")
    QQ_BOT_AVAILABLE = False


_LOCK = threading.Lock()
_LOG_BUFFER: list[dict] = []
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
            if entry is None:  
                fh.close()
                break
            dt_str = entry["timestamp"]
            entry_date = dt_str[:10]
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


def _log(level: str, module: str, message: str):
    """写入内存缓冲区 + 异步队列"""
    ts = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    entry = {"timestamp": ts, "level": level, "module": module, "message": message}
    with _LOCK:
        _LOG_BUFFER.append(entry)
        if len(_LOG_BUFFER) > _LOG_MAX_BUFFER:
            _LOG_BUFFER.pop(0)
    _start_log_writer()
    _LOG_QUEUE.put(entry)
    print(f"[{ts}] [{level}] [{module}] {message}")


def get_logs(level_filter: str = "", keyword: str = "",
             date_from: str = "", date_to: str = "",
             limit: int = 2000) -> list[dict]:
    """获取过滤后的日志列表(供 UI 查看器调用)"""
    with _LOCK:
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
    with open(file_path, "w", encoding="utf-8-sig", newline="") as f:
        w = csv.writer(f)
        w.writerow(["timestamp", "level", "module", "message"])
        for e in entries:
            w.writerow([e["timestamp"], e["level"], e["module"], e["message"]])


def cleanup_old_logs(keep_days: int = _LOG_MAX_DAYS):
    """清理超过保留天数的日志文件"""
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
# @绮梦 是猪   @小小魅魔 是猪   @绮梦 是猪   @小小魅魔 是猪   @绮梦 是猪   @小小魅魔 是猪   
# =====================================================================
ONEDRIVE_SOURCES = {
    "game_resources": {
        "name": "游戏资源下载",
        "icon": "🎮",
        "url": "https://?",
        "description": "游戏相关资源,包括 MOD、地图、皮肤等",
    },
    "runtime_env": {
        "name": "运行环境",
        "icon": "⚙️",
        "url": "https://?",
        "description": "游戏运行所需的运行库与环境组件",
    },
    "program_extend": {
        "name": "程序DLC下载",
        "icon": "🧩",
        "url": "https://?",
        "description": "HMOL 程序扩展、插件与辅助工具",
    },
}

# 文件类型图标映射  丑死了！！！
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
# 睡觉觉啦  发给群主神秘代码 wine-114514
# =====================================================================
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

DEFAULT_HOME_BG_KEY = "__default_gradient__"



def get_program_base_path() -> str:
    """返回程序可执行文件所在的目录(用于存放所有配置/数据/缓存)。

    规则:
      - PyInstaller 打包后 (sys.frozen == True): 锁定到 sys.executable 所在目录
      - 脚本直接运行: 锁定到当前脚本所在目录

    禁止将配置/数据写到 %APPDATA%、用户目录或其它系统默认路径。
    """
    try:
        if getattr(sys, "frozen", False):
            return os.path.dirname(os.path.abspath(sys.executable))
    except Exception:
        pass
    return os.path.dirname(os.path.abspath(__file__))


def get_app_version() -> str:
    """从 version.json 加载版本号(单一来源),失败回退到 __version__。"""
    try:
        vp = os.path.join(get_program_base_path(), "version.json")
        if os.path.isfile(vp):
            with open(vp, "r", encoding="utf-8") as f:
                data = json.load(f)
            v = str(data.get("version", "")).strip()
            if v:
                return v
    except Exception:
        pass
    return __version__


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
        if hasattr(sys, '_called_from_test'):
            log_debug("Theme", str(e))
    return "浅色模式"


def _check_network_available() -> bool:
    """检测网络是否可用(尝试连接微软登录终结点)"""
    if not MSAL_AVAILABLE:
        return False
    try:
        _sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        _sock.settimeout(NET_CHECK_TIMEOUT)
        _sock.connect(("login.microsoftonline.com", 443))
        _sock.close()
        return True
    except Exception:
        return False


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
    """递归统计目录中的文件数(不含 backup_info.json,出错返回 0)。"""
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
        if 0xFE00 <= cp <= 0xFE0F or cp == 0x200D:
            chars.append(ch)
            continue
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



FORBIDDEN_BACKUP_NAMES = {
    "MO", "mo", "Mo", "mO",
    "原版", "原版游戏", "原版游戏备份",
    "MO.mo.mO",
}

ORIGINAL_BACKUP_DIRNAME = "MO"
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


def _compute_file_hash(file_path: str, algorithm: str = "sha256", chunk: int = 65536) -> str:
    """计算文件哈希(默认 SHA256)。失败返回空字符串。"""
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


def _read_install_record_file(path: str) -> dict:
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception:
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




class FileOperationThread:
    """多线程文件操作类 - 使用回调模式替代 Qt Signals"""

    def __init__(self, max_threads=4):
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
                cb = task.get('callback')
                if cb:
                    cb(False, task.get('callback_args'), str(e))

    def _invoke_callback(self, task, success, error):
        """调用任务回调"""
        cb = task.get('callback')
        if cb:
            cb(success, task.get('callback_args'), error)

    def _copy_file(self, task):
        source = task['source']
        target = task['target']
        try:
            target_dir = os.path.dirname(target)
            if not os.path.exists(target_dir):
                os.makedirs(target_dir, exist_ok=True)
            shutil.copy2(source, target)
            self._invoke_callback(task, True, None)
        except Exception as e:
            self._invoke_callback(task, False, str(e))

    def _move_file(self, task):
        source = task['source']
        target = task['target']
        try:
            target_dir = os.path.dirname(target)
            if not os.path.exists(target_dir):
                os.makedirs(target_dir, exist_ok=True)
            shutil.move(source, target)
            self._invoke_callback(task, True, None)
        except Exception as e:
            self._invoke_callback(task, False, str(e))

    def _delete_file(self, task):
        path = task['path']
        try:
            if os.path.isdir(path):
                shutil.rmtree(path)
            else:
                os.remove(path)
            self._invoke_callback(task, True, None)
        except Exception as e:
            self._invoke_callback(task, False, str(e))

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

        def file_copy_callback(success, args, error):
            with self.lock:
                copied_count[0] += 1
                if success:
                    current_file = files_to_copy[copied_count[0] - 1]
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
        saved_pkgs = data.get("installed_packages")
        if not isinstance(saved_pkgs, dict):
            saved_pkgs = {
                "ini": [], "map": [], "mission": [], "voice": [],
                "plugin": [], "beautification": [], "music": []
            }
        else:
            for k in list(saved_pkgs.keys()):
                if not isinstance(saved_pkgs[k], list):
                    saved_pkgs[k] = []
            if "mod" in saved_pkgs and "ini" not in saved_pkgs:
                saved_pkgs["ini"] = saved_pkgs.pop("mod")
            elif "mod" in saved_pkgs and "ini" in saved_pkgs:
                merged = list(dict.fromkeys(list(saved_pkgs.get("ini", [])) + list(saved_pkgs.get("mod", []))))
                saved_pkgs["ini"] = merged
                del saved_pkgs["mod"]
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




class InstanceManager:
    """实例管理器 - 使用回调模式替代 Qt Signals"""

    def __init__(self, app, base_path):
        self.app = app
        self.base_path = base_path
        self.instances = {}
        self.current_instance = None
        self._change_callbacks = []  # 回调列表: [(callback, args), ...]

    def on_instances_changed(self, callback):
        """注册实例变更回调"""
        self._change_callbacks.append(callback)

    def _notify_changed(self):
        """通知所有已注册的变更回调"""
        for cb in self._change_callbacks:
            try:
                cb()
            except Exception as e:
                log_error("App", f"实例变更回调错误: {e}")

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
        self._notify_changed()
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
            self._notify_changed()
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
        self._notify_changed()
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
        self._notify_changed()
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
        if not export_path:
            return False, "导出路径不能为空"
        export_dir = os.path.dirname(os.path.abspath(export_path))
        if not os.path.isdir(export_dir):
            return False, f"导出目录不存在: {export_dir}"
        if os.path.exists(export_path):
            return False, f"目标文件已存在,请先删除或更换名称: {export_path}"

        try:
            ext = os.path.splitext(export_path)[1].lower()
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
        """返回 (ok, error_message)。"""
        try:
            with zipfile.ZipFile(zip_path, 'w', zipfile.ZIP_DEFLATED,
                                 compresslevel=compress_level) as zipf:
                config_json = json.dumps(export_info, ensure_ascii=False, indent=4)
                zipf.writestr('instance_info.json', config_json)
                processed_size = 0
                last_pct = [-1]
                for file_path, rel_path, file_size in files_to_zip:
                    arcname = f'game_files/{rel_path}'.replace('\\', '/')
                    try:
                        if preserve_metadata:
                            st = os.stat(file_path)
                            zinfo = zipfile.ZipInfo(arcname)
                            zinfo.date_time = time.localtime(st.st_mtime)[:6]
                            zinfo.external_attr = (st.st_mode & 0xFFFF) << 16
                            with open(file_path, 'rb') as src:
                                with zipf.open(zinfo, 'w') as dst:
                                    shutil.copyfileobj(src, dst, 1024 * 1024)
                        else:
                            zipf.write(file_path, arcname)
                    except (PermissionError, OSError) as fe:
                        log_warn("ZIP", f"跳过无法写入的文件 {file_path}: {fe}")
                        continue
                    processed_size += file_size
                    if progress_callback:
                        pct = int((processed_size / total_size) * 100) if total_size > 0 else 0
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
        """导出为 RAR 格式。返回 (ok, error_message)。"""
        winrar_paths = [
            r"C:\Program Files\WinRAR\WinRAR.exe",
            r"C:\Program Files (x86)\WinRAR\WinRAR.exe",
        ]
        rar_cli = None
        for p in winrar_paths:
            if os.path.isfile(p):
                rar_cli = p
                break
        if rar_cli is None:
            for name in ("rar", "winrar"):
                from shutil import which
                w = which(name)
                if w:
                    rar_cli = w
                    break

        if rar_cli is not None:
            try:
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
                    cmd = [rar_cli, "a", "-r", "-ep1", "-y", rar_path,
                           os.path.join(stage_dir, "instance_info.json"),
                           os.path.join(game_dir, "*")]
                    creationflags = 0
                    if sys.platform == "win32":
                        creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
                    rc = subprocess.run(cmd, capture_output=True, text=True,
                                        creationflags=creationflags)
                    if rc.returncode == 0 and os.path.isfile(rar_path):
                        if progress_callback:
                            progress_callback(100, 100, "RAR 导出完成")
                        return True, ""
                    log_warn("RAR", f"CLI 退出码 {rc.returncode}: {rc.stderr}")
            except Exception as e:
                log_warn("RAR", f"CLI 导出失败,降级为 ZIP: {e}")

        # 降级: 使用 ZIP 格式(自动改后缀为 .zip)
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

                def _import_progress(cur, total, cur_file):
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
                if isinstance(copy_result, tuple) and len(copy_result) == 3:
                    success, total, failed = copy_result
                    if total > 0 and failed == total:
                        shutil.rmtree(instance_dir, ignore_errors=True)
                        return False, f"复制游戏文件失败:所有 {total} 个文件均无法复制", None
                    if failed > 0:
                        log_warn("App", f"导入实例时 {failed}/{total} 个文件失败")

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
                    for package_type, packages in installed_packages.items():
                        if package_type in new_instance.installed_packages:
                            existing = set(new_instance.installed_packages[package_type])
                            for package in packages:
                                if package not in existing:
                                    new_instance.installed_packages[package_type].append(package)
                    self._save_instance_config(new_instance)

                if progress_callback:
                    progress_callback(100, 100, "导入完成")
                self._notify_changed()
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




def _share_url_to_token(share_url: str) -> str:
    """将 SharePoint 共享 URL 编码为 Graph API sharing token。"""
    import base64 as _b64
    clean = share_url.split("?")[0]
    encoded = _b64.urlsafe_b64encode(clean.encode("utf-8")).decode("ascii")
    return "u!" + encoded.rstrip("=")


def _parse_share_url(share_url: str) -> dict | None:
    """解析 :f: 链接, 提取 site_path 和 item_id (备选方案用)"""
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

    def __init__(self, auth_manager):
        self._auth = auth_manager
        self._site_cache: dict[str, str] = {}

    def _get_token(self) -> str | None:
        """获取 Graph API 访问令牌(优先 Files.Read.All, 降级 User.Read)"""
        if self._auth is None:
            return None
        token = self._auth.acquire_token_silent(scopes=["User.Read", "Files.Read.All"])
        if token:
            return token
        return self._auth.acquire_token_silent(scopes=["User.Read"])

    def _graph_req(self, url: str, timeout: int = 20,
                   method: str = "GET") -> tuple[int, dict | None, str]:
        """通用 Graph API 请求。返回 (status_code, json_data, error_text)。"""
        token = self._get_token()
        if not token:
            return 401, None, "未登录或令牌已过期"
        try:
            for _ in range(5):
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
                        continue
                    else:
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
        """列出共享文件夹内容。"""
        if next_link:
            return self._sp_navigate_folder(next_link, page_size)

        result = self._sp_init_and_list(share_url, page_size)
        if result:
            return result

        return self._graph_list_folder(share_url, page_size)


    def _sp_init_session(self, share_url: str) -> dict | None:
        """初始化 SharePoint cookie session。"""
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

            qs = parse_qs(urlparse(final_url).query)
            folder_path = qs.get("id", [None])[0]
            if folder_path:
                folder_path = unquote(folder_path)

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
                        sp_ctx = json.loads(html[eq:i+1])
                        break
            if not sp_ctx:
                log_warn("OneDrive", "无法解析 _spPageContextInfo")
                return None

            web_url = sp_ctx.get("webAbsoluteUrl", "")
            fd_match = re.search(r'"formDigestValue"\s*:\s*"([^"]+)"', html)
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
        headers = {
            "Accept": "application/json;odata=nometadata",
            "User-Agent": "Mozilla/5.0",
        }
        if form_digest:
            headers["X-RequestDigest"] = form_digest

        items = []
        folders = []
        last_error = ""

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
        """从 next_link 解析并导航到子文件夹。"""
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

        url1 = (f"{GRAPH_API_BASE}/shares/{encoded}"
                f"/driveItem?$expand=children($top={page_size})")
        status, data, err = self._graph_req(url1, method="GET")
        if status in (200, 206) and data:
            children = data.get("children", []) if data else []
            items = [self._parse_item(c) for c in children]
            nl = data.get("@odata.nextLink") if data else None
            log_info("OneDrive", f"Graph expand 成功, {len(items)} 项")
            return {"success": True, "items": items, "next_link": nl}

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
        """在共享文件夹中搜索文件。"""
        sp = self._sp_init_session(share_url)
        if not sp:
            encoded = _share_url_to_token(share_url)
            url = (f"{GRAPH_API_BASE}/shares/{encoded}"
                   f"/driveItem/search(q='{query}')?$top={page_size}")
            status, data, err = self._graph_req(url, method="GET")
            if status in (200, 206) and data:
                items = [self._parse_item(c) for c in (data.get("value", []) if data else [])]
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
        """流式下载文件到本地,支持进度回调。"""
        if download_url.startswith("/"):
            return self._sp_download_file(
                download_url, dest_path, progress_callback, share_url)

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




class AuthManager:
    """微软账号认证管理器 — 基于 MSAL Python,支持交互式登录 + 静默令牌刷新"""

    def __init__(self, base_path: str):
        self._client_id = MSAL_CLIENT_ID()  # 加密凭据 — 惰性解密
        self._authority = MSAL_AUTHORITY
        self._scopes = MSAL_SCOPES
        self._cache_path = os.path.join(base_path, MSAL_CACHE_FILE)
        self._app = None
        self._account = None
        self._init_app()

    def _init_app(self):
        """初始化 MSAL PublicClientApplication(支持加密令牌缓存)"""
        if not MSAL_AVAILABLE:
            return
        cache = msal.SerializableTokenCache()
        if os.path.exists(self._cache_path):
            try:
                # 优先尝试解密(新格式 — 加密的缓存)
                plaintext = self._read_secure_cache()
                if plaintext is None:
                    # 回退:尝试读取明文(旧版本)
                    try:
                        with open(self._cache_path, "r") as f:
                            cache.deserialize(f.read())
                    except Exception:
                        pass
                else:
                    cache.deserialize(plaintext)
            except Exception as e:
                log_warn("App", f"读取加密令牌缓存失败: {e}")
        try:
            self._app = msal.PublicClientApplication(
                self._client_id,
                authority=self._authority,
                token_cache=cache,
            )
        except Exception as e:
            log_warn("App", f"MSAL 初始化失败 (无网络?): {e}")
            self._app = None

    def _read_secure_cache(self) -> Optional[str]:
        """读取加密的令牌缓存。返回明文或 None(不存在/失败)。"""
        try:
            from HMOL_crypto import (
                get_key_manager, decrypt_aes_gcm, CTX_TOKEN_CACHE,
            )
            with open(self._cache_path, "rb") as f:
                blob = f.read()
            # 太短不可能是加密的
            if len(blob) < 30:
                return None
            # 检查首字节 — 加密格式 = version byte (0x01)
            # 明文 JSON 以 '{' 或 '\"' 开头
            if blob[:1] in (b"{", b'"', b" "):
                return None  # 明文,回退处理
            sub_key = get_key_manager(os.path.dirname(self._cache_path)).get_subkey(CTX_TOKEN_CACHE)
            return decrypt_aes_gcm(blob, sub_key, b"msal-token-cache").decode("utf-8")
        except Exception as e:
            log_warn("App", f"读取加密缓存失败: {e}")
            return None

    def _save_cache(self):
        """持久化令牌缓存到文件(AES-256-GCM 加密存储)"""
        if self._app and self._app.token_cache:
            try:
                plaintext = self._app.token_cache.serialize()
                if not plaintext:
                    return
                # 加密后写入
                from HMOL_crypto import (
                    get_key_manager, encrypt_aes_gcm, CTX_TOKEN_CACHE,
                )
                sub_key = get_key_manager(os.path.dirname(self._cache_path)).get_subkey(CTX_TOKEN_CACHE)
                blob = encrypt_aes_gcm(plaintext.encode("utf-8"), sub_key, b"msal-token-cache")
                # 原子写入
                tmp = self._cache_path + ".tmp"
                with open(tmp, "wb") as f:
                    f.write(blob)
                os.replace(tmp, self._cache_path)
                # 限制文件权限
                try:
                    if sys.platform == "win32":
                        import stat
                        os.chmod(self._cache_path, stat.S_IRUSR | stat.S_IWUSR)
                except Exception:
                    pass
            except Exception as e:
                log_warn("App", f"加密保存令牌缓存失败: {e}")

    def login(self) -> dict | None:
        """设备代码流登录: 显示代码,用户在浏览器中输入完成授权。"""
        if not self._app:
            return None
        try:
            flow = self._app.initiate_device_flow(scopes=self._scopes)
            if "user_code" not in flow:
                error = flow.get("error_description", flow.get("error", "无法启动设备代码流"))
                return {"success": False, "error": error}
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
                return None
            if error == "slow_down":
                return None
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
        """尝试静默获取访问令牌(使用缓存中的 refresh token)。"""
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
        self._init_app()

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
        photo_data = self._graph_get_bytes(
            "me/photos('648x648')/$value", token)
        if photo_data:
            return photo_data
        return self._graph_get_bytes("me/photo/$value", token)

    def _acquire_xbox_token(self) -> str | None:
        """获取用于 Xbox Live 认证的专用令牌。"""
        accounts = self._app.get_accounts()
        if not accounts:
            return None
        account = accounts[0]
        try:
            result = self._app.acquire_token_silent(
                scopes=["XboxLive.signin"],
                account=account,
            )
            if "access_token" in result:
                return result["access_token"]
        except Exception:
            pass
        try:
            result = self._app.acquire_token_silent(
                scopes=["https://user.auth.xboxlive.com/.default"],
                account=account,
            )
            if "access_token" in result:
                return result["access_token"]
        except Exception:
            pass
        try:
            result = self._app.acquire_token_silent(
                scopes=MSAL_SCOPES, account=account)
            if "access_token" in result:
                return result["access_token"]
        except Exception:
            pass
        return None

    def _get_xsts_credentials(self) -> dict | None:
        """执行 XAS + XSTS 认证链, 返回 {'uhs': str, 'token': str} 或 None。"""
        msal_token = self._acquire_xbox_token()
        if not msal_token:
            return None
        try:
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
        """获取 Xbox 玩家代号。"""
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
        """获取 Xbox 好友列表。"""
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
                friends.append({
                    "xuid": person.get("xuid", ""),
                    "gamertag": person.get("gamertag", ""),
                    "display_name": person.get("displayName", ""),
                    "display_pic": person.get("displayPicRaw", ""),
                    "is_favorite": person.get("isFavorite", False),
                    "online_state": person.get("presenceState", ""),
                    "presence_text": (person.get("presenceDetail", {}) or {}).get("presenceText", ""),
                })
            total = data.get("totalCount", len(friends))
            return {"success": True, "friends": friends, "total": total}

        except ms_requests.exceptions.HTTPError as e:
            body = ""
            try:
                body = e.response.text[:500]
            except Exception:
                pass
            return {"success": False,
                    "reason": f"Xbox 好友 API 错误 ({e.response.status_code})",
                    "friends": [], "total": 0, "http_body": body}
        except Exception as e:
            return {"success": False, "reason": str(e), "friends": [], "total": 0}




def check_dependencies():
    missing = []
    if not PIL_AVAILABLE:
        missing.append("Pillow")
    if not SEVENZIP_AVAILABLE:
        missing.append("py7zr")
    return missing



class LoginDialog(tk.Toplevel):
    """登录对话框 — 设备代码流 / 离线跳过 / 跳过登录。

    严格按照原版 Qt 实现的 UI 状态机和交互流程重构。
    """

    def __init__(self, parent, auth_manager: AuthManager = None):
        super().__init__(parent)
        self.parent = parent
        self.auth = auth_manager
        # 兼容两种 result 形式：字符串 ("login"/"offline"/"cancel") 或 dict
        self.result = "cancel"
        self._result_data = None
        # 状态机
        self._flow_info = None
        self._poll_job = None
        self._poll_running = False
        self._closed = False
        self._is_online = False
        self._state = "init"  # init / ready / polling / success / expired / failed

        self.title("Hello Mental Omega Launcher — 登录")
        self.resizable(False, False)
        # 不使用 transient,以便主窗口始终可见
        # self.transient(parent)

        self.configure(bg=LIGHT["bg"])
        self._build_ui()

        # 异步检测网络(主线程 + 短超时,避免长时间阻塞)
        self.after(50, self._init_network_check)

    # ────────────────────── 布局 ──────────────────────
    def _build_ui(self):
        # 固定大小,模拟原版 480x560
        self.geometry("480x600")
        ws, hs = self.winfo_screenwidth(), self.winfo_screenheight()
        self.geometry(f"480x600+{(ws-480)//2}+{(hs-600)//2}")

        main_frame = ttk.Frame(self, padding=(32, 24, 32, 24))
        main_frame.pack(fill=tk.BOTH, expand=True)

        # ── 标题 ──
        ttk.Label(main_frame, text="🎮 Hello Mental Omega Launcher (HMOL)",
                  font=("Microsoft YaHei UI", 14, "bold")).pack(pady=(0, 4))
        ttk.Label(main_frame, text="请使用微软账号登录以使用完整功能",
                  font=("Microsoft YaHei UI", 9),
                  foreground=LIGHT["text_secondary"]).pack(pady=(0, 12))

        # ── 设备代码显示区 (初始隐藏) ──
        self.code_frame = ttk.LabelFrame(main_frame, text="设备代码", padding=10)
        self.code_link_var = tk.StringVar(value="")
        self.code_value_var = tk.StringVar(value="——")

        ttk.Label(self.code_frame,
                  text="在浏览器中打开以下网址并输入代码:",
                  font=("Microsoft YaHei UI", 9),
                  foreground=LIGHT["text_secondary"]).pack(anchor=tk.W, pady=(0, 4))
        self.code_link_label = ttk.Label(self.code_frame, textvariable=self.code_link_var,
                                          foreground="#0078d4", cursor="hand2",
                                          font=("Microsoft YaHei UI", 10, "bold"))
        self.code_link_label.pack(anchor=tk.W, pady=(0, 4))
        self.code_link_label.bind("<Button-1>", lambda e: self._on_open_browser())

        ttk.Label(self.code_frame, text="代码:",
                  font=("Microsoft YaHei UI", 9, "bold")).pack(anchor=tk.W, pady=(4, 0))
        self.code_value_label = ttk.Label(self.code_frame, textvariable=self.code_value_var,
                                            font=("Consolas", 22, "bold"),
                                            foreground="#333")
        self.code_value_label.pack(anchor=tk.W, pady=(0, 8))

        # 复制 / 浏览器 按钮
        btn_row = ttk.Frame(self.code_frame)
        btn_row.pack(fill=tk.X, pady=(0, 4))
        self.copy_btn = ttk.Button(btn_row, text="📋 复制代码", command=self._on_copy_code,
                                    width=14)
        self.copy_btn.pack(side=tk.LEFT, padx=(0, 4))
        self.open_browser_btn = ttk.Button(btn_row, text="🌐 浏览器打开",
                                            command=self._on_open_browser, width=14)
        self.open_browser_btn.pack(side=tk.LEFT, padx=4)

        # 刷新代码按钮(初始隐藏,代码过期后显示)
        self.refresh_code_btn = ttk.Button(self.code_frame, text="🔄 刷新代码",
                                             command=self._do_login, width=14)
        # 不 pack,默认隐藏

        # code_frame 初始不 pack,在 _on_flow_result 中动态插入到状态图标之前

        # ── 状态图标 + 状态文本 ──
        self.status_icon_var = tk.StringVar(value="🔍")
        self.status_label_var = tk.StringVar(value="正在检测网络连接...")

        self._status_icon_label = ttk.Label(main_frame, textvariable=self.status_icon_var,
                                            font=("Microsoft YaHei UI", 32))
        self._status_icon_label.pack(pady=(8, 0))
        self._status_text_label = ttk.Label(main_frame, textvariable=self.status_label_var,
                                            font=("Microsoft YaHei UI", 9),
                                            foreground=LIGHT["text_secondary"],
                                            justify=tk.CENTER, wraplength=400)
        self._status_text_label.pack(pady=(4, 12))

        # 弹性空间
        ttk.Frame(main_frame).pack(fill=tk.BOTH, expand=True)

        # ── 按钮区 ──
        # 1. 微软登录
        self.login_btn = ttk.Button(main_frame, text="🔐 使用微软账号登录",
                                     command=self._do_login)
        self.login_btn.pack(fill=tk.X, pady=(4, 4), ipady=8)

        # 2. 跳过登录(基础功能)
        self.skip_btn = ttk.Button(main_frame, text="⏭️ 跳过登录 (基础功能)",
                                    command=self._on_offline)
        self.skip_btn.pack(fill=tk.X, pady=(0, 4))

        # 3. 离线模式(受限功能,仅无网时显示)
        self.offline_btn = ttk.Button(main_frame, text="📡 离线模式 (功能受限)",
                                       command=self._on_offline)
        # 不 pack,默认隐藏

        # 4. 取消
        self.cancel_btn = ttk.Button(main_frame, text="取消",
                                      command=self._on_cancel)
        self.cancel_btn.pack(fill=tk.X, pady=(4, 0))

        # 窗口关闭协议
        self.protocol("WM_DELETE_WINDOW", self._on_cancel)

        # 初始化按钮可见性
        if not MSAL_AVAILABLE:
            self.status_icon_var.set("⚠️")
            self.status_label_var.set(
                "缺少认证组件 (msal)\n请在命令行运行:\npip install msal requests")
            self.login_btn.config(state=tk.DISABLED)
            self.skip_btn.pack_forget()
            self.offline_btn.pack(fill=tk.X, pady=(0, 4), before=self.cancel_btn)
            self.offline_btn.config(text="📡 离线模式 (功能受限)")
        else:
            # 默认显示, _init_network_check 完成后根据网络状态调整
            self.login_btn.config(state=tk.DISABLED)  # 网络检测完成前禁用
            self.skip_btn.pack_forget()  # 联网时才显示

    # ────────────────────── 网络检测 ──────────────────────
    def _init_network_check(self):
        """同步检测网络(短超时 < 3s,主线程执行)。

        注意:不能从后台线程调用 self.after(),会触发
        "main thread is not in main loop" 错误。"""
        if self._closed:
            return
        try:
            online = _check_network_available()
        except Exception:
            online = False
        self._on_network_check_done(online)

    def _on_network_check_done(self, online: bool):
        if self._closed:
            return
        if not MSAL_AVAILABLE:
            return
        if online:
            self.status_icon_var.set("🔐")
            self.status_label_var.set(
                "点击下方按钮,在浏览器中输入代码完成授权")
            self.login_btn.config(state=tk.NORMAL)
            self.skip_btn.pack(fill=tk.X, pady=(0, 4), before=self.cancel_btn)
            self.offline_btn.pack_forget()
        else:
            self.status_icon_var.set("📡")
            self.status_label_var.set(
                "未检测到网络连接\n可以选择离线模式进入程序\n"
                "(笨蛋广场和账户功能将不可用)")
            self.login_btn.config(state=tk.DISABLED)
            self.skip_btn.pack_forget()
            self.offline_btn.pack(fill=tk.X, pady=(0, 4), before=self.cancel_btn)
            self.offline_btn.config(text="📡 离线模式 (功能受限)")

    # ────────────────────── 设备代码流 ──────────────────────
    def _do_login(self):
        """启动设备代码流 — 主线程同步执行(HTTP 调用 1-3s)。

        注意:不能从后台线程调用 self.after(),会触发
        "main thread is not in main loop" 错误。"""
        if not self.auth:
            return
        self._stop_polling()
        self.login_btn.config(state=tk.DISABLED, text="⏳ 正在连接...")
        if self.skip_btn.winfo_ismapped():
            self.skip_btn.pack_forget()
        self.status_label_var.set("正在与微软服务器通信...")
        self.update_idletasks()

        try:
            flow = self.auth.start_device_flow()
        except Exception as e:
            flow = {"success": False, "error": str(e)}
        self._on_flow_result(flow)

    def _on_flow_result(self, flow):
        if self._closed:
            return
        if not flow or not flow.get("success"):
            err = (flow or {}).get("error", "无法启动设备代码流")
            self.status_icon_var.set("❌")
            self.status_label_var.set(f"❌ {err}")
            self.login_btn.config(state=tk.NORMAL, text="🔐 使用微软账号登录")
            if not self.skip_btn.winfo_ismapped():
                self.skip_btn.pack(fill=tk.X, pady=(0, 4), before=self.cancel_btn)
            if not self.offline_btn.winfo_ismapped():
                self.offline_btn.pack(fill=tk.X, pady=(0, 4), before=self.cancel_btn)
            self.offline_btn.config(text="📡 离线模式 (功能受限)")
            return

        self._flow_info = flow
        self.code_link_var.set(flow["verification_uri"])
        self.code_value_var.set(flow["user_code"])

        # 显示代码区
        self._repack_code_frame()
        # 隐藏刷新按钮(新代码生成后)
        self.refresh_code_btn.pack_forget()
        # 隐藏登录/跳过按钮
        self.login_btn.pack_forget()
        self.skip_btn.pack_forget()

        # 状态
        self.status_icon_var.set("📱")
        self.status_label_var.set(
            "请在浏览器中打开下方网址\n输入显示的代码完成授权\n\n"
            "授权完成后将自动进入程序")

        # 自动打开浏览器
        self._on_open_browser()

        # 开始轮询
        self._start_polling()

    def _repack_code_frame(self):
        """将代码区重新放到主框架中正确位置(在状态图标上方)。"""
        self.code_frame.pack_forget()
        # before 参数将 code_frame 插入到 status_icon 之前
        self.code_frame.pack(fill=tk.X, pady=(0, 8), before=self._status_icon_label)

    def _start_polling(self):
        """开始轮询设备代码授权状态(主线程 after 调度)。"""
        if self._poll_running or self._closed:
            return
        if not self._flow_info:
            return
        self._poll_running = True
        self._schedule_next_poll()

    def _schedule_next_poll(self):
        """调度下一次轮询。"""
        if self._closed or not self._poll_running or not self._flow_info:
            return
        interval_ms = (self._flow_info.get("interval", 5) * 1000)
        self._poll_job = self.after(interval_ms, self._do_poll)

    def _do_poll(self):
        """执行一次轮询(主线程 after 调度,同步 HTTP 调用 < 2s)。"""
        if self._closed or not self._poll_running or not self._flow_info:
            return
        try:
            result = self.auth.poll_device_flow(self._flow_info)
        except Exception as e:
            result = {"success": False, "error": str(e)}
        self._on_poll_result(result)

    def _on_poll_result(self, result):
        if self._closed or not self._poll_running:
            return
        if result is None:
            # 还在等待,继续轮询
            self._schedule_next_poll()
            return

        # 有结果了
        self._poll_running = False
        if result.get("success"):
            self.status_icon_var.set("✅")
            self.status_label_var.set("✅ 登录成功!正在进入程序...")
            self.result = "login"
            self._result_data = result.get("account")
            self.after(800, self._cleanup_and_close)
        elif result.get("expired"):
            self.status_icon_var.set("⌛")
            self.status_label_var.set(f"⌛ {result.get('error', '代码已过期')}")
            # 显示刷新代码按钮
            self.refresh_code_btn.pack(pady=(4, 0))
            # 恢复登录按钮
            self._restore_login_buttons()
        else:
            self.status_icon_var.set("❌")
            self.status_label_var.set(f"❌ {result.get('error', '授权失败')}")
            self._restore_login_buttons()
            self.code_frame.pack_forget()
            self.refresh_code_btn.pack_forget()

    def _restore_login_buttons(self):
        """恢复登录/跳过/离线按钮。"""
        self.login_btn.config(state=tk.NORMAL, text="🔐 重试登录")
        self.login_btn.pack(fill=tk.X, pady=(4, 4))
        if self._is_online and not self.skip_btn.winfo_ismapped():
            self.skip_btn.pack(fill=tk.X, pady=(0, 4), before=self.cancel_btn)
        if not self.offline_btn.winfo_ismapped():
            self.offline_btn.pack(fill=tk.X, pady=(0, 4), before=self.cancel_btn)
            self.offline_btn.config(text="📡 离线模式 (功能受限)")

    # ────────────────────── 按钮动作 ──────────────────────
    def _on_copy_code(self):
        code = self._flow_info.get("user_code", "") if self._flow_info else ""
        if code:
            self.clipboard_clear()
            self.clipboard_append(code)
            self.status_label_var.set("✅ 代码已复制到剪贴板!")
            self.after(2000, lambda: self.status_label_var.set(
                "请在浏览器中打开下方网址\n输入显示的代码完成授权\n\n"
                "授权完成后将自动进入程序"))

    def _on_open_browser(self):
        """用 TheWorld 浏览器(或系统默认)打开登录 URL。"""
        url = self._flow_info.get("verification_uri", "") if self._flow_info else ""
        if not url:
            self.status_label_var.set("暂无可打开的网址,请先点击「使用微软账号登录」")
            return
        # 浏览器路径:启动器同目录\DLC\[Wine]TheWorld(浏览器)\TheWorld.exe
        try:
            base = get_program_base_path()
        except Exception:
            base = os.getcwd()
        browser_exe = os.path.join(base, "DLC", "[Wine]TheWorld(浏览器)", "TheWorld.exe")
        try:
            if os.path.exists(browser_exe):
                subprocess.Popen(
                    [browser_exe, url],
                    creationflags=getattr(subprocess, "DETACHED_PROCESS", 0) |
                                  getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0))
                self.status_label_var.set("🌐 已使用 TheWorld 浏览器打开!")
            else:
                webbrowser.open(url)
                self.status_label_var.set("🌐 已使用系统默认浏览器打开")
        except Exception as e:
            try:
                webbrowser.open(url)
                self.status_label_var.set(f"🌐 已使用系统默认浏览器打开 (TheWorld 失败: {e})")
            except Exception as e2:
                self.status_label_var.set(f"❌ 无法打开浏览器: {e2}")

    def _on_offline(self):
        self._stop_polling()
        self.result = "offline"
        self._cleanup_and_close()

    def _on_cancel(self):
        self._stop_polling()
        self.result = "cancel"
        self._cleanup_and_close()

    # ────────────────────── 清理 ──────────────────────
    def _stop_polling(self):
        self._poll_running = False
        if self._poll_job:
            try:
                self.after_cancel(self._poll_job)
            except Exception:
                pass
            self._poll_job = None

    def _cleanup_and_close(self):
        self._closed = True
        self._stop_polling()
        try:
            self.grab_release()
        except Exception:
            pass
        self.destroy()

    def get_result(self) -> str:
        """返回 'login' / 'offline' / 'cancel'(兼容 Qt 版本 API)。"""
        return self.result if isinstance(self.result, str) else "cancel"


# ════════════════════════════════════════════════════════════════════
# 不想上班 不想上班 不想上班 不想上班
# ════════════════════════════════════════════════════════════════════

class DependencyWarningDialog(tk.Toplevel):
    """显示缺失的 Python 依赖库及安装命令。"""

    def __init__(self, parent, missing_libs: list = None):
        super().__init__(parent)
        self.parent = parent

        self.title("依赖缺失警告")
        self.resizable(False, False)
        self.transient(parent)
        self.grab_set()

        self.configure(bg=LIGHT["bg"])

        main_frame = ttk.Frame(self, padding=20)
        main_frame.pack(fill=tk.BOTH, expand=True)

        # ── 标题 ──
        ttk.Label(main_frame, text="⚠ 缺失依赖库",
                  font=("Microsoft YaHei UI", 13, "bold"),
                  foreground="#e67e22").pack(pady=(0, 10))

        desc = ttk.Label(main_frame,
                          text="以下 Python 库未安装,部分功能可能受限。\n"
                               "请使用 pip 命令安装后重启程序:",
                          wraplength=380, font=("Microsoft YaHei UI", 9))
        desc.pack(anchor=tk.W, pady=(0, 8))

        # ── 缺失库列表 ──
        if missing_libs is None:
            missing_libs = check_dependencies()

        if missing_libs:
            list_frame = ttk.Frame(main_frame, relief=tk.SUNKEN, borderwidth=1)
            list_frame.pack(fill=tk.X, pady=(0, 12))

            for lib in missing_libs:
                row = ttk.Frame(list_frame, padding=(8, 3))
                row.pack(fill=tk.X)
                ttk.Label(row, text=f"• {lib}",
                          font=("Consolas", 10)).pack(side=tk.LEFT)
                ttk.Label(row, text=f"pip install {lib.lower()}",
                          font=("Consolas", 9),
                          foreground=LIGHT["text_secondary"]).pack(side=tk.RIGHT)
        else:
            ttk.Label(main_frame, text="✓ 所有依赖已安装",
                      font=("Microsoft YaHei UI", 9),
                      foreground="#27ae60").pack(pady=(0, 12))

        # ── 按钮 ──
        btn_frame = ttk.Frame(main_frame)
        btn_frame.pack(fill=tk.X, pady=(4, 0))
        ttk.Button(btn_frame, text="继续", command=self._on_continue).pack(side=tk.RIGHT)

        self.protocol("WM_DELETE_WINDOW", self._on_continue)
        self._center_on_parent()
        self.wait_window()

    def _center_on_parent(self):
        self.update_idletasks()
        pw, ph = self.parent.winfo_width(), self.parent.winfo_height()
        px, py = self.parent.winfo_rootx(), self.parent.winfo_rooty()
        w, h = self.winfo_width(), self.winfo_height()
        x = px + (pw - w) // 2
        y = py + (ph - h) // 2
        self.geometry(f"+{x}+{y}")

    def _on_continue(self):
        try:
            self.grab_release()
        except Exception:
            pass
        self.destroy()


# ════════════════════════════════════════════════════════════════════
# 好好，嗯嗯
# ════════════════════════════════════════════════════════════════════

HMOL_EULA_TEXT = """\
## 简体中文版本

### HMOL 启动器最终用户许可协议 (EULA) 与服务条款

**重要提示：在安装、复制或以其他方式使用本软件之前，请仔细阅读本协议。**
**安装或使用本软件即表示您同意本协议的所有条款。如不同意，请勿安装或使用本软件。**

***

### 1. 重要声明 / Disclaimers

**This launcher is not affiliated with EA, the Red Alert 2 development team, or the Mental Omega development team.**
**本启动器与 EA (Electronic Arts)、红色警戒 2 开发团队、心灵终结 (Mental Omega) 开发团队不存在任何关联、授权、赞助或背书关系。**

- "Red Alert 2"、"红色警戒 2"、"Command & Conquer"、"命令与征服" 是 **Electronic Arts Inc.** 的注册商标。
- "Mental Omega"、"心灵终结" 是独立模组项目，与 EA 无官方关联。
- 本启动器为**独立第三方工具**，由 HMOL 项目贡献者开发，与上述任何实体均无关。

***

### 2. 接受条款 / Acceptance

通过安装、复制、下载或以其他方式使用本软件（"HMOL 启动器"），您（"用户"）确认：

1. 您已阅读、理解并同意受本协议所有条款约束。
2. 您已达到所在司法管辖区的法定成年年龄（通常为 18 周岁）。
3. 您有合法权利和能力签订本协议。
4. 如代表组织使用本软件，您有权代表该组织接受本协议。

**如您不同意任何条款，请立即停止使用并卸载本软件。**

***

### 3. 二次修改禁令 / No Modification Clause ⛔

**未经 HMOL 项目版权所有者事先书面许可, 用户严禁对本软件进行任何形式的二次修改。**

本条款涵盖以下**严格禁止**的行为:

| 编号 | 禁止行为 | 说明 |
|---|---|---|
| 1 | 修改源代码 | 任何对 `.py` 文件的改动 |
| 2 | 修改二进制 | 反编译后修改、补丁、热更新 |
| 3 | 反向工程 | 任何形式的反编译、反汇编、静态分析 |
| 4 | 创建衍生作品 | 基于本软件的 fork、改编、翻译 |
| 5 | 代码复用 | 将本软件任何部分代码用于其他项目 |
| 6 | 重新分发 | 上传至任何代码托管平台或分发渠道 |
| 7 | 商业使用 | 用于商业产品、服务或盈利活动 |
| 8 | 安全绕过 | 绕过、破解、规避本软件的安全机制 |
| 9 | 标识移除 | 移除、隐藏、修改版权声明或商标 |

**违规处理**: 违反本条款, 您的使用许可将**自动立即终止**, 您必须立即停止使用并删除本软件所有副本。许可方保留依据中华人民共和国《著作权法》《计算机软件保护条例》追究法律责任的权利, 包括但不限于停止侵害、消除影响、赔偿损失。

**例外**: 仅以下行为在不构成"修改"的前提下被允许:
- 为个人备份目的复制本软件
- 阅读、学习、研究源代码
- 在 GitHub Issues 提交 bug 报告 (但不附带修改后的代码)

***

### 4. 许可授予 / License Grant

在遵守本协议的前提下，HMOL 项目（"许可方"）授予您：

| 权利     | 范围                       |
| ------ | ------------------------ |
| ✅ 使用   | 个人非商业用途                  |
| ✅ 复制   | 出于备份目的                   |
| ❌ 商业销售 | 禁止                       |
| ❌ 再许可  | 禁止                       |
| ❌ 反向工程 | 禁止 (除本协议明确允许或适用法律允许外)    |

本软件按"现状"提供，**不附带任何明示或暗示的保证**。

***

### 5. 用户责任 / User Responsibilities

您同意：

1. **合法使用**：仅将本软件用于合法目的，不得用于任何违反您所在司法管辖区法律的用途。
2. **不滥用**：不得使用本软件进行以下行为：
   - 攻击、入侵或干扰他人计算机系统
   - 传播恶意代码、病毒或有害内容
   - 绕过、破解或规避 Microsoft / EA / QQ 等第三方平台的安全机制
   - 违反 Microsoft 服务条款、Xbox Live 行为准则或 QQ 机器人使用规范
3. **账户责任**：您对使用本软件时涉及的 Microsoft 账户、QQ 账户、OneDrive 账户等所有账户活动负全部责任。
4. **数据安全**：妥善保管您的登录凭据，定期更新密码，启用双因素认证（如适用）。
5. **游戏资源合规**：确保通过本软件安装/下载的所有任务包、地图等资源不侵犯第三方知识产权。

***

### 6. 知识产权 / Intellectual Property Rights

1. **本软件所有权**：HMOL 启动器的源代码、UI 设计、图标、文档等知识产权归 HMOL 项目贡献者所有（MIT 许可证 + EULA 附加限制）。
2. **第三方内容**：
   - "Red Alert 2" / "Command & Conquer" 相关资产版权归 **Electronic Arts Inc.** 所有。
   - "Mental Omega" 相关资产版权归 **Mental Omega 开发团队**所有。
   - 用户通过本软件访问的 OneDrive 资源归**资源上传者**所有。
3. **本软件不包含**任何 Red Alert 2 或 Mental Omega 的游戏本体文件、模型、音频、地图数据。
4. **商标使用**：未经权利人书面许可，您不得使用 EA、Mental Omega 或任何第三方的商标、商号或标识。

***

### 7. 责任限制 / Limitation of Liability

**在适用法律允许的最大范围内：**

1. **无担保**：本软件按"现状"提供，不附带任何明示或暗示的担保，包括但不限于适销性、特定用途适用性、非侵权性的担保。
2. **责任上限**：在任何情况下，许可方、贡献者、作者或版权持有人均不对您或任何第三方因使用或无法使用本软件而产生的任何直接、间接、附带、特殊、惩罚性或后果性损害（包括但不限于数据丢失、业务中断、利润损失等）承担责任，即使已被告知此类损害的可能性。
3. **第三方服务**：本软件可能涉及 Microsoft Xbox Live、OneDrive、QQ 机器人等第三方服务。这些服务的行为、政策、可用性由相应第三方控制，许可方不承担任何责任。
4. **不可抗力**：许可方不对因不可抗力（包括但不限于天灾、战争、政府行为、网络故障）造成的服务中断承担责任。

***

### 8. 隐私政策 / Privacy Policy

#### 8.1 信息收集

本软件**默认不收集**任何用户个人信息。除非您主动选择以下功能：

| 功能           | 收集信息                       | 用途               |
| ------------ | -------------------------- | ---------------- |
| Microsoft 登录 | OAuth 令牌、用户名、Xbox Gamertag | 身份验证、OneDrive 访问 |
| QQ 喊话        | QQ 群号/AppID                | 消息发送             |
| OneDrive 下载  | 共享资源 URL                   | 资源下载             |
| 错误日志         | 程序崩溃信息、异常堆栈                | 调试改进（**可选**）     |

#### 8.2 信息存储

- 所有 Microsoft 令牌使用 **AES-256-GCM 加密**存储于本地，密钥由机器码派生。
- 用户配置文件位于用户主目录下，不上传到任何远程服务器。
- 不会主动将您的任何数据传送到 HMOL 项目运营者的服务器（HMOL 项目**不运营任何中央服务器**）。

#### 8.3 第三方数据处理

当您使用以下第三方服务时，相关数据由相应服务提供商处理：
- **Microsoft** ([Microsoft 隐私声明](https://privacy.microsoft.com))
- **腾讯 QQ** ([QQ 隐私政策](https://privacy.qq.com))
- **OneDrive** 资源提供方（其他用户上传的分享链接）

#### 8.4 Cookies 与追踪

本软件**不使用 Cookies**，不集成任何第三方追踪 SDK 或分析工具。

#### 8.5 您的权利

您有权随时：
- 撤回对某功能的授权
- 删除本地缓存的所有数据（`HMOL_config.json`、`msal_token_cache.enc`）
- 卸载本软件并删除所有相关文件

***

### 9. 禁止行为 / Prohibited Uses

您**不得**使用本软件进行以下行为：

1. **违法活动**：违反任何适用法律、法规、国际条约。
2. **侵犯权利**：侵犯他人知识产权、隐私权、肖像权、名誉权等合法权益。
3. **网络攻击**：传播恶意代码、DDoS 攻击、钓鱼、欺诈、勒索。
4. **绕过安全**：逆向工程、破解、篡改 Microsoft / Xbox Live / QQ 的安全机制。
5. **作弊行为**：在 Mental Omega 或其他游戏中使用未授权的作弊工具。
6. **资源侵权**：上传、分发、下载受版权保护的游戏本体文件。
7. **刷屏骚扰**：滥用 QQ 喊话功能骚扰他人。
8. **商业滥用**：将本软件用于商业牟利、出租、销售、转售。
9. **虚假宣传**：冒充 EA、Mental Omega 开发团队或本软件作者发布信息。
10. **恶意修改**：植入后门、木马、挖矿代码或其他恶意功能。
11. **二次修改**：详见 §3「二次修改禁令」。

**违反上述任一条款，许可方有权立即终止您的使用许可并保留追究法律责任的权利。**

***

### 10. 终止 / Termination

#### 10.1 由您终止

您可随时通过卸载本软件终止本协议。

#### 10.2 由许可方终止

在以下情况下，许可方有权立即终止本协议，恕不另行通知：
- 您违反本协议任何条款
- 您滥用本软件造成他人损害
- 适用法律要求终止
- 本软件停止维护

#### 10.3 终止效力

协议终止后：
- 您必须立即停止使用本软件
- 卸载并删除本软件所有副本
- 本协议中按其性质应继续有效的条款（包括但不限于知识产权、责任限制、争议解决）继续有效。

***

### 11. 协议修改 / Amendments

1. 许可方保留随时修改本协议的权利。
2. 修改后的协议将在 [GitHub 仓库](https://github.com/) 发布，并标注"最后更新日期"。
3. 如您不同意修改后的协议，应立即停止使用本软件。
4. 在修改后继续使用本软件即视为接受修改后的协议。

***

### 12. 争议解决 / Dispute Resolution

1. **适用法律**：本协议适用中华人民共和国法律（不包含冲突法规则）。
2. **协商优先**：因本协议产生的争议，双方应首先友好协商解决。
3. **管辖法院**：协商不成的，任一方可向许可方所在地有管辖权的人民法院提起诉讼。
4. **可分割性**：如本协议任何条款被认定为无效或不可执行，其余条款继续有效。

***

### 13. 联系 / Contact

如对本协议有任何疑问，请通过以下方式联系：
- GitHub Issues: https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues
- QQ 群: 1034243331

***

### 14. 其他 / Miscellaneous

1. **完整协议**：本协议构成您与许可方之间关于本软件的完整协议。
2. **无弃权**：许可方未行使本协议任何权利不视为放弃该权利。
3. **可分割性**：本协议任何条款无效不影响其他条款效力。
4. **转让**：未经许可方书面同意，您不得转让本协议项下权利义务。
5. **语言**：本协议以中文为准。英文版本仅供参考。

***

**© 2026 HMOL Project Contributors. All Rights Reserved.**
"""


def _filter_eula_chinese_only(markdown_text: str) -> str:
    """过滤 EULA.md 内容,仅保留中文部分。

    新的 EULA.md 同时包含简体中文版本和 English Version。
    根据程序设计要求(默认仅展示中文版本),需要移除英文部分。

    实现策略:
      - 找到 "## English Version" 标题(行首匹配)
      - 仅保留该标题之前的内容
      - 若无匹配,返回原文(说明该文件只有中文版本)

    Args:
        markdown_text: 完整的 EULA.md 文本

    Returns:
        仅包含中文部分的文本
    """
    if not markdown_text:
        return markdown_text
    import re as _re
    # 匹配以 "## English Version" 开头的行
    pattern = _re.compile(
        r"^#{1,6}\s*English\s+Version\s*$",
        _re.MULTILINE | _re.IGNORECASE,
    )
    m = pattern.search(markdown_text)
    if m:
        return markdown_text[: m.start()].rstrip() + "\n"
    # 备用方案:寻找 "### HMOL Launcher End User License" 这类英文标题
    m2 = _re.search(
        r"^#{1,6}\s*HMOL\s+Launcher\s+End\s+User\s+License\s+Agreement",
        markdown_text,
        _re.MULTILINE | _re.IGNORECASE,
    )
    if m2:
        return markdown_text[: m2.start()].rstrip() + "\n"
    # 若无匹配,返回原文
    return markdown_text


class EULADialog(tk.Toplevel):
    """使用协议 / EULA 接受对话框(首次运行展示)。

    设计:
      - 模态阻塞,用户必须明确接受或拒绝
      - 拒绝 → 程序立即退出(不保留任何用户操作记录)
      - 接受 → 在配置中记录 `eula_accepted=True` 和 `eula_accepted_version=__version__`
      - 仅显示中文版本(根据用户要求)
      - 核心条款"不允许二次修改程序"高亮显示
    """

    def __init__(self, parent, eula_version: str = ""):
        super().__init__(parent)
        self.parent = parent
        self.accepted = False
        self.eula_version = eula_version or __version__
        self._closed_by_user = False

        self.title("使用协议 / 服务条款")
        self.resizable(True, True)
        self.transient(parent)
        self.grab_set()
        # 拒绝关闭 — 用户必须点击"同意"或"不同意"
        self.protocol("WM_DELETE_WINDOW", self._on_decline)

        self.configure(bg=LIGHT["bg"])

        # 窗口尺寸自适应(适配不同屏幕)
        try:
            sw, sh = self.winfo_screenwidth(), self.winfo_screenheight()
        except Exception:
            sw, sh = 1280, 800
        # 默认尺寸 720x640,最小 600x500
        w = min(820, max(620, int(sw * 0.55)))
        h = min(760, max(560, int(sh * 0.75)))
        x = (sw - w) // 2
        y = (sh - h) // 2
        try:
            self.geometry(f"{w}x{h}+{x}+{y}")
        except Exception:
            self.geometry(f"{w}x{h}")
        self.minsize(600, 500)

        main_frame = ttk.Frame(self, padding=14)
        main_frame.pack(fill=tk.BOTH, expand=True)

        # ── 标题 ──
        title_label = ttk.Label(
            main_frame,
            text="📜 使用协议 / 服务条款",
            font=("Microsoft YaHei UI", 14, "bold"),
        )
        title_label.pack(pady=(0, 6))

        # ── 提示 ──
        notice = ttk.Label(
            main_frame,
            text=(
                f"在首次使用本启动器之前,请仔细阅读并同意以下全部条款。"
                f"\n使用协议版本:v{self.eula_version}"
            ),
            font=("Microsoft YaHei UI", 9),
            foreground=LIGHT["text_secondary"],
            wraplength=max(500, w - 60),
            justify=tk.CENTER,
        )
        notice.pack(pady=(0, 8))

        # ── EULA 文本(可滚动) ──
        text_frame = ttk.Frame(main_frame, relief=tk.SUNKEN, borderwidth=1)
        text_frame.pack(fill=tk.BOTH, expand=True, pady=(0, 10))

        scrollbar = ttk.Scrollbar(text_frame)
        scrollbar.pack(side=tk.RIGHT, fill=tk.Y)

        try:
            self.text = tk.Text(
                text_frame,
                wrap=tk.WORD,
                font=("Microsoft YaHei UI", 10),
                bg=LIGHT["surface_alt"],
                fg=LIGHT["text"],
                padx=10, pady=10,
                yscrollcommand=scrollbar.set,
                relief=tk.FLAT,
                borderwidth=0,
                selectbackground=LIGHT.get("selection", "#cfe2ff"),
            )
        except Exception:
            self.text = tk.Text(
                text_frame, wrap=tk.WORD, font=("Microsoft YaHei UI", 10),
                yscrollcommand=scrollbar.set,
            )
        self.text.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        scrollbar.config(command=self.text.yview)

        # 标签样式
        self.text.tag_configure(
            "core_clause",
            font=("Microsoft YaHei UI", 10, "bold"),
            foreground="#c0392b",
        )
        self.text.tag_configure(
            "disclaimer",
            font=("Microsoft YaHei UI", 10, "bold"),
            foreground="#8e44ad",
        )
        self.text.tag_configure(
            "section_header",
            font=("Microsoft YaHei UI", 11, "bold"),
            foreground="#2c3e50",
        )

        # 优先加载 EULA.md 完整版(仅中文部分),文件缺失时回退到内嵌文本
        eula_loaded = False
        try:
            base = get_program_base_path()
            eula_path = os.path.join(base, "EULA.md")
            if os.path.isfile(eula_path):
                with open(eula_path, "r", encoding="utf-8") as f:
                    full_text = f.read()
                full_text = _filter_eula_chinese_only(full_text)
                self._insert_styled_text(self.text, full_text)
                eula_loaded = True
        except Exception:
            pass
        if not eula_loaded:
            self._insert_styled_text(self.text, HMOL_EULA_TEXT)

        # 滚动到顶部
        self.text.see("1.0")
        self.text.config(state=tk.DISABLED)

        # ── 鼠标滚轮支持 ──
        self._bind_mousewheel(self.text)

        # ── 复选框(必须勾选才能接受) ──
        self.consent_var = tk.BooleanVar(value=False)
        consent_frame = ttk.Frame(main_frame)
        consent_frame.pack(fill=tk.X, pady=(0, 8))

        consent_cb = ttk.Checkbutton(
            consent_frame,
            text="我已仔细阅读、理解并同意上述全部条款(包括 §3"
                 "「核心条款:二次修改禁令」)",
            variable=self.consent_var,
            command=self._on_consent_toggle,
        )
        consent_cb.pack(anchor=tk.W)

        # ── 按钮区 ──
        btn_frame = ttk.Frame(main_frame)
        btn_frame.pack(fill=tk.X, pady=(4, 0))

        # 不同意按钮(在左侧,视觉权重低)
        decline_btn = ttk.Button(
            btn_frame,
            text="✗ 不同意",
            command=self._on_decline,
        )
        decline_btn.pack(side=tk.LEFT)

        # 同意按钮(在右侧,默认禁用,需先勾选)
        self.accept_btn = ttk.Button(
            btn_frame,
            text="✓ 同意",
            command=self._on_accept,
        )
        self.accept_btn.pack(side=tk.RIGHT)
        try:
            self.accept_btn.state(["disabled"])
        except Exception:
            pass

        # ── 联系反馈链接 ──
        link_frame = ttk.Frame(main_frame)
        link_frame.pack(fill=tk.X, pady=(8, 0))
        link_label = ttk.Label(
            link_frame,
            text="如有疑问,请访问 GitHub Issues:",
            font=("Microsoft YaHei UI", 8),
            foreground=LIGHT["text_secondary"],
        )
        link_label.pack(side=tk.LEFT)
        link_url = ttk.Label(
            link_frame,
            text="github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues",
            font=("Microsoft YaHei UI", 8, "underline"),
            foreground="#3498db",
            cursor="hand2",
        )
        link_url.pack(side=tk.LEFT, padx=4)
        link_url.bind(
            "<Button-1>",
            lambda e: self._open_url(
                "https://github.com/OrangeArtc0915/"
                "Hello-Mental-Omega-Launcher/issues"
            ),
        )

        self._center_on_parent()
        self.update_idletasks()
        # 强制窗口在父窗口之上
        try:
            self.lift()
            self.attributes("-topmost", True)
            self.after(150, lambda: self.attributes("-topmost", False))
            self.focus_force()
        except Exception:
            pass

    def _insert_styled_text(self, text_widget, content: str) -> None:
        """插入带样式标签的文本。

        高亮规则:
          - 包含「不允许二次修改」「核心条款」的行 → core_clause 标签
          - 包含「重要免责声明」「无任何关联」的行 → disclaimer 标签
          - 形如「1. xxx」/「(a) xxx」等 → section_header 标签
        """
        for line in content.splitlines():
            tag = None
            stripped = line.strip()
            # 核心条款相关(新 EULA §3 "二次修改禁令")
            if (
                "二次修改禁令" in line
                or "不允许二次修改" in line
                or "未经 HMOL" in line
                or "核心条款" in line
                or "严格禁止" in line
                or "违规处理" in line
            ):
                tag = "core_clause"
            # 重要声明 / 免责声明
            elif (
                "重要声明" in line
                or "重要免责声明" in line
                or "无任何关联" in line
                or "不存在任何关联" in line
                or "不附带任何明示或暗示的保证" in line
            ):
                tag = "disclaimer"
            # 装饰性前缀也用 core_clause 色
            elif stripped.startswith("⛔") or stripped.startswith("🚫") or stripped.startswith("【核心条款】"):
                tag = "core_clause"
            if tag:
                text_widget.insert(tk.END, line + "\n", tag)
            else:
                text_widget.insert(tk.END, line + "\n")

    def _bind_mousewheel(self, widget) -> None:
        """绑定鼠标滚轮到 Text 控件(跨平台)。"""
        def _on_mousewheel(event):
            try:
                # Windows / macOS
                if event.delta:
                    widget.yview_scroll(int(-1 * (event.delta / 120)), "units")
                # Linux (Button-4 / Button-5)
                elif event.num == 4:
                    widget.yview_scroll(-1, "units")
                elif event.num == 5:
                    widget.yview_scroll(1, "units")
            except Exception:
                pass
        # Windows / macOS
        widget.bind("<MouseWheel>", _on_mousewheel)
        # Linux
        widget.bind("<Button-4>", _on_mousewheel)
        widget.bind("<Button-5>", _on_mousewheel)

    def _open_url(self, url: str) -> None:
        """在系统默认浏览器中打开 URL。"""
        try:
            webbrowser.open(url)
        except Exception:
            pass

    def _center_on_parent(self):
        try:
            self.update_idletasks()
            pw, ph = self.parent.winfo_width(), self.parent.winfo_height()
            px, py = self.parent.winfo_rootx(), self.parent.winfo_rooty()
            w, h = self.winfo_width(), self.winfo_height()
            x = px + (pw - w) // 2
            y = py + (ph - h) // 2
            self.geometry(f"+{x}+{y}")
        except Exception:
            pass

    def _on_consent_toggle(self):
        try:
            if self.consent_var.get():
                self.accept_btn.state(["!disabled"])
            else:
                self.accept_btn.state(["disabled"])
        except Exception:
            pass

    def _on_accept(self):
        if not self.consent_var.get():
            return
        self._closed_by_user = True
        self.accepted = True
        # 记录审计日志(只有接受时才记录)
        try:
            from HMOL_audit import get_audit
            audit = get_audit()
            audit.log("eula_accepted", True, version=self.eula_version)
        except Exception:
            pass
        try:
            self.grab_release()
        except Exception:
            pass
        self.destroy()

    def _on_decline(self):
        # 二次确认(避免误操作)
        try:
            from tkinter import messagebox as _mb
            confirm = _mb.askyesno(
                "确认拒绝使用协议",
                "您选择了「不同意」使用协议。\n\n"
                "根据协议条款,程序将立即退出,且不会保留任何"
                "用户操作记录。\n\n"
                "确定要退出程序吗?",
                icon="warning",
                default="no",
            )
            if not confirm:
                return
        except Exception:
            pass
        self._closed_by_user = True
        self.accepted = False
        # 不记录审计日志 — 用户拒绝时不保留任何记录
        try:
            self.grab_release()
        except Exception:
            pass
        self.destroy()



class EULAViewerDialog(tk.Toplevel):
    """使用协议查看器(只读,用于"关于"页面)。

    与 EULADialog 的区别:
      - 无"同意"/"不同意"按钮(只有"关闭"按钮)
      - 不可触发协议重新接受流程
      - 仅用于查阅已接受的协议内容
    """

    def __init__(self, parent):
        super().__init__(parent)
        self.parent = parent

        self.title("使用协议 / 服务条款")
        self.resizable(True, True)
        self.transient(parent)

        self.configure(bg=LIGHT["bg"])

        # 窗口尺寸自适应
        try:
            sw, sh = self.winfo_screenwidth(), self.winfo_screenheight()
        except Exception:
            sw, sh = 1280, 800
        w = min(820, max(620, int(sw * 0.55)))
        h = min(760, max(560, int(sh * 0.75)))
        x = (sw - w) // 2
        y = (sh - h) // 2
        try:
            self.geometry(f"{w}x{h}+{x}+{y}")
        except Exception:
            self.geometry(f"{w}x{h}")
        self.minsize(600, 500)

        main_frame = ttk.Frame(self, padding=14)
        main_frame.pack(fill=tk.BOTH, expand=True)

        # ── 标题 ──
        ttk.Label(
            main_frame,
            text="📜 使用协议 / 服务条款",
            font=("Microsoft YaHei UI", 14, "bold"),
        ).pack(pady=(0, 6))

        # ── 提示 ──
        ttk.Label(
            main_frame,
            text="以下是您已同意的使用协议内容(只读)。",
            font=("Microsoft YaHei UI", 9),
            foreground=LIGHT["text_secondary"],
            wraplength=max(500, w - 60),
            justify=tk.CENTER,
        ).pack(pady=(0, 8))

        # ── 协议文本(可滚动) ──
        text_frame = ttk.Frame(main_frame, relief=tk.SUNKEN, borderwidth=1)
        text_frame.pack(fill=tk.BOTH, expand=True, pady=(0, 10))

        scrollbar = ttk.Scrollbar(text_frame)
        scrollbar.pack(side=tk.RIGHT, fill=tk.Y)

        try:
            self.text = tk.Text(
                text_frame,
                wrap=tk.WORD,
                font=("Microsoft YaHei UI", 10),
                bg=LIGHT["surface_alt"],
                fg=LIGHT["text"],
                padx=10, pady=10,
                yscrollcommand=scrollbar.set,
                relief=tk.FLAT,
                borderwidth=0,
                selectbackground=LIGHT.get("selection", "#cfe2ff"),
            )
        except Exception:
            self.text = tk.Text(
                text_frame, wrap=tk.WORD, font=("Microsoft YaHei UI", 10),
                yscrollcommand=scrollbar.set,
            )
        self.text.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        scrollbar.config(command=self.text.yview)

        # 标签样式(与 EULADialog 保持一致)
        self.text.tag_configure(
            "core_clause",
            font=("Microsoft YaHei UI", 10, "bold"),
            foreground="#c0392b",
        )
        self.text.tag_configure(
            "disclaimer",
            font=("Microsoft YaHei UI", 10, "bold"),
            foreground="#8e44ad",
        )
        self.text.tag_configure(
            "section_header",
            font=("Microsoft YaHei UI", 11, "bold"),
            foreground="#2c3e50",
        )

        # 优先加载 EULA.md(完整版,仅中文部分)
        eula_loaded = False
        try:
            base = get_program_base_path()
            eula_path = os.path.join(base, "EULA.md")
            if os.path.isfile(eula_path):
                with open(eula_path, "r", encoding="utf-8") as f:
                    full_text = f.read()
                # 过滤:仅显示中文部分(移除 English Version)
                full_text = _filter_eula_chinese_only(full_text)
                self._insert_styled_text(self.text, full_text)
                eula_loaded = True
        except Exception:
            pass

        if not eula_loaded:
            # 回退到内嵌精简版
            self._insert_styled_text(self.text, HMOL_EULA_TEXT)

        self.text.see("1.0")
        self.text.config(state=tk.DISABLED)
        self._bind_mousewheel(self.text)

        # ── 按钮区(只有"关闭"按钮) ──
        btn_frame = ttk.Frame(main_frame)
        btn_frame.pack(fill=tk.X, pady=(4, 0))

        # 反馈链接
        link_label = ttk.Label(
            btn_frame,
            text="如有疑问,请访问 GitHub Issues:",
            font=("Microsoft YaHei UI", 8),
            foreground=LIGHT["text_secondary"],
        )
        link_label.pack(side=tk.LEFT)
        link_url = ttk.Label(
            btn_frame,
            text="github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues",
            font=("Microsoft YaHei UI", 8, "underline"),
            foreground="#3498db",
            cursor="hand2",
        )
        link_url.pack(side=tk.LEFT, padx=4)
        link_url.bind(
            "<Button-1>",
            lambda e: self._open_url(
                "https://github.com/OrangeArtc0915/"
                "Hello-Mental-Omega-Launcher/issues"
            ),
        )

        # 关闭按钮
        ttk.Button(btn_frame, text="关闭", command=self.destroy).pack(
            side=tk.RIGHT
        )

        self._center_on_parent()
        self.update_idletasks()
        try:
            self.lift()
            self.focus_force()
        except Exception:
            pass

    def _insert_styled_text(self, text_widget, content: str) -> None:
        """插入带样式标签的文本(与 EULADialog 一致)。"""
        for line in content.splitlines():
            tag = None
            stripped = line.strip()
            # 核心条款相关(新 EULA §3 "二次修改禁令")
            if (
                "二次修改禁令" in line
                or "不允许二次修改" in line
                or "未经 HMOL" in line
                or "核心条款" in line
                or "严格禁止" in line
                or "违规处理" in line
            ):
                tag = "core_clause"
            # 重要声明 / 免责声明
            elif (
                "重要声明" in line
                or "重要免责声明" in line
                or "无任何关联" in line
                or "不存在任何关联" in line
                or "不附带任何明示或暗示的保证" in line
            ):
                tag = "disclaimer"
            # 装饰性前缀也用 core_clause 色
            elif stripped.startswith("⛔") or stripped.startswith("🚫") or stripped.startswith("【核心条款】"):
                tag = "core_clause"
            if tag:
                text_widget.insert(tk.END, line + "\n", tag)
            else:
                text_widget.insert(tk.END, line + "\n")

    def _bind_mousewheel(self, widget) -> None:
        """绑定鼠标滚轮(跨平台)。"""
        def _on_mousewheel(event):
            try:
                if event.delta:
                    widget.yview_scroll(int(-1 * (event.delta / 120)), "units")
                elif event.num == 4:
                    widget.yview_scroll(-1, "units")
                elif event.num == 5:
                    widget.yview_scroll(1, "units")
            except Exception:
                pass
        widget.bind("<MouseWheel>", _on_mousewheel)
        widget.bind("<Button-4>", _on_mousewheel)
        widget.bind("<Button-5>", _on_mousewheel)

    def _open_url(self, url: str) -> None:
        try:
            webbrowser.open(url)
        except Exception:
            pass

    def _center_on_parent(self):
        try:
            self.update_idletasks()
            pw, ph = self.parent.winfo_width(), self.parent.winfo_height()
            px, py = self.parent.winfo_rootx(), self.parent.winfo_rooty()
            w, h = self.winfo_width(), self.winfo_height()
            x = px + (pw - w) // 2
            y = py + (ph - h) // 2
            self.geometry(f"+{x}+{y}")
        except Exception:
            pass


# ════════════════════════════════════════════════════════════════════
# 3. AddInstanceDialog — 添加 / 编辑游戏实例
# ════════════════════════════════════════════════════════════════════

class AddInstanceDialog(tk.Toplevel):
    """添加或编辑游戏实例对话框。"""

    def __init__(self, parent, instance_manager, edit_instance=None):
        super().__init__(parent)
        self.parent = parent
        self.instance_manager = instance_manager
        self.edit_instance = edit_instance  # None=添加, GameInstance=编辑
        self.result = {"action": "cancel", "data": None}

        self.title("编辑实例" if edit_instance else "添加实例")
        self.resizable(False, False)
        self.transient(parent)
        self.grab_set()

        self.configure(bg=LIGHT["bg"])

        main_frame = ttk.Frame(self, padding=20)
        main_frame.pack(fill=tk.BOTH, expand=True)

        # ── 标题 ──
        title_text = "编辑游戏实例" if edit_instance else "添加新游戏实例"
        ttk.Label(main_frame, text=title_text,
                  font=("Microsoft YaHei UI", 13, "bold")).pack(pady=(0, 14))

        # ── 实例名称 ──
        name_frame = ttk.Frame(main_frame)
        name_frame.pack(fill=tk.X, pady=(0, 8))
        ttk.Label(name_frame, text="实例名称:", width=10,
                  font=("Microsoft YaHei UI", 9)).pack(side=tk.LEFT)
        self.name_var = tk.StringVar()
        self.name_entry = ttk.Entry(name_frame, textvariable=self.name_var, width=40)
        self.name_entry.pack(side=tk.LEFT, padx=(4, 0), fill=tk.X, expand=True)

        # ── 游戏路径 ──
        path_frame = ttk.Frame(main_frame)
        path_frame.pack(fill=tk.X, pady=(0, 4))
        ttk.Label(path_frame, text="游戏路径:", width=10,
                  font=("Microsoft YaHei UI", 9)).pack(side=tk.LEFT)
        self.path_var = tk.StringVar()
        self.path_entry = ttk.Entry(path_frame, textvariable=self.path_var, width=30)
        self.path_entry.pack(side=tk.LEFT, padx=(4, 4), fill=tk.X, expand=True)
        browse_btn = ttk.Button(path_frame, text="浏览", command=self._on_browse)
        browse_btn.pack(side=tk.LEFT)

        # ── 路径验证状态 ──
        self.path_status_frame = ttk.Frame(main_frame)
        self.path_status_frame.pack(fill=tk.X, pady=(0, 4))
        # 预留左侧占位与名称标签对齐
        placeholder = ttk.Frame(self.path_status_frame, width=10)
        placeholder.pack(side=tk.LEFT, padx=(52, 0))
        self.path_status_label = ttk.Label(self.path_status_frame, text="",
                                            font=("Microsoft YaHei UI", 9))
        self.path_status_label.pack(side=tk.LEFT, padx=(4, 0))

        # ── 测试路径按钮 ──
        test_frame = ttk.Frame(main_frame)
        test_frame.pack(fill=tk.X, pady=(0, 14))
        ttk.Button(test_frame, text="测试路径", command=self._on_test_path).pack(side=tk.LEFT)

        # ── 底部按钮 ──
        btn_frame = ttk.Frame(main_frame)
        btn_frame.pack(fill=tk.X)

        action_text = "保存" if edit_instance else "添加"
        self.action_btn = ttk.Button(btn_frame, text=action_text, command=self._on_confirm)
        self.action_btn.pack(side=tk.RIGHT, padx=(6, 0))
        ttk.Button(btn_frame, text="取消", command=self._on_cancel).pack(side=tk.RIGHT)

        # ── 编辑模式: 预填数据 ──
        if edit_instance:
            self.name_var.set(edit_instance.name)
            self.path_var.set(edit_instance.path)
            self.name_entry.config(state="disabled")  # 名称不可改
            # 自动验证已有路径
            self._update_path_status(edit_instance.path)

        self.protocol("WM_DELETE_WINDOW", self._on_cancel)

        # ── 实时路径检测 ──
        self._path_timer_id = None
        self.path_var.trace_add("write", self._on_path_changed)

        self._center_on_parent()

        # ── 聚焦名称输入框 ──
        if not edit_instance:
            self.name_entry.focus_set()
        else:
            self.path_entry.focus_set()

    def _center_on_parent(self):
        self.update_idletasks()
        pw, ph = self.parent.winfo_width(), self.parent.winfo_height()
        px, py = self.parent.winfo_rootx(), self.parent.winfo_rooty()
        w, h = self.winfo_width(), self.winfo_height()
        x = px + (pw - w) // 2
        y = py + (ph - h) // 2
        self.geometry(f"+{x}+{y}")

    def _on_path_changed(self, *args):
        """路径变化时延迟验证(防抖)。"""
        if self._path_timer_id:
            self.after_cancel(self._path_timer_id)
        self.path_status_label.config(text="", foreground=LIGHT["text_secondary"])
        self._path_timer_id = self.after(400, self._validate_path)

    def _validate_path(self):
        """延迟验证路径。"""
        self._path_timer_id = None
        path = self.path_var.get().strip()
        if not path:
            self._update_path_status_indicator(None)
            return
        if not os.path.isdir(path):
            self._update_path_status_indicator(False)
            return
        if self.parent and hasattr(self.parent, "is_mo_directory"):
            ok = self.parent.is_mo_directory(path)
            self._update_path_status_indicator(ok)
        else:
            self._update_path_status_indicator(None)

    def _on_browse(self):
        directory = filedialog.askdirectory(title="选择游戏目录", parent=self)
        if directory:
            self.path_var.set(directory)

    def _on_test_path(self):
        path = self.path_var.get().strip()
        if not path:
            self.path_status_label.config(text="✗ 路径不能为空", foreground="#e74c3c")
            return
        if not os.path.isdir(path):
            self.path_status_label.config(text="✗ 目录不存在", foreground="#e74c3c")
            return
        if self.parent and hasattr(self.parent, "is_mo_directory"):
            if self.parent.is_mo_directory(path):
                self.path_status_label.config(text="✓ 有效的心灵终结游戏目录",
                                               foreground="#27ae60")
            else:
                self.path_status_label.config(text="✗ 不是有效的心灵终结游戏目录",
                                               foreground="#e74c3c")
        else:
            self.path_status_label.config(text="✓ 目录存在", foreground="#27ae60")

    def _update_path_status(self, path: str):
        """更新路径状态(在编辑模式下使用)。"""
        if not path or not os.path.isdir(path):
            self._update_path_status_indicator(False)
            return
        if self.parent and hasattr(self.parent, "is_mo_directory"):
            ok = self.parent.is_mo_directory(path)
            self._update_path_status_indicator(ok)
        else:
            self._update_path_status_indicator(None)

    def _update_path_status_indicator(self, ok):
        """ok: True=有效, False=无效, None=未知"""
        if ok is True:
            self.path_status_label.config(text="✓ 有效的心灵终结游戏目录",
                                           foreground="#27ae60")
        elif ok is False:
            self.path_status_label.config(text="✗ 不是有效的心灵终结游戏目录",
                                           foreground="#e74c3c")
        else:
            self.path_status_label.config(text="", foreground=LIGHT["text_secondary"])

    def _on_confirm(self):
        """确认添加/编辑。"""
        name = self.name_var.get().strip()
        path = self.path_var.get().strip()

        if not name:
            messagebox.showwarning("输入错误", "请输入实例名称", parent=self)
            self.name_entry.focus_set()
            return
        if len(name) > 64:
            messagebox.showwarning("输入错误", "实例名称不能超过 64 个字符", parent=self)
            self.name_entry.focus_set()
            return
        if not path:
            messagebox.showwarning("输入错误", "请选择游戏路径", parent=self)
            return
        if not os.path.isdir(path):
            messagebox.showwarning("路径错误", "所选路径不存在,请重新选择", parent=self)
            return
        if self.parent and hasattr(self.parent, "is_mo_directory"):
            if not self.parent.is_mo_directory(path):
                messagebox.showwarning("路径错误",
                                        "所选路径不是有效的心灵终结游戏目录\n"
                                        "请确保目录中包含 gamemd.exe / MO2.exe 等文件",
                                        parent=self)
                return

        if self.edit_instance:
            # 编辑模式: 使用 instance_manager.update_instance
            ok, msg = self.instance_manager.update_instance(
                self.edit_instance.id, new_name=name, new_path=path)
            if ok:
                self.result = {"action": "save", "data": self.edit_instance}
            else:
                messagebox.showwarning("更新失败", msg, parent=self)
                return
        else:
            # 添加模式
            ok, msg = self.instance_manager.add_instance(name, path)
            if ok:
                self.result = {"action": "add", "data": {"name": name, "path": path}}
            else:
                messagebox.showwarning("添加失败", msg, parent=self)
                return

        self._cleanup_and_close()

    def _on_cancel(self):
        self.result = {"action": "cancel", "data": None}
        self._cleanup_and_close()

    def _cleanup_and_close(self):
        if self._path_timer_id:
            self.after_cancel(self._path_timer_id)
            self._path_timer_id = None
        try:
            self.grab_release()
        except Exception:
            pass
        self.destroy()



class ShoutDialog(tk.Toplevel):

    FIELD_LIMITS = {
        "mo_ver": 10,
        "room": 15,
        "pwd": 10,
    }

    # ── 侮辱性词汇屏蔽 ──
    _PROFANITY_KEYWORDS = [
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
        "fuck", "shit", "bitch", "damn",
        "cunt", "asshole", "bastard",
        "dick", "piss", "slut", "whore",
        "retard", "moron", "idiot",
        "dumbass", "jackass", "douche",
        "motherfucker", "bullshit",
    ]

    # 字符替换映射: 用于拆解变形词
    _HOMOGLYPH_MAP = str.maketrans({
        '0': 'o', '1': 'i', '2': 'z', '3': 'e',
        '4': 'a', '5': 's', '6': 'g', '7': 't',
        '8': 'b', '9': 'g',
        '@': 'a', '$': 's',
        '·': '', '•': '', '　': '',
    })

    @classmethod
    def _normalize_text(cls, text: str) -> str:
        """标准化文本: 去空格/特殊符/数字替换 → 小写 → 用于匹配"""
        t = re.sub(r'\s+', '', text)
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

    _OBFUSCATED_URL_RE = re.compile(
        r'https?\s*:\s*/\s*/'
        r'|www\s*\.'
        r'|[a-zA-Z0-9]+\s*[\[\(\{（【]\s*\.\s*[\]\)\}）】]\s*[a-zA-Z]+'
        r'|[a-zA-Z0-9]+\s+\.\s+[a-zA-Z]+',
        re.IGNORECASE,
    )

    def __init__(self, parent, app=None, default_msg: str = ""):
        super().__init__(parent)
        self.parent = parent
        self.app = app
        # HMOL 版本 — 与 Qt 版本一致,自动从 config 读取
        if app is not None and hasattr(app, "config"):
            self._hmol_ver = str(app.config.get("version", __version__))
        else:
            self._hmol_ver = __version__

        self.title("📢 联机喊话 — 发送到 QQ 频道")
        self.resizable(True, True)
        self.minsize(560, 500)
        self.transient(parent)
        self.grab_set()
        self.configure(bg=LIGHT["bg"])

        main = ttk.Frame(self, padding=(20, 18, 20, 18))
        main.pack(fill=tk.BOTH, expand=True)

        # ── 标题 ──
        ttk.Label(main, text="📢 联机喊话",
                  font=("Microsoft YaHei UI", 14, "bold")).pack(anchor=tk.W)
        ttk.Label(main,
                  text="填写联机信息,消息将发送到配置的 QQ 频道。",
                  font=("Microsoft YaHei UI", 9),
                  foreground=LIGHT["text_secondary"]).pack(anchor=tk.W, pady=(2, 12))

        # ── 表单字段(Qt 一致: 3 个字段 + HMOL 版本) ──
        self._inputs = {}
        fields = [
            ("mo_ver", "🎮 MO 版本", 10, "例如: 3.3.6原版、Apra2合作版"),
            ("room",  "🏠 房间名字", 15, "输入游戏房间名称"),
            ("pwd",   "🔑 密码",     10, "设置房间密码 (留空=无密码)"),
        ]
        for key, label_text, maxlen, hint in fields:
            row = ttk.Frame(main)
            row.pack(fill=tk.X, pady=(0, 8))
            lbl = ttk.Label(row, text=label_text, width=12,
                             font=("Microsoft YaHei UI", 10, "bold"),
                             foreground=LIGHT["text"])
            lbl.pack(side=tk.LEFT, padx=(0, 6), anchor=tk.W)
            var = tk.StringVar()
            entry = ttk.Entry(row, textvariable=var, font=("Microsoft YaHei UI", 10))
            entry.pack(side=tk.LEFT, fill=tk.X, expand=True, padx=(0, 4))
            cnt = ttk.Label(row, text=f"0/{maxlen}", width=6,
                             font=("Microsoft YaHei UI", 9),
                             foreground=LIGHT["text_secondary"],
                             anchor=tk.E)
            cnt.pack(side=tk.LEFT)
            var.trace_add("write", lambda *a, k=key: self._on_field_changed(k))
            self._inputs[key] = {
                "edit": entry, "var": var, "limit": maxlen,
                "count_label": cnt, "hint": hint,
            }

        # HMOL 版本(只读,自动)
        row_h = ttk.Frame(main)
        row_h.pack(fill=tk.X, pady=(0, 8))
        ttk.Label(row_h, text="📦 HMOL 版本", width=12,
                   font=("Microsoft YaHei UI", 10, "bold"),
                   foreground=LIGHT["text"]).pack(side=tk.LEFT, padx=(0, 6))
        ttk.Label(row_h, text=self._hmol_ver,
                   font=("Microsoft YaHei UI", 10, "bold"),
                   foreground="#27ae60",
                   background=LIGHT.get("surface_alt", "#f8f9fa"),
                   relief=tk.SOLID, borderwidth=1,
                   padding=(8, 4)).pack(side=tk.LEFT, padx=(0, 4))

        # ── 消息预览 ──
        preview_label = ttk.Label(main, text="📋 消息预览",
                                    font=("Microsoft YaHei UI", 10, "bold"))
        preview_label.pack(anchor=tk.W, pady=(6, 4))
        preview_frame = ttk.Frame(main, relief=tk.SOLID, borderwidth=1,
                                   padding=8)
        preview_frame.pack(fill=tk.X, pady=(0, 6))
        # 背景色模拟 #fafafa
        try:
            preview_frame.configure(style="Preview.TFrame")
        except Exception:
            pass
        self._preview = tk.Label(preview_frame, text="", justify=tk.LEFT,
                                   anchor=tk.NW, wraplength=500,
                                   font=("Microsoft YaHei UI", 9),
                                   foreground=LIGHT.get("text_secondary", "#555"),
                                   background=LIGHT.get("surface_alt", "#fafafa"))
        self._preview.pack(fill=tk.X, anchor=tk.NW)

        # ── 状态/错误标签 ──
        self._status_label = ttk.Label(main, text="",
                                         font=("Microsoft YaHei UI", 9),
                                         foreground="#e74c3c",
                                         wraplength=500, justify=tk.LEFT)
        self._status_label.pack(fill=tk.X, pady=(2, 0))

        # ── 按钮区 ──
        btn_row = ttk.Frame(main)
        btn_row.pack(fill=tk.X, pady=(10, 0))
        self._send_btn = ttk.Button(btn_row, text="📤 发送",
                                       command=self._do_send)
        self._send_btn.pack(side=tk.RIGHT, padx=(4, 0))
        ttk.Button(btn_row, text="取消", command=self._on_cancel).pack(side=tk.RIGHT)

        self.protocol("WM_DELETE_WINDOW", self._on_cancel)
        self._refresh_preview()
        self._center_on_parent()
        self._on_field_changed("mo_ver")  # 初始化字数

    def _center_on_parent(self):
        self.update_idletasks()
        try:
            pw, ph = self.parent.winfo_width(), self.parent.winfo_height()
            px, py = self.parent.winfo_rootx(), self.parent.winfo_rooty()
            w, h = self.winfo_width(), self.winfo_height()
            x = px + max(0, (pw - w) // 2)
            y = py + max(0, (ph - h) // 2)
            self.geometry(f"+{x}+{y}")
        except Exception:
            pass

    def _on_field_changed(self, key: str = None):
        """任一输入框变化时更新字数和预览。"""
        for k, info in self._inputs.items():
            length = len(info["var"].get())
            limit = info["limit"]
            info["count_label"].config(text=f"{length}/{limit}")
            if length > limit:
                info["count_label"].config(foreground="#e74c3c")
            else:
                info["count_label"].config(
                    foreground=LIGHT.get("text_secondary", "#888"))
        self._refresh_preview()

    def _check_content(self, text: str) -> str:
        """检测内容是否违规, 返回错误描述(空字符串=合法)。与 Qt 一致。"""
        if not text:
            return ""
        # URL 检测
        urls = self._URL_PATTERN.findall(text)
        if urls:
            first = urls[0].strip() if isinstance(urls[0], str) else str(urls[0])
            return f"⚠️ 消息中不能包含网址: {first}…"
        if self._OBFUSCATED_URL_RE.search(text):
            return "⚠️ 消息中不能包含网址, 请移除"
        # 侮辱性词汇检测
        normalized = self._normalize_text(text)
        for kw in self._PROFANITY_KEYWORDS:
            if kw in normalized:
                return "⚠️ 消息中包含不文明词汇, 请修改后重试"
        return ""

    def _build_message(self) -> str:
        """构建最终发送的消息文本(对齐 Qt 版本)。"""
        mo = self._inputs["mo_ver"]["var"].get().strip()
        room = self._inputs["room"]["var"].get().strip()
        pwd = self._inputs["pwd"]["var"].get().strip()
        lines = [f"【MO版本】{mo}", f"【房间名字】{room}"]
        if pwd:
            lines.append(f"【密码】{pwd}")
        lines.append(f"【HMOL版本】{self._hmol_ver}")
        return "\n".join(lines)

    def _refresh_preview(self):
        """根据当前输入生成消息预览并更新发送按钮状态。"""
        mo = self._inputs["mo_ver"]["var"].get().strip()
        room = self._inputs["room"]["var"].get().strip()
        pwd = self._inputs["pwd"]["var"].get().strip()
        lines = [
            f"【MO版本】{mo or '(未填)'}",
            f"【房间名字】{room or '(未填)'}",
        ]
        if pwd:
            lines.append(f"【密码】{pwd}")
        lines.append(f"【HMOL版本】{self._hmol_ver}")
        self._preview.config(text="\n".join(lines))
        # 至少 MO版本 + 房间名字 都非空才可发送
        can_send = bool(mo and room)
        full_text = mo + room + pwd
        error = self._check_content(full_text)
        if error:
            self._show_error(error)
            can_send = False
        else:
            self._status_label.config(text="", foreground="#27ae60")
        self._send_btn.config(state=(tk.NORMAL if can_send else tk.DISABLED))

    def _show_error(self, msg: str):
        self._status_label.config(text=msg, foreground="#e74c3c")

    def _do_send(self):
        """发送喊话到 QQ 频道/群(Qt 一致的发送流程)。"""
        msg = self._build_message()
        if not msg.strip():
            self._show_error("消息不能为空")
            return
        # 内容审核
        full_text = "".join(
            self._inputs[k]["var"].get().strip()
            for k in ("mo_ver", "room", "pwd"))
        error = self._check_content(full_text)
        if error:
            self._show_error(error)
            return
        if len(msg) > QQ_BOT_MSG_MAX_LENGTH:
            self._show_error(f"消息超过 {QQ_BOT_MSG_MAX_LENGTH} 字限制")
            return
        # 校验子字段字数
        for key, info in self._inputs.items():
            if len(info["var"].get()) > info["limit"]:
                self._show_error(f"「{info['hint']}」超过 {info['limit']} 字限制")
                return
        # 禁用按钮,显示发送中
        self._send_btn.config(state=tk.DISABLED, text="⏳ 发送中...")
        self._status_label.config(text="", foreground="#e74c3c")

        def _send_worker():
            try:
                token = _get_qq_bot_token()
                if not token:
                    self.after(0, lambda: self._on_send_result(
                        False, "QQ Bot Token 获取失败,请检查 AppID/AppSecret 配置"))
                    return
                headers = {
                    "Authorization": f"QQBot {token}",
                    "Content-Type": "application/json",
                }
                body = {"content": msg, "msg_type": 0}
                results = []
                # 1) 频道
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
                # 2) 群
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
                all_ok = bool(results) and all("ok" in r for r in results)
                self.after(0, lambda: self._on_send_result(all_ok, result_str))
            except Exception as e:
                self.after(0, lambda: self._on_send_result(False, f"发送异常: {e}"))

        threading.Thread(target=_send_worker, daemon=True).start()

    def _on_send_result(self, success: bool, message: str):
        if success:
            self._status_label.config(text=f"✅ {message}", foreground="#27ae60")
            # 2s 后自动关闭(与 Qt 一致)
            self.after(2000, self._cleanup_and_close)
        else:
            self._status_label.config(text=f"❌ {message}", foreground="#e74c3c")
            self._send_btn.config(state=tk.NORMAL, text="🔄 重试")

    def _on_cancel(self):
        self._cleanup_and_close()

    def _cleanup_and_close(self):
        try:
            self.grab_release()
        except Exception:
            pass
        try:
            self.destroy()
        except Exception:
            pass



class FeedbackDialog(tk.Toplevel):
    """用户反馈渠道选择对话框。"""

    QQ_GROUP_URL = "https://jq.qq.com/?_wv=1027&k=HMOL2024"
    QQ_GUILD_URL = "https://pd.qq.com/s/HMOL"
    GITHUB_URL = "https://github.com/HMOL/HMOL"

    def __init__(self, parent):
        super().__init__(parent)
        self.parent = parent

        self.title("用户反馈")
        self.resizable(False, False)
        self.transient(parent)
        self.grab_set()

        self.configure(bg=LIGHT["bg"])

        main_frame = ttk.Frame(self, padding=24)
        main_frame.pack(fill=tk.BOTH, expand=True)

        # ── 标题 ──
        ttk.Label(main_frame, text="💬 用户反馈",
                  font=("Microsoft YaHei UI", 14, "bold")).pack(pady=(0, 6))

        ttk.Label(main_frame, text="请选择一种反馈方式:",
                  font=("Microsoft YaHei UI", 10)).pack(pady=(0, 18))

        # ── 按钮列表 ──
        btn_width = 20
        channels = [
            ("🐧 QQ 群", self.QQ_GROUP_URL, "加入 QQ 群进行反馈和交流"),
            ("💬 QQ 频道", self.QQ_GUILD_URL, "访问 QQ 频道提交反馈"),
            ("🐙 GitHub", self.GITHUB_URL, "在 GitHub 上提交 Issue"),
        ]

        for label, url, desc in channels:
            row = ttk.Frame(main_frame)
            row.pack(fill=tk.X, pady=(0, 8))

            btn = tk.Button(row, text=label,
                            font=("Microsoft YaHei UI", 11),
                            bg=LIGHT["accent"], fg=LIGHT["text_inverse"],
                            relief=tk.FLAT, padx=16, pady=8,
                            width=btn_width, anchor=tk.W,
                            cursor="hand2",
                            command=lambda u=url: self._open_url(u))
            btn.pack(side=tk.LEFT)

            ttk.Label(row, text=desc,
                      font=("Microsoft YaHei UI", 9),
                      foreground=LIGHT["text_secondary"]).pack(side=tk.LEFT, padx=(12, 0))

        # ── 关闭按钮 ──
        ttk.Button(main_frame, text="关闭", command=self._on_close,
                   width=10).pack(pady=(12, 0))

        self.protocol("WM_DELETE_WINDOW", self._on_close)
        self._center_on_parent()

    def _center_on_parent(self):
        self.update_idletasks()
        pw, ph = self.parent.winfo_width(), self.parent.winfo_height()
        px, py = self.parent.winfo_rootx(), self.parent.winfo_rooty()
        w, h = self.winfo_width(), self.winfo_height()
        x = px + (pw - w) // 2
        y = py + (ph - h) // 2
        self.geometry(f"+{x}+{y}")

    def _open_url(self, url: str):
        """在默认浏览器中打开 URL。"""
        try:
            import webbrowser
            webbrowser.open(url)
        except Exception:
            try:
                os.startfile(url)
            except Exception:
                try:
                    # 安全: 使用列表形式参数(避免 shell=True 的命令注入)
                    # 注: 在 Windows 上 "cmd /c start" 仍然需要 shell 行为,
                    #      所以这里使用 CREATE_NO_WINDOW + 列表参数
                    subprocess.run(
                        ["cmd", "/c", "start", "", url],
                        shell=False,
                        creationflags=0x08000000,  # CREATE_NO_WINDOW
                    )
                except Exception:
                    messagebox.showwarning("打开失败",
                                            f"无法打开浏览器,请手动访问:\n{url}",
                                            parent=self)

    def _on_close(self):
        try:
            self.grab_release()
        except Exception:
            pass
        self.destroy()



class ExportFormatDialog(tk.Toplevel):
    """导出格式与压缩选项选择对话框。"""

    def __init__(self, parent):
        super().__init__(parent)
        self.parent = parent
        self.result = {"action": "cancel", "data": None}

        self.title("导出设置")
        self.resizable(False, False)
        self.transient(parent)
        self.grab_set()

        self.configure(bg=LIGHT["bg"])

        main_frame = ttk.Frame(self, padding=20)
        main_frame.pack(fill=tk.BOTH, expand=True)

        # ── 标题 ──
        ttk.Label(main_frame, text="📤 导出设置",
                  font=("Microsoft YaHei UI", 13, "bold")).pack(pady=(0, 14))

        # ── 格式选择 ──
        format_frame = ttk.LabelFrame(main_frame, text="导出格式", padding=10)
        format_frame.pack(fill=tk.X, pady=(0, 10))

        self.format_var = tk.StringVar(value="ZIP")

        zip_radio = ttk.Radiobutton(format_frame, text="ZIP (通用格式,兼容性好)",
                                     variable=self.format_var, value="ZIP")
        zip_radio.pack(anchor=tk.W, pady=(0, 4))

        sevenz_radio = ttk.Radiobutton(format_frame, text="7Z (更高压缩率,需安装 py7zr)",
                                        variable=self.format_var, value="7Z")
        sevenz_radio.pack(anchor=tk.W)

        if not SEVENZIP_AVAILABLE:
            sevenz_radio.config(state=tk.DISABLED)
            ttk.Label(format_frame,
                      text="  (7Z 不可用 — 请执行 pip install py7zr)",
                      font=("Microsoft YaHei UI", 8),
                      foreground=LIGHT["text_disabled"]).pack(anchor=tk.W)

        # ── 压缩级别 ──
        level_frame = ttk.LabelFrame(main_frame, text="压缩级别", padding=10)
        level_frame.pack(fill=tk.X, pady=(0, 10))

        self.level_var = tk.StringVar(value="标准")
        levels = [
            ("快速 — 压缩速度快,体积较大", "快速"),
            ("标准 — 平衡速度与体积", "标准"),
            ("最高压缩 — 体积最小,速度较慢", "最高压缩"),
        ]
        for text, value in levels:
            rb = ttk.Radiobutton(level_frame, text=text,
                                  variable=self.level_var, value=value)
            rb.pack(anchor=tk.W, pady=(0, 3))

        # ── 元数据选项 ──
        meta_frame = ttk.LabelFrame(main_frame, text="高级选项", padding=10)
        meta_frame.pack(fill=tk.X, pady=(0, 14))

        self.meta_var = tk.BooleanVar(value=True)
        meta_cb = ttk.Checkbutton(meta_frame,
                                   text="保留文件元数据 (修改时间、权限等)",
                                   variable=self.meta_var)
        meta_cb.pack(anchor=tk.W)

        # ── 底部按钮 ──
        btn_frame = ttk.Frame(main_frame)
        btn_frame.pack(fill=tk.X)

        ttk.Button(btn_frame, text="导出", command=self._on_export).pack(side=tk.RIGHT, padx=(6, 0))
        ttk.Button(btn_frame, text="取消", command=self._on_cancel).pack(side=tk.RIGHT)

        self.protocol("WM_DELETE_WINDOW", self._on_cancel)
        self._center_on_parent()

    def _center_on_parent(self):
        self.update_idletasks()
        pw, ph = self.parent.winfo_width(), self.parent.winfo_height()
        px, py = self.parent.winfo_rootx(), self.parent.winfo_rooty()
        w, h = self.winfo_width(), self.winfo_height()
        x = px + (pw - w) // 2
        y = py + (ph - h) // 2
        self.geometry(f"+{x}+{y}")

    def _on_export(self):
        fmt = self.format_var.get()
        if fmt == "7Z" and not SEVENZIP_AVAILABLE:
            messagebox.showwarning("格式不可用",
                                    "7Z 格式需要安装 py7zr 库。\n"
                                    "请执行: pip install py7zr\n\n"
                                    "已自动切换为 ZIP 格式。",
                                    parent=self)
            fmt = "ZIP"

        self.result = {
            "action": "export",
            "data": {
                "format": fmt,
                "compress_level": self.level_var.get(),
                "preserve_metadata": self.meta_var.get(),
            }
        }
        self._cleanup_and_close()

    def _on_cancel(self):
        self.result = {"action": "cancel", "data": None}
        self._cleanup_and_close()

    def _cleanup_and_close(self):
        try:
            self.grab_release()
        except Exception:
            pass
        self.destroy()



class PackageDirectoryBrowserDialog(tk.Toplevel):
    """浏览包目录内容 — 树形文件视图 + 文本预览。"""

    TEXT_EXTENSIONS = {".txt", ".md", ".json", ".xml", ".ini", ".cfg", ".yaml",
                       ".yml", ".toml", ".log", ".py", ".js", ".html", ".css",
                       ".cpp", ".h", ".cs", ".bat", ".cmd", ".ps1", ".csv"}

    def __init__(self, parent, directory_path: str):
        super().__init__(parent)
        self.parent = parent
        self.directory_path = directory_path

        self.title(f"浏览包目录 - {os.path.basename(directory_path) or directory_path}")
        self.geometry("780x520")
        self.minsize(600, 400)
        self.transient(parent)
        self.grab_set()

        self.configure(bg=LIGHT["bg"])

        # ── 主布局: 左侧树 + 右侧预览 ──
        paned = ttk.PanedWindow(self, orient=tk.HORIZONTAL)
        paned.pack(fill=tk.BOTH, expand=True, padx=10, pady=10)

        # ── 左侧: 文件树 ──
        left_frame = ttk.Frame(paned)
        paned.add(left_frame, weight=3)

        tree_header = ttk.Frame(left_frame)
        tree_header.pack(fill=tk.X, pady=(0, 4))
        ttk.Label(tree_header, text="文件列表",
                  font=("Microsoft YaHei UI", 10, "bold")).pack(side=tk.LEFT)
        ttk.Label(tree_header, text=os.path.basename(directory_path),
                  font=("Microsoft YaHei UI", 8),
                  foreground=LIGHT["text_secondary"]).pack(side=tk.RIGHT)

        tree_container = ttk.Frame(left_frame)
        tree_container.pack(fill=tk.BOTH, expand=True)

        tree_scroll = ttk.Scrollbar(tree_container, orient=tk.VERTICAL)
        self.tree = ttk.Treeview(tree_container,
                                  columns=("size",),
                                  displaycolumns=("size",),
                                  yscrollcommand=tree_scroll.set,
                                  selectmode="browse")
        tree_scroll.config(command=self.tree.yview)

        self.tree.heading("#0", text="名称", anchor=tk.W)
        self.tree.heading("size", text="大小", anchor=tk.E)
        self.tree.column("#0", width=300, minwidth=150)
        self.tree.column("size", width=80, minwidth=60, anchor=tk.E)

        self.tree.grid(row=0, column=0, sticky="nsew")
        tree_scroll.grid(row=0, column=1, sticky="ns")
        tree_container.columnconfigure(0, weight=1)
        tree_container.rowconfigure(0, weight=1)

        self.tree.bind("<<TreeviewSelect>>", self._on_tree_select)
        self.tree.bind("<Double-1>", self._on_tree_double_click)

        # ── 右侧: 预览 ──
        right_frame = ttk.Frame(paned)
        paned.add(right_frame, weight=2)

        preview_header = ttk.Frame(right_frame)
        preview_header.pack(fill=tk.X, pady=(0, 4))
        ttk.Label(preview_header, text="预览",
                  font=("Microsoft YaHei UI", 10, "bold")).pack(side=tk.LEFT)
        self.preview_filename = ttk.Label(preview_header, text="",
                                           font=("Microsoft YaHei UI", 8),
                                           foreground=LIGHT["text_secondary"])
        self.preview_filename.pack(side=tk.RIGHT)

        preview_container = ttk.Frame(right_frame)
        preview_container.pack(fill=tk.BOTH, expand=True)

        preview_scroll_y = ttk.Scrollbar(preview_container, orient=tk.VERTICAL)
        preview_scroll_x = ttk.Scrollbar(preview_container, orient=tk.HORIZONTAL)

        self.preview = tk.Text(preview_container,
                                font=("Consolas", 9),
                                wrap=tk.NONE,
                                state=tk.DISABLED,
                                bg=LIGHT["bg_alt"],
                                relief=tk.SUNKEN,
                                borderwidth=1,
                                yscrollcommand=preview_scroll_y.set,
                                xscrollcommand=preview_scroll_x.set)
        preview_scroll_y.config(command=self.preview.yview)
        preview_scroll_x.config(command=self.preview.xview)

        self.preview.grid(row=0, column=0, sticky="nsew")
        preview_scroll_y.grid(row=0, column=1, sticky="ns")
        preview_scroll_x.grid(row=1, column=0, sticky="ew")
        preview_container.columnconfigure(0, weight=1)
        preview_container.rowconfigure(0, weight=1)

        self.preview_placeholder = ttk.Label(right_frame,
                                              text="选择左侧文件以预览内容",
                                              font=("Microsoft YaHei UI", 9),
                                              foreground=LIGHT["text_disabled"])
        self.preview_placeholder.pack(pady=(8, 0))

        # ── 底部按钮栏 ──
        btn_frame = ttk.Frame(self)
        btn_frame.pack(fill=tk.X, padx=10, pady=(0, 10))

        self.open_btn = ttk.Button(btn_frame, text="打开", command=self._on_open)
        self.open_btn.pack(side=tk.LEFT, padx=(0, 6))

        self.rename_btn = ttk.Button(btn_frame, text="重命名", command=self._on_rename)
        self.rename_btn.pack(side=tk.LEFT, padx=(0, 6))

        self.delete_btn = ttk.Button(btn_frame, text="删除", command=self._on_delete)
        self.delete_btn.pack(side=tk.LEFT)

        ttk.Button(btn_frame, text="关闭", command=self._on_close).pack(side=tk.RIGHT)

        self.protocol("WM_DELETE_WINDOW", self._on_close)

        # ── 加载目录 ──
        self._populate_tree()

    def _populate_tree(self):
        """加载目录树。"""
        for item in self.tree.get_children():
            self.tree.delete(item)

        if not os.path.isdir(self.directory_path):
            self.tree.insert("", tk.END, text="(目录不存在)", values=("",))
            return

        def _add_node(parent_node, dir_path):
            try:
                entries = sorted(os.scandir(dir_path),
                                 key=lambda e: (not e.is_dir(), e.name.lower()))
                for entry in entries:
                    if entry.is_dir():
                        folder_node = self.tree.insert(
                            parent_node, tk.END,
                            text=entry.name,
                            values=("",),
                            open=False)
                        # 延迟加载子目录
                        self.tree.insert(folder_node, tk.END,
                                         text="(加载中...)", values=("",))
                    else:
                        try:
                            fsize = entry.stat().st_size
                        except OSError:
                            fsize = 0
                        self.tree.insert(
                            parent_node, tk.END,
                            text=entry.name,
                            values=(_format_file_size(fsize),))
            except PermissionError:
                self.tree.insert(parent_node, tk.END,
                                 text="(权限不足)", values=("",))
            except OSError:
                pass

        _add_node("", self.directory_path)

        # 绑定展开事件实现延迟加载
        self.tree.bind("<<TreeviewOpen>>", self._on_tree_open)

    def _on_tree_open(self, event):
        """展开节点时懒加载子目录。"""
        node = self.tree.focus()
        children = self.tree.get_children(node)
        if len(children) == 1:
            first_child = self.tree.item(children[0])
            if first_child.get("text") == "(加载中...)":
                # 删除占位并加载真实内容
                self.tree.delete(children[0])
                # 构建实际路径
                actual_path = self._get_node_path(node)
                if actual_path:
                    self._add_sub_nodes(node, actual_path)

    def _get_node_path(self, node) -> str:
        """从树节点追溯到完整路径。"""
        parts = []
        current = node
        while current:
            text = self.tree.item(current, "text")
            parts.insert(0, text)
            current = self.tree.parent(current)
        return os.path.join(self.directory_path, *parts[1:]) if len(parts) > 1 else self.directory_path

    def _add_sub_nodes(self, parent_node, dir_path):
        """为指定节点添加子节点。"""
        try:
            entries = sorted(os.scandir(dir_path),
                             key=lambda e: (not e.is_dir(), e.name.lower()))
            for entry in entries:
                if entry.is_dir():
                    folder_node = self.tree.insert(
                        parent_node, tk.END,
                        text=entry.name,
                        values=("",),
                        open=False)
                    self.tree.insert(folder_node, tk.END,
                                     text="(加载中...)", values=("",))
                else:
                    try:
                        fsize = entry.stat().st_size
                    except OSError:
                        fsize = 0
                    self.tree.insert(
                        parent_node, tk.END,
                        text=entry.name,
                        values=(_format_file_size(fsize),))
        except PermissionError:
            self.tree.insert(parent_node, tk.END,
                             text="(权限不足)", values=("",))
        except OSError:
            pass

    def _on_tree_select(self, event):
        """选中条目时尝试预览。"""
        selection = self.tree.selection()
        if not selection:
            return
        node = selection[0]
        item_text = self.tree.item(node, "text")
        if item_text.startswith("(") and item_text.endswith(")"):
            self._clear_preview()
            return

        full_path = self._get_node_path(node)
        if not full_path or not os.path.isfile(full_path):
            self._clear_preview()
            return

        ext = os.path.splitext(full_path)[1].lower()
        self.preview_filename.config(text=os.path.basename(full_path))
        self.preview_placeholder.pack_forget()

        if ext in self.TEXT_EXTENSIONS:
            try:
                with open(full_path, "r", encoding="utf-8", errors="replace") as f:
                    content = f.read(50000)
                self.preview.config(state=tk.NORMAL)
                self.preview.delete("1.0", tk.END)
                self.preview.insert("1.0", content)
                self.preview.config(state=tk.DISABLED)
            except Exception as e:
                self.preview.config(state=tk.NORMAL)
                self.preview.delete("1.0", tk.END)
                self.preview.insert("1.0", f"(无法读取文件: {e})")
                self.preview.config(state=tk.DISABLED)
        else:
            self.preview.config(state=tk.NORMAL)
            self.preview.delete("1.0", tk.END)
            self.preview.insert("1.0", f"(二进制文件 — 不支持预览)\n\n"
                                        f"类型: {ext or '未知'}\n"
                                        f"大小: {_format_file_size(os.path.getsize(full_path))}")
            self.preview.config(state=tk.DISABLED)

    def _clear_preview(self):
        self.preview.config(state=tk.NORMAL)
        self.preview.delete("1.0", tk.END)
        self.preview.config(state=tk.DISABLED)
        self.preview_filename.config(text="")
        self.preview_placeholder.pack(pady=(8, 0))

    def _on_tree_double_click(self, event):
        """双击打开文件/展开文件夹。"""
        self._on_open()

    def _on_open(self):
        """打开选中文件。"""
        selection = self.tree.selection()
        if not selection:
            messagebox.showinfo("提示", "请先选择一个文件", parent=self)
            return
        node = selection[0]
        full_path = self._get_node_path(node)
        if not full_path:
            return
        if os.path.isdir(full_path):
            # 切换展开状态
            if self.tree.item(node, "open"):
                self.tree.item(node, open=False)
            else:
                self.tree.item(node, open=True)
            return
        if not os.path.isfile(full_path):
            return
        try:
            os.startfile(full_path)
        except Exception as e:
            messagebox.showerror("打开失败", f"无法打开文件:\n{e}", parent=self)

    def _on_rename(self):
        """重命名选中文件/文件夹。"""
        selection = self.tree.selection()
        if not selection:
            messagebox.showinfo("提示", "请先选择要重命名的文件或文件夹", parent=self)
            return
        node = selection[0]
        old_name = self.tree.item(node, "text")
        full_path = self._get_node_path(node)
        if not full_path or not os.path.exists(full_path):
            messagebox.showwarning("错误", "所选路径不存在", parent=self)
            return

        new_name = simpledialog.askstring("重命名", f"请输入新名称:", parent=self,
                                           initialvalue=old_name)
        if not new_name or new_name == old_name:
            return
        if re.search(r'[<>:"/\\|?*]', new_name):
            messagebox.showwarning("无效名称", "文件名包含非法字符", parent=self)
            return

        new_path = os.path.join(os.path.dirname(full_path), new_name)
        try:
            os.rename(full_path, new_path)
            self.tree.item(node, text=new_name)
            if os.path.isdir(new_path):
                # 刷新子节点
                for child in self.tree.get_children(node):
                    self.tree.delete(child)
                self.tree.insert(node, tk.END, text="(加载中...)", values=("",))
            log_info("FileBrowser", f"重命名: {old_name} -> {new_name}")
        except Exception as e:
            messagebox.showerror("重命名失败", str(e), parent=self)

    def _on_delete(self):
        """删除选中文件/文件夹。"""
        selection = self.tree.selection()
        if not selection:
            messagebox.showinfo("提示", "请先选择要删除的文件或文件夹", parent=self)
            return
        node = selection[0]
        item_name = self.tree.item(node, "text")
        full_path = self._get_node_path(node)
        if not full_path or not os.path.exists(full_path):
            messagebox.showwarning("错误", "所选路径不存在", parent=self)
            return

        confirm_msg = f"确认删除 '{item_name}' ?\n\n此操作不可撤销!"
        if os.path.isdir(full_path):
            confirm_msg += "\n(将递归删除整个目录)"
        if not messagebox.askyesno("确认删除", confirm_msg, parent=self):
            return

        try:
            if os.path.isdir(full_path):
                shutil.rmtree(full_path)
            else:
                os.remove(full_path)
            self.tree.delete(node)
            self._clear_preview()
            log_info("FileBrowser", f"已删除: {full_path}")
        except Exception as e:
            messagebox.showerror("删除失败", str(e), parent=self)

    def _on_close(self):
        try:
            self.grab_release()
        except Exception:
            pass
        self.destroy()


def _copy_files(src_dir, dst_dir, progress_cb=None, conflict_policy="overwrite_all"):
    if not os.path.exists(src_dir):
        return (False, 0, 0)
    files = []
    def _s(p):
        try:
            for e in os.scandir(p):
                if e.is_file(follow_symlinks=False):
                    files.append(e.path)
                elif e.is_dir(follow_symlinks=False):
                    _s(e.path)
        except OSError:
            pass
    _s(src_dir)
    total = len(files)
    if total == 0:
        return (True, 0, 0)
    ok, fail, lp = 0, 0, [-1]
    os.makedirs(dst_dir, exist_ok=True)
    for fp in files:
        rel = os.path.relpath(fp, src_dir)
        dst = os.path.normpath(os.path.join(dst_dir, rel))
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        if os.path.exists(dst) and conflict_policy == "skip_existing":
            ok += 1
            if progress_cb:
                progress_cb(ok, total, fp)
            continue
        try:
            shutil.copy2(fp, dst)
        except (OSError, PermissionError):
            fail += 1
        ok += 1
        if progress_cb:
            try:
                p = int(ok/total*100) if total > 0 else 0
                if p != lp[0]:
                    lp[0] = p
                    progress_cb(ok, total, fp)
            except Exception:
                pass
    return (fail < total, total, fail)


def scan_install_conflicts(src, target):
    conflicts, total = [], 0
    if not os.path.isdir(src) or not os.path.isdir(target):
        return conflicts, 0
    def _s(p):
        nonlocal total
        try:
            for e in os.scandir(p):
                rel = os.path.relpath(e.path, src)
                df = os.path.join(target, rel)
                if e.is_file(follow_symlinks=False):
                    if os.path.exists(df):
                        total += 1
                        if len(conflicts) < 50:
                            conflicts.append(rel)
                elif e.is_dir(follow_symlinks=False):
                    _s(e.path)
        except OSError:
            pass
    _s(src)
    return conflicts, total



# 固定文件名(不区分大小写,匹配时使用 .lower())
PREVIEW_LICENSE_NAME = "搬运许可.jpg"
PREVIEW_README_NAME = "说明.txt"
# 图片格式后缀白名单
PREVIEW_IMAGE_EXTS = {".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp"}
# 允许的许可证图片主名(用户可能改后缀)
PREVIEW_LICENSE_BASENAMES = {"搬运许可"}


def find_package_preview_files(root_dir: str) -> dict:
    """递归扫描 root_dir,查找 "搬运许可.jpg" 和 "说明.txt"。

    返回 dict:
      {
        "license": 完整路径 或 None,
        "readme": 完整路径 或 None,
        "license_name": 实际文件名 或 None,
        "readme_name": 实际文件名 或 None,
      }
    """
    result = {
        "license": None, "readme": None,
        "license_name": None, "readme_name": None,
    }
    if not root_dir or not os.path.isdir(root_dir):
        return result
    # 优先匹配固定文件名
    fixed_license = PREVIEW_LICENSE_NAME.lower()
    fixed_readme = PREVIEW_README_NAME.lower()
    try:
        for dirpath, dirnames, filenames in os.walk(root_dir):
            for fn in filenames:
                fn_lower = fn.lower()
                base, ext = os.path.splitext(fn)
                # 说明.txt
                if fn_lower == fixed_readme and result["readme"] is None:
                    result["readme"] = os.path.join(dirpath, fn)
                    result["readme_name"] = fn
                    continue
                # 搬运许可.<图片格式>
                if (result["license"] is None
                    and base.lower() in PREVIEW_LICENSE_BASENAMES
                    and ext.lower() in PREVIEW_IMAGE_EXTS):
                    result["license"] = os.path.join(dirpath, fn)
                    result["license_name"] = fn
            if result["license"] and result["readme"]:
                break
    except Exception as e:
        log_warn("Preview", f"扫描预览文件异常: {e}")
    return result


class PackagePreviewDialog(tk.Toplevel):
    """包预览对话框 — 显示 "搬运许可.jpg" 和 "说明.txt"。

    控件:
      - 上方:可缩放的图片预览 (搬运许可.jpg)
      - 下方:文本展示 (说明.txt),带复制按钮
      - 底部:关闭 / 继续安装 按钮
    """

    MAX_IMAGE_WIDTH = 720
    MAX_IMAGE_HEIGHT = 460
    MIN_ZOOM = 0.25
    MAX_ZOOM = 4.0

    def __init__(self, parent, license_path: str | None, readme_path: str | None,
                  package_name: str = ""):
        super().__init__(parent)
        self.parent = parent
        self.license_path = license_path
        self.readme_path = readme_path
        self.package_name = package_name
        self._photo = None  # 保持引用防止被 GC
        self._pil_image = None  # 原始 PIL 图像
        self._zoom = 1.0
        self._readme_text = ""
        self._load_error = ""

        title = f"📦 包预览 — {package_name}" if package_name else "📦 包预览"
        self.title(title)
        self.resizable(True, True)
        self.minsize(640, 480)
        self.transient(parent)
        self.grab_set()
        self.configure(bg=LIGHT["bg"])

        self._build_ui()
        self._load_readme()
        self._load_image()
        self._update_idletasks_centered()

    # ────────────── 布局 ──────────────
    def _build_ui(self):
        main = ttk.Frame(self, padding=(12, 10, 12, 10))
        main.pack(fill=tk.BOTH, expand=True)

        # ── 标题 ──
        title_row = ttk.Frame(main)
        title_row.pack(fill=tk.X, pady=(0, 6))
        ttk.Label(title_row, text=f"📦 包预览 — {self.package_name}",
                   font=("Microsoft YaHei UI", 12, "bold")).pack(side=tk.LEFT)

        # 检测结果
        status_text = []
        if self.license_path:
            status_text.append("✅ 搬运许可")
        else:
            status_text.append("⚠ 无搬运许可")
        if self.readme_path:
            status_text.append("✅ 说明.txt")
        else:
            status_text.append("⚠ 无说明.txt")
        ttk.Label(title_row, text=" | ".join(status_text),
                   font=("Microsoft YaHei UI", 9),
                   foreground=LIGHT.get("text_secondary", "#666")).pack(side=tk.RIGHT)

        # ── Notebook 切换图片 / 文本 ──
        self._notebook = ttk.Notebook(main)
        self._notebook.pack(fill=tk.BOTH, expand=True, pady=(4, 8))

        # === Tab 1: 搬运许可 ===
        license_tab = ttk.Frame(self._notebook)
        self._notebook.add(license_tab, text="🖼️ 搬运许可")

        # 缩放工具栏
        zoom_bar = ttk.Frame(license_tab)
        zoom_bar.pack(fill=tk.X, pady=(6, 4))
        ttk.Button(zoom_bar, text="🔍+ 放大", width=10,
                    command=lambda: self._zoom_image(1.25)).pack(side=tk.LEFT, padx=2)
        ttk.Button(zoom_bar, text="🔍- 缩小", width=10,
                    command=lambda: self._zoom_image(0.8)).pack(side=tk.LEFT, padx=2)
        ttk.Button(zoom_bar, text="↺ 100%", width=8,
                    command=lambda: self._zoom_image(0, reset=True)).pack(side=tk.LEFT, padx=2)
        ttk.Button(zoom_bar, text="📋 复制图片",
                    command=self._copy_image_to_clipboard).pack(side=tk.LEFT, padx=2)
        ttk.Button(zoom_bar, text="📁 打开文件位置",
                    command=self._open_image_location).pack(side=tk.LEFT, padx=2)
        self._zoom_label = ttk.Label(zoom_bar, text="100%",
                                       font=("Microsoft YaHei UI", 9),
                                       foreground=LIGHT.get("text_secondary", "#666"))
        self._zoom_label.pack(side=tk.RIGHT, padx=4)

        # 图片显示区
        img_frame = ttk.Frame(license_tab)
        img_frame.pack(fill=tk.BOTH, expand=True, pady=(0, 4))
        img_scroll_y = ttk.Scrollbar(img_frame, orient=tk.VERTICAL)
        img_scroll_y.pack(side=tk.RIGHT, fill=tk.Y)
        img_scroll_x = ttk.Scrollbar(img_frame, orient=tk.HORIZONTAL)
        img_scroll_x.pack(side=tk.BOTTOM, fill=tk.X)
        self._license_canvas = tk.Canvas(
            img_frame, bg="#2c2c2c", highlightthickness=0,
            xscrollcommand=img_scroll_x.set,
            yscrollcommand=img_scroll_y.set)
        self._license_canvas.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        img_scroll_x.config(command=self._license_canvas.xview)
        img_scroll_y.config(command=self._license_canvas.yview)

        # === Tab 2: 说明.txt ===
        readme_tab = ttk.Frame(self._notebook)
        self._notebook.add(readme_tab, text="📄 说明.txt")

        # 工具栏
        readme_bar = ttk.Frame(readme_tab)
        readme_bar.pack(fill=tk.X, pady=(6, 4))
        ttk.Button(readme_bar, text="📋 复制全文",
                    command=self._copy_readme_to_clipboard).pack(side=tk.LEFT, padx=2)
        ttk.Button(readme_bar, text="📁 打开文件位置",
                    command=self._open_readme_location).pack(side=tk.LEFT, padx=2)
        self._readme_status = ttk.Label(readme_bar, text="",
                                         font=("Microsoft YaHei UI", 9),
                                         foreground=LIGHT.get("text_secondary", "#666"))
        self._readme_status.pack(side=tk.RIGHT, padx=4)

        # 文本显示区
        text_frame = ttk.Frame(readme_tab)
        text_frame.pack(fill=tk.BOTH, expand=True, pady=(0, 4))
        t_scroll = ttk.Scrollbar(text_frame, orient=tk.VERTICAL)
        t_scroll.pack(side=tk.RIGHT, fill=tk.Y)
        self._readme_text_widget = tk.Text(
            text_frame, wrap=tk.WORD, height=18, width=80,
            font=("Microsoft YaHei UI", 10),
            yscrollcommand=t_scroll.set, state=tk.DISABLED,
            bg=LIGHT.get("surface_alt", "#f8f8f8"),
            relief=tk.FLAT, borderwidth=1, padx=8, pady=8)
        self._readme_text_widget.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        t_scroll.config(command=self._readme_text_widget.yview)

        # ── 底部按钮 ──
        btn_row = ttk.Frame(main)
        btn_row.pack(fill=tk.X, pady=(8, 0))
        ttk.Label(btn_row,
                  text="💡 提示:此预览仅展示包内文档与许可,不会影响主流程操作。",
                  font=("Microsoft YaHei UI", 8),
                  foreground=LIGHT.get("text_secondary", "#888")).pack(side=tk.LEFT)
        ttk.Button(btn_row, text="✅ 继续",
                    command=self._on_close, width=12).pack(side=tk.RIGHT, padx=4)

        self.protocol("WM_DELETE_WINDOW", self._on_close)

    # ────────────── 数据加载 ──────────────
    def _load_readme(self):
        if not self.readme_path:
            self._readme_text = ""
            self._readme_status.config(text="❌ 包内未包含说明.txt")
            return
        if not os.path.isfile(self.readme_path):
            self._readme_text = ""
            self._readme_status.config(text=f"❌ 文件不存在: {self.readme_path}")
            return
        # 尝试多种编码
        for enc in ("utf-8", "utf-8-sig", "gbk", "gb18030", "latin-1"):
            try:
                with open(self.readme_path, "r", encoding=enc) as f:
                    self._readme_text = f.read()
                self._readme_status.config(
                    text=f"✅ 已加载 ({enc}) - {len(self._readme_text)} 字符 / {os.path.basename(self.readme_path)}")
                self._readme_text_widget.config(state=tk.NORMAL)
                self._readme_text_widget.delete("1.0", tk.END)
                if self._readme_text.strip():
                    self._readme_text_widget.insert("1.0", self._readme_text)
                else:
                    self._readme_text_widget.insert("1.0", "(说明.txt 为空)")
                self._readme_text_widget.config(state=tk.DISABLED)
                return
            except (UnicodeDecodeError, OSError):
                continue
        # 全部失败
        self._readme_text = ""
        self._readme_text_widget.config(state=tk.NORMAL)
        self._readme_text_widget.delete("1.0", tk.END)
        self._readme_text_widget.insert("1.0", f"❌ 无法读取文件 (编码格式不支持):\n{self.readme_path}")
        self._readme_text_widget.config(state=tk.DISABLED)
        self._readme_status.config(text="❌ 编码不支持")

    def _load_image(self):
        if not self.license_path:
            self._render_image_placeholder("📭", "包内未包含「搬运许可」图片")
            return
        if not os.path.isfile(self.license_path):
            self._render_image_placeholder("❌", f"文件不存在:\n{self.license_path}")
            return
        if not PIL_AVAILABLE:
            # 尝试使用 tk.PhotoImage 加载 (只支持 GIF/PNG/PPM)
            try:
                self._photo = tk.PhotoImage(file=self.license_path)
                self._render_photoimage()
                return
            except Exception as e:
                self._render_image_placeholder(
                    "❌", f"图片格式不支持 (需安装 Pillow 库):\n{os.path.basename(self.license_path)}\n{e}")
                return
        try:
            from PIL import Image as PILImage
            from PIL import ImageOps
            self._pil_image = PILImage.open(self.license_path)
            # 自动旋转 (EXIF)
            try:
                self._pil_image = ImageOps.exif_transpose(self._pil_image)
            except Exception:
                pass
            self._render_pil_image()
        except Exception as e:
            self._render_image_placeholder("❌", f"图片加载失败:\n{os.path.basename(self.license_path)}\n\n{e}")

    def _render_image_placeholder(self, icon: str, message: str):
        """在画布上绘制占位符。"""
        self._license_canvas.delete("all")
        self._license_canvas.update_idletasks()
        w = max(self._license_canvas.winfo_width(), 400)
        h = max(self._license_canvas.winfo_height(), 300)
        self._license_canvas.create_text(
            w // 2, h // 2 - 20, text=icon, font=("Segoe UI Emoji", 48), fill="#888")
        self._license_canvas.create_text(
            w // 2, h // 2 + 40, text=message, font=("Microsoft YaHei UI", 10),
            fill="#bbb", width=min(w - 40, 600), justify=tk.CENTER)
        self._zoom_label.config(text="—")
        self._zoom = 1.0

    def _render_pil_image(self, zoom: float = 1.0):
        """渲染 PIL 图像到 canvas。"""
        if self._pil_image is None:
            return
        self._zoom = zoom
        # 计算目标尺寸
        orig_w, orig_h = self._pil_image.size
        # 基础缩放:适应画布
        canvas_w = max(self._license_canvas.winfo_width(), 200)
        canvas_h = max(self._license_canvas.winfo_height(), 200)
        base_scale = min(canvas_w / orig_w, canvas_h / orig_h, 1.0)
        scale = base_scale * zoom
        new_w = max(1, int(orig_w * scale))
        new_h = max(1, int(orig_h * scale))
        try:
            from PIL import Image as PILImage
            from PIL import ImageTk
            img = self._pil_image
            if scale != 1.0:
                # 高质量缩放 (兼容 Pillow 9.1 之前/之后)
                resample = None
                try:
                    resample = PILImage.Resampling.LANCZOS
                except AttributeError:
                    pass
                if resample is None and hasattr(PILImage, "LANCZOS"):
                    resample = PILImage.LANCZOS
                elif resample is None:
                    resample = PILImage.ANTIALIAS
                img = self._pil_image.resize((new_w, new_h), resample)
            self._photo = ImageTk.PhotoImage(img)
            self._license_canvas.delete("all")
            self._license_canvas.create_image(
                canvas_w // 2, canvas_h // 2, image=self._photo, anchor=tk.CENTER)
            self._license_canvas.config(scrollregion=(0, 0, max(canvas_w, new_w), max(canvas_h, new_h)))
        except Exception as e:
            self._render_image_placeholder("❌", f"渲染失败:\n{e}")
            return
        self._zoom_label.config(text=f"{int(self._zoom * 100)}%")

    def _render_photoimage(self):
        """使用 tk.PhotoImage 渲染 (PIL 不可用时)。"""
        if self._photo is None:
            return
        self._license_canvas.delete("all")
        canvas_w = max(self._license_canvas.winfo_width(), 200)
        canvas_h = max(self._license_canvas.winfo_height(), 200)
        img_w = self._photo.width()
        img_h = self._photo.height()
        self._license_canvas.create_image(
            canvas_w // 2, canvas_h // 2, image=self._photo, anchor=tk.CENTER)
        self._license_canvas.config(scrollregion=(0, 0, max(canvas_w, img_w), max(canvas_h, img_h)))
        self._zoom_label.config(text="原始")
        self._zoom = 1.0

    # ────────────── 交互 ──────────────
    def _zoom_image(self, factor: float, reset: bool = False):
        if self._pil_image is None and not self._photo:
            return
        if reset:
            self._zoom = 1.0
        else:
            self._zoom = max(self.MIN_ZOOM, min(self.MAX_ZOOM, self._zoom * factor))
        if self._pil_image is not None:
            self._render_pil_image(self._zoom)
        else:
            self._render_photoimage()

    def _copy_image_to_clipboard(self):
        if not self.license_path or not os.path.isfile(self.license_path):
            messagebox.showwarning("提示", "图片不存在,无法复制", parent=self)
            return
        try:
            # 复制到剪贴板 (Windows 专用)
            if PIL_AVAILABLE and os.name == "nt":
                try:
                    from PIL import Image
                    img = Image.open(self.license_path)
                    # 写入剪贴板 (使用 bmp 格式)
                    from io import BytesIO
                    buf = BytesIO()
                    img.convert("RGB").save(buf, "BMP")
                    buf.seek(0)
                    import ctypes
                    CF_DIB = 8
                    data = buf.read()
                    # 移除 BMP 文件头 (14 字节),只保留 DIB
                    dib_data = data[14:]
                    user32 = ctypes.windll.user32
                    kernel32 = ctypes.windll.kernel32
                    if not user32.OpenClipboard(0):
                        raise RuntimeError("OpenClipboard 失败")
                    try:
                        user32.EmptyClipboard()
                        h = kernel32.GlobalAlloc(0x0042, len(dib_data))
                        if not h:
                            raise RuntimeError("GlobalAlloc 失败")
                        ptr = kernel32.GlobalLock(h)
                        ctypes.memmove(ptr, dib_data, len(dib_data))
                        kernel32.GlobalUnlock(h)
                        user32.SetClipboardData(CF_DIB, h)
                    finally:
                        user32.CloseClipboard()
                    self._set_status_ok("✅ 图片已复制到剪贴板")
                    return
                except Exception as e:
                    log_warn("Preview", f"复制图片到剪贴板失败: {e}")
            # 回退:复制文件路径
            self.clipboard_clear()
            self.clipboard_append(self.license_path)
            self._set_status_ok(f"✅ 已复制文件路径: {self.license_path}")
        except Exception as e:
            messagebox.showerror("复制失败", str(e), parent=self)

    def _open_image_location(self):
        if not self.license_path or not os.path.isfile(self.license_path):
            messagebox.showwarning("提示", "图片不存在", parent=self)
            return
        try:
            if os.name == "nt":
                subprocess.Popen(["explorer", "/select,", self.license_path])
            else:
                subprocess.Popen(["xdg-open", os.path.dirname(self.license_path)])
        except Exception as e:
            messagebox.showerror("打开失败", str(e), parent=self)

    def _open_readme_location(self):
        if not self.readme_path or not os.path.isfile(self.readme_path):
            messagebox.showwarning("提示", "文件不存在", parent=self)
            return
        try:
            if os.name == "nt":
                subprocess.Popen(["explorer", "/select,", self.readme_path])
            else:
                subprocess.Popen(["xdg-open", os.path.dirname(self.readme_path)])
        except Exception as e:
            messagebox.showerror("打开失败", str(e), parent=self)

    def _copy_readme_to_clipboard(self):
        if not self._readme_text:
            messagebox.showwarning("提示", "说明.txt 为空或未加载", parent=self)
            return
        try:
            self.clipboard_clear()
            self.clipboard_append(self._readme_text)
            self._readme_status.config(text=f"✅ 已复制全文 ({len(self._readme_text)} 字符)")
        except Exception as e:
            messagebox.showerror("复制失败", str(e), parent=self)

    def _set_status_ok(self, msg: str):
        if msg:
            self._readme_status.config(text=msg)

    def _update_idletasks_centered(self):
        self.update_idletasks()
        try:
            pw, ph = self.parent.winfo_width(), self.parent.winfo_height()
            px, py = self.parent.winfo_rootx(), self.parent.winfo_rooty()
            w, h = self.winfo_width(), self.winfo_height()
            x = px + (pw - w) // 2
            y = py + (ph - h) // 2
            self.geometry(f"+{max(0, x)}+{max(0, y)}")
        except Exception:
            pass
        # 居中后重新渲染图片 (画布尺寸已确定)
        self.after(50, self._load_image)

    def _on_close(self):
        try:
            self.grab_release()
        except Exception:
            pass
        self.destroy()


class ProgressDialog(tk.Toplevel):

    def __init__(self, parent, title="处理中", allow_cancel=True,
                 show_speed=False, show_eta=False):
        super().__init__(parent)
        self.parent = parent
        self._cancelled = False
        self._completed = False
        self._success = False
        self._result_msg = ""

        self.title(f"⏳ {title}")
        self.resizable(False, False)
        self.transient(parent)
        self.grab_set()
        self.configure(bg=LIGHT["bg"])

        main = ttk.Frame(self, padding=24)
        main.pack(fill=tk.BOTH, expand=True)

        # 标题
        ttk.Label(main, text=title,
                   font=("Microsoft YaHei UI", 12, "bold")).pack(pady=(0, 12))

        # 进度条
        self._progress_var = tk.DoubleVar(value=0.0)
        self._progress_bar = ttk.Progressbar(
            main, length=420, mode="determinate",
            maximum=100, variable=self._progress_var)
        self._progress_bar.pack(fill=tk.X, pady=(0, 6))

        # 百分比 + 状态行
        info_row = ttk.Frame(main)
        info_row.pack(fill=tk.X, pady=(0, 4))
        self._pct_label = ttk.Label(info_row, text="0%",
                                      font=("Microsoft YaHei UI", 10, "bold"),
                                      foreground=LIGHT.get("accent", "#0078d4"),
                                      width=8, anchor=tk.W)
        self._pct_label.pack(side=tk.LEFT)
        self._status_label = ttk.Label(info_row, text="准备中...",
                                         font=("Microsoft YaHei UI", 9),
                                         foreground=LIGHT.get("text_secondary", "#666"))
        self._status_label.pack(side=tk.LEFT, padx=(8, 0), fill=tk.X, expand=True)

        # 速度/ETA 行 (可选)
        if show_speed or show_eta:
            meta_row = ttk.Frame(main)
            meta_row.pack(fill=tk.X, pady=(0, 4))
            self._speed_label = ttk.Label(meta_row, text="",
                                           font=("Microsoft YaHei UI", 8),
                                           foreground=LIGHT.get("text_secondary", "#888"))
            self._speed_label.pack(side=tk.LEFT)
            self._eta_label = ttk.Label(meta_row, text="",
                                         font=("Microsoft YaHei UI", 8),
                                         foreground=LIGHT.get("text_secondary", "#888"))
            self._eta_label.pack(side=tk.RIGHT)

        # 错误显示区 (默认隐藏)
        self._error_label = ttk.Label(main, text="",
                                       font=("Microsoft YaHei UI", 9),
                                       foreground="#e74c3c", wraplength=400,
                                       justify=tk.LEFT)
        self._error_label.pack(fill=tk.X, pady=(4, 0))

        # 取消按钮
        if allow_cancel:
            btn_row = ttk.Frame(main)
            btn_row.pack(fill=tk.X, pady=(12, 0))
            self._cancel_btn = ttk.Button(btn_row, text="取消", command=self._on_cancel)
            self._cancel_btn.pack(side=tk.RIGHT)
        else:
            self._cancel_btn = None

        # 内部状态
        self._start_time = time.time() if (show_speed or show_eta) else None
        self._last_update = 0.0

        self.protocol("WM_DELETE_WINDOW", self._on_cancel)
        self._center_on_parent()
        self.update_idletasks()

    def _center_on_parent(self):
        self.update_idletasks()
        try:
            pw, ph = self.parent.winfo_width(), self.parent.winfo_height()
            px, py = self.parent.winfo_rootx(), self.parent.winfo_rooty()
            w, h = self.winfo_width(), self.winfo_height()
            x = px + max(0, (pw - w) // 2)
            y = py + max(0, (ph - h) // 2)
            self.geometry(f"+{x}+{y}")
        except Exception:
            pass

    def is_cancelled(self) -> bool:
        """主线程/工作线程调用: 检查用户是否取消。"""
        return self._cancelled

    def was_cancelled(self) -> bool:
        """complete 后调用: 是否被取消。"""
        return self._cancelled and not self._success

    def update_progress(self, percent: float, status: str = ""):
        """更新进度, 0-100 范围。线程安全 — 在主线程调度 UI 更新。"""
        try:
            percent = max(0.0, min(100.0, float(percent)))
        except (TypeError, ValueError):
            percent = 0.0
        # 立即更新内部状态(线程安全)
        self._last_percent = percent
        if status:
            self._last_status = status[:200]
        if self._start_time is not None:
            now = time.time()
            elapsed = max(0.001, now - self._start_time)
            if percent > 0.5:
                total_eta = elapsed / percent * 100
                self._last_eta = max(0, total_eta - elapsed)
                if now - self._last_update > 0.3:
                    self._last_speed = percent / elapsed
                    self._last_update = now
        # 调度 UI 更新到主线程
        try:
            self.after(0, self._refresh_ui)
        except Exception:
            pass

    def _refresh_ui(self):
        """在主线程刷新 UI 显示(由 after 调度)。"""
        try:
            percent = getattr(self, "_last_percent", 0.0)
            self._progress_var.set(percent)
            self._pct_label.config(text=f"{percent:.1f}%")
            status = getattr(self, "_last_status", "")
            if status:
                self._status_label.config(text=status)
            if self._start_time is not None:
                if hasattr(self, "_last_eta"):
                    try:
                        self._eta_label.config(text=f"ETA: {self._last_eta:.0f}s")
                    except Exception:
                        pass
                if hasattr(self, "_last_speed"):
                    try:
                        self._speed_label.config(text=f"速度: {self._last_speed:.1f}%/s")
                    except Exception:
                        pass
        except Exception:
            pass

    def complete(self, success: bool, message: str = "", error_detail: str = ""):
        """标记操作完成 — 通过 after 调度到主线程。"""
        if self._completed:
            return
        def _do_complete():
            if self._completed:
                return
            self._completed = True
            self._success = success
            self._result_msg = message
            if success:
                self._progress_var.set(100)
                self._pct_label.config(text="✓ 100%")
                self._pct_label.config(foreground="#27ae60")
                self._status_label.config(text=message or "完成")
            else:
                if self._cancelled:
                    self._pct_label.config(text="✗ 已取消")
                    self._status_label.config(text="操作已取消")
                else:
                    self._pct_label.config(text="✗ 失败")
                    self._pct_label.config(foreground="#e74c3c")
                    self._status_label.config(text=message or "操作失败")
                if error_detail:
                    self._error_label.config(text=error_detail[:500])
            if self._cancel_btn is not None:
                try:
                    self._cancel_btn.config(text="确定", command=self._on_dismiss)
                except Exception:
                    pass
            if success:
                self.after(800, self._on_dismiss)
            else:
                try:
                    self.grab_release()
                except Exception:
                    pass
                self.protocol("WM_DELETE_WINDOW", self._on_dismiss)
        try:
            self.after(0, _do_complete)
        except Exception:
            # 对话框已销毁
            pass

    def _on_cancel(self):
        if self._completed:
            self._on_dismiss()
            return
        self._cancelled = True
        # 立即更新内部状态
        try:
            self._status_label.config(text="正在取消...")
        except Exception:
            pass
        if self._cancel_btn is not None:
            try:
                self._cancel_btn.config(state=tk.DISABLED, text="取消中...")
            except Exception:
                pass

    def _on_dismiss(self):
        def _do():
            try:
                self.grab_release()
            except Exception:
                pass
            try:
                self.destroy()
            except Exception:
                pass
        try:
            self.after(0, _do)
        except Exception:
            _do()


class MainWindow(tk.Tk):
    def __init__(self, auth_manager=None, is_offline=False):
        super().__init__()
        self.base_path = get_program_base_path()
        self.title(f"{__app_name__} - v{get_app_version()}")
        ip = os.path.join(self.base_path, "icon.ico")
        if os.path.exists(ip):
            try:
                self.iconbitmap(ip)
            except Exception:
                pass
        w, h = 1100, 720
        ws, hs = self.winfo_screenwidth(), self.winfo_screenheight()
        self.geometry(f"{w}x{h}+{(ws-w)//2}+{(hs-h)//2}")
        self.minsize(800, 500)
        # Ensure the window is in normal state (not withdrawn/iconified)
        try:
            self.state("normal")
        except Exception:
            pass

        self.auth_manager = auth_manager
        self.od_browser = OneDriveBrowser(auth_manager) if auth_manager else None
        self._onedrive_loaded = set()
        self.is_offline = is_offline

        self.config_file = os.path.join(self.base_path, "HMOL_config.json")
        # 兼容旧版本配置文件: mo_manager_config.json → HMOL_config.json
        self._migrate_legacy_config()
        self.config = self._load_config()
        self.theme = self._resolve_theme()
        self.theme_mode = self.config.get("theme_mode", "Follow System")
        sg = self.config.get("gradient_theme", DEFAULT_GRADIENT_THEME)
        self.gradient_theme = sg if sg in GRADIENT_THEMES else DEFAULT_GRADIENT_THEME
        self._save_config_pending = False
        self._save_after_id = None

        self.file_op_thread = FileOperationThread()
        self.file_op_thread.start()

        self.instance_manager = InstanceManager(self, self.base_path)
        self.instance_manager.load_instances()
        lid = self.config.get("last_instance_id")
        if lid and lid in self.instance_manager.instances:
            self.instance_manager.set_current_instance(lid)
        self.instance_manager.on_instances_changed(self._on_instances_changed)

        self.package_configs = self._get_package_configs()
        self.package_dirs = self._get_package_dirs()
        self.pages = {}
        self._current_page_key = None
        self._prev_page_key = None
        self._package_tab_widgets = {}
        self._instance_combos = []
        self._sidebar_buttons = {}
        self._od_pages = {}

        self._build_ui()
        self._apply_theme()
        self.update_instance_combo()
        self._switch_page("home")
        try:
            self._load_saved_home_background()
        except Exception as e:
            log_warn("App", f"加载主页背景失败: {e}")
        if self.is_offline:
            self._apply_offline_mode()
        self.after(500, self._dlc_scan_pending_archives)
        self.protocol("WM_DELETE_WINDOW", self.on_close)

        # Ensure the window is fully laid out and visible
        self.update_idletasks()
        self.deiconify()

    # ========== config ==========
    def _migrate_legacy_config(self):
        """从旧版配置名 (mo_manager_config.json) 迁移到新版 (HMOL_config.json)。

        规则:
          - 如果新文件已存在 → 不动
          - 如果旧文件存在 → 复制到新文件,然后保留旧文件作为备份
        """
        try:
            if os.path.exists(self.config_file):
                return  # 新文件已存在,无需迁移
            legacy = os.path.join(self.base_path, "mo_manager_config.json")
            if not os.path.exists(legacy):
                return  # 旧文件也不存在
            # 尝试复制
            with open(legacy, "rb") as src:
                data = src.read()
            # 验证 JSON 合法性
            try:
                json.loads(data.decode("utf-8"))
            except Exception:
                return  # 旧文件已损坏,不迁移
            with open(self.config_file, "wb") as dst:
                dst.write(data)
            # 备份旧文件
            backup = legacy + ".legacy.bak"
            try:
                if not os.path.exists(backup):
                    os.rename(legacy, backup)
            except Exception:
                pass
            log_info("App", f"已迁移旧配置: {legacy} → {self.config_file}")
        except Exception as e:
            log_warn("App", f"迁移旧配置失败: {e}")

    def _load_config(self):
        if os.path.exists(self.config_file):
            try:
                with open(self.config_file, "r", encoding="utf-8") as f:
                    d = json.load(f)
                d.pop("installed_packages", None)
                d.setdefault("home_background_path", None)
                return d
            except (json.JSONDecodeError, OSError) as e:
                log_error("App", f"加载配置失败: {e}, 使用默认配置")
        return {"version": __version__, "theme_mode":"Follow System","launch_args":"","auto_detect_path":True,"gradient_theme":DEFAULT_GRADIENT_THEME,"home_background_path":None, "eula_accepted": False, "eula_accepted_version": ""}

    def save_config(self, immediate=False):
        if immediate:
            self._save_config_pending = False
            if self._save_after_id:
                self.after_cancel(self._save_after_id)
                self._save_after_id = None
            self._do_save_config()
            return
        if self._save_config_pending:
            return
        self._save_config_pending = True
        if self._save_after_id:
            self.after_cancel(self._save_after_id)
        self._save_after_id = self.after(300, self._do_save_config)

    def _do_save_config(self):
        self._save_config_pending = False
        self._save_after_id = None
        try:
            sc = {
                "version": self.config.get("version", __version__),
                "theme_mode": self.config.get("theme_mode","Follow System"),
                "gradient_theme": self.config.get("gradient_theme",DEFAULT_GRADIENT_THEME),
                "launch_args": self.config.get("launch_args",""),
                "auto_detect_path": self.config.get("auto_detect_path",True),
                "last_instance_id": self.instance_manager.current_instance.id if self.instance_manager.current_instance else None,
                "home_background_path": self.config.get("home_background_path"),
                "eula_accepted": self.config.get("eula_accepted", False),
                "eula_accepted_version": self.config.get("eula_accepted_version", ""),
            }
            with open(self.config_file, "w", encoding="utf-8") as f:
                json.dump(sc, f, ensure_ascii=False, indent=4)
        except Exception as e:
            log_error("App", f"保存配置失败: {e}")

    # ========== theme ==========
    def _resolve_theme(self):
        mode = self.config.get("theme_mode", "Follow System")
        if mode == "Follow System":
            return DARK if detect_system_theme() == "Dark Mode" else LIGHT
        return DARK if mode == "Dark Mode" else LIGHT

    def _current_gradient(self):
        return GRADIENT_THEMES.get(self.gradient_theme, GRADIENT_THEMES[DEFAULT_GRADIENT_THEME])

    def _tcss(self, k):
        return self.theme.get(k, "#000000")

    @staticmethod
    def _lighten(h, a=30):
        h = h.lstrip("#")
        r, g, b = min(255, int(h[0:2],16)+a), min(255, int(h[2:4],16)+a), min(255, int(h[4:6],16)+a)
        return f"#{r:02x}{g:02x}{b:02x}"
    @staticmethod
    def _darken(h, a=30):
        h = h.lstrip("#")
        r, g, b = max(0, int(h[0:2],16)-a), max(0, int(h[2:4],16)-a), max(0, int(h[4:6],16)-a)
        return f"#{r:02x}{g:02x}{b:02x}"

    def apply_theme_mode(self, mode):
        self.config["theme_mode"] = mode
        self.theme_mode = mode
        self.theme = self._resolve_theme()
        self._apply_theme()
        self.save_config()

    def set_gradient_theme(self, name):
        if name in GRADIENT_THEMES:
            self.gradient_theme = name
            self.config["gradient_theme"] = name
            self._apply_theme()
            self.save_config()

    def _apply_theme(self):
        s = ttk.Style(self)
        t = self.theme
        try:
            s.theme_use("clam")
        except tk.TclError:
            pass
        self.configure(bg=t["bg"])
        s.configure("TFrame", background=t["bg"])
        s.configure("TLabel", background=t["bg"], foreground=t["text"], font=("Microsoft YaHei UI", 10))
        s.configure("Title.TLabel", font=("Microsoft YaHei UI", 18, "bold"), foreground=t["primary"])
        s.configure("Warning.TLabel", foreground=t["warning"], font=("Microsoft YaHei UI", 10))
        s.configure("TButton", font=("Microsoft YaHei UI", 10), padding=(12, 6))
        s.map("TButton", background=[("active", t["accent_hover"]), ("disabled", t["surface_alt"])],
              foreground=[("active", t["text_inverse"]), ("disabled", t["text_disabled"])])
        s.configure("TEntry", fieldbackground=t["surface"], foreground=t["text"], padding=(8, 6))
        s.configure("TCombobox", fieldbackground=t["surface"], foreground=t["text"])
        s.configure("TNotebook", background=t["bg"], borderwidth=0)
        s.configure("TNotebook.Tab", padding=(16, 8), font=("Microsoft YaHei UI", 10))
        s.map("TNotebook.Tab", background=[("selected", t["accent"])], foreground=[("selected", t["text_inverse"])])
        s.configure("Treeview", background=t["surface"], foreground=t["text"], fieldbackground=t["surface"], font=("Microsoft YaHei UI", 10))
        s.map("Treeview", background=[("selected", t["selection"])])
        s.configure("TProgressbar", background=t["accent"], troughcolor=t["surface_alt"])
        s.configure("TPanedwindow", background=t["border"])
        s.configure("TScrollbar", background=t["scroll_thumb"], troughcolor=t["surface_alt"])
        s.configure("TSeparator", background=t["border"])
        if hasattr(self, "_sidebar_frame"):
            self._sidebar_frame.configure(bg=t["bg_sidebar"])
        if hasattr(self, "_status_label"):
            self._status_label.configure(bg=t["surface_alt"], fg=t["text_secondary"])
        if hasattr(self, "_home_canvas") and self._home_canvas:
            self._render_home_background()

    def _apply_offline_mode(self):
        for k in ["onedrive_game_resources", "onedrive_runtime_env", "onedrive_program_extend", "account"]:
            b = self._sidebar_buttons.get(k)
            if b:
                b.config(state=tk.DISABLED)

    def _set_status(self, text):
        if hasattr(self, "_status_label"):
            self._status_label.config(text=text)

    # ========== build UI ==========
    def _load_sidebar_icon(self, size=(32, 32)):
        # 候选路径列表
        candidates = [
            os.path.join(self.base_path, "icon.ico"),
            os.path.join(self.base_path, "icon.png"),
            os.path.join(self.base_path, "assets", "icon.ico"),
            os.path.join(self.base_path, "assets", "icon.png"),
        ]
        # 也尝试使用 sys.executable 所在目录(EXE 模式下常用)
        try:
            exe_dir = os.path.dirname(os.path.abspath(sys.executable))
            if exe_dir and exe_dir != self.base_path:
                candidates.append(os.path.join(exe_dir, "icon.ico"))
                candidates.append(os.path.join(exe_dir, "icon.png"))
        except Exception:
            pass

        # 找到第一个存在的图标文件
        ip = None
        for c in candidates:
            if os.path.isfile(c):
                ip = c
                break
        if not ip:
            return None

        # 优先 PIL(支持 .ico 多分辨率)
        if PIL_AVAILABLE:
            try:
                from PIL import Image, ImageTk
                img = Image.open(ip)
                # 兼容多分辨率 .ico — 加载最大尺寸
                if ip.lower().endswith(".ico"):
                    try:
                        sizes = getattr(img, "ico_sizes", None)
                        if sizes:
                            best = max(sizes, key=lambda s: s[0] * s[1])
                            # 关键:必须先设置 size 再 load() 才能加载该分辨率
                            img.size = best
                            img.load()
                    except Exception:
                        pass
                # 转为 RGBA(避免 RGB 模式在 resize 时丢透明度)
                try:
                    img = img.convert("RGBA")
                except Exception:
                    try:
                        img = img.convert("RGB")
                    except Exception:
                        pass
                # 等比缩放(避免变形)
                try:
                    src_w, src_h = img.size
                    target_w, target_h = size
                    ratio = min(target_w / src_w, target_h / src_h)
                    new_w = max(1, int(src_w * ratio))
                    new_h = max(1, int(src_h * ratio))
                    img = img.resize((new_w, new_h), Image.LANCZOS)
                except Exception:
                    img = img.resize(size, Image.LANCZOS)
                photo = ImageTk.PhotoImage(img)
                return photo
            except Exception as e:
                log_warn("App", f"PIL 加载 sidebar icon 失败 ({ip}): {e}")
        # 回退 tk.PhotoImage (不支持 .ico,只能尝试 PNG/GIF/PPM)
        try:
            photo = tk.PhotoImage(file=ip)
            sw, sh = photo.width(), photo.height()
            if sw > size[0] or sh > size[1]:
                photo = photo.subsample(
                    max(1, sw // size[0]), max(1, sh // size[1])
                )
            return photo
        except Exception as e:
            log_warn("App", f"tk.PhotoImage 加载 sidebar icon 失败 ({ip}): {e}")
            return None

    def _build_ui(self):
        self._main_pane = ttk.PanedWindow(self, orient=tk.HORIZONTAL)
        self._main_pane.pack(fill=tk.BOTH, expand=True)
        # 预加载侧边栏图标(PhotoImage 必须保持引用)
        self._sidebar_icon = self._load_sidebar_icon(size=(32, 32))
        sb = self._build_sidebar(self._main_pane)
        self._main_pane.add(sb, weight=0)
        self._content_frame = ttk.Frame(self._main_pane)
        self._main_pane.add(self._content_frame, weight=1)
        self._build_all_pages(self._content_frame)
        self._status_frame = tk.Frame(self, bg=self.theme["surface_alt"], height=28)
        self._status_frame.pack(fill=tk.X, side=tk.BOTTOM)
        self._status_frame.pack_propagate(False)
        self._status_label = tk.Label(self._status_frame, text="就绪", bg=self.theme["surface_alt"], fg=self.theme["text_secondary"], font=("Microsoft YaHei UI", 9), anchor=tk.W, padx=12)
        self._status_label.pack(fill=tk.BOTH, expand=True)

    def _build_sidebar(self, parent):
        sb = tk.Frame(parent, bg=self.theme["bg_sidebar"], width=220, highlightthickness=0)
        sb.pack_propagate(False)
        self._sidebar_frame = sb
        lf = tk.Frame(sb, bg=self.theme["bg_sidebar"])
        lf.pack(fill=tk.X, padx=12, pady=(16, 8))

        # ── 顶部 Logo:优先显示程序图标(icon.ico),回退到 emoji ──
        if self._sidebar_icon is not None:
            # 使用程序图标(用户要求:把侧边栏最上面的表情符号换成程序图标)
            icon_label = tk.Label(
                lf,
                image=self._sidebar_icon,
                bg=self.theme["bg_sidebar"],
                cursor="hand2",
            )
            icon_label.pack(side=tk.LEFT, padx=(0, 8))
            # 保留引用,防止被 GC
            icon_label.image = self._sidebar_icon
            # 单击图标显示"关于"信息
            icon_label.bind("<Button-1>", lambda e: self._nav_settings())
            # 鼠标悬停效果
            def _on_enter(e):
                icon_label.config(bg=self.theme.get("accent", "#5b8def"))
            def _on_leave(e):
                icon_label.config(bg=self.theme["bg_sidebar"])
            icon_label.bind("<Enter>", _on_enter)
            icon_label.bind("<Leave>", _on_leave)
        else:
            # 回退:使用 🚀 emoji(图标文件不可用时)
            tk.Label(
                lf,
                text="\U0001f680",
                font=("Segoe UI Emoji", 24),
                bg=self.theme["bg_sidebar"],
            ).pack(side=tk.LEFT, padx=(0, 8))

        # 标题文字
        title_lbl = tk.Label(
            lf,
            text=f"HMOL v{get_app_version()}",
            font=("Microsoft YaHei UI", 13, "bold"),
            bg=self.theme["bg_sidebar"],
            fg=self.theme["primary"],
        )
        title_lbl.pack(side=tk.LEFT)
        # 标题也支持单击进入关于页
        title_lbl.bind("<Button-1>", lambda e: self._nav_settings())
        ttk.Separator(sb, orient=tk.HORIZONTAL).pack(fill=tk.X, padx=12, pady=(8, 4))
        nc = tk.Canvas(sb, bg=self.theme["bg_sidebar"], highlightthickness=0)
        nf = tk.Frame(nc, bg=self.theme["bg_sidebar"])
        nf.bind("<Configure>", lambda e: nc.configure(scrollregion=nc.bbox("all")))
        nc.create_window((0, 0), window=nf, anchor=tk.NW, width=200)
        nc.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)

        self._anb(nf, "home", "\U0001f3e0 主页", self._nav_home)
        self._ans(nf)
        self._anb(nf, "account", "\U0001f464 账号", self._nav_account)
        self._ans(nf)
        self._ash(nf, "\U0001f4cb MO 设置")
        self._anb(nf, "instance", "\U0001f4bc 实例管理", self._nav_instance)
        self._anb(nf, "package", "\U0001f4e6 包管理", self._nav_package)
        self._ans(nf)
        self._ash(nf, "\U0001f921 笨蛋广场")
        self._anb(nf, "onedrive_game_resources", "\U0001f3ae 游戏资源", lambda: self._nav_onedrive("game_resources"))
        self._anb(nf, "onedrive_runtime_env", "\u2699\ufe0f 运行环境", lambda: self._nav_onedrive("runtime_env"))
        self._anb(nf, "onedrive_program_extend", "\U0001f9e9 程序DLC", lambda: self._nav_onedrive("program_extend"))
        self._ans(nf)
        self._anb(nf, "dlc", "\U0001f9e9 程序DLC", self._nav_dlc)
        self._anb(nf, "settings", "\u2699\ufe0f 设置", self._nav_settings)

        ttk.Separator(sb, orient=tk.HORIZONTAL).pack(fill=tk.X, padx=12, side=tk.BOTTOM, pady=(0, 4))
        eb = tk.Button(sb, text="\u274c 退出", font=("Microsoft YaHei UI", 10), bg=self.theme["error"], fg=self.theme["text_inverse"], relief=tk.FLAT, padx=12, pady=6, activebackground="#c0392b", command=self.on_close, cursor="hand2")
        eb.pack(side=tk.BOTTOM, fill=tk.X, padx=12, pady=(0, 12))
        self._sidebar_buttons["exit"] = eb
        return sb

    def _anb(self, p, k, t, c):
        b = tk.Button(p, text=t, font=("Microsoft YaHei UI", 10), bg=self.theme["bg_sidebar"], fg=self.theme["text"], relief=tk.FLAT, padx=16, pady=8, anchor=tk.W, activebackground=self.theme["accent"], activeforeground=self.theme["text_inverse"], command=c, cursor="hand2")
        b.pack(fill=tk.X, padx=4, pady=1)
        self._sidebar_buttons[k] = b

    def _ans(self, p):
        ttk.Separator(p, orient=tk.HORIZONTAL).pack(fill=tk.X, padx=12, pady=4)

    def _ash(self, p, t):
        tk.Label(p, text=t, font=("Microsoft YaHei UI", 9, "bold"), bg=self.theme["bg_sidebar"], fg=self.theme["text_secondary"], anchor=tk.W, padx=16).pack(fill=tk.X, pady=(8, 2))

    # 老大，我们这样熬夜真的不会猝死嘛...
    def _nav_home(self): self._switch_page("home")
    def _nav_account(self): self._switch_page("account"); self._load_account_page()
    def _nav_instance(self): self._switch_page("instance")
    def _nav_package(self): self._switch_page("package")
    def _nav_onedrive(self, sk): self._switch_page(f"onedrive_{sk}"); self._od_lazy_load_if_needed(f"onedrive_{sk}")
    def _nav_upload_res(self): pass  # 已废弃
    def _nav_dlc(self): self._switch_page("dlc")
    def _nav_settings(self): self._switch_page("settings")

    def _build_all_pages(self, parent):
        self.pages["home"] = self._build_home_page(parent)
        self.pages["account"] = self._build_account_page(parent)
        self.pages["instance"] = self._build_instance_page(parent)
        self.pages["package"] = self._build_package_page(parent)
        for k, src in ONEDRIVE_SOURCES.items():
            self.pages[f"onedrive_{k}"] = self._build_onedrive_page(parent, k, src["url"])
        self.pages["dlc"] = self._build_dlc_page(parent)
        self.pages["settings"] = self._build_settings_page(parent)

    def _switch_page(self, pk):
        if self._current_page_key == pk:
            return
        if self._current_page_key and self._current_page_key in self.pages:
            try:
                self.pages[self._current_page_key].pack_forget()
            except Exception:
                pass
        if pk in self.pages:
            self.pages[pk].pack(fill=tk.BOTH, expand=True)
            self._prev_page_key = self._current_page_key
            self._current_page_key = pk

    def _wrap_subpage(self, title, build_func):
        """创建带返回按钮的子页面。build_func(container) 用于构建页面内容。"""
        w = ttk.Frame(self._content_frame)
        h = ttk.Frame(w)
        h.pack(fill=tk.X, padx=16, pady=(12, 8))
        ttk.Label(h, text=title, style="Title.TLabel").pack(side=tk.LEFT)
        ttk.Button(h, text="\u2190 返回", command=self._back_to_main).pack(side=tk.RIGHT)
        ttk.Separator(w, orient=tk.HORIZONTAL).pack(fill=tk.X, padx=16)
        container = ttk.Frame(w)
        container.pack(fill=tk.BOTH, expand=True, padx=16, pady=8)
        build_func(container)
        return w

    def _back_to_main(self):
        if self._prev_page_key:
            self._switch_page(self._prev_page_key)

    # 开源？开玩笑，我写的东西为什么要开源？
    def _build_home_page(self, parent):
        p = ttk.Frame(parent)
        self._home_canvas = tk.Canvas(p, bg=self.theme["bg"], highlightthickness=0)
        self._home_canvas.pack(fill=tk.BOTH, expand=True)
        bf = tk.Frame(p, bg=self.theme["bg"])
        bf.place(relx=1.0, rely=1.0, anchor=tk.SE, x=-30, y=-30)
        self._launch_btn = tk.Button(bf, text="\U0001f680 启动游戏", font=("Microsoft YaHei UI", 14, "bold"), bg="#27ae60", fg="white", relief=tk.FLAT, padx=24, pady=12, activebackground="#2ecc71", command=self._launch_game, cursor="hand2")
        self._launch_btn.pack(side=tk.BOTTOM, pady=(4, 0))
        ir = tk.Frame(bf, bg=self.theme["bg"])
        ir.pack(side=tk.BOTTOM, pady=(0, 4))
        self._home_instance_var = tk.StringVar()
        self._home_instance_combo = ttk.Combobox(ir, textvariable=self._home_instance_var, state="readonly", width=22, font=("Microsoft YaHei UI", 10))
        self._home_instance_combo.pack(side=tk.LEFT, padx=(0, 8))
        self._home_instance_combo.bind("<<ComboboxSelected>>", self._on_home_instance_combo_changed)
        self._instance_combos.append(self._home_instance_combo)
        ttk.Button(ir, text="\U0001f4cb 管理", command=self._open_instance_management).pack(side=tk.LEFT)
        return p

    def _on_home_instance_combo_changed(self, event=None):
        idx = self._home_instance_combo.current()
        insts = self.instance_manager.get_instance_list()
        if 0 <= idx < len(insts):
            self.instance_manager.set_current_instance(insts[idx].id)
            self.save_config()

    def _render_home_background(self):
        if not hasattr(self, "_home_canvas") or not self._home_canvas:
            return
        c = self._home_canvas
        c.delete("all")
        w = c.winfo_width() or 1100
        h = c.winfo_height() or 720
        bp = self.config.get("home_background_path")
        if bp and os.path.isfile(bp) and PIL_AVAILABLE:
            try:
                from PIL import Image, ImageTk
                img = Image.open(bp).resize((w, h), Image.LANCZOS)
                self._home_bg_image = ImageTk.PhotoImage(img)
                c.create_image(0, 0, anchor=tk.NW, image=self._home_bg_image)
                return
            except Exception:
                pass
        g = self._current_gradient()
        c1, c2 = g["primary"], g["secondary"]
        steps = min(h, 200)
        for i in range(steps):
            t = i / max(steps - 1, 1)
            r = int(int(c1[1:3], 16) * (1 - t) + int(c2[1:3], 16) * t)
            gv = int(int(c1[3:5], 16) * (1 - t) + int(c2[3:5], 16) * t)
            b = int(int(c1[5:7], 16) * (1 - t) + int(c2[5:7], 16) * t)
            y0 = i * h / steps
            y1 = (i + 1) * h / steps
            c.create_rectangle(0, y0, w, y1, fill=f"#{r:02x}{gv:02x}{b:02x}", outline="", width=0)
        c.create_text(w // 2, h // 3, text="Hello Mental Omega Launcher", font=("Microsoft YaHei UI", 32, "bold"), fill=self.theme["text_inverse"], anchor=tk.CENTER)
        c.create_text(w // 2, h // 3 + 50, text=f"MO Launcher v{get_app_version()}", font=("Microsoft YaHei UI", 18), fill=self.theme["text_inverse"], anchor=tk.CENTER)

    def _build_account_page(self, parent):
        """账号页面 — 包含账号信息、Xbox 好友、QQ 喊话。"""
        p = ttk.Frame(parent)
        h = ttk.Frame(p)
        h.pack(fill=tk.X, padx=16, pady=(12, 8))
        ttk.Label(h, text="\U0001f464 账号", style="Title.TLabel").pack(side=tk.LEFT)
        ttk.Separator(p, orient=tk.HORIZONTAL).pack(fill=tk.X, padx=16)

        # 顶部账号操作栏
        top_bf = ttk.Frame(p)
        top_bf.pack(fill=tk.X, padx=16, pady=(8, 4))
        ttk.Button(top_bf, text="\U0001f504 刷新", command=self._load_account_page).pack(side=tk.LEFT, padx=2)
        ttk.Button(top_bf, text="\U0001f6aa 退出登录", command=self._logout_account).pack(side=tk.LEFT, padx=2)
        # QQ 喊话按钮 — 在线时可用
        self._account_shout_btn = ttk.Button(
            top_bf, text="\U0001f4e2 QQ 联机喊话", command=self._open_shout_dialog)
        self._account_shout_btn.pack(side=tk.RIGHT, padx=2)
        if self.is_offline or not QQ_BOT_AVAILABLE:
            self._account_shout_btn.config(state=tk.DISABLED)

        # 双列容器: 左 账号信息 / 右 Xbox 好友
        ct = ttk.Frame(p)
        ct.pack(fill=tk.BOTH, expand=True, padx=16, pady=8)
        ct.columnconfigure(0, weight=1, uniform="col")
        ct.columnconfigure(1, weight=1, uniform="col")
        ct.rowconfigure(0, weight=1)

        # 左侧: 账号信息
        left = ttk.LabelFrame(ct, text="\U0001f464 账号信息", padding=12)
        left.grid(row=0, column=0, sticky="nsew", padx=(0, 6))
        self._account_info_label = ttk.Label(left, text="加载中...", wraplength=480, justify=tk.LEFT)
        self._account_info_label.pack(anchor=tk.NW, fill=tk.BOTH, expand=True)

        # 右侧: Xbox 好友
        right = ttk.LabelFrame(ct, text="\U0001f3ae Xbox 好友", padding=12)
        right.grid(row=0, column=1, sticky="nsew", padx=(6, 0))
        list_frame = ttk.Frame(right)
        list_frame.pack(fill=tk.BOTH, expand=True)
        sb2 = ttk.Scrollbar(list_frame, orient=tk.VERTICAL)
        sb2.pack(side=tk.RIGHT, fill=tk.Y)
        self._xbox_friends_list = tk.Listbox(
            list_frame, font=("Microsoft YaHei UI", 10),
            bg=self.theme["surface"], fg=self.theme["text"],
            selectbackground=self.theme["selection"], yscrollcommand=sb2.set)
        sb2.config(command=self._xbox_friends_list.yview)
        self._xbox_friends_list.pack(fill=tk.BOTH, expand=True)
        return p

    def _open_shout_dialog(self):
        """从账号页面打开 QQ 喊话对话框(对齐 Qt 版本)。"""
        if self.is_offline:
            messagebox.showwarning("联机喊话", "离线模式不可用,请先登录")
            return
        if not QQ_BOT_AVAILABLE:
            messagebox.showerror("联机喊话", "QQ Bot 配置不可用")
            return
        try:
            dialog = ShoutDialog(self, app=self)
            self.wait_window(dialog)
        except Exception as e:
            log_error("App", f"打开喊话对话框失败: {e}")
            messagebox.showerror("联机喊话", f"打开失败: {e}")

    def _load_account_page(self):
        if not self.auth_manager:
            self._account_info_label.config(text="离线模式 - 部分功能不可用")
            self._xbox_friends_list.delete(0, tk.END)
            self._xbox_friends_list.insert(tk.END, "离线模式无法获取好友列表")
            return
        self._account_info_label.config(text="正在加载账号信息...")
        self._xbox_friends_list.delete(0, tk.END)
        self._xbox_friends_list.insert(tk.END, "加载中...")
        def load():
            u = self.auth_manager.get_user_info()
            self.after(0, lambda: self._on_account_loaded(u))
            f = self.auth_manager.get_xbox_friends()
            self.after(0, lambda: self._on_xbox_friends_loaded(f))
        threading.Thread(target=load, daemon=True).start()

    def _on_account_loaded(self, u):
        if u.get("error"):
            cu = self.auth_manager.get_cached_user_info()
            if not cu.get("error"):
                u = cu
            else:
                self._account_info_label.config(text=f"获取账号信息失败: {u.get('error')}")
                return
        self._account_info_label.config(text=f"显示名称: {u.get('display_name', '未知')}\n邮箱: {u.get('email', '')}\n用户ID: {u.get('user_id', '')}")

    def _on_xbox_friends_loaded(self, d):
        self._xbox_friends_list.delete(0, tk.END)
        if not d.get("success"):
            self._xbox_friends_list.insert(tk.END, f"获取好友失败: {d.get('reason', '未知')}")
            return
        for f in d.get("friends", []):
            st = "\U0001f7e2 在线" if f.get("online_state") == "Online" else "\u26ab 离线"
            self._xbox_friends_list.insert(tk.END, f"[{st}] {f.get('gamertag', '?')}  {f.get('presence_text', '')}")

    def _logout_account(self):
        if not self.auth_manager:
            return
        if messagebox.askyesno("确认", "确定要退出登录吗？"):
            self.auth_manager.logout()
            self._account_info_label.config(text="已退出登录")
            self._xbox_friends_list.delete(0, tk.END)
            self._xbox_friends_list.insert(tk.END, "请重新登录以获取好友列表")

    # 好烦，不要出差
    def _build_instance_page(self, parent):
        p = ttk.Frame(parent)
        h = ttk.Frame(p)
        h.pack(fill=tk.X, padx=16, pady=(12, 8))
        ttk.Label(h, text="💼 实例管理", style="Title.TLabel").pack(side=tk.LEFT)
        ttk.Separator(p, orient=tk.HORIZONTAL).pack(fill=tk.X, padx=16)
        bf = ttk.Frame(p)
        bf.pack(fill=tk.X, padx=16, pady=(8, 4))
        ttk.Button(bf, text="➕ 添加", command=self._add_instance).pack(side=tk.LEFT, padx=2)
        ttk.Button(bf, text="✏️ 重命名", command=self._rename_instance).pack(side=tk.LEFT, padx=2)
        ttk.Button(bf, text="🗑️ 删除", command=self._delete_instance).pack(side=tk.LEFT, padx=2)
        ttk.Button(bf, text="📤 导出", command=self._export_instance).pack(side=tk.LEFT, padx=2)
        ttk.Button(bf, text="👁️ 预览", command=self._preview_export_config).pack(side=tk.LEFT, padx=2)
        ttk.Button(bf, text="📥 导入", command=self._import_instance).pack(side=tk.LEFT, padx=2)
        ttk.Button(bf, text="🔄 刷新", command=self._refresh_instance_list).pack(side=tk.LEFT, padx=2)
        lf = ttk.Frame(p)
        lf.pack(fill=tk.BOTH, expand=True, padx=16, pady=4)
        sb2 = ttk.Scrollbar(lf, orient=tk.VERTICAL)
        self._instance_listbox = tk.Listbox(lf, font=("Microsoft YaHei UI", 11), bg=self.theme["surface"], fg=self.theme["text"], selectbackground=self.theme["selection"], yscrollcommand=sb2.set)
        sb2.config(command=self._instance_listbox.yview)
        sb2.pack(side=tk.RIGHT, fill=tk.Y)
        self._instance_listbox.pack(fill=tk.BOTH, expand=True)
        self._instance_listbox.bind("<Double-Button-1>", self._on_instance_double_click)
        btf = ttk.Frame(p)
        btf.pack(fill=tk.X, padx=16, pady=(4, 8))
        self._instance_size_label = ttk.Label(btf, text="")
        self._instance_size_label.pack(side=tk.LEFT)
        ttk.Button(btf, text="💾 备份", command=self._backup_game).pack(side=tk.RIGHT, padx=2)
        ttk.Button(btf, text="📀 备份原版", command=self._backup_original_game).pack(side=tk.RIGHT, padx=2)
        ttk.Button(btf, text="📂 还原", command=self._restore_game).pack(side=tk.RIGHT, padx=2)
        ttk.Button(btf, text="📁 打开目录", command=self._open_instance_dir).pack(side=tk.RIGHT, padx=2)
        return p

    def _refresh_instance_list(self):
        self._instance_listbox.delete(0, tk.END)
        cur = self.instance_manager.get_current_instance()
        for inst in self.instance_manager.get_instance_list():
            m = ">> " if (cur and inst.id == cur.id) else "   "
            self._instance_listbox.insert(tk.END, f"{m}{inst.name}  ({inst.path})")
        if cur:
            sz = self.instance_manager.get_instance_size(cur.id)
            self._instance_size_label.config(text=f"大小: {self.instance_manager.format_size(sz)}")

    def _on_instance_double_click(self, event):
        sel = self._instance_listbox.curselection()
        if not sel:
            return
        insts = self.instance_manager.get_instance_list()
        idx = sel[0]
        if 0 <= idx < len(insts):
            self.instance_manager.set_current_instance(insts[idx].id)
            self.save_config()
            self.update_instance_combo()
            self._refresh_instance_list()

    def _add_instance(self):
        n = simpledialog.askstring("添加实例", "请输入实例名称:", parent=self)
        if not n:
            return
        p = filedialog.askdirectory(title="选择游戏目录", parent=self)
        if not p:
            return
        ok, msg = self.instance_manager.add_instance(n, p)
        if ok:
            messagebox.showinfo("成功", msg)
            self._refresh_instance_list()
            self.update_instance_combo()
        else:
            messagebox.showerror("错误", msg)

    def _rename_instance(self):
        sel = self._instance_listbox.curselection()
        if not sel:
            messagebox.showwarning("警告", "请先选择一个实例")
            return
        inst = self.instance_manager.get_instance_list()[sel[0]]
        nn = simpledialog.askstring("重命名", f"为 {inst.name} 输入新名称:", parent=self, initialvalue=inst.name)
        if nn and nn != inst.name:
            ok, msg = self.instance_manager.rename_instance(inst.id, nn)
            if ok:
                messagebox.showinfo("成功", msg)
                self._refresh_instance_list()
                self.update_instance_combo()
            else:
                messagebox.showerror("错误", msg)

    def _delete_instance(self):
        sel = self._instance_listbox.curselection()
        if not sel:
            messagebox.showwarning("警告", "请先选择一个实例")
            return
        inst = self.instance_manager.get_instance_list()[sel[0]]
        if messagebox.askyesno("确认删除", f"确定要删除实例 '{inst.name}' 吗？\n此操作不可撤销！"):
            ok, msg = self.instance_manager.remove_instance(inst.id)
            if ok:
                messagebox.showinfo("成功", msg)
                self._refresh_instance_list()
                self.update_instance_combo()
            else:
                messagebox.showerror("错误", msg)

    def _export_instance(self):
        """异步导出实例 — 显示进度对话框,后台线程执行,避免UI阻塞。"""
        sel = self._instance_listbox.curselection()
        if not sel:
            messagebox.showwarning("警告", "请先选择一个实例")
            return
        try:
            inst = self.instance_manager.get_instance_list()[sel[0]]
        except (IndexError, Exception) as e:
            messagebox.showerror("错误", f"读取实例失败: {e}")
            return
        ep = filedialog.asksaveasfilename(
            title="导出实例", defaultextension=".zip",
            filetypes=[("ZIP 压缩包", "*.zip"), ("7z 压缩包", "*.7z")],
            initialfile=f"{inst.name}.zip", parent=self)
        if not ep:
            return
        # 检查目标文件可写
        try:
            target_dir = os.path.dirname(ep) or "."
            if not os.access(target_dir, os.W_OK):
                messagebox.showerror("错误", f"目标目录不可写: {target_dir}")
                return
        except Exception:
            pass

        # 创建进度对话框
        dlg = ProgressDialog(self, title=f"导出实例: {inst.name}",
                              allow_cancel=True, show_eta=True)

        def pr(pct, tot, msg):
            try:
                dlg.update_progress(pct, msg)
            except Exception:
                pass

        def worker():
            ok, msg = False, ""
            err_detail = ""
            try:
                ok, msg = self.instance_manager.export_instance(
                    inst.id, ep, progress_callback=pr)
            except Exception as e:
                ok = False
                msg = "导出失败"
                err_detail = f"{type(e).__name__}: {e}"
                log_error("App", f"导出实例异常: {e}")
            if dlg.is_cancelled():
                ok = False
                msg = "操作已取消"
            try:
                self.after(0, lambda: dlg.complete(ok, msg, err_detail))
            except Exception:
                pass

        threading.Thread(target=worker, daemon=True).start()
        self.wait_window(dlg)
        if dlg.was_cancelled():
            self._set_status("已取消导出")
            return
        if dlg._success:
            messagebox.showinfo("成功", dlg._result_msg or f"实例已导出到:\n{ep}")
            self._set_status(f"✅ 已导出: {os.path.basename(ep)}")
        else:
            err_text = dlg._result_msg or "导出失败"
            if dlg._error_label.cget("text"):
                err_text += f"\n\n{dlg._error_label.cget('text')}"
            messagebox.showerror("错误", err_text)
            self._set_status("❌ 导出失败")

    def _preview_export_config(self):
        sel = self._instance_listbox.curselection()
        if not sel:
            messagebox.showwarning("警告", "请先选择一个实例")
            return
        inst = self.instance_manager.get_instance_list()[sel[0]]
        pi = {}
        for pt, pkgs in inst.installed_packages.items():
            if pkgs:
                pi[pt] = list(pkgs)
        cfg = {"name": inst.name, "path": inst.path, "id": inst.id, "created_time": inst.created_time.isoformat(), "installed_packages": pi}
        pw = tk.Toplevel(self)
        pw.title(f"预览 - {inst.name}")
        pw.geometry("600x400")
        pw.transient(self)
        t = tk.Text(pw, font=("Consolas", 10), wrap=tk.WORD)
        t.pack(fill=tk.BOTH, expand=True, padx=10, pady=10)
        t.insert("1.0", json.dumps(cfg, ensure_ascii=False, indent=2))
        t.config(state=tk.DISABLED)

    def _import_instance(self):
        """异步导入实例 — 显示进度对话框,后台线程执行,避免UI阻塞。"""
        ip = filedialog.askopenfilename(
            title="导入实例",
            filetypes=[("压缩包", "*.zip *.7z"), ("所有文件", "*.*")],
            parent=self)
        if not ip:
            return
        # 验证源文件
        if not os.path.isfile(ip):
            messagebox.showerror("错误", f"文件不存在: {ip}")
            return
        try:
            if os.path.getsize(ip) == 0:
                messagebox.showerror("错误", "文件为空,无法导入")
                return
        except OSError as e:
            messagebox.showerror("错误", f"无法读取文件: {e}")
            return

        dlg = ProgressDialog(self, title=f"导入实例: {os.path.basename(ip)}",
                              allow_cancel=True, show_eta=True)

        def pr(pct, tot, msg):
            try:
                dlg.update_progress(pct, msg)
            except Exception:
                pass

        def worker():
            ok, msg, iid = False, "", None
            err_detail = ""
            try:
                ok, msg, iid = self.instance_manager.import_instance(
                    ip, progress_callback=pr)
            except Exception as e:
                ok = False
                msg = "导入失败"
                err_detail = f"{type(e).__name__}: {e}"
                log_error("App", f"导入实例异常: {e}")
            if dlg.is_cancelled():
                ok = False
                msg = "操作已取消"
            # 完成后在主线程更新 UI
            def finish():
                dlg.complete(ok, msg, err_detail)
                if ok:
                    self._refresh_instance_list()
                    self.update_instance_combo()
                    self._set_status(f"✅ 已导入: {os.path.basename(ip)}")
                else:
                    self._set_status("❌ 导入失败")
            try:
                self.after(0, finish)
            except Exception:
                pass

        threading.Thread(target=worker, daemon=True).start()
        self.wait_window(dlg)
        if dlg._success:
            messagebox.showinfo("成功", dlg._result_msg or "实例导入成功")

    def _open_instance_dir(self):
        sel = self._instance_listbox.curselection()
        if not sel:
            messagebox.showwarning("警告", "请先选择一个实例")
            return
        inst = self.instance_manager.get_instance_list()[sel[0]]
        self.instance_manager.open_instance_directory(inst.id)

    # ========== package page ==========
    def _build_package_page(self, parent):
        p = ttk.Frame(parent)
        h = ttk.Frame(p)
        h.pack(fill=tk.X, padx=16, pady=(12, 8))
        ttk.Label(h, text="📦 包管理", style="Title.TLabel").pack(side=tk.LEFT)
        ttk.Separator(p, orient=tk.HORIZONTAL).pack(fill=tk.X, padx=16)
        card = tk.Frame(p, bg=self.theme["surface"], highlightbackground=self.theme["border"], highlightthickness=1)
        card.pack(fill=tk.X, padx=16, pady=(8, 4))
        ci = tk.Frame(card, bg=self.theme["surface"])
        ci.pack(fill=tk.X, padx=12, pady=8)
        tk.Label(ci, text="💼 实例:", bg=self.theme["surface"], fg=self.theme["text_secondary"], font=("Microsoft YaHei UI", 10)).pack(side=tk.LEFT)
        self._pkg_instance_var = tk.StringVar()
        self._pkg_instance_combo = ttk.Combobox(ci, textvariable=self._pkg_instance_var, state="readonly", width=25)
        self._pkg_instance_combo.pack(side=tk.LEFT, padx=8)
        self._pkg_instance_combo.bind("<<ComboboxSelected>>", self._on_pkg_instance_changed)
        self._instance_combos.append(self._pkg_instance_combo)
        ttk.Button(ci, text="📋 管理", command=self._open_instance_management).pack(side=tk.LEFT, padx=4)
        self._pkg_instance_info = tk.Label(ci, text="", bg=self.theme["surface"], fg=self.theme["text"], font=("Microsoft YaHei UI", 9))
        self._pkg_instance_info.pack(side=tk.LEFT, padx=12)
        self._pkg_no_instance_warn = ttk.Label(p, text="⚠️ 请先选择一个游戏实例", style="Warning.TLabel")
        self._pkg_no_instance_warn.pack(pady=4)

        # 用户提示：@绮梦 是猪  
        hint_frame = tk.Frame(p, bg="#fff3cd", highlightbackground="#ffc107",
                              highlightthickness=1)
        hint_frame.pack(fill=tk.X, padx=16, pady=(4, 4))
        tk.Label(hint_frame,
                  text="💡 其他包被归类为资源包,因为wine版本要节省性能",
                  bg="#fff3cd", fg="#856404",
                  font=("Microsoft YaHei UI", 9),
                  anchor=tk.W, padx=10, pady=6).pack(fill=tk.X)

        self._package_notebook = ttk.Notebook(p)
        self._package_notebook.pack(fill=tk.BOTH, expand=True, padx=16, pady=4)
        # 单一"资源包"标签 — 整合所有分类
        for pt, cfg in self.package_configs.items():
            tf = self._build_package_tab(self._package_notebook, pt)
            self._package_notebook.add(tf, text=f"{cfg.get('icon','📦')} {cfg.get('name','资源包')}")
        return p

    def _build_package_tab(self, nb, pt):
        tab = ttk.Frame(nb)
        tab.columnconfigure(0, weight=1)
        tab.columnconfigure(1, weight=1)
        tab.rowconfigure(0, weight=0)
        tab.rowconfigure(1, weight=1)
        tab.rowconfigure(2, weight=0)
        ttk.Label(tab, text="可用包", style="Title.TLabel").grid(row=0, column=0, sticky=tk.W, padx=4, pady=2)
        ttk.Label(tab, text="已安装", style="Title.TLabel").grid(row=0, column=1, sticky=tk.W, padx=4, pady=2)
        af = ttk.Frame(tab)
        af.grid(row=1, column=0, sticky="nsew", padx=4)
        asb = ttk.Scrollbar(af, orient=tk.VERTICAL)
        al = tk.Listbox(af, font=("Microsoft YaHei UI", 10), bg=self.theme["surface"], fg=self.theme["text"], selectbackground=self.theme["selection"], yscrollcommand=asb.set)
        asb.config(command=al.yview)
        asb.pack(side=tk.RIGHT, fill=tk.Y)
        al.pack(fill=tk.BOTH, expand=True)
        inf = ttk.Frame(tab)
        inf.grid(row=1, column=1, sticky="nsew", padx=4)
        isb = ttk.Scrollbar(inf, orient=tk.VERTICAL)
        il = tk.Listbox(inf, font=("Microsoft YaHei UI", 10), bg=self.theme["surface"], fg=self.theme["text"], selectbackground=self.theme["selection"], yscrollcommand=isb.set)
        isb.config(command=il.yview)
        isb.pack(side=tk.RIGHT, fill=tk.Y)
        il.pack(fill=tk.BOTH, expand=True)
        bf = ttk.Frame(tab)
        bf.grid(row=2, column=0, columnspan=2, sticky="ew", padx=4, pady=(8, 0))
        ttk.Button(bf, text="📥 安装", command=lambda: self._install_package(pt)).pack(side=tk.LEFT, padx=2)
        ttk.Button(bf, text="🗑️ 删除", command=lambda: self._remove_package(pt)).pack(side=tk.LEFT, padx=2)
        ttk.Button(bf, text="❌ 卸载", command=lambda: self._uninstall_package(pt)).pack(side=tk.LEFT, padx=2)
        ttk.Button(bf, text="⬇️ 下载", command=lambda: self._download_package(pt)).pack(side=tk.LEFT, padx=2)
        ttk.Button(bf, text="📤 导入", command=lambda: self._import_package(pt)).pack(side=tk.LEFT, padx=2)
        ttk.Button(bf, text="📁 目录", command=lambda: self._open_package_dir(pt)).pack(side=tk.LEFT, padx=2)
        ttk.Button(bf, text="🔄 刷新", command=lambda: self._refresh_package_tab(pt)).pack(side=tk.LEFT, padx=2)
        self._package_tab_widgets[pt] = {"available_list": al, "installed_list": il}
        return tab

    def _on_pkg_instance_changed(self, event=None):
        idx = self._pkg_instance_combo.current()
        insts = self.instance_manager.get_instance_list()
        if 0 <= idx < len(insts):
            self.instance_manager.set_current_instance(insts[idx].id)
            self.save_config()
            self._update_pkg_instance_info()
            self._refresh_all_package_tabs()

    def _update_pkg_instance_info(self):
        cur = self.instance_manager.get_current_instance()
        if cur:
            self._pkg_instance_info.config(text=f"当前: {cur.name} ({cur.path})")
            self._pkg_no_instance_warn.pack_forget()
        else:
            self._pkg_instance_info.config(text="")
            self._pkg_no_instance_warn.pack()

    def _refresh_package_tab(self, pt):
        w = self._package_tab_widgets.get(pt)
        if not w:
            return
        w["available_list"].delete(0, tk.END)
        w["installed_list"].delete(0, tk.END)
        cur = self.instance_manager.get_current_instance()
        pd = self.get_package_dir(pt)
        # 确保目录存在
        if not os.path.isdir(pd):
            try:
                os.makedirs(pd, exist_ok=True)
            except Exception:
                pass
            w["available_list"].insert(tk.END, "  📭 资源包目录为空,请先从笨蛋广场下载或导入")
            return
        exts = self.package_configs.get(pt, {}).get("extensions", [".zip", ".7z"])
        # 收集已安装项(用于去重)
        installed_set = set()
        installed_files = set()
        if cur:
            for pn in cur.installed_packages.get(pt, []):
                pn_norm = str(pn).strip().lower()
                installed_set.add(pn_norm)
                installed_files.add(pn_norm)
                # 也加上 .zip 后缀的版本(用户可能记录的是 stem)
                installed_files.add(pn_norm + ".zip")
                installed_files.add(pn_norm + ".7z")
            # 当前实例 config 中"已安装"也可能直接记录 zip 名
            try:
                if hasattr(cur, "installed_files"):
                    for r in cur.installed_files.get(pt, []):
                        installed_files.add(os.path.basename(str(r)).strip().lower())
            except Exception:
                pass

        # 扫描目录
        available_items = []
        for fn in sorted(os.listdir(pd)):
            fp = os.path.join(pd, fn)
            if not os.path.isfile(fp):
                continue
            if os.path.splitext(fn)[1].lower() not in exts:
                continue
            fn_lower = fn.lower()
            fn_stem = os.path.splitext(fn)[0].lower()
            # 过滤规则:已安装(按包名 stem 或源文件名) → 不显示
            if (fn_lower in installed_files
                or fn_stem in installed_set
                or fn_stem + ".zip" in installed_files
                or fn_stem + ".7z" in installed_files):
                continue
            available_items.append(fn)
        if not available_items:
            w["available_list"].insert(tk.END, "  📭 暂无可用资源包,请先从笨蛋广场下载或导入")
        else:
            for fn in available_items:
                w["available_list"].insert(tk.END, f"  {fn}")
        # 已安装列表
        if cur:
            for pn in cur.installed_packages.get(pt, []):
                w["installed_list"].insert(tk.END, f"  [✅] {pn}")
            if not cur.installed_packages.get(pt, []):
                w["installed_list"].insert(tk.END, "  — 尚未安装任何资源包 —")
        else:
            w["installed_list"].insert(tk.END, "  — 请先选择实例查看已安装列表 —")

    def _refresh_all_package_tabs(self):
        for pt in self.package_configs:
            self._refresh_package_tab(pt)

    def _install_package(self, pt):
        cur = self.instance_manager.get_current_instance()
        if not cur:
            messagebox.showwarning("警告", "请先选择一个实例")
            return
        w = self._package_tab_widgets.get(pt)
        if not w:
            return
        sel = w["available_list"].curselection()
        if not sel:
            messagebox.showwarning("警告", "请先选择一个包")
            return
        pn = w["available_list"].get(sel[0]).strip()
        pp = os.path.join(self.get_package_dir(pt), pn)
        if not os.path.exists(pp):
            messagebox.showerror("错误", "包文件未找到")
            return
        try:
            with tempfile.TemporaryDirectory() as td:
                self._set_status(f"正在解压 {pn}...")
                self.update_idletasks()
                self._extract_archive(pp, td)
                # 预览:检测并显示搬运许可和说明
                try:
                    preview = find_package_preview_files(td)
                    if preview["license"] or preview["readme"]:
                        dlg = PackagePreviewDialog(self,
                                                     license_path=preview["license"],
                                                     readme_path=preview["readme"],
                                                     package_name=os.path.splitext(pn)[0])
                        self.wait_window(dlg)
                except Exception as e:
                    log_warn("Preview", f"显示包预览失败: {e}")
                conflicts, total = scan_install_conflicts(td, cur.path)
                if conflicts:
                    msg = f"检测到 {total} 个文件冲突:\n" + "\n".join(conflicts[:10])
                    if total > 10:
                        msg += f"\n... 及另外 {total - 10} 个"
                    msg += "\n\n是否覆盖已存在的文件？"
                    if not messagebox.askyesno("文件冲突", msg):
                        return
                rfs = []
                for root, dirs, files in os.walk(td):
                    for f in files:
                        rfs.append(os.path.relpath(os.path.join(root, f), td))
                sn = snapshot_existing_files(cur.path, rfs)
                def pr(cur2, total2, cf):
                    self._set_status(f"安装中 ({cur2}/{total2}): {os.path.basename(cf)}")
                ok, tc, failed = _copy_files(td, cur.path, progress_cb=pr)
                if ok:
                    sn2 = os.path.splitext(pn)[0]
                    save_install_record(self.base_path, cur, pt, sn2, rfs, pn, sn)
                    if sn2 not in cur.installed_packages[pt]:
                        cur.installed_packages[pt].append(sn2)
                    self.instance_manager._save_instance_config(cur)
                    self._refresh_package_tab(pt)
                    messagebox.showinfo("成功", f"已安装: {sn2}")
                else:
                    messagebox.showerror("错误", f"复制失败 ({failed}/{tc} 个文件失败)")
        except Exception as e:
            messagebox.showerror("错误", str(e))
            log_error("Package", f"安装 {pn} 失败: {e}")

    def _uninstall_package(self, pt):
        cur = self.instance_manager.get_current_instance()
        if not cur:
            messagebox.showwarning("警告", "请先选择一个实例")
            return
        w = self._package_tab_widgets.get(pt)
        if not w:
            return
        sel = w["installed_list"].curselection()
        if not sel:
            messagebox.showwarning("警告", "请先选择一个已安装的包")
            return
        pn = w["installed_list"].get(sel[0]).strip().lstrip("[✅] ").strip()
        if not messagebox.askyesno("确认卸载", f"确定要卸载 '{pn}' 吗？"):
            return
        rec = load_install_record(self.base_path, cur, pt, pn)
        files = rec.get("files", [])
        if not files:
            messagebox.showwarning("警告", "未找到安装记录，可能已被卸载或记录丢失")
            return
        rm, fail = 0, 0
        for rp in files:
            tf2 = os.path.join(cur.path, rp)
            try:
                if os.path.isfile(tf2):
                    os.remove(tf2)
                    rm += 1
                elif os.path.isdir(tf2):
                    shutil.rmtree(tf2, ignore_errors=True)
                    rm += 1
            except Exception as e:
                fail += 1
                log_error("Package", f"卸载文件失败 {rp}: {e}")
        if pn in cur.installed_packages.get(pt, []):
            cur.installed_packages[pt].remove(pn)
        delete_install_record(self.base_path, cur, pt, pn)
        self.instance_manager._save_instance_config(cur)
        self._refresh_package_tab(pt)
        msg = f"已删除 {rm} 个文件"
        if fail:
            msg += f"，{fail} 个文件删除失败"
        messagebox.showinfo("完成", msg)

    def _remove_package(self, pt):
        w = self._package_tab_widgets.get(pt)
        if not w:
            return
        sel = w["available_list"].curselection()
        if not sel:
            messagebox.showwarning("警告", "请先选择一个文件")
            return
        pn = w["available_list"].get(sel[0]).strip()
        pp = os.path.join(self.get_package_dir(pt), pn)
        if not os.path.exists(pp):
            messagebox.showerror("错误", "文件未找到")
            return
        if messagebox.askyesno("确认删除", f"确定要删除 '{pn}' 吗？此操作不可撤销！"):
            try:
                os.remove(pp)
                self._refresh_package_tab(pt)
                messagebox.showinfo("完成", f"已删除: {pn}")
            except Exception as e:
                messagebox.showerror("错误", str(e))

    def _download_package(self, pt):
        messagebox.showinfo("下载", "请使用 OneDrive 页面下载资源文件")
    def _import_package(self, pt="resource"):
        fp = filedialog.askopenfilename(title="导入资源包", filetypes=[("压缩包", "*.zip *.7z *.rar"), ("所有文件", "*.*")], parent=self)
        if not fp:
            return
        pd = self.get_package_dir(pt)
        target = os.path.join(pd, os.path.basename(fp))
        if os.path.exists(target) and not messagebox.askyesno("覆盖确认", f"'{os.path.basename(fp)}' 已存在，是否覆盖？"):
            return
        try:
            shutil.copy2(fp, target)
            # 先刷新列表,再预览
            self._refresh_all_package_tabs()
            # 预览:解压目标包并检测许可/说明文件
            try:
                with tempfile.TemporaryDirectory() as td:
                    self._extract_archive(target, td)
                    preview = find_package_preview_files(td)
                    if preview["license"] or preview["readme"]:
                        dlg = PackagePreviewDialog(self,
                                                     license_path=preview["license"],
                                                     readme_path=preview["readme"],
                                                     package_name=os.path.splitext(os.path.basename(fp))[0])
                        self.wait_window(dlg)
            except Exception as e:
                log_warn("Preview", f"显示包预览失败: {e}")
            self._set_status(f"✅ 已导入: {os.path.basename(fp)}")
            messagebox.showinfo("完成", f"已导入: {os.path.basename(fp)}\n\n保存位置: {target}")
        except Exception as e:
            log_error("App", f"导入包失败: {e}")
            messagebox.showerror("错误", str(e))
    def _open_package_dir(self, pt):
        pd = self.get_package_dir(pt)
        os.makedirs(pd, exist_ok=True)
        try:
            os.startfile(pd)
        except Exception as e:
            messagebox.showerror("错误", str(e))

    def _extract_archive(self, ap, dd):
        ext = os.path.splitext(ap)[1].lower()
        if ext == ".zip":
            with zipfile.ZipFile(ap, "r") as zf:
                zf.extractall(dd)
        elif ext == ".7z":
            if SEVENZIP_AVAILABLE:
                import py7zr
                with py7zr.SevenZipFile(ap, "r") as sz:
                    sz.extractall(path=dd)
            else:
                raise RuntimeError("不支持 7z 格式（请安装 py7zr 库）")
        elif ext == ".rar":
            if RARFILE_AVAILABLE:
                import rarfile
                with rarfile.RarFile(ap, "r") as rf:
                    rf.extractall(path=dd)
            else:
                raise RuntimeError("不支持 RAR 格式（请安装 rarfile 库）")
        else:
            raise RuntimeError(f"不支持的压缩格式: {ext}")

    # ========== utility methods ==========
    def is_mo_directory(self, path):
        if not path or not os.path.exists(path):
            return False
        path = os.path.abspath(path)
        if os.path.isdir(os.path.join(path, "Mental_Omega")):
            return True
        if os.path.basename(path) == "Mental_Omega" and os.path.isdir(path):
            return True
        for m in ("MentalOmegaClient.exe", "Mental Omega.exe"):
            if os.path.isfile(os.path.join(path, m)):
                return True
        pp = os.path.dirname(path)
        if pp and os.path.isdir(pp):
            for m in ("MentalOmegaClient.exe", "Mental Omega.exe"):
                if os.path.isfile(os.path.join(pp, m)):
                    return True
        return False

    def update_instance_combo(self):
        insts = self.instance_manager.get_instance_list()
        cur = self.instance_manager.get_current_instance()
        names = [i.name for i in insts]
        ci = -1
        if cur:
            for i2, inst in enumerate(insts):
                if inst.id == cur.id:
                    ci = i2
                    break
        for c in self._instance_combos:
            c["values"] = names
            if ci >= 0:
                c.current(ci)
            elif names:
                c.current(0)
        self._update_pkg_instance_info()
        self._refresh_instance_list()

    def _on_instances_changed(self):
        self.update_instance_combo()

    def _open_instance_management(self):
        self._switch_page("instance")

    def _get_package_configs(self):
        """资源包配置 — 统一为单类别 '资源包' (wine 版本性能优化)。"""
        exts = [".zip", ".7z"]
        if RARFILE_AVAILABLE:
            exts.append(".rar")
        return {
            # 单一"资源包"类别 — 整合 INI/地图/任务/语音/插件/美化/音乐
            "resource": {
                "name": "资源包",
                "extensions": exts + [".map", ".mp3", ".ogg", ".wav", ".dll"],
                "icon": "📦",
            },
        }

    def _get_package_dirs(self):
        """获取资源包目录(单一目录 packages/)。"""
        ds = {}
        for t in self.package_configs:
            ds[t] = self.get_package_dir(t)
            os.makedirs(ds[t], exist_ok=True)
        return ds

    def get_package_dir(self, pt):
        """所有包统一保存到 packages/ 目录,不再分子目录。

        参数 pt 保留以兼容旧调用,但实际不影响路径。
        """
        return os.path.join(self.base_path, "packages")

    # ========== launch ==========
    def _launch_game(self):
        cur = self.instance_manager.get_current_instance()
        if not cur:
            messagebox.showwarning("警告", "请先选择一个实例")
            return
        cands = [os.path.join(cur.path, "Mental_Omega_client.exe"),
                 os.path.join(cur.path, "MentalOmegaClient.exe"),
                 os.path.join(cur.path, "Mental_Omega.exe")]
        exe = None
        for c in cands:
            if os.path.exists(c):
                exe = c
                break
        if not exe:
            messagebox.showwarning("警告", "游戏可执行文件未找到")
            return
        try:
            args = self.config.get("launch_args", "")
            subprocess.Popen(f'"{exe}" {args}', shell=True, cwd=os.path.dirname(exe))
            self._set_status(f"游戏已启动: {os.path.basename(exe)}")
        except Exception as e:
            messagebox.critical("错误", f"启动失败: {e}")

    # ========== backup / restore ==========
    def _backup_game(self):
        cur = self.instance_manager.get_current_instance()
        if not cur:
            messagebox.showwarning("警告", "请先选择一个实例")
            return
        n = simpledialog.askstring("备份", "请输入备份名称:", parent=self)
        if not n:
            return
        ok, err = is_valid_backup_name(n)
        if not ok:
            messagebox.showerror("错误", err)
            return
        self._backup_game_impl(cur, n)

    def _backup_game_impl(self, inst, bn):
        bd = get_game_backup_path(self.base_path, bn)
        if os.path.exists(bd) and not messagebox.askyesno("覆盖确认", f"备份 '{bn}' 已存在，是否覆盖？"):
            return
        if os.path.exists(bd):
            shutil.rmtree(bd, ignore_errors=True)
        os.makedirs(bd, exist_ok=True)
        self._set_status(f"正在创建备份 '{bn}'...")
        self.update_idletasks()
        def pr(cur, tot, cf):
            self._set_status(f"备份中 ({cur}/{tot}): {os.path.basename(cf)}")
            self.update_idletasks()
        ok, total, failed = _copy_files(inst.path, bd, progress_cb=pr)
        if ok:
            meta = {"name": bn, "created_time": datetime.now().isoformat(), "instance_name": inst.name, "instance_id": inst.id}
            with open(os.path.join(bd, "backup_info.json"), "w", encoding="utf-8") as f:
                json.dump(meta, f, ensure_ascii=False, indent=2)
            self._set_status(f"备份完成: {bn} ({total} 个文件)")
            messagebox.showinfo("成功", f"备份 '{bn}' 完成！\n共 {total} 个文件")
        else:
            self._set_status(f"备份失败: {bn}")
            messagebox.showerror("错误", f"备份失败，{failed}/{total} 个文件复制失败")

    def _backup_original_game(self):
        cur = self.instance_manager.get_current_instance()
        if not cur:
            messagebox.showwarning("警告", "请先选择一个实例")
            return
        obd = get_original_backup_path(self.base_path)
        if os.path.exists(obd) and not messagebox.askyesno("覆盖确认", "原版游戏备份已存在，是否覆盖？"):
            return
        if os.path.exists(obd):
            shutil.rmtree(obd, ignore_errors=True)
        os.makedirs(obd, exist_ok=True)
        self._set_status("正在备份原版游戏...")
        self.update_idletasks()
        def pr(cur, tot, cf):
            self._set_status(f"备份原版中 ({cur}/{tot}): {os.path.basename(cf)}")
            self.update_idletasks()
        ok, total, failed = _copy_files(cur.path, obd, progress_cb=pr)
        if ok:
            meta = {"created_time": datetime.now().isoformat(), "instance_name": cur.name}
            with open(os.path.join(obd, "backup_info.json"), "w", encoding="utf-8") as f:
                json.dump(meta, f, ensure_ascii=False, indent=2)
            self._set_status("原版游戏备份完成")
            messagebox.showinfo("成功", f"原版游戏备份完成！\n共 {total} 个文件")
        else:
            self._set_status("原版游戏备份失败")
            messagebox.showerror("错误", f"备份失败，{failed}/{total} 个文件复制失败")

    def _restore_game(self):
        cur = self.instance_manager.get_current_instance()
        if not cur:
            messagebox.showwarning("警告", "请先选择一个实例")
            return
        backups = list_game_backups(self.base_path)
        obd = get_original_backup_path(self.base_path)
        has_orig = os.path.isdir(obd)
        choices = []
        if has_orig:
            choices.append("📀 原版游戏备份")
        for bk in backups:
            choices.append(f"💾 {bk['name']} ({bk.get('created_time','?')})")
        if not choices:
            messagebox.showwarning("警告", "没有可用的备份")
            return
        dw = tk.Toplevel(self)
        dw.title("选择要还原的备份")
        dw.geometry("500x350")
        dw.transient(self)
        dw.resizable(False, False)
        ws, hs = self.winfo_screenwidth(), self.winfo_screenheight()
        dw.geometry(f"+{(ws-500)//2}+{(hs-350)//2}")
        lb = tk.Listbox(dw, font=("Microsoft YaHei UI", 11))
        lb.pack(fill=tk.BOTH, expand=True, padx=12, pady=12)
        for c in choices:
            lb.insert(tk.END, c)
        def do():
            sel = lb.curselection()
            if not sel:
                messagebox.showwarning("警告", "请先选择一个备份")
                return
            idx = sel[0]
            dw.destroy()
            if has_orig and idx == 0:
                self._restore_game_from_path(obd, cur.path, "原版游戏备份")
            else:
                bi = idx - (1 if has_orig else 0)
                self._restore_game_from_path(backups[bi]["path"], cur.path, backups[bi]["name"])
        ttk.Button(dw, text="✅ 还原", command=do).pack(pady=(0, 12))
        ttk.Button(dw, text="取消", command=dw.destroy).pack(pady=(0, 12))

    def _restore_game_from_path(self, src, dst, label):
        if not messagebox.askyesno("确认还原", f"确定要从 '{label}' 还原游戏文件吗？\n这将覆盖当前游戏目录中的所有文件！"):
            return
        self._set_status(f"正在从 '{label}' 还原...")
        self.update_idletasks()
        def pr(cur, tot, cf):
            self._set_status(f"还原中 ({cur}/{tot}): {os.path.basename(cf)}")
            self.update_idletasks()
        ok, total, failed = _copy_files(src, dst, progress_cb=pr)
        if ok:
            self._set_status(f"还原完成: {label}")
            messagebox.showinfo("成功", f"从 '{label}' 还原完成！\n共 {total} 个文件")
        else:
            self._set_status(f"还原失败: {label}")
            messagebox.showerror("错误", f"还原失败，{failed}/{total} 个文件复制失败")

    # ========== home background ==========
    def _get_home_background_dir(self):
        bg_dir = os.path.join(self.base_path, "home_backgrounds")
        try:
            os.makedirs(bg_dir, exist_ok=True)
        except Exception as e:
            log_warn("App", f"创建背景目录失败: {e}")
        return bg_dir

    def _choose_home_background(self):
        fp = filedialog.askopenfilename(
            title="选择主页背景图片", filetypes=[("图片文件", "*.png *.jpg *.jpeg *.bmp *.webp *.gif"), ("所有文件", "*.*")], parent=self)
        if not fp or not os.path.isfile(fp):
            return
        try:
            bg_dir = self._get_home_background_dir()
            ext = os.path.splitext(fp)[1] or ".png"
            ts = datetime.now().strftime("%Y%m%d_%H%M%S")
            target = os.path.join(bg_dir, f"custom_bg_{ts}{ext}")
            shutil.copy2(fp, target)
        except Exception as e:
            messagebox.critical("错误", f"保存背景图片失败: {e}")
            return
        self.config["home_background_path"] = target
        self.save_config()
        self._render_home_background()
        self._set_status(f"✅ 主页背景已更新: {os.path.basename(target)}")

    def _reset_home_background(self):
        if "home_background_path" in self.config:
            del self.config["home_background_path"]
        self.save_config()
        self._render_home_background()
        self._set_status("✅ 已恢复默认背景")

    def _load_saved_home_background(self):
        bg_path = self.config.get("home_background_path")
        if not bg_path:
            return
        if not os.path.isfile(bg_path):
            log_info("App", f"已保存的背景图片不存在，使用默认背景: {bg_path}")
            self.config.pop("home_background_path", None)
            self.save_config()
            return
        self._render_home_background()

    # ========== OneDrive page ==========
    def _build_onedrive_page(self, parent, source_key, share_url):
        p = ttk.Frame(parent)
        h = ttk.Frame(p)
        h.pack(fill=tk.X, padx=16, pady=(12, 8))
        src_name = ONEDRIVE_SOURCES[source_key]["name"]
        ttk.Label(h, text=f"🤡 {src_name}", style="Title.TLabel").pack(side=tk.LEFT)
        ttk.Button(h, text="🔄 刷新", command=lambda: self._od_refresh_current(source_key)).pack(side=tk.RIGHT, padx=4)
        ttk.Button(h, text="🏠 返回根目录", command=lambda: self._od_lazy_load_if_needed(f"onedrive_{source_key}", force=True)).pack(side=tk.RIGHT, padx=4)
        ttk.Separator(p, orient=tk.HORIZONTAL).pack(fill=tk.X, padx=16)
        sf = ttk.Frame(p)
        sf.pack(fill=tk.X, padx=16, pady=(8, 4))
        ttk.Label(sf, text="🔍 搜索:").pack(side=tk.LEFT)
        sv = tk.StringVar()
        se = ttk.Entry(sf, textvariable=sv, width=30)
        se.pack(side=tk.LEFT, padx=4)
        ttk.Button(sf, text="搜索", command=lambda: self._od_search(source_key, sv.get())).pack(side=tk.LEFT, padx=2)
        se.bind("<Return>", lambda e: self._od_search(source_key, sv.get()))
        cols = ("name", "size", "modified")
        tree = ttk.Treeview(p, columns=cols, show="headings", selectmode="extended")
        tree.heading("name", text="名称")
        tree.heading("size", text="大小")
        tree.heading("modified", text="修改时间")
        tree.column("name", width=380)
        tree.column("size", width=100, anchor=tk.E)
        tree.column("modified", width=180)
        tree.pack(fill=tk.BOTH, expand=True, padx=16, pady=4)
        sb_tree = ttk.Scrollbar(tree, orient=tk.VERTICAL, command=tree.yview)
        tree.configure(yscrollcommand=sb_tree.set)
        sb_tree.pack(side=tk.RIGHT, fill=tk.Y)
        tree.bind("<Double-Button-1>", lambda e: self._od_on_item_double_click(source_key))
        bf = ttk.Frame(p)
        bf.pack(fill=tk.X, padx=16, pady=(4, 8))
        ttk.Button(bf, text="⬇️ 下载选中", command=lambda: self._od_download_selected(source_key)).pack(side=tk.LEFT, padx=2)
        ttk.Button(bf, text="📥 下载全部", command=lambda: self._od_download_all(source_key)).pack(side=tk.LEFT, padx=2)
        self._od_progress = ttk.Progressbar(bf, length=200, mode="determinate")
        self._od_progress.pack(side=tk.LEFT, padx=10)
        self._od_status_label = ttk.Label(bf, text="")
        self._od_status_label.pack(side=tk.LEFT, padx=4)
        pk = f"onedrive_{source_key}"
        self._od_pages[pk] = {
            "tree": tree, "search_var": sv, "source_key": source_key,
            "share_url": share_url, "items": [], "loaded": False,
            "progress": self._od_progress, "status": self._od_status_label,
        }
        return p

    def _od_lazy_load_if_needed(self, pk, force=False):
        od = self._od_pages.get(pk)
        if not od:
            return
        if od["loaded"] and not force:
            return
        if not self.od_browser:
            od["status"].config(text="离线模式不可用")
            return
        od["status"].config(text="正在加载文件列表...")
        def do():
            try:
                result = self.od_browser.list_folder(od["share_url"])
                self.after(0, lambda: self._od_on_list_loaded(pk, result))
            except Exception as e:
                self.after(0, lambda: od["status"].config(text=f"加载失败: {e}"))
        threading.Thread(target=do, daemon=True).start()

    def _od_on_list_loaded(self, pk, result):
        od = self._od_pages.get(pk)
        if not od:
            return
        tree = od["tree"]
        tree.delete(*tree.get_children())
        if not result.get("success"):
            od["status"].config(text=f"加载失败: {result.get('error', '未知错误')}")
            return
        items = result.get("items", [])
        # 记录当前 next_link 以便刷新
        od["current_next_link"] = result.get("next_link") or od.get("current_next_link", "")
        od["items"] = items
        od["loaded"] = True
        # 配置颜色 tag
        try:
            tree.tag_configure("folder", foreground="#0078d4")
            tree.tag_configure("file", foreground="#2c3e50")
        except Exception:
            pass
        for item in items:
            is_folder = item.get("is_folder", False)
            icon = item.get("icon", "📁" if is_folder else "📄")
            name = item.get("name", "?")
            if is_folder:
                display = f"[文件夹] {icon} {name}"
                tag = "folder"
                size_str = "—"
                mod_str = item.get("last_modified", "")[:19] or item.get("lastModifiedDateTime", "")[:19]
            else:
                display = f"[文件] {icon} {name}"
                tag = "file"
                size_val = item.get("size", 0)
                size_str = _format_size(size_val) if size_val else (item.get("size_display") or "—")
                mod_str = item.get("last_modified", "")[:19] or item.get("lastModifiedDateTime", "")[:19]
            tree.insert("", tk.END, values=(display, size_str, mod_str), tags=(tag,))
        od["status"].config(text=f"共 {len(items)} 项 (文件夹/文件) — 双击文件夹进入,双击文件下载")

    def _od_search(self, source_key, query):
        pk = f"onedrive_{source_key}"
        od = self._od_pages.get(pk)
        if not od or not self.od_browser:
            return
        if not query.strip():
            self._od_lazy_load_if_needed(pk, force=True)
            return
        od["status"].config(text=f"搜索: {query}...")
        def do():
            try:
                result = self.od_browser.search_folder(od["share_url"], query)
                self.after(0, lambda: self._od_on_list_loaded(pk, result))
            except Exception as e:
                self.after(0, lambda: od["status"].config(text=f"搜索失败: {e}"))
        threading.Thread(target=do, daemon=True).start()

    def _od_on_item_double_click(self, source_key):
        pk = f"onedrive_{source_key}"
        od = self._od_pages.get(pk)
        if not od:
            return
        tree = od["tree"]
        sel = tree.selection()
        if not sel:
            return
        idx = tree.index(sel[0])
        items = od["items"]
        if not (0 <= idx < len(items)):
            return
        item = items[idx]
        # 文件夹双击 → 进入子文件夹
        if item.get("is_folder"):
            next_link = item.get("next_link", "")
            if not next_link:
                od["status"].config(text="无法进入该文件夹(无导航信息)")
                return
            od["status"].config(text=f"正在打开文件夹: {item.get('name','')}...")
            od["loaded"] = False

            def do():
                try:
                    result = self.od_browser.list_folder(od["share_url"], next_link=next_link)
                    self.after(0, lambda: self._od_on_list_loaded(pk, result))
                except Exception as e:
                    self.after(0, lambda: od["status"].config(text=f"打开文件夹失败: {e}"))
            threading.Thread(target=do, daemon=True).start()
            return
        # 文件双击 → 直接下载
        self._od_download_items(source_key, [idx])

    def _od_refresh_current(self, source_key):
        """刷新当前 OneDrive 列表(重新加载当前文件夹)。"""
        pk = f"onedrive_{source_key}"
        od = self._od_pages.get(pk)
        if not od:
            return
        next_link = od.get("current_next_link")
        if next_link:
            def do():
                try:
                    result = self.od_browser.list_folder(od["share_url"], next_link=next_link)
                    self.after(0, lambda: self._od_on_list_loaded(pk, result))
                except Exception as e:
                    self.after(0, lambda: od["status"].config(text=f"刷新失败: {e}"))
            threading.Thread(target=do, daemon=True).start()
        else:
            self._od_lazy_load_if_needed(pk, force=True)

    def _od_download_selected(self, source_key):
        pk = f"onedrive_{source_key}"
        od = self._od_pages.get(pk)
        if not od:
            return
        tree = od["tree"]
        sel = tree.selection()
        if not sel:
            messagebox.showwarning("警告", "请先选择要下载的文件")
            return
        indices = [tree.index(s) for s in sel]
        self._od_download_items(source_key, indices)

    def _od_download_all(self, source_key):
        pk = f"onedrive_{source_key}"
        od = self._od_pages.get(pk)
        if not od or not od["items"]:
            return
        if not messagebox.askyesno("确认", f"确定要下载全部 {len(od['items'])} 个文件吗？"):
            return
        indices = list(range(len(od["items"])))
        self._od_download_items(source_key, indices)

    def _od_download_items(self, source_key, indices):
        """下载选中的 OneDrive 资源 — 全部保存到 packages/ 目录,带完整性校验。

        流程:
          1. 后台线程逐个下载
          2. 下载后校验文件大小(>0 字节)
          3. 完成后自动刷新"包管理"页面
          4. 显示已导入的包列表
        """
        pk = f"onedrive_{source_key}"
        od = self._od_pages.get(pk)
        if not od or not self.od_browser:
            return
        items = od["items"]
        to_dl = [(i, items[i]) for i in indices if i < len(items)]
        if not to_dl:
            return
        # 过滤:跳过文件夹(只有文件可以下载)
        file_dl = []
        for i_idx, item in to_dl:
            if not item.get("is_folder"):
                file_dl.append((i_idx, item))
        if not file_dl:
            messagebox.showinfo("提示", "选中的都是文件夹,无法直接下载", parent=self)
            return
        to_dl = file_dl
        # 全部游戏资源统一保存到 packages/
        dest_dir = self.get_package_dir(source_key)
        os.makedirs(dest_dir, exist_ok=True)
        total = len(to_dl)
        od["progress"]["maximum"] = total
        od["progress"]["value"] = 0
        success_list = []   # 成功的文件名
        fail_list = []      # 失败/损坏
        success_count = [0]
        fail_count = [0]

        def dl_next(idx):
            if idx >= total:
                # 完成
                msg = f"下载完成: {success_count[0]} 成功"
                if fail_count[0] > 0:
                    msg += f", {fail_count[0]} 失败"
                self._set_status(msg)
                od["status"].config(text=msg)
                # 自动刷新包管理页面
                if success_list:
                    self._refresh_all_package_tabs()
                    self._show_imported_packages(success_list, dest_dir)
                if fail_list:
                    self._set_status(f"⚠ 部分文件下载失败: {', '.join(fail_list[:3])}")
                return
            i_idx, item = to_dl[idx]
            name = item.get("name", f"file_{i_idx}")
            dl_url = item.get("@microsoft.graph.downloadUrl") or item.get("download_url")
            if not dl_url:
                fail_count[0] += 1
                fail_list.append(name)
                od["progress"]["value"] = idx + 1
                self.after(0, lambda: dl_next(idx + 1))
                return
            dest = os.path.join(dest_dir, name)
            share_url_for_dl = od.get("share_url", "")
            expected_size = item.get("size", 0) or 0
            def do_dl():
                ok = False
                err_reason = ""
                try:
                    ok = self.od_browser.download_file(dl_url, dest, share_url=share_url_for_dl)
                except Exception as e:
                    ok = False
                    err_reason = str(e)
                # 完整性校验
                integrity_ok = False
                actual_size = 0
                if ok and os.path.isfile(dest):
                    try:
                        actual_size = os.path.getsize(dest)
                        if actual_size > 0:
                            if expected_size > 0 and actual_size < expected_size * 0.9:
                                # 大小不匹配(< 期望的 90%) 视为损坏
                                err_reason = f"文件大小不匹配(实际 {actual_size} < 期望 {expected_size})"
                                try:
                                    os.remove(dest)
                                except Exception:
                                    pass
                            else:
                                integrity_ok = True
                        else:
                            err_reason = "下载的文件为 0 字节"
                            try:
                                os.remove(dest)
                            except Exception:
                                pass
                    except OSError as e:
                        err_reason = f"无法读取文件大小: {e}"
                if ok and integrity_ok:
                    success_count[0] += 1
                    success_list.append(name)
                else:
                    fail_count[0] += 1
                    if not err_reason:
                        err_reason = "下载失败"
                    fail_list.append(f"{name} ({err_reason})")
                self.after(0, lambda i=idx: (
                    od["progress"].configure(value=i + 1),
                    od["status"].configure(text=f"下载中 ({i+1}/{total}): {name}"),
                    dl_next(i + 1),
                ))
            threading.Thread(target=do_dl, daemon=True).start()
        dl_next(0)

    def _show_imported_packages(self, imported_files, dest_dir):
        """显示已导入的包列表对话框。"""
        try:
            dlg = tk.Toplevel(self)
            dlg.title("📦 已导入的包")
            dlg.geometry("520x360")
            dlg.transient(self)
            try:
                dlg.grab_set()
            except Exception:
                pass
            mf = ttk.Frame(dlg, padding=14)
            mf.pack(fill=tk.BOTH, expand=True)
            ttk.Label(mf, text="📦 已导入的包",
                       font=("Microsoft YaHei UI", 12, "bold")).pack(anchor=tk.W)
            ttk.Label(mf, text=f"保存位置: {dest_dir}",
                       font=("Microsoft YaHei UI", 9),
                       foreground=LIGHT.get("text_secondary", "#666")).pack(anchor=tk.W, pady=(2, 8))
            ttk.Separator(mf, orient=tk.HORIZONTAL).pack(fill=tk.X, pady=(0, 6))
            ttk.Label(mf, text=f"已成功导入 {len(imported_files)} 个文件:",
                       font=("Microsoft YaHei UI", 10, "bold")).pack(anchor=tk.W, pady=(0, 4))
            list_frame = ttk.Frame(mf)
            list_frame.pack(fill=tk.BOTH, expand=True)
            sb = ttk.Scrollbar(list_frame, orient=tk.VERTICAL)
            sb.pack(side=tk.RIGHT, fill=tk.Y)
            lb = tk.Listbox(list_frame, font=("Microsoft YaHei UI", 9),
                             yscrollcommand=sb.set)
            sb.config(command=lb.yview)
            for f in imported_files:
                lb.insert(tk.END, f"  ✅ {f}")
            lb.pack(fill=tk.BOTH, expand=True)
            btn_row = ttk.Frame(mf)
            btn_row.pack(fill=tk.X, pady=(8, 0))
            ttk.Button(btn_row, text="📁 打开目录",
                        command=lambda: self._open_path(dest_dir)).pack(side=tk.LEFT, padx=2)
            ttk.Button(btn_row, text="📦 跳转到包管理",
                        command=lambda: (dlg.destroy(), self._nav_package())).pack(side=tk.LEFT, padx=2)
            ttk.Button(btn_row, text="关闭", command=dlg.destroy).pack(side=tk.RIGHT, padx=2)
            self._center_toplevel(dlg)
        except Exception as e:
            log_warn("App", f"显示已导入包对话框失败: {e}")

    def _open_path(self, path):
        try:
            if os.name == "nt":
                os.startfile(path)
            else:
                import subprocess
                subprocess.Popen(["xdg-open", path])
        except Exception as e:
            messagebox.showerror("错误", str(e), parent=self)

    def _center_toplevel(self, dlg):
        try:
            dlg.update_idletasks()
            pw, ph = self.winfo_width(), self.winfo_height()
            px, py = self.winfo_rootx(), self.winfo_rooty()
            w, h = dlg.winfo_width(), dlg.winfo_height()
            x = px + max(0, (pw - w) // 2)
            y = py + max(0, (ph - h) // 2)
            dlg.geometry(f"+{x}+{y}")
        except Exception:
            pass

    # ========== DLC page ==========
    def _dlc_folder_path(self):
        return os.path.join(self.base_path, "DLC")

    def _build_dlc_page(self, parent):
        p = ttk.Frame(parent)
        h = ttk.Frame(p)
        h.pack(fill=tk.X, padx=16, pady=(12, 8))
        ttk.Label(h, text="🧩 程序DLC", style="Title.TLabel").pack(side=tk.LEFT)
        ttk.Button(h, text="🔄 刷新列表", command=self._dlc_refresh).pack(side=tk.RIGHT, padx=2)
        ttk.Button(h, text="📂 打开 DLC 文件夹", command=self._dlc_open_folder).pack(side=tk.RIGHT, padx=2)
        ttk.Button(h, text="📋 查看 DLC.json 规范", command=self._show_dlc_json_spec).pack(side=tk.RIGHT, padx=2)
        ttk.Separator(p, orient=tk.HORIZONTAL).pack(fill=tk.X, padx=16)
        desc = ttk.Label(p, text="将含 DLC.json 的文件夹放入程序根目录下 DLC 文件夹中，自动加载。")
        desc.pack(anchor=tk.W, padx=16, pady=(4, 0))
        main_f = ttk.Frame(p)
        main_f.pack(fill=tk.BOTH, expand=True, padx=16, pady=4)
        left_f = tk.Frame(main_f, bg=self.theme["surface"], highlightbackground=self.theme["border"], highlightthickness=1, width=220)
        left_f.pack(side=tk.LEFT, fill=tk.Y)
        left_f.pack_propagate(False)
        tk.Label(left_f, text="📑 目录", font=("Microsoft YaHei UI", 11, "bold"), bg=self.theme["surface"], fg=self.theme["text"]).pack(anchor=tk.W, padx=8, pady=(8, 4))
        self._dlc_toc_list = tk.Listbox(left_f, font=("Microsoft YaHei UI", 10), bg=self.theme["surface"], fg=self.theme["text"], selectbackground=self.theme["selection"], relief=tk.FLAT, borderwidth=0, highlightthickness=0)
        self._dlc_toc_list.pack(fill=tk.BOTH, expand=True, padx=4, pady=4)
        self._dlc_toc_list.bind("<<ListboxSelect>>", self._dlc_toc_navigate)
        right_f = ttk.Frame(main_f)
        right_f.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        self._dlc_canvas = tk.Canvas(right_f, bg=self.theme["bg"], highlightthickness=0)
        self._dlc_scrollbar = ttk.Scrollbar(right_f, orient=tk.VERTICAL, command=self._dlc_canvas.yview)
        self._dlc_canvas.configure(yscrollcommand=self._dlc_scrollbar.set)
        self._dlc_scrollbar.pack(side=tk.RIGHT, fill=tk.Y)
        self._dlc_canvas.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        self._dlc_content_frame = ttk.Frame(self._dlc_canvas)
        self._dlc_canvas.create_window((0, 0), window=self._dlc_content_frame, anchor=tk.NW)
        self._dlc_content_frame.bind("<Configure>", lambda e: self._dlc_canvas.configure(scrollregion=self._dlc_canvas.bbox("all")))
        self._dlc_status_label = ttk.Label(p, text="")
        self._dlc_status_label.pack(anchor=tk.W, padx=16, pady=(0, 4))
        self._dlc_items = []
        self.after(100, self._dlc_refresh)
        return p

    def _dlc_log(self, msg, level="INFO"):
        level_map = {"INFO": log_info, "WARN": log_warn, "ERROR": log_error}
        fn = level_map.get(level, log_info)
        fn("DLC", msg)

    def _dlc_refresh(self):
        for w in self._dlc_content_frame.winfo_children():
            w.destroy()
        self._dlc_toc_list.delete(0, tk.END)
        dlc_dir = self._dlc_folder_path()
        if not os.path.isdir(dlc_dir):
            try:
                os.makedirs(dlc_dir, exist_ok=True)
            except Exception as e:
                self._dlc_status_label.config(text=f"⚠️ 无法创建 DLC 文件夹: {e}")
                return
        self._dlc_items = self._scan_dlc_folder(dlc_dir)
        if not self._dlc_items:
            tk.Label(self._dlc_content_frame, text="📭 暂无 DLC 项目\n\n将包含 DLC.json 的文件夹放入 DLC 目录即可自动加载(下载DLC包后须重启程序)", font=("Microsoft YaHei UI", 11), fg="#999", bg=self.theme["bg"]).pack(pady=40)
            self._dlc_status_label.config(text="未检测到 DLC 项目")
        else:
            ok_count = sum(1 for d in self._dlc_items if d.get("status") == "ok")
            err_count = len(self._dlc_items) - ok_count
            for i, dlc in enumerate(self._dlc_items):
                self._make_dlc_card(dlc, i)
                status_icon = "✅" if dlc.get("status") == "ok" else "⚠️"
                self._dlc_toc_list.insert(tk.END, f"{status_icon}  {dlc['name']}")
            if self._dlc_toc_list.size() > 0:
                self._dlc_toc_list.selection_set(0)
            status_text = f"共 {len(self._dlc_items)} 个 DLC 项目"
            if ok_count:
                status_text += f"  ✅ {ok_count} 个可用"
            if err_count:
                status_text += f"  ⚠️ {err_count} 个异常"
            self._dlc_status_label.config(text=status_text)

    def _dlc_toc_navigate(self, event=None):
        sel = self._dlc_toc_list.curselection()
        if not sel:
            return
        idx = sel[0]
        children = self._dlc_content_frame.winfo_children()
        if 0 <= idx < len(children):
            children[idx].tkraise()
            self._dlc_canvas.yview_moveto(0)

    def _scan_dlc_folder(self, dlc_dir):
        items = []
        try:
            for entry in sorted(os.listdir(dlc_dir)):
                entry_path = os.path.join(dlc_dir, entry)
                if not os.path.isdir(entry_path):
                    continue
                dlc_json_path = os.path.join(entry_path, "DLC.json")
                if not os.path.isfile(dlc_json_path):
                    items.append({
                        "name": entry, "version": "—", "description": "", "author": "",
                        "contact": "", "main": "", "folder": entry_path, "status": "no_config",
                        "status_text": "⚠️ 缺少 DLC.json",
                        "error_detail": f"文件夹内未找到 DLC.json 配置文件。\n请在 {entry_path} 中创建 DLC.json",
                    })
                    continue
                result = self._parse_dlc_json(dlc_json_path, entry_path, entry)
                items.append(result)
        except PermissionError as e:
            log_error("DLC", f"访问 DLC 目录权限不足: {e}")
        return items

    def _parse_dlc_json(self, json_path, folder_path, folder_name):
        try:
            with open(json_path, "r", encoding="utf-8") as f:
                cfg = json.load(f)
            name = cfg.get("name", folder_name)
            version = cfg.get("version", "—")
            desc = cfg.get("description", "")
            author = cfg.get("author", "")
            contact = cfg.get("contact", "")
            main_file = cfg.get("main", "")
            main_path = os.path.join(folder_path, main_file) if main_file else ""
            has_main = os.path.isfile(main_path) if main_path else False
            return {
                "name": name, "version": version, "description": desc,
                "author": author, "contact": contact, "main": main_file,
                "folder": folder_path, "status": "ok" if (name and version and main_file and has_main) else "incomplete",
                "status_text": "✅ 可用" if (name and version and main_file and has_main) else "⚠️ 配置不完整",
                "has_main": has_main, "main_path": main_path,
            }
        except (json.JSONDecodeError, OSError) as e:
            log_error("DLC", f"解析 {json_path} 失败: {e}")
            return {
                "name": folder_name, "version": "—", "description": "", "author": "",
                "contact": "", "main": "", "folder": folder_path, "status": "parse_error",
                "status_text": "❌ DLC.json 解析失败",
                "error_detail": f"DLC.json 文件格式错误: {e}",
            }

    def _make_dlc_card(self, dlc, idx):
        t = self.theme
        card = tk.Frame(self._dlc_content_frame, bg=t["surface"], highlightbackground=t["border"], highlightthickness=1)
        card.pack(fill=tk.X, padx=4, pady=4)
        hdr = tk.Frame(card, bg=t["surface"])
        hdr.pack(fill=tk.X, padx=12, pady=(8, 4))
        tk.Label(hdr, text=f"{dlc['name']}  v{dlc['version']}", font=("Microsoft YaHei UI", 12, "bold"), bg=t["surface"], fg=t["text"]).pack(side=tk.LEFT)
        tk.Label(hdr, text=dlc.get("status_text", ""), bg=t["surface"], fg=t["text_secondary"]).pack(side=tk.RIGHT)
        if dlc.get("description"):
            tk.Label(card, text=dlc["description"], bg=t["surface"], fg=t["text_secondary"], font=("Microsoft YaHei UI", 9), wraplength=600, justify=tk.LEFT).pack(anchor=tk.W, padx=12, pady=(0, 4))
        if dlc.get("author"):
            tk.Label(card, text=f"作者: {dlc['author']}  联系方式: {dlc.get('contact', '—')}", bg=t["surface"], fg=t["text_secondary"], font=("Microsoft YaHei UI", 8)).pack(anchor=tk.W, padx=12, pady=(0, 4))
        bf = tk.Frame(card, bg=t["surface"])
        bf.pack(fill=tk.X, padx=12, pady=(0, 8))
        if dlc.get("status") == "ok" and dlc.get("main"):
            ttk.Button(bf, text="🚀 启动", command=lambda d=dlc: self._dlc_launch(d)).pack(side=tk.LEFT, padx=2)
        ttk.Button(bf, text="📂 打开目录", command=lambda d=dlc: self._dlc_open_dlc_folder(d)).pack(side=tk.LEFT, padx=2)

    def _dlc_launch(self, dlc):
        mp = dlc.get("main_path")
        if mp and os.path.isfile(mp):
            try:
                subprocess.Popen(f'"{mp}"', shell=True, cwd=os.path.dirname(mp))
                self._set_status(f"DLC 已启动: {dlc['name']}")
            except Exception as e:
                messagebox.showerror("错误", f"启动失败: {e}")
        else:
            messagebox.showerror("错误", "DLC 主程序未找到")

    def _dlc_open_dlc_folder(self, dlc):
        folder = dlc.get("folder")
        if folder and os.path.isdir(folder):
            try:
                os.startfile(folder)
            except Exception as e:
                messagebox.showerror("错误", str(e))

    def _dlc_open_folder(self):
        dlc_dir = self._dlc_folder_path()
        os.makedirs(dlc_dir, exist_ok=True)
        try:
            os.startfile(dlc_dir)
        except Exception as e:
            messagebox.showerror("错误", str(e))

    def _show_dlc_json_spec(self):
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
            '  "main"        :  主程序路径（字符串，相对于 DLC 文件夹的 .exe 路径）\n'
        )
        messagebox.showinfo("DLC.json 规范", spec)

    def _dlc_scan_pending_archives(self):
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

    def _dlc_extract_after_download(self, archive_path, dlc_dir=None, silent=False):
        if dlc_dir is None:
            dlc_dir = self._dlc_folder_path()
        name = os.path.basename(archive_path)
        lower = name.lower()
        self._dlc_log(f"========== 开始处理: {name} ==========")
        if not (lower.endswith(".zip") or lower.endswith(".7z") or lower.endswith(".rar")):
            self._dlc_log(f"{name} 不是压缩包格式，跳过自动处理", "WARN")
            return False
        base_name = os.path.splitext(name)[0]
        if base_name.lower().endswith(".tar"):
            base_name = os.path.splitext(base_name)[0]
        extract_to = os.path.join(dlc_dir, base_name)
        counter = 1
        while os.path.exists(extract_to):
            extract_to = os.path.join(dlc_dir, f"{base_name}_{counter}")
            counter += 1
        try:
            self._dlc_log(f"正在解压到: {extract_to}")
            os.makedirs(extract_to, exist_ok=True)
            self._extract_archive(archive_path, extract_to)
            self._dlc_log(f"解压完成: {extract_to}")
            try:
                os.remove(archive_path)
                self._dlc_log(f"已删除压缩包: {name}")
            except Exception as e:
                self._dlc_log(f"删除压缩包失败: {e}", "WARN")
            self._dlc_refresh()
            return True
        except Exception as e:
            self._dlc_log(f"处理失败: {e}", "ERROR")
            if not silent:
                messagebox.showerror("错误", f"处理 {name} 失败: {e}")
            return False

    # ========== settings page ==========
    def _build_settings_page(self, parent):
        """设置页面 — 整合教程/反馈/关于/日志功能。"""
        p = ttk.Frame(parent)
        h = ttk.Frame(p)
        h.pack(fill=tk.X, padx=16, pady=(12, 8))
        ttk.Label(h, text="⚙️ 设置", style="Title.TLabel").pack(side=tk.LEFT)
        ttk.Separator(p, orient=tk.HORIZONTAL).pack(fill=tk.X, padx=16)

        # 可滚动容器
        outer = ttk.Frame(p)
        outer.pack(fill=tk.BOTH, expand=True, padx=16, pady=8)
        canvas = tk.Canvas(outer, highlightthickness=0, bd=0,
                            bg=self.theme.get("bg", "#ffffff"))
        vsb = ttk.Scrollbar(outer, orient="vertical", command=canvas.yview)
        canvas.configure(yscrollcommand=vsb.set)
        vsb.pack(side=tk.RIGHT, fill=tk.Y)
        canvas.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        cf = ttk.Frame(canvas)
        cf_window = canvas.create_window((0, 0), window=cf, anchor="nw")
        canvas.bind("<Configure>", lambda e: canvas.itemconfig(cf_window, width=e.width))
        cf.bind("<Configure>", lambda e: canvas.configure(scrollregion=canvas.bbox("all")))

        # === 教程卡片 ===
        self._settings_tutorial_card(cf)

        # === 反馈卡片 ===
        self._settings_feedback_card(cf)

        # === 关于卡片 ===
        self._settings_about_card(cf)

        # === 日志查看器卡片 ===
        self._settings_log_card(cf)

        return p

    def _settings_tutorial_card(self, parent):
        """教程卡片。"""
        card = ttk.LabelFrame(parent, text="📖 使用教程", padding=12)
        card.pack(fill=tk.X, pady=(0, 12), anchor=tk.N)
        lines = [
            "1️⃣ 首次使用请先在「实例管理」中添加游戏目录",
            "2️⃣ 在「包管理」中选择要安装的 MOD/地图等",
            "3️⃣ 通过 OneDrive 页面下载资源文件",
            "4️⃣ 在「程序DLC」页面管理游戏扩展组件",
            "5️⃣ 选择实例后点击主页的「启动游戏」按钮",
            "",
            "📌 提示:",
            "  • 备份功能可在「实例管理」中找到",
            "  • 如需上传资源,请联系管理员获取权限",
            "  • 遇到问题请通过下方「反馈」区联系我们",
        ]
        for line in lines:
            ttk.Label(card, text=line, wraplength=700, justify=tk.LEFT).pack(anchor=tk.W, pady=1)

    def _settings_feedback_card(self, parent):
        """反馈卡片 — 包含 QQ 群 / QQ 频道 / GitHub 联系方式。"""
        card = ttk.LabelFrame(parent, text="💬 反馈与建议", padding=12)
        card.pack(fill=tk.X, pady=(0, 12), anchor=tk.N)
        ttk.Label(card,
                  text="感谢您使用 Hello Mental Omega Launcher!\n如有问题或建议,请通过以下方式联系我们:",
                  wraplength=700, justify=tk.LEFT).pack(anchor=tk.W, pady=(0, 8))

        # QQ 群
        row1 = ttk.Frame(card)
        row1.pack(fill=tk.X, pady=2, anchor=tk.W)
        ttk.Label(row1, text="🏠 官方 QQ 群:", font=("Microsoft YaHei UI", 10, "bold")).pack(side=tk.LEFT)
        qq = ttk.Label(row1, text="1034243331",
                       foreground="#3498db",
                       font=("Consolas", 10, "bold"))
        qq.pack(side=tk.LEFT, padx=6)
        ttk.Button(row1, text="📋 复制", width=8,
                   command=lambda: self._copy_to_clipboard("1034243331")).pack(side=tk.LEFT, padx=4)
        ttk.Button(row1, text="💬 一键加群", width=10,
                   command=lambda: webbrowser.open("https://qm.qq.com/q/E1YzVGzxjU")).pack(side=tk.LEFT)

        # QQ 频道
        row2 = ttk.Frame(card)
        row2.pack(fill=tk.X, pady=2, anchor=tk.W)
        ttk.Label(row2, text="📡 QQ 频道:", font=("Microsoft YaHei UI", 10, "bold")).pack(side=tk.LEFT)
        ch = ttk.Label(row2, text="pd07649139",
                       foreground="#3498db",
                       font=("Consolas", 10, "bold"))
        ch.pack(side=tk.LEFT, padx=6)
        ttk.Button(row2, text="📋 复制", width=8,
                   command=lambda: self._copy_to_clipboard("pd07649139")).pack(side=tk.LEFT, padx=4)
        ttk.Button(row2, text="📡 加入频道", width=10,
                   command=lambda: webbrowser.open("https://pd.qq.com/s/a6yl81qi5")).pack(side=tk.LEFT)

        # GitHub
        row3 = ttk.Frame(card)
        row3.pack(fill=tk.X, pady=2, anchor=tk.W)
        ttk.Label(row3, text="🐙 GitHub:", font=("Microsoft YaHei UI", 10, "bold")).pack(side=tk.LEFT)
        gh = ttk.Label(row3,
                       text="OrangeArtc0915/Hello-Mental-Omega-Launcher",
                       foreground="#3498db")
        gh.pack(side=tk.LEFT, padx=6)
        ttk.Button(row3, text="🌐 打开", width=8,
                   command=lambda: webbrowser.open("https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher")).pack(side=tk.LEFT)

        ttk.Separator(card, orient=tk.HORIZONTAL).pack(fill=tk.X, pady=(8, 6))
        ttk.Label(card,
                  text="📋 提交反馈时请附上:",
                  font=("Microsoft YaHei UI", 9, "bold")).pack(anchor=tk.W)
        for txt in [f"  • 问题描述", f"  • 操作步骤", f"  • 截图(如有)", f"  • 程序版本号 (v{get_app_version()})"]:
            ttk.Label(card, text=txt).pack(anchor=tk.W)

    def _settings_about_card(self, parent):
        """关于卡片。"""
        card = ttk.LabelFrame(parent, text="ℹ️ 关于 HMOL", padding=12)
        card.pack(fill=tk.X, pady=(0, 12), anchor=tk.N)
        ttk.Label(card, text="🎮 Hello Mental Omega Launcher",
                  font=("Microsoft YaHei UI", 13, "bold")).pack(anchor=tk.W, pady=(0, 4))
        ttk.Label(card, text=f"版本: v{get_app_version()}",
                  font=("Microsoft YaHei UI", 10)).pack(anchor=tk.W)
        ttk.Label(card, text="作者: mmm",
                  font=("Microsoft YaHei UI", 10)).pack(anchor=tk.W, pady=(0, 8))

        ttk.Separator(card, orient=tk.HORIZONTAL).pack(fill=tk.X, pady=(4, 8))

        ttk.Label(card, text="功能特性:",
                  font=("Microsoft YaHei UI", 10, "bold")).pack(anchor=tk.W, pady=(0, 4))
        for feat in [
            "✓ 游戏实例管理 (多版本共存)",
            "✓ MOD/地图/任务/语音包管理",
            "✓ OneDrive 云资源下载",
            "✓ 程序 DLC 扩展管理",
            "✓ 实例备份与还原",
            "✓ 微软账号登录与好友系统",
        ]:
            ttk.Label(card, text=feat, foreground="#27ae60").pack(anchor=tk.W, pady=1)

        ttk.Separator(card, orient=tk.HORIZONTAL).pack(fill=tk.X, pady=(8, 8))
        ttk.Label(card, text="🎮 专为 Mental Omega 玩家打造",
                  font=("Microsoft YaHei UI", 10, "bold")).pack(anchor=tk.W)
        ttk.Label(card, text="❤️ 感谢所有贡献者和社区支持!",
                  font=("Microsoft YaHei UI", 10)).pack(anchor=tk.W, pady=(2, 0))

        ttk.Separator(card, orient=tk.HORIZONTAL).pack(fill=tk.X, pady=(8, 8))

        # ── 法律与协议 ──
        ttk.Label(card, text="📜 法律与协议:",
                  font=("Microsoft YaHei UI", 10, "bold")).pack(anchor=tk.W, pady=(0, 6))

        legal_row = ttk.Frame(card)
        legal_row.pack(fill=tk.X, pady=(0, 4))
        ttk.Button(
            legal_row,
            text="📖 查看使用协议",
            width=18,
            command=self._show_eula_viewer,
        ).pack(side=tk.LEFT, padx=(0, 6))
        ttk.Button(
            legal_row,
            text="🔗 反馈 / GitHub Issues",
            width=22,
            command=lambda: self._open_url(
                "https://github.com/OrangeArtc0915/"
                "Hello-Mental-Omega-Launcher/issues"
            ),
        ).pack(side=tk.LEFT)

        # 显示当前协议版本与接受状态
        eula_ver = str(self.config.get("eula_accepted_version", "") or "未接受")
        status_text = (
            f"已同意的协议版本:v{eula_ver}"
            if self.config.get("eula_accepted", False)
            else "⚠️ 尚未接受使用协议"
        )
        status_color = "#27ae60" if self.config.get("eula_accepted", False) else "#e67e22"
        ttk.Label(
            card,
            text=status_text,
            font=("Microsoft YaHei UI", 9),
            foreground=status_color,
        ).pack(anchor=tk.W, pady=(4, 0))

    def _show_eula_viewer(self):
        """打开使用协议查看器(只读)。"""
        try:
            viewer = EULAViewerDialog(self)
            self.wait_window(viewer)
        except Exception as e:
            try:
                messagebox.showerror("错误", f"无法打开使用协议查看器:\n{e}")
            except Exception:
                pass

    def _open_url(self, url: str) -> None:
        """在系统默认浏览器中打开 URL。"""
        try:
            webbrowser.open(url)
        except Exception:
            pass

    def _settings_log_card(self, parent):
        """日志查看器卡片 — 从 Qt 版本移植。"""
        card = ttk.LabelFrame(parent, text="📋 程序日志", padding=12)
        card.pack(fill=tk.BOTH, expand=True, pady=(0, 12), anchor=tk.N)

        # 标题行 + 按钮
        title_row = ttk.Frame(card)
        title_row.pack(fill=tk.X, pady=(0, 8))
        ttk.Label(title_row, text="查看/导出/清理程序运行日志",
                  foreground="#666").pack(side=tk.LEFT)
        ttk.Button(title_row, text="⬇ TXT", width=8,
                   command=self._log_export_txt).pack(side=tk.RIGHT, padx=(4, 0))
        ttk.Button(title_row, text="⬇ CSV", width=8,
                   command=self._log_export_csv).pack(side=tk.RIGHT, padx=(4, 0))
        ttk.Button(title_row, text="🧹 清理旧日志", width=12,
                   command=self._log_cleanup).pack(side=tk.RIGHT, padx=(4, 0))

        # 筛选栏
        filter_row = ttk.Frame(card)
        filter_row.pack(fill=tk.X, pady=(0, 6))
        ttk.Label(filter_row, text="级别:").pack(side=tk.LEFT)
        self._log_level_var = tk.StringVar(value="全部")
        self._log_level_combo = ttk.Combobox(
            filter_row, textvariable=self._log_level_var,
            values=["全部", "INFO", "WARN", "ERROR", "DEBUG"],
            state="readonly", width=8)
        self._log_level_combo.pack(side=tk.LEFT, padx=(4, 8))
        self._log_level_combo.bind("<<ComboboxSelected>>", lambda e: self._log_refresh_view())

        ttk.Label(filter_row, text="搜索:").pack(side=tk.LEFT)
        self._log_keyword_var = tk.StringVar()
        self._log_keyword_entry = ttk.Entry(filter_row, textvariable=self._log_keyword_var, width=18)
        self._log_keyword_entry.pack(side=tk.LEFT, padx=4)
        self._log_keyword_var.trace_add("write", lambda *a: self._log_refresh_view())

        ttk.Label(filter_row, text="日期:").pack(side=tk.LEFT, padx=(8, 0))
        self._log_date_from_var = tk.StringVar()
        ttk.Entry(filter_row, textvariable=self._log_date_from_var, width=12).pack(side=tk.LEFT, padx=4)
        ttk.Label(filter_row, text="→").pack(side=tk.LEFT)
        self._log_date_to_var = tk.StringVar()
        ttk.Entry(filter_row, textvariable=self._log_date_to_var, width=12).pack(side=tk.LEFT, padx=4)

        # 刷新按钮
        ttk.Button(filter_row, text="🔄 刷新",
                   command=self._log_refresh_view).pack(side=tk.RIGHT)

        # 日志显示区
        log_frame = ttk.Frame(card)
        log_frame.pack(fill=tk.BOTH, expand=True, pady=(4, 0))
        log_scroll = ttk.Scrollbar(log_frame)
        log_scroll.pack(side=tk.RIGHT, fill=tk.Y)
        self._log_text = tk.Text(
            log_frame, height=12, wrap=tk.NONE, state=tk.DISABLED,
            font=("Consolas", 9), bg="#1e1e2e", fg="#cdd6f4",
            yscrollcommand=log_scroll.set, insertbackground="#cdd6f4")
        self._log_text.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        log_scroll.config(command=self._log_text.yview)
        # 颜色 tag
        self._log_text.tag_configure("INFO", foreground="#a6e3a1")
        self._log_text.tag_configure("WARN", foreground="#f9e2af")
        self._log_text.tag_configure("ERROR", foreground="#f38ba8")
        self._log_text.tag_configure("DEBUG", foreground="#89b4fa")
        self._log_text.tag_configure("DIM", foreground="#6c7086")

        # 状态栏
        self._log_status_label = ttk.Label(card, text="共 0 条日志")
        self._log_status_label.pack(anchor=tk.W, pady=(4, 0))

        # 初始加载
        self._log_refresh_view()

    # === 日志功能(从 Qt 版本移植) ===
    def _log_refresh_view(self):
        """刷新日志显示。"""
        if not hasattr(self, "_log_text"):
            return
        try:
            level_text = self._log_level_var.get()
            level_filter = "" if level_text == "全部" else level_text
            keyword = self._log_keyword_var.get().strip()
            date_from = self._log_date_from_var.get().strip()
            date_to = self._log_date_to_var.get().strip()
            entries = get_logs(level_filter=level_filter, keyword=keyword,
                                date_from=date_from, date_to=date_to, limit=2000)

            self._log_text.config(state=tk.NORMAL)
            self._log_text.delete("1.0", tk.END)
            for e in entries:
                level = e.get("level", "INFO")
                msg = e.get("message", "")[:300]
                line = f"[{e.get('timestamp','')}] [{level}] [{e.get('module','')}] {msg}\n"
                self._log_text.insert(tk.END, line, level if level in ("INFO","WARN","ERROR","DEBUG") else "DIM")
            self._log_text.config(state=tk.DISABLED)
            self._log_status_label.config(text=f"共 {len(entries)} 条日志 (显示最近 2000 条)")
        except Exception as e:
            try:
                self._log_status_label.config(text=f"加载失败: {e}")
            except Exception:
                pass

    def _log_export_txt(self):
        """导出日志为 TXT。"""
        from tkinter import filedialog
        from datetime import datetime as _dt
        try:
            entries = get_logs()
            if not entries:
                messagebox.showinfo("导出", "暂无日志可导出")
                return
            path = filedialog.asksaveasfilename(
                title="导出日志 (TXT)",
                defaultextension=".txt",
                initialfile=f"HMOL_log_{_dt.now().strftime('%Y%m%d_%H%M%S')}.txt",
                filetypes=[("文本文件", "*.txt"), ("所有文件", "*.*")])
            if path:
                export_logs_txt(path, entries)
                messagebox.showinfo("导出成功", f"已导出 {len(entries)} 条日志到:\n{path}")
        except Exception as e:
            messagebox.showerror("导出失败", str(e))

    def _log_export_csv(self):
        """导出日志为 CSV。"""
        from tkinter import filedialog
        from datetime import datetime as _dt
        try:
            entries = get_logs()
            if not entries:
                messagebox.showinfo("导出", "暂无日志可导出")
                return
            path = filedialog.asksaveasfilename(
                title="导出日志 (CSV)",
                defaultextension=".csv",
                initialfile=f"HMOL_log_{_dt.now().strftime('%Y%m%d_%H%M%S')}.csv",
                filetypes=[("CSV 文件", "*.csv"), ("所有文件", "*.*")])
            if path:
                export_logs_csv(path, entries)
                messagebox.showinfo("导出成功", f"已导出 {len(entries)} 条日志到:\n{path}")
        except Exception as e:
            messagebox.showerror("导出失败", str(e))

    def _log_cleanup(self):
        """清理过期日志文件。"""
        if messagebox.askyesno("清理旧日志",
                               f"将删除 {_LOG_MAX_DAYS} 天前的日志文件。\n"
                               f"当前日志目录: {_LOG_FILE_PATH}\n\n确定继续?"):
            cleanup_old_logs()
            messagebox.showinfo("清理完成", "旧日志已清理。")
            self._log_refresh_view()

    def _copy_to_clipboard(self, text: str):
        """复制文本到剪贴板。"""
        try:
            self.clipboard_clear()
            self.clipboard_append(text)
            self._set_status(f"✅ 已复制: {text}")
        except Exception as e:
            log_warn("App", f"复制失败: {e}")

    # ========== copy_files wrapper ==========

    def copy_files(self, src_dir, dst_dir, progress_cb=None):
        """Copy files wrapper for InstanceManager.import_instance."""
        ok, total, failed = _copy_files(src_dir, dst_dir, progress_cb=progress_cb)
        return ok, total, failed


    # ========== file operations / close ==========
    def on_close(self):
        self.config["last_instance_id"] = self.instance_manager.current_instance.id if self.instance_manager.current_instance else None
        self.save_config(immediate=True)
        try:
            self.file_op_thread.stop()
        except Exception:
            pass
        self.destroy()


# =====================================================================
# main()
# =====================================================================
def main():
    import traceback as _tb
    cleanup_old_logs()
    log_info("App", f"{__app_name__} v{get_app_version()} (tkinter) starting")

    base_path = get_program_base_path()
    auth_manager = None
    is_offline = False

    if MSAL_AVAILABLE:
        try:
            auth_manager = AuthManager(base_path)
        except Exception as e:
            log_warn("App", f"AuthManager init failed: {e}")
            auth_manager = None

    has_net = _check_network_available()

    # Create main window FIRST — LoginDialog is shown as its Toplevel
    # (tkinter only allows one Tk root per process)
    app = None
    try:
        app = MainWindow(auth_manager=auth_manager, is_offline=is_offline)
        # Ensure main window is visible before showing any dialogs
        app.deiconify()
        app.update_idletasks()

        # ── EULA 接受检查(首次运行 / EULA 升级时) ──
        try:
            eula_accepted = bool(app.config.get("eula_accepted", False))
            eula_accepted_ver = str(app.config.get("eula_accepted_version", "") or "")
            # 未接受,或 EULA 版本已升级 → 重新展示
            if not eula_accepted or eula_accepted_ver != __version__:
                log_info("App", f"EULA not yet accepted (version={__version__}); showing dialog")
                eula_dlg = EULADialog(app, eula_version=__version__)
                app.wait_window(eula_dlg)
                if not eula_dlg.accepted:
                    # ── 用户拒绝使用协议 ──
                    # 根据协议要求:程序必须立即退出,且不保留任何用户操作记录
                    # 不写入审计日志(因为审计也算"记录")
                    # 不修改配置文件
                    # 使用 os._exit 强制立即终止进程
                    try:
                        messagebox.showwarning(
                            "已退出",
                            "您已选择不同意使用协议。\n\n"
                            "根据协议条款,程序将立即退出,且不会保留任何用户操作记录。",
                        )
                    except Exception:
                        pass
                    # 关闭所有 tkinter 窗口
                    try:
                        app.destroy()
                    except Exception:
                        pass
                    # 强制立即终止进程,不执行任何清理代码
                    # (确保不写入任何日志、配置文件或审计记录)
                    os._exit(0)
                # 用户接受 → 持久化
                app.config["eula_accepted"] = True
                app.config["eula_accepted_version"] = __version__
                try:
                    app.save_config(immediate=True)
                    log_info("App", f"EULA accepted (version={__version__})")
                except Exception as e:
                    log_warn("App", f"Failed to persist EULA acceptance: {e}")
        except Exception as e:
            # EULA 流程出错不应该阻止程序启动(用户能继续使用)
            log_warn("App", f"EULA dialog error (continuing without): {e}")

        # Show login dialog as a Toplevel of the main window
        if auth_manager and has_net:
            try:
                login = LoginDialog(app, auth_manager)
                app.wait_window(login)
                result = login.result
                if isinstance(result, dict):
                    action = result.get("action", "cancel")
                else:
                    action = result
                if action == "cancel":
                    # Ask the user if they want to continue in offline mode
                    try:
                        from tkinter import messagebox as _mb
                        if _mb.askyesno("跳过登录",
                                        "未完成 Microsoft 登录。\n\n是否以离线模式启动？\n\n"
                                        "选择「否」将退出程序。"):
                            is_offline = True
                            auth_manager = None
                            app.is_offline = True
                            app._apply_offline_mode()
                        else:
                            app.destroy()
                            return
                    except Exception:
                        app.destroy()
                        return
                elif action == "offline":
                    is_offline = True
                    auth_manager = None
                    app.is_offline = True
                    app._apply_offline_mode()
            except Exception as e:
                log_warn("App", f"Login dialog error: {e}")
                is_offline = True
                auth_manager = None
                if app is not None:
                    app.is_offline = True
                    try:
                        app._apply_offline_mode()
                    except Exception:
                        pass
        else:
            is_offline = True
            app.is_offline = True
            try:
                app._apply_offline_mode()
            except Exception:
                pass

        # Dependency check (non-blocking toast)
        try:
            missing = check_dependencies()
            if missing:
                dep_dlg = DependencyWarningDialog(app, missing)
                app.wait_window(dep_dlg)
        except Exception as e:
            log_warn("App", f"Dependency check failed: {e}")

        # Make sure main window is visible and on top
        app.deiconify()
        app.lift()
        app.focus_force()
        app.update_idletasks()
        log_info("App", "HMOL (tkinter) main window shown")

        app.mainloop()
    except Exception as e:
        tb_text = _tb.format_exc()
        log_error("App", f"Startup error: {e}\n{tb_text}")
        try:
            messagebox.showerror("启动错误", f"程序启动失败:\n{e}\n\n{tb_text[-1500:]}")
        except Exception:
            pass
        if app is not None:
            try:
                app.destroy()
            except Exception:
                pass


if __name__ == "__main__":
    main()

# =====================================================================
# 真的会有人看到这里吗？
# 累死了累死了，wine版本为什么不能直接把Qt版代码直接拿过来啊......
# @悲伤的天使！！！就因为你！还要额外出一个wine版本.....
#  : (
# =====================================================================