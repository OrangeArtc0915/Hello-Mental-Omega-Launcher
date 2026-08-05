---
title: HMOL 双线路更新功能详解
published: 2026-07-30
description: 详细介绍 v2.6.2 新增的 Github / Gitee 双线路自动更新机制，包括用户使用方式和后台自动同步流程。
tags: [自动更新, Github, Gitee, 教程, 双版本]
category: 功能介绍
draft: false
---

# HMOL标准版 双线路更新功能详解

v2.6.2 引入了双线路更新系统，国内用户可以通过 Gitee 线路获得更快的下载速度。

## 使用方式

启动 HMOL 后，程序会自动检查更新（每 24 小时检查一次）。发现新版本时会弹出更新对话框：

- 默认线路为 **Github**（通过 jsDelivr CDN 加速）
- 点击 **Gitee** 切换到国内线路下载
- 选择线路后点击「立即更新」即可

## 工作原理

```
启动 HMOL
    │
    ├─ 3秒后静默检查更新
    │   ├─ 尝试 Github 线路（jsDelivr CDN）
    │   └─ 失败则尝试 Gitee 线路
    │
    └─ 用户手动检查
        ├─ 默认 Github 线路
        └─ 可切换 Gitee 线路
```

