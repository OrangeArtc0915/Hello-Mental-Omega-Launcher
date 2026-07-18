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
#
# GitHub: https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher
# Issues: https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues
# ==============================================================================
"""
HMOL Env Config — 敏感配置外部化
==========================================================

提供统一的配置加载接口,支持多级优先级:

  1. 环境变量(最高优先级,生产推荐)
     例: HMOL_QQ_BOT_APPSECRET=xxx
  2. 外部 JSON 配置文件(团队协作推荐)
     例: HMOL_secrets.json(在启动器同目录,**不**提交到 git)
  3. 加密 seal 模块(默认,适合开源仓库)

设计目标:
  - 源代码和开源仓库中**绝不**包含任何明文凭据
  - 用户可以选择自己偏好的配置方式
  - 配置加载失败时,优雅降级到下一个优先级
"""
import json
import os
import sys
import threading
from typing import Any, Dict, Optional

# ============================================================
# 配置来源
# ============================================================
class ConfigSource:
    """配置来源标识。"""
    ENV = "env"
    EXTERNAL_FILE = "external_file"
    SEAL = "seal"  # 加密的 seal 模块
    NONE = "none"


# ============================================================
# 外部配置文件路径
# ============================================================
EXTERNAL_CONFIG_NAME = "HMOL_secrets.json"
EXTERNAL_CONFIG_EXAMPLE = "HMOL_secrets.json.example"


# 环境变量前缀
ENV_PREFIX = "HMOL_"


# 凭据的环境变量名映射
ENV_VAR_NAMES = {
    "QQ_BOT_APPID": "HMOL_QQ_BOT_APPID",
    "QQ_BOT_APPSECRET": "HMOL_QQ_BOT_APPSECRET",
    "QQ_BOT_CHANNEL_ID": "HMOL_QQ_BOT_CHANNEL_ID",
    "QQ_BOT_GROUP_ID": "HMOL_QQ_BOT_GROUP_ID",
    "MSAL_CLIENT_ID": "HMOL_MSAL_CLIENT_ID",
}


# ============================================================
# 加载器
# ============================================================
class ExternalConfigLoader:
    """外部配置加载器 — 支持环境变量和 JSON 配置文件。"""

    def __init__(self, base_path: Optional[str] = None):
        if base_path is None:
            base_path = os.path.dirname(os.path.abspath(__file__))
        self._base_path = base_path
        self._external_config: Optional[Dict[str, Any]] = None
        self._external_config_loaded = False
        self._lock = threading.Lock()

    def _get_external_config_path(self) -> str:
        return os.path.join(self._base_path, EXTERNAL_CONFIG_NAME)

    def _load_external_config(self) -> Dict[str, Any]:
        """从外部 JSON 文件加载配置(如果存在)。"""
        if self._external_config_loaded:
            return self._external_config or {}
        # 备注:这函数最初想用装饰器缓存,但发现装饰器写出来有同事说看 3 遍都
        # 没看懂,算了保留这种 if-early-return 的笨写法,起码能跑
        with self._lock:
            if self._external_config_loaded:
                return self._external_config or {}
            path = self._get_external_config_path()
            if not os.path.isfile(path):
                self._external_config = {}
            else:
                try:
                    with open(path, "r", encoding="utf-8") as f:
                        data = json.load(f)
                    if not isinstance(data, dict):
                        raise ValueError("root must be object")
                    self._external_config = data
                except (OSError, json.JSONDecodeError, ValueError) as e:
                    # 用户手改 JSON 写错是常事,启动器不能因为这个就拒绝服务
                    # 退到只读环境变量,够大部分场景(QQ Bot AppID 这种本来就走 env)
                    sys.stderr.write(
                        f"[HMOL Env Config] 外部配置加载失败: {e}\n")
                    self._external_config = {}
            self._external_config_loaded = True
            return self._external_config

    def get(self, name: str) -> Optional[str]:
        """获取配置项(按优先级:env > external_file)。

        Args:
            name: 凭据名(如 QQ_BOT_APPSECRET)

        Returns:
            配置值,未找到返回 None
        """
        # 1. 环境变量(最高优先级)
        env_name = ENV_VAR_NAMES.get(name, ENV_PREFIX + name)
        env_val = os.environ.get(env_name)
        if env_val:
            return env_val.strip()
        # 2. 外部 JSON 配置文件
        cfg = self._load_external_config()
        val = cfg.get(name)
        if isinstance(val, str) and val.strip():
            return val.strip()
        return None

    def get_source(self, name: str) -> str:
        """获取配置项的来源(用于审计)。"""
        env_name = ENV_VAR_NAMES.get(name, ENV_PREFIX + name)
        if os.environ.get(env_name):
            return ConfigSource.ENV
        if self._load_external_config().get(name):
            return ConfigSource.EXTERNAL_FILE
        return ConfigSource.NONE

    def is_configured_externally(self) -> bool:
        """是否有任何外部配置(环境变量或配置文件)。"""
        for name in ENV_VAR_NAMES.keys():
            if self.get(name) is not None:
                return True
        return False

    def write_example_file(self) -> None:
        """生成示例配置文件(用户可重命名使用)。"""
        path = os.path.join(self._base_path, EXTERNAL_CONFIG_EXAMPLE)
        example = {
            "_comment": (
                "HMOL 外部配置文件示例。\n"
                "用法: 复制此文件为 HMOL_secrets.json 并填入真实值。\n"
                "重要: HMOL_secrets.json 应加入 .gitignore,不提交到代码仓库!\n"
                "如果同时使用环境变量,环境变量优先。"
            ),
            "QQ_BOT_APPID": "your_qq_bot_appid",
            "QQ_BOT_APPSECRET": "your_qq_bot_appsecret",
            "QQ_BOT_CHANNEL_ID": "your_channel_id",
            "QQ_BOT_GROUP_ID": "your_group_id",
            "MSAL_CLIENT_ID": "your_azure_ad_client_id",
        }
        try:
            with open(path, "w", encoding="utf-8") as f:
                json.dump(example, f, indent=2, ensure_ascii=False)
                f.write("\n")
        except Exception as e:
            sys.stderr.write(f"[HMOL Env Config] 写入示例失败: {e}\n")


