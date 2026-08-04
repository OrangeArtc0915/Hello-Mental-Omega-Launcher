---
title: HMOL wine版 侧边栏布局编辑器功能介绍
published: 2025-07-15
description: v2.4 起正式开放侧边栏布局编辑器，支持可视化拖拽自定义导航栏，YAML 持久化存储，让你的启动器界面完全由你掌控。
tags: [功能介绍, 布局编辑器, 侧边栏, 拖拽]
category: 功能介绍
draft: false
---

# HMOL wine版 侧边栏布局编辑器

## 概述

从 v2.4 版本开始，HMOL wine版正式向所有用户开放**侧边栏布局编辑器**。你可以通过可视化拖拽的方式自由排列导航栏中的各个功能入口，隐藏不常用的模块，甚至可以导入/导出布局方案与好友分享。

## 核心能力

### 可视化拖拽排序

进入编辑模式后，每个侧边栏项目都可以通过鼠标拖拽重新排列：

- 长按项目 200ms 或拖动超过 6px 阈值后进入拖拽状态
- 拖拽过程中会显示半透明覆盖层，实时指示插入位置
- 松开鼠标后自动更新 `order` 字段并刷新 UI
- 拖拽阈值设计防止了普通点击被误识别为拖拽操作

### YAML 持久化存储

布局配置以 YAML 格式持久化保存，文件位于启动器配置目录下的 `sidebar_layout.yaml`：

```yaml
version: 1
sidebar_width: 0.18
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
```

- 几何参数以**分数**存储（0..1 占父容器比例），无论窗口如何缩放都能自动适配
- 编辑时以像素为单位操作以保证流畅度，保存时自动转换回分数
- 侧边栏宽度默认占主窗口的 18%，可在编辑器中自由调整

### 导入/导出方案

- **导出方案**：将当前布局导出为 `<方案名>.yaml` 文件
- **导入方案**：选择任意有效的 YAML 布局文件，替换当前布局
- 导入前会自动校验 YAML 格式和字段完整性，避免加载损坏的配置

### 撤销/重做

- 每次拖拽或显隐操作自动压入快照栈
- 点击「取消」按钮可恢复到进入编辑模式前的布局状态
- 点击「保存」退出编辑模式后清空快照栈

## 防误触设计

| 机制 | 说明 |
|------|------|
| 拖拽阈值 6px | 避免普通点击触发拖拽 |
| `ButtonRelease-1` 返回 `"break"` | 抑制拖拽结束后的原点击事件 |
| 编辑模式隔离 | 编辑模式外所有拖拽绑定被禁用 |

## 可用模块

以下模块支持显示/隐藏和排序：

| 模块 ID | 名称 | 说明 |
|---------|------|------|
| home | 首页 | 启动器主面板 |
| instances | 实例管理 | 创建、切换、管理游戏实例 |
| packages | 插件包 | 安装、卸载、管理插件包 |
| backup | 备份还原 | 一键备份/还原游戏目录 |
| onedrive | 笨蛋广场 | OneDrive 资源共享浏览 |
| qqshout | QQ 喊话 | 联机房间信息发布 |
| dlc | DLC 启动器 | 启动附加内容 |
| settings | 设置 | 启动器全局配置 |

---

> 更多功能介绍请参考 [HMOL Wine 功能详解](https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/wiki/HMOL-Wine-Features)
