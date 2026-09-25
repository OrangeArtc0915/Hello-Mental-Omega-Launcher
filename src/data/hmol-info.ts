/**
 * HMOL 发行与项目信息。
 *
 * 这里是「取真实值」的唯一入口，避免版本号/链接在页面里被手写多份：
 * - 版本号、作者、仓库、QQ 群：构建时读取 `src/HMOL.Core/App/AppInfo.cs`（唯一来源）
 * - 产物名与目标平台：`build.bat` 第 6 步组装发行包时的 `HMOL-v%VERSION%-win-x64.zip`、
 *   以及 `dotnet publish -r win-x64 --self-contained true -p:PublishSingleFile=true`
 * - 发行包内容：`build.bat` 第 6 步（HMOL.exe、存在的 README.md/NOTICE/LICENSE、runtime\）
 * - 运行时组件：仓库根 `runtime\` 目录清单（7zip / easytier / n2n / tap / winipbroadcast）
 *
 * 读不到 AppInfo.cs 时会回退到 FALLBACK 常量并在构建日志里打印警告，
 * 这样在「只拷贝 Web 目录去构建」的场景下也不会直接把构建打挂。
 */
import fs from "node:fs";
import path from "node:path";

export interface HmolInfo {
	/** AppInfo.Name */
	name: string;
	/** AppInfo.Version，如 "1.0.0" */
	version: string;
	/** AppInfo.VersionDisplay，如 "v1.0.0" */
	versionDisplay: string;
	/** AppInfo.Author */
	author: string;
	/** AppInfo.License（许可声明摘要：保留所有权利） */
	license: string;
	githubUrl: string;
	githubIssuesUrl: string;
	githubReleasesUrl: string;
	giteeUrl: string;
	giteeReleasesUrl: string;
	qqGroup: string;
	qqGroupUrl: string;
	/** 发行压缩包文件名（build.bat: HMOL-v%VERSION%-win-x64.zip） */
	zipName: string;
}

/** AppInfo.cs 读取失败时的兜底值，来源同 AppInfo.cs（2026-09 快照） */
const FALLBACK: HmolInfo = {
	name: "Hello Mental Omega Launcher",
	version: "1.0.0",
	versionDisplay: "v1.0.0",
	author: "mmm",
	license: "保留所有权利",
	githubUrl:
		"https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher",
	githubIssuesUrl:
		"https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues",
	githubReleasesUrl:
		"https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/releases",
	giteeUrl: "https://gitee.com/orangearc655743/Hello-Mental-Omega-Launcher",
	giteeReleasesUrl:
		"https://gitee.com/orangearc655743/Hello-Mental-Omega-Launcher/releases",
	qqGroup: "1034243331",
	qqGroupUrl: "https://qm.qq.com/q/ia8Zv2AtEY",
	zipName: "HMOL-v1.0.0-win-x64.zip",
};

const APPINFO_REL = path.join("src", "HMOL.Core", "App", "AppInfo.cs");
const APPINFO_ABS = path.resolve(process.cwd(), "..", APPINFO_REL);

/** 解析 AppInfo.cs 里的 `public const string X = "...";` 与 `= Y + "...";` */
function parseAppInfo(source: string): Record<string, string> {
	const values: Record<string, string> = {};
	const literal = /public const string (\w+) = "([^"]*)";/g;
	const concat = /public const string (\w+) = (\w+) \+ "([^"]*)";/g;

	for (const m of source.matchAll(literal)) {
		values[m[1]] = m[2];
	}
	// 派生常量（IssuesUrl / ReleasesUrl）依赖前置常量，多跑几轮直到收敛
	for (let round = 0; round < 5; round++) {
		let changed = false;
		for (const m of source.matchAll(concat)) {
			if (values[m[1]] !== undefined) continue;
			const base = values[m[2]];
			if (base === undefined) continue;
			values[m[1]] = base + m[3];
			changed = true;
		}
		if (!changed) break;
	}
	return values;
}

function loadInfo(): HmolInfo {
	try {
		const source = fs.readFileSync(APPINFO_ABS, "utf-8");
		const v = parseAppInfo(source);
		if (!v.Version || !v.GitHubUrl) {
			throw new Error("没有解析到 Version / GitHubUrl");
		}
		return {
			name: v.Name || FALLBACK.name,
			version: v.Version,
			versionDisplay: v.VersionDisplay || `v${v.Version}`,
			author: v.Author || FALLBACK.author,
			license: v.License || FALLBACK.license,
			githubUrl: v.GitHubUrl,
			githubIssuesUrl: v.GitHubIssuesUrl || `${v.GitHubUrl}/issues`,
			githubReleasesUrl: v.GitHubReleasesUrl || `${v.GitHubUrl}/releases`,
			giteeUrl: v.GiteeUrl || FALLBACK.giteeUrl,
			giteeReleasesUrl: v.GiteeReleasesUrl || `${v.GiteeUrl}/releases`,
			qqGroup: v.QqGroup || FALLBACK.qqGroup,
			qqGroupUrl: v.QqGroupUrl || FALLBACK.qqGroupUrl,
			zipName: `HMOL-v${v.Version}-win-x64.zip`,
		};
	} catch (error) {
		console.warn(
			`[hmol-info] 读取 ${APPINFO_ABS} 失败，回退到内置值：${(error as Error).message}`,
		);
		return FALLBACK;
	}
}

export const hmol: HmolInfo = loadInfo();

/**
 * 下载链接一律指向 Release 的「最新版」页面，不猜具体 tag：
 * - GitHub: `<repo>/releases/latest`（GitHub 的固定「最新版」跳转地址）
 * - Gitee : `<repo>/releases`（Gitee 的发行版列表页，取自 AppInfo.GiteeReleasesUrl）
 */
export const releaseLinks = {
	githubLatest: `${hmol.githubReleasesUrl}/latest`,
	giteeReleases: hmol.giteeReleasesUrl,
} as const;

/** 系统要求（build.bat 结尾提示 + src/HMOL.App/app.manifest 的 supportedOS） */
export const systemRequirements = [
	{ label: "操作系统", value: "Windows 10 1809+ / Windows 11（仅 x64）" },
	{ label: "运行时", value: "无需安装 —— .NET 8 运行时已打进单文件 exe" },
	{ label: "磁盘", value: "含 runtime\\ 组网组件，建议预留 1 GB 以上" },
	{ label: "权限", value: "普通权限即可；安装 TAP 虚拟网卡等少数操作会再提权" },
] as const;

/**
 * 发行压缩包内容（build.bat 第 6 步组装，逐条对应）：
 * 1. publish\HMOL.exe —— 自包含单文件
 * 2. 仓库根存在 README.md / NOTICE / LICENSE 时复制进包（本仓库目前只有 LICENSE）
 * 3. runtime\ —— 组网组件与 7-Zip，不内嵌进 exe
 */
export const packageContents = [
	{ name: "HMOL.exe", desc: "启动器本体，自包含 .NET 8 单文件" },
	{ name: "LICENSE", desc: "许可声明（保留所有权利）" },
	{
		name: "runtime\\",
		desc: "组网组件与 7-Zip：7zip\\、easytier\\、n2n\\、tap\\、winipbroadcast\\",
	},
] as const;
