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
}

export const dlcItems: DLCItem[] = [
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
	},
];
