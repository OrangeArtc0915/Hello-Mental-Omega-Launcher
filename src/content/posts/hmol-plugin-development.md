---
title: HMOL 包开发者须知
published: 2026-07-30
description: 教你如何制作自己的 HMOL 插件包和资源包，发布到社区供其他玩家使用。
tags: [资源包, 开发教程, DLC, QSS, 自定义,双版本]
category: 教程
draft: false
---

# HMOL标准版 资源包开发教程

想为 HMOL 社区贡献自己的作品？这篇教程带你从零开始制作一个 HMOL 支持的资源包。

## 第一步：确定类型

选择你的资源包类型：

| 类型   | 说明                | 格式                 |
| ---- | ----------------- | ------------------ |
| INI包 | 修改游戏内单位数值         | .7z/.rar/.zip      |
| 任务包  | 战役和合作任务           | .7z/.rar/.zip      |
| 语音包  | 改变游戏内副官语音         | .7z/.rar/.zip      |
| 音乐包  | 改变游戏内背景音乐         | .7z/.rar/.zip      |
| 美化包  | 改变游戏内UI和其它组件元素的外观 | .7z/.rar/.zip      |
| 插件包  | 游戏体验增强，比如：注释、血量显示 | .7z/.rar/.zip      |
| 地图包  | 游戏内地图             | .7z/.rar/.zip/.map |
| DLC包 | HMOL启动器的拓展包       | .7z/.rar/.zip/     |

## 第二步：了解目录结构

### 资源包不支持加密（密码）和套文件（见错误示例包）！

### 示例资源包（.zip/.7z/.rar）  \[双版本支持]

```
📦 HMOL新兵训练.zip/                    ← 示例资源包（.zip/.7z/.rar）
   ├── 📁 INI                   
   ├── 📁 MapsMO                             
   ├── 📄 必看.txt                ←资源包的说明文档（更新日志、鸣谢名单、QQ群、必看等都写在这里）
   ├── 📄 搬运许可.jpg            ←（原创包可选）    
   ├── 📄 stringtable99.csf               
   ├── 📄 expandmo90.mix       
   └── 📄 ....其他文件 
```

### 示例资源包（.map）  \[双版本支持]

```
📄 冰天雪地.map                  ← 示例包（.map）
```

### 错误示例包（.zip .7z/.rar）

```
📦 Apra2.zip/                    ← 错误示例包（.zip .7z/.rar）
   └── 📁 Apra2                  ← 套文件（启动器不识别） 
        ├── 📁 MapsMO                             
        ├── 📄 必看.txt                
        ├── 📄 stringtable99.csf               
        ├── 📄 expandmo90.mix       
        └── 📄 ....其他文件 
```

### 错误示例包（.map）

```
📦 Apra2.zip/                    ← 错误示例包（.map）
   └── 📄 冰天雪地.map 
```

### HMOL-DLC包示例（.zip .7z/.rar）  \[双版本支持]

```
📦 DLC包名称.zip                        ← DLC包本体
   │
   ├── 📁 111文件夹                  ← 文件夹（用于存放DLC包的文件）
   │   ├── 111/                      ← 资源文件
   │   └── 其他资源文件             
   │
   ├── 📄 必看.txt                  ← 说明文档（更新日志、鸣谢名单、QQ群、使用教程都写这里）
   ├── 📄 搬运许可.jpg              ← 转载授权证明（原创包可选）
   │
   ├── 📄 mmmnb.HMOL                ← 主文件（.exe后缀改成.HMOL）
   ├── 📄 DLC.json                 ← DLC包的配置文件（注：标准版和wine版的配置文件不一样！）
   └── 📄 其他文件                  ← 直接放到游戏根目录的文件
```

## DLC包教程：1.编写 DLC.json

### HMOL标准版的DLC.json示例

```json
{
    "name": "示例DLC包",
    "version": "1.0.0",
    "description": "展示主题包的基本结构。包含自定义QSS样式。",
    "author": "YourName",
    "contact": "qq:123456789",
    "main": "bin/example.HMOL",
    "type": "美化",
    "has_qss": true,                         ←true为美化包
    "qss_file": "theme/style.qss"
}
```

### HMOL-wine版的DLC.json示例

```json
{
    "name": "Hello World 示例",
    "version": "v1.0.0",
    "description": "一个 DLC 示例项目，展示 DLC 的基本结构。",
    "author": "制作人：你的名字",
    "contact": "QQ群：123456789",
    "main": "HelloWorld.bat"
}
```

## DLC.json字段规范

| 字段     | 必填 | 格式要求                | 注释                  |
| :---------- | -- | ----------------- | --------------------- |
| name        | 是  | 纯中文或英文，不要混用       | ←DLC 显示名称             |
| version     | 是  | 语义化版本，如 `1.0.0`   | ←版本号                  |
| author      | 是  | 无                 | ←作者                   |
| description | 是  | 无                 | ←简要描述                 |
| contact     | 是  | 联系方式，QQ 或邮箱       | ←联系方式                 |
| main        | 是  | 相对路径，不带 `..`      | ←DLC 主入口文件（相对路径）      |
| type        | 是  | 标签，最好是2个字         | ←仅标准版，分类标签（如"美化""工具"） |
| has\_qss    | 否  | 布尔值，默认 false      | ←仅标准版，是否包含 QSS 样式文件   |
| qss\_file   | 否  | has\_qss=true 时必填 | ←QSS 样式文件（相对路径）       |

## DLC包教程：2.编写 QSS 样式表（仅用于HMOL标准版的美化包）

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

## 资源包教程：网站成熟的教程有很多，这里不加赘述，笨蛋广场的DLC包中有一些工具。

## 第四步：测试

1. 将插件包文件夹放入 HMOL 的 `DLC` 目录/将资源部导入并安装
2. 测试启动和功能是否正常

## 第六步：发布

打包，然后分享到社区。可以在以下平台发布：

- 笨蛋广场
- 官方 QQ 群文件

