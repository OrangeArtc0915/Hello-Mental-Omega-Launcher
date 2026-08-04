---
title: HMOL标准版 DLC 插件包系统介绍
published: 2026-07-30
description: 介绍 HMOL 的 DLC 插件包系统，包括配置文件规范、插件包类型和使用方式。
tags: [DLC, 插件包, 教程, 美化, 功能扩展]
category: 功能介绍
draft: false
---

# HMOL标准版 DLC 插件包系统介绍

HMOL 从 v2.6 起支持 DLC（可下载扩展内容）插件包系统。你可以安装社区制作的插件包来扩展启动器功能、更换界面主题或优化游戏体验。

## 插件包类型

| 类型 | 说明 |
|------|------|
| 美化 | 提供 QSS 主题样式表，更换启动器外观 |
| 优化 | 性能优化、内存清理等辅助工具 |
| 功能 | 地图编辑器、文本编辑器、模型转换等独立工具 |

## DLC 配置文件

每个插件包必须包含一个 `DLC.json` 文件，格式如下：

```json
{
  "name": "我的主题包",
  "version": "1.0.0",
  "description": "优雅的暗色主题",
  "author": "作者名",
  "contact": "QQ群: xxxxxxxx",
  "main": "bin/mytheme.exe",
  "type": "美化",
  "has_qss": true,
  "qss_file": "theme/style.qss"
}
```

## 安装方式

1. 将插件包文件夹放入 HMOL 程序目录下的 `DLC` 文件夹
2. 启动 HMOL，程序会自动扫描并加载
3. 在设置页面查看和管理已安装的插件包

## 制作自己的插件包

参考 `DLC/Example_MyTheme/` 示例文件夹，包含完整的目录结构和配置文件模板。将你的作品分享到社区，帮助更多玩家获得更好的体验。
