---
title: 更新日志
---

> 本页为 wiki 版更新日志，与原 `更新日志.md` 内容一致。

## v3.2.0（2026-08-19）

### 新增

- **第四组网方案 Anywherelan（三层 · 无服务器）**，与 EasyTier、n2n、ZeroTier 并列，共四种方案可选。
  - 完全去中心化 P2P 组网（libp2p 公共 DHT 发现设备），无服务器、无账号、无令牌，零成本零配置。
  - 房主建房后自动生成 Peer ID，连接成功即写回节点栏；把分享文本发给队友即可自动加入，房主后台自动接受加入请求。
  - 成员收到分享文本后：复制 → 自动检测 → 一键加入，无需任何手动操作。
  - 内置 headless 服务端（资源内 awl.exe + wintun.dll），随插件包分发，离线可用、无在线下载依赖。
  - 默认虚拟网段 10.66.0.0/16，游戏房间通过虚拟网卡广播自动发现，无需手动填 IP。
  - Anywherelan 方案下"节点"栏自动切换为"Anywherelan Peer ID"，自动分配虚拟 IP，手动 IP 模式不适用。
- 分享文本支持 Anywherelan 方案与房主 Peer ID 传递；"四方案选择"说明同步更新。

### 修复 / 优化

- DLC 描述由"EasyTier/n2n/ZeroTier 三方案"更新为"EasyTier/n2n/ZeroTier/Anywherelan 四方案"。
- 关于页内核行同步为"n2n + EasyTier 2.6.4 + ZeroTier + Anywherelan"。

---

## v3.1.0（2026-08-18）

### 新增

- **第三组网方案 ZeroTier（三层 · API 建网）**，与 EasyTier、n2n 并列，共三种方案可选。
  - 房主：在网络设置页填写自己的 API 令牌（my.zerotier.com → Account → API Access Tokens 获取），连接时自动创建或复用网络、自动授权自己，并后台轮询自动授权新成员，全程零网页操作。
  - 成员：无需令牌，收到分享文本中的网络 ID 即可加入，等待房主授权后自动获取虚拟 IP。
  - 内置无界面 zerotier-one 核心服务：官方 MSI 静默安装（ZTHEADLESS=Yes，不含 GUI 客户端），无本地安装包时自动在线下载。
  - 默认虚拟网段 172.24.1.0/24，避开 EasyTier（10.0.0.x）与 n2n（192.168.100.x）网段。
- 网络设置页新增 ZeroTier 专属面板（仅选 ZeroTier 方案时显示）：
  - API 令牌输入框（掩码显示）+ "打开官网" 直达 my.zerotier.com。
  - "ZeroTier 工具"：安装 / 启动 / 停止核心服务、保存令牌、调 Central API 校验令牌有效性。
- ZeroTier 方案下"节点"栏自动切换为"网络 ID（16 位 hex）"，支持手动编辑。
- 分享文本支持 ZeroTier 方案与网络 ID 传递；分享版本号升级为 v3.1.0（版本不一致拒绝联机）。

### 修复 / 优化

- EasyTier 旧默认节点 tcp://39.108.52.138:11010 已被防火墙拦截，配置自动迁移为同主机 UDP 协议（udp://39.108.52.138:11010），避免用户一直连不上。
- DLC 描述由"EasyTier/n2n 双方案"更新为"EasyTier/n2n/ZeroTier 三方案"。

---

*历史版本记录待补（v3.0.0 及更早版本未在此文件登记）。*