# ============================================================
# 全局单例
# ============================================================
_loader: Optional[ExternalConfigLoader] = None
_loader_lock = threading.Lock()


def get_loader(base_path: Optional[str] = None) -> ExternalConfigLoader:
    """获取全局配置加载器(单例)。"""
    global _loader
    if _loader is not None:
        # 单例已存在,检查 base_path 是否一致
        if base_path is not None and base_path != _loader._base_path:
            import warnings
            warnings.warn(
                f"ExternalConfigLoader 单例已创建 (base={_loader._base_path}),"
                f"忽略新的 base_path={base_path}",
                RuntimeWarning, stacklevel=2,
            )
        return _loader
    with _loader_lock:
        if _loader is None:
            _loader = ExternalConfigLoader(base_path)
    return _loader


def reset_loader() -> None:
    """重置全局单例(测试用)。"""
    global _loader
    with _loader_lock:
        _loader = None


# ============================================================
# 便捷接口
# ============================================================
def get_secret_with_fallback(name: str, fallback_resolver) -> Optional[str]:
    """带外部回退的凭据获取。

    优先级: 环境变量 > 外部 JSON > 加密 seal

    Args:
        name: 凭据名
        fallback_resolver: 加密 seal 的解封函数

    Returns:
        明文凭据
    """
    loader = get_loader()
    val = loader.get(name)
    if val is not None:
        return val
    # 回退到加密 seal
    try:
        return fallback_resolver()
    except Exception:
        return None


def get_source(name: str) -> str:
    """获取配置来源(用于 UI 显示)。"""
    return get_loader().get_source(name)


# ============================================================
# 自检
# ============================================================
def self_test() -> dict:
    """运行配置加载器自检。"""
    results = []
    # 1. 默认(无外部配置)
    try:
        loader = get_loader()
        results.append({
            "op": "load_defaults",
            "ok": True,
            "external_configured": loader.is_configured_externally(),
        })
    except Exception as e:
        results.append({"op": "load_defaults", "ok": False, "error": str(e)})
    # 2. 环境变量
    try:
        os.environ["HMOL_TEST_KEY"] = "test_value_12345"
        loader = get_loader()
        v = loader.get("TEST_KEY")
        ok = (v == "test_value_12345")
        results.append({"op": "env_var", "ok": ok, "value": v})
        del os.environ["HMOL_TEST_KEY"]
    except Exception as e:
        results.append({"op": "env_var", "ok": False, "error": str(e)})
    # 3. 外部 JSON
    try:
        import tempfile
        with tempfile.TemporaryDirectory() as td:
            test_path = os.path.join(td, EXTERNAL_CONFIG_NAME)
            with open(test_path, "w", encoding="utf-8") as f:
                json.dump({"QQ_BOT_APPID": "12345"}, f)
            loader = ExternalConfigLoader(td)
            v = loader.get("QQ_BOT_APPID")
            ok = (v == "12345")
            results.append({"op": "external_file", "ok": ok, "value": v})
    except Exception as e:
        results.append({"op": "external_file", "ok": False, "error": str(e)})
    return {
        "passed": all(r.get("ok") for r in results),
        "results": results,
    }


if __name__ == "__main__":
    import json
    res = self_test()
    print(json.dumps(res, indent=2, ensure_ascii=False))
