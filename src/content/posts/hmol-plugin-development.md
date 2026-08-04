---
title: HMOL标准版 插件包开发教程
published: 2026-07-30
description: 教你如何制作自己的 HMOL 插件包，从 DLC.json 配置到目录结构设计，发布到社区供其他玩家使用。
tags: [插件包, 开发教程, DLC, QSS, 自定义]
category: 教程
draft: false
---

# HMOL标准版 插件包开发教程

想为 HMOL 社区贡献自己的作品？这篇教程带你从零开始制作一个 HMOL 插件包。

## 第一步：确定类型

选择你的插件包类型：

| 类型 | 说明 | 示例 |
|------|------|------|
| 功能 | 独立可执行工具 | 地图编辑器、CSF 编辑器 |
| 优化 | 系统辅助工具 | 内存清理、进程管理 |
| 美化 | QSS 主题样式表 | 暗色/亮色主题 |

## 第二步：创建目录结构

```
DLC/
└── 你的作者名_插件名称/
    ├── DLC.json
    ├── your_tool.exe  (或 .HMOL)
    └── resources/     (可选)
```

## 第三步：编写 DLC.json

### 功能型插件包

```json
{
  "name": "我的工具",
  "version": "1.0.0",
  "description": "一个实用的辅助工具",
  "author": "你的名字",
  "contact": "QQ: 你的QQ号",
  "main": "my_tool.exe",
  "type": "功能",
  "has_qss": false
}
```

### 美化型插件包

```json
{
  "name": "暗夜主题",
  "version": "1.0.0",
  "description": "优雅的暗色 QSS 主题",
  "author": "你的名字",
  "contact": "QQ群: xxxxxxxx",
  "main": "bin/launcher.exe",
  "type": "美化",
  "has_qss": true,
  "qss_file": "theme/dark.qss"
}
```

## 第四步：编写 QSS 样式表（美化型）

QSS 语法类似 CSS，用于定制 Qt 控件外观：

```css
QMainWindow {
    background-color: #1e1e2e;
}
QPushButton {
    background-color: #45475a;
    color: #cdd6f4;
    border-radius: 4px;
    padding: 8px 16px;
}
QPushButton:hover {
    background-color: #585b70;
}
```

## 第五步：测试

1. 将插件包文件夹放入 HMOL 的 `DLC` 目录
2. 启动 HMOL，在设置页面确认插件已加载
3. 测试启动和功能是否正常

## 第六步：发布

打包为 7z 或 zip 压缩包，分享到社区。可以在以下平台发布：

- 笨蛋广场 → 程序 DLC 下载
- 官方 QQ 群文件
- 红色警戒相关论坛

## 字段规范

| 字段 | 必填 | 格式要求 |
|------|------|---------|
| name | 是 | 纯中文或英文，不要混用 |
| version | 是 | 语义化版本，如 `1.0.0` |
| author | 是 | 作者名，建议加上社区 ID |
| contact | 是 | 联系方式，QQ 或邮箱 |
| main | 是 | 相对路径，不带 `..` |
| has_qss | 否 | 布尔值，默认 false |
| qss_file | 否 | has_qss=true 时必填 |
