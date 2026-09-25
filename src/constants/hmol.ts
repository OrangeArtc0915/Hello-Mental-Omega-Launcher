/**
 * HMOL 官网的站点级真实信息（单一出处）。
 *
 * 取值来源（不要臆造，改动前请同步核对）：
 * - 版本号 / 作者 / 仓库 / QQ 群：`src/HMOL.Core/App/AppInfo.cs`
 * - 许可口径：仓库根 `LICENSE`（专有软件，保留所有权利）
 * - 第三方组件清单：`docs/third-party.md`
 *
 * 站点配置（src/config.ts）、导航、页脚、首页都从这里取值，
 * 避免同一个事实在多处硬编码后互相对不上。
 */
export const HMOL = {
	/** 程序全名，对应 AppInfo.Name */
	name: "Hello Mental Omega Launcher",
	/** 简称，用于导航标题与徽标 */
	shortName: "HMOL",
	/** 当前版本号，对应 AppInfo.Version / VersionDisplay */
	version: "1.0.0",
	versionDisplay: "v1.0.0",
	/** 作者，对应 AppInfo.Author */
	author: "mmm",
	/** 许可摘要，对应 AppInfo.License 与仓库根 LICENSE */
	license: "保留所有权利",
	/** 一句话简介（官网首页与页脚共用） */
	tagline: "为心灵终结玩家打造的轻量级启动器",
	/** 站点描述，用于 meta description */
	description:
		"Hello Mental Omega Launcher（HMOL）是面向《心灵终结》（Mental Omega）玩家的第三方启动器：多实例管理、资源包一键安装与备份还原、联机对战、界面自定义。",
	/** 展示用仓库地址（不带结尾 .git），对应 AppInfo.GitHubUrl */
	github: "https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher",
	/** Gitee 镜像，对应 AppInfo.GiteeUrl */
	gitee: "https://gitee.com/orangearc655743/Hello-Mental-Omega-Launcher",
	/** 官方 QQ 群号，对应 AppInfo.QqGroup */
	qqGroup: "1034243331",
	/** 加群链接，对应 AppInfo.QqGroupUrl */
	qqGroupUrl: "https://qm.qq.com/q/ia8Zv2AtEY",
} as const;

/** GitHub Releases（海外用户推荐线路） */
export const HMOL_GITHUB_RELEASES = `${HMOL.github}/releases`;

/** Gitee Releases（国内用户推荐线路） */
export const HMOL_GITEE_RELEASES = `${HMOL.gitee}/releases`;

/** 仓库内许可正文 */
export const HMOL_LICENSE_URL = `${HMOL.github}/blob/main/LICENSE`;

/** 仓库内第三方组件说明（页脚的「第三方组件」入口） */
export const HMOL_THIRD_PARTY_URL = `${HMOL.github}/blob/main/docs/third-party.md`;
