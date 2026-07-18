"""
rate_limiter.py — HMOL 速率限制与暴力破解防护

防护:
1. 登录失败计数 + 临时锁定
2. 网络请求速率限制 (防止 API 滥用)
3. 文件下载带宽限制
4. QQ 喊话频次限制 (防刷屏)
"""

import time
import threading
from collections import deque
from typing import Optional


class LoginAttemptLimiter:
    """
    登录失败计数器
    连续失败 N 次后锁定 M 分钟
    """

    def __init__(self, max_attempts: int = 5, lockout_seconds: int = 300):
        self.max_attempts = max_attempts
        self.lockout_seconds = lockout_seconds
        self._attempts: dict[str, list[float]] = {}  # {username: [timestamps]}
        self._lockouts: dict[str, float] = {}  # {username: unlock_time}
        self._lock = threading.Lock()

    def is_locked(self, username: str) -> tuple:
        """
        Check if a username is currently locked.
        Returns: (locked: bool, remaining_seconds: int)
        """
        with self._lock:
            unlock_time = self._lockouts.get(username, 0)
            if unlock_time > time.time():
                remaining = int(unlock_time - time.time())
                return True, remaining
            return False, 0

    def record_failure(self, username: str) -> tuple:
        """
        Record a login failure.
        Returns: (locked_now: bool, remaining_attempts: int)
          - locked_now: True if this failure just triggered a lockout
          - remaining_attempts: how many more failures before lockout (0 if locked)
        """
        with self._lock:
            now = time.time()
            attempts = self._attempts.setdefault(username, [])
            # 只保留 1 小时内的记录
            attempts[:] = [t for t in attempts if now - t < 3600]
            attempts.append(now)
            if len(attempts) >= self.max_attempts:
                self._lockouts[username] = now + self.lockout_seconds
                return True, 0
            return False, self.max_attempts - len(attempts)

    def record_success(self, username: str) -> None:
        """登录成功, 清空计数"""
        with self._lock:
            self._attempts.pop(username, None)
            self._lockouts.pop(username, None)

    def persist_to(self, state: dict) -> None:
        """Export current state for persistence into HMOL_config.json."""
        with self._lock:
            now = time.time()
            state["_login_attempts"] = {
                u: [t for t in times if now - t < 3600]
                for u, times in self._attempts.items()
            }
            state["_login_lockouts"] = {
                u: t for u, t in self._lockouts.items() if t > now
            }

    def load_from(self, state: dict) -> None:
        """
        Restore state previously saved by persist_to().
        Validates types to defend against malicious config files.
        """
        with self._lock:
            new_attempts = {}
            raw_attempts = state.get("_login_attempts", {})
            if isinstance(raw_attempts, dict):
                for u, times in raw_attempts.items():
                    if not isinstance(u, str) or not isinstance(times, list):
                        continue
                    # Each entry must be a number (timestamp); filter bad data
                    safe_times = [
                        float(t) for t in times
                        if isinstance(t, (int, float))
                    ]
                    if safe_times:
                        new_attempts[u] = safe_times
            self._attempts = new_attempts

            new_lockouts = {}
            raw_lockouts = state.get("_login_lockouts", {})
            if isinstance(raw_lockouts, dict):
                for u, t in raw_lockouts.items():
                    if isinstance(u, str) and isinstance(t, (int, float)):
                        new_lockouts[u] = float(t)
            self._lockouts = new_lockouts


class RateLimiter:
    """
    通用速率限制器 (滑动窗口)
    限制 N 次操作 / 时间窗口
    """

    # Maximum number of distinct keys to remember. Prevents memory
    # blowup when keys are user-controlled.
    MAX_KEYS = 1024

    def __init__(self, max_calls: int, window_seconds: float):
        self.max_calls = max_calls
        self.window_seconds = window_seconds
        self._calls: dict[str, deque] = {}
        self._lock = threading.Lock()

    def check(self, key: str = "_default") -> tuple[bool, float]:
        """
        检查是否允许调用
        Returns: (is_allowed, retry_after_seconds)
        """
        if not isinstance(key, str) or not key:
            key = "_default"
        with self._lock:
            now = time.time()
            calls = self._calls.get(key)
            if calls is None:
                # Bounded growth: if at cap, drop the least-recently-used
                # entry (the first call in any deque is the oldest).
                if len(self._calls) >= self.MAX_KEYS:
                    # Find and drop one stale key
                    stale_key = next(
                        (k for k, v in self._calls.items() if not v or v[0] < now - self.window_seconds),
                        None
                    )
                    if stale_key is not None:
                        self._calls.pop(stale_key, None)
                    else:
                        # No stale entry: drop the first key (LRU approx)
                        first = next(iter(self._calls))
                        self._calls.pop(first, None)
                calls = deque()
                self._calls[key] = calls
            # 移除窗口外的旧记录
            while calls and calls[0] < now - self.window_seconds:
                calls.popleft()
            if len(calls) >= self.max_calls:
                retry_after = self.window_seconds - (now - calls[0])
                return False, max(retry_after, 0.0)
            calls.append(now)
            return True, 0.0

    def reset(self, key: Optional[str] = None) -> None:
        """Clear rate limit state for one key, or all keys."""
        with self._lock:
            if key is None:
                self._calls.clear()
            else:
                self._calls.pop(key, None)


# 全局单例
_login_limiter = LoginAttemptLimiter()
_qq_shout_limiter = RateLimiter(max_calls=5, window_seconds=60)  # 喊话 5 次/分钟
_api_limiter = RateLimiter(max_calls=60, window_seconds=60)       # API 60 次/分钟


def get_login_limiter() -> LoginAttemptLimiter:
    return _login_limiter


def get_qq_shout_limiter() -> RateLimiter:
    return _qq_shout_limiter


def get_api_limiter() -> RateLimiter:
    return _api_limiter


def reset_all_limiters() -> None:
    """Reset all global limiters (used in tests and on EULA re-accept)."""
    _login_limiter._attempts.clear()
    _login_limiter._lockouts.clear()
    _qq_shout_limiter.reset()
    _api_limiter.reset()


__all__ = [
    'LoginAttemptLimiter',
    'RateLimiter',
    'get_login_limiter',
    'get_qq_shout_limiter',
    'get_api_limiter',
    'reset_all_limiters',
]
