export interface DLCItem {
	id: string;
	name: string;
	englishName: string;
	description: string;
	badge: string;
	accent: string;
	accent2: string;
	icon: string;
	isThirdParty?: boolean;
	/** 是否为 HMOL 原创 DLC（原创包可点击查看详细介绍页） */
	original?: boolean;
	/** 搬运包的展示分组（如：红警开发工具 / 实用工具） */
	category?: string;
	/** 原创包详情页的功能特性列表 */
	features?: string[];
	/** 原创包详情页的快速开始步骤 */
	quickStart?: string[];
}

export const dlcItems: DLCItem[] = [
	{
		id: "hmol-network",
		name: "HMOL 联机模块",
		englishName: "HMOL Network Module",
		description:
			"红警2 / 心灵终结等支持局域网的旧游戏，跨异地（跨公网）零成本联机工具。双方案组网，一键分享入伙，完全由 HMOL 团队原创开发。",
		badge: "标准版",
		accent: "#eab308",
		accent2: "#f59e0b",
		icon: '<path d="M5 12.55a11 11 0 0 1 14.08 0" /><path d="M1.42 9a16 16 0 0 1 21.16 0" /><path d="M8.53 16.11a6 6 0 0 1 6.95 0" /><line x1="12" y1="20" x2="12.01" y2="20" />',
		original: true,
		features: [
			"跨公网零成本联机：让红警2 / 心灵终结等局域网游跨异地互联",
			"EasyTier 三层组网（推荐）：免装驱动，内置公共节点即可用",
			"n2n 二层组网：兼容依赖 MAC 地址的老游戏，兼容性最好",
			"一键分享入伙：文本 / 二维码分享小组信息，队友一键加入",
			"服务端模式：公共节点不通时，本机自建节点服务器",
			"测试工具箱：连通性检测 / 一键防火墙 / TAP 安装 / 网卡优先级",
			"必须从 HMOL 启动器「程序DLC」页面启动，不能独立运行",
		],
		quickStart: [
			"打开 HMOL，使用微软账号登录（未登录时模块会拒绝启动）",
			"在 HMOL 左侧导航进入「程序DLC」→ 找到 HMOL联机模块 → 点击 🚀 启动",
			"选择方案（EasyTier 推荐）→ 选择节点服务器 → 填写或随机生成小组名 → 点击连接",
			"队友做同样操作并填入相同小组名，即可在「在线对端」中互相看到",
		],
	},
	{
		id: "memreduct",
		name: "Mem Reduct 内存优化",
		englishName: "Mem Reduct",
		description: "轻量级系统内存清理工具。游戏运行时自动释放被占用的内存，显著减少卡顿与闪退，尤其适合低配置设备。",
		badge: "标准版",
		accent: "#14b8a6",
		accent2: "#6366f1",
		icon: '<rect x="2" y="3" width="20" height="14" rx="2" /><line x1="8" y1="21" x2="16" y2="21" /><line x1="12" y1="17" x2="12" y2="21" />',
		isThirdParty: true,
		category: "实用工具",
	},
	{
		id: "theworld",
		name: "TheWorld 浏览器",
		englishName: "TheWorld Browser",
		description: "轻量级浏览器，已针对 HMOL Wine 版 / Linux / Winlator 环境完成适配。启动快速、内存占用低，满足在 Wine 容器内的日常浏览需求。",
		badge: "Wine 版",
		accent: "#a78bfa",
		accent2: "#f472b6",
		icon: '<circle cx="12" cy="12" r="10" /><line x1="2" y1="12" x2="22" y2="12" /><path d="M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z" />',
		isThirdParty: true,
		category: "实用工具",
	},
	{
		id: "fa2sp",
		name: "FA2SP 地图编辑器",
		englishName: "FA2SP Map Editor",
		description: "FinalAlert 2 SP HDM Edition 是心灵终结阵营专用的地图编辑器。完整支持 MO 地形、科技建筑与触发系统，版本 v1.4.4。",
		badge: "标准版",
		accent: "#22c55e",
		accent2: "#0891b2",
		icon: '<rect x="3" y="3" width="18" height="18" rx="2" /><line x1="3" y1="9" x2="21" y2="9" /><line x1="9" y1="21" x2="9" y2="9" /><line x1="15" y1="9" x2="15" y2="21" /><line x1="3" y1="15" x2="21" y2="15" />',
		isThirdParty: true,
		category: "红警开发工具",
	},
	{
		id: "ra2cstr",
		name: "RA2CStrEditor INI编辑器",
		englishName: "RA2CStrEditor",
		description: "红警 2 INI 配置编辑器，支持直观编辑单位属性和武器参数。",
		badge: "标准版",
		accent: "#f59e0b",
		accent2: "#dc2626",
		icon: '<path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" /><polyline points="14 2 14 8 20 8" /><line x1="16" y1="13" x2="8" y2="13" /><line x1="16" y1="17" x2="8" y2="17" /><polyline points="10 9 9 9 8 9" />',
		category: "红警开发工具",
	},
	{
		id: "3ds2vxl",
		name: "3DS2VXL 模型转换",
		englishName: "3DS2VXL Converter",
		description: "3DS 模型转 VXL 体素格式，制作自定义单位的必备工具。",
		badge: "标准版",
		accent: "#3b82f6",
		accent2: "#06b6d4",
		icon: '<path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z" /><polyline points="3.27 6.96 12 12.01 20.73 6.96" /><line x1="12" y1="22.08" x2="12" y2="12" />',
		category: "红警开发工具",
	},
	{
		id: "hvabuilder",
		name: "HvaBuilder 动画编辑",
		englishName: "HvaBuilder",
		description: "HVA 动画文件编辑器，调整炮塔旋转与帧动画参数。",
		badge: "标准版",
		accent: "#f97316",
		accent2: "#ef4444",
		icon: '<polygon points="5 3 19 12 5 21 5 3" />',
		category: "红警开发工具",
	},
	{
		id: "shpbuilder",
		name: "SHP-Builder 图像编辑",
		englishName: "SHP Builder",
		description: "SHP 图像编辑器，支持帧动画、调色板与色系转换。",
		badge: "标准版",
		accent: "#ec4899",
		accent2: "#f97316",
		icon: '<rect x="3" y="3" width="18" height="18" rx="2" ry="2" /><circle cx="8.5" cy="8.5" r="1.5" /><polyline points="21 15 16 10 5 21" />',
		category: "红警开发工具",
	},
	{
		id: "aieditor",
		name: "AI 编辑器",
		englishName: "AI Editor",
		description: "红警电脑 AI 行为编辑器，控制建造序列与攻击倾向。",
		badge: "标准版",
		accent: "#ef4444",
		accent2: "#dc2626",
		icon: '<path d="M12 2a10 10 0 1 0 10 10A10 10 0 0 0 12 2z" /><path d="M9.09 9a3 3 0 0 1 5.83 1c0 2-3 3-3 3" /><line x1="12" y1="17" x2="12.01" y2="17" />',
		category: "红警开发工具",
	},
	{
		id: "csfeditor",
		name: "CSF 字符串编辑器",
		englishName: "CSF Editor",
		description: "编辑游戏中所有文本资源，支持多语言管理与批量修改。",
		badge: "标准版",
		accent: "#eab308",
		accent2: "#f97316",
		icon: '<polyline points="4 7 4 4 20 4 20 7" /><line x1="9" y1="20" x2="15" y2="20" /><line x1="12" y1="4" x2="12" y2="20" />',
		category: "红警开发工具",
	},
	{
		id: "covermaker",
		name: "封面制作器",
		englishName: "Cover Maker",
		description: "游戏载入图批量生成工具，内置大量红警风格模板。",
		badge: "标准版",
		accent: "#a855f7",
		accent2: "#ec4899",
		icon: '<rect x="3" y="3" width="18" height="18" rx="2" ry="2" /><line x1="3" y1="9" x2="21" y2="9" /><line x1="9" y1="21" x2="9" y2="9" />',
		category: "红警开发工具",
	},
];
