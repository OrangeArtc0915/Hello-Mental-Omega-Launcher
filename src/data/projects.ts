export interface Project {
	id: string;
	title: string;
	description: string;
	image: string;
	category: "web" | "mobile" | "desktop" | "other";
	techStack: string[];
	status: "completed" | "in-progress" | "planned";
	startDate: string;
	endDate?: string;
	featured?: boolean;
	tags?: string[];
	visitUrl?: string;
	sourceCode?: string;
	sections?: { title: string; content: string }[];
	badge?: string;
}

const GITHUB = "https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher";
const GITEE = "https://gitee.com/orangearc655743/Hello-Mental-Omega-Launcher";
const GITHUB_RELEASES = `${GITHUB}/releases/latest`;

export const projectsData: Project[] = [
	{
		id: "hmol",
		title: "HMOL 启动器",
		description: "Windows 10 / 11 原生标准版本，完整功能与个性化设置，一键启动心灵终结。",
		image: "/docs/HMOL.webp",
		category: "desktop",
		techStack: ["x64", "6 套主题", "多实例", "个性化设置", "原生中文"],
		status: "completed",
		sourceCode: GITHUB,
		visitUrl: GITHUB_RELEASES,
		startDate: "2024-01-01",
		featured: true,
		tags: ["HMOL", "标准版"],
		badge: "标准版",
		sections: [
			{
				title: "关于",
				content: "HMOL (Hello Mental Omega Launcher) 是一款为心灵终结玩家打造的轻量级独立启动器。提供一键启动、多实例管理、主题切换、双语支持等功能。"
			},
			{
				title: "功能特性",
				content: "一键启动游戏 · 多实例同时运行 · 6 套内置主题 · 中英文切换 · QSS 自定义美化 · 个性化设置 · 自动检测游戏路径"
			},
			{
				title: "安装说明",
				content: "1. 从 GitHub 下载最新版本\n2. 解压到任意目录\n3. 运行 HMOL.exe\n4. 首次运行阅读并同意 EULA\n5. 设置游戏路径即可使用"
			},
		],
	},
	{
		id: "hmol-wine",
		title: "HMOL (Wine)",
		description: "Linux / Winlator 兼容版本，功能与普通版一致，适配 Wine 容器环境。",
		image: "/docs/HMOL-wine.webp",
		category: "desktop",
		techStack: ["Wine", "Linux", "Winlator", "6 套主题", "多实例", "双语支持"],
		status: "completed",
		sourceCode: GITHUB,
		visitUrl: GITHUB_RELEASES,
		startDate: "2024-01-01",
		featured: true,
		tags: ["HMOL", "Wine 版"],
		badge: "Wine 版",
		sections: [
			{
				title: "关于",
				content: "HMOL Wine 版是专为 Wine 容器环境（Linux / Winlator）优化的兼容版本。功能与标准版一致（仅缺少个性化设置模块）。"
			},
			{
				title: "功能特性",
				content: "一键启动 · 多实例 · 6 套内置主题 · 中英文切换 · Wine 容器自动适配 · Linux/Winlator 原生兼容"
			},
			{
				title: "安装说明",
				content: "1. 从 GitHub 下载 Wine 版\n2. 在 Wine 容器中运行 HMOL.exe\n3. 首次运行阅读 EULA\n4. 设置游戏路径\n5. 正常使用"
			},
		],
	},
	{
		id: "hmol-dlc",
		title: "HMOL DLC 插件包",
		description: "官方 DLC 包合集，程序拓展、主题包等追加内容，安装即用。",
		image: "",
		category: "other",
		techStack: ["即装即用", "独立管理", "一键切换"],
		status: "completed",
		visitUrl: "/dlc/",
		startDate: "2024-01-01",
		featured: true,
		tags: ["DLC", "插件包"],
		badge: "DLC",
		sections: [
			{
				title: "关于",
				content: "HMOL DLC 插件包为 HMOL 启动器提供程序拓展、主题包、工具等追加内容。DLC 分普通版和 Wine 版两种格式，可在启动器内一键安装管理。"
			},
			{
				title: "DLC 列表",
				content: "Mem Reduct 内存优化 / TheWorld 浏览器 / FA2SP 地图编辑器 / RA2CStrEditor / 3DS2VXL 模型转换 / HvaBuilder / SHP Builder / AI 编辑器 / CSF 编辑器 / Cover Maker"
			},
			{
				title: "安装说明",
				content: "1. 打开 HMOL 启动器\n2. 进入 DLC 下载\n3. 选择需要的 DLC 点击下载\n4. 重启启动器完成安装"
			},
		],
	},
];

export const getProjectStats = () => {
	const total = projectsData.length;
	const completed = projectsData.filter((p) => p.status === "completed").length;
	const inProgress = projectsData.filter((p) => p.status === "in-progress").length;
	const planned = projectsData.filter((p) => p.status === "planned").length;
	return { total, byStatus: { completed, inProgress, planned } };
};

export const getProjectsByCategory = (category?: string) => {
	if (!category || category === "all") return projectsData;
	return projectsData.filter((p) => p.category === category);
};

export const getFeaturedProjects = () => {
	return projectsData.filter((p) => p.featured);
};

export const getAllTechStack = () => {
	const techSet = new Set<string>();
	projectsData.forEach((project) => {
		project.techStack.forEach((tech) => techSet.add(tech));
	});
	return Array.from(techSet).sort();
};
