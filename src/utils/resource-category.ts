// Resource 目录下的分类文件夹名 → 展示分类名
// 注意：Astro glob loader 生成的 id 会将 ASCII 部分小写化（如 GameFile → gamefile），
// 因此这里的 key 统一使用小写。
export const RESOURCE_CATEGORY_MAP: Record<string, string> = {
	ini: "INI包",
	mission: "任务包",
	map: "地图包",
	beautification: "美化包",
	music: "音乐包",
	voice: "语音包",
	plugin: "插件包",
	gamefile: "游戏实例",
};

// 资源集合 id 格式为 "<文件夹>/<文件名>"，按第一段文件夹名推导分类
export function getResourceCategory(id: string): string {
	const folder = id.split("/")[0].toLowerCase();
	return RESOURCE_CATEGORY_MAP[folder] || folder;
}
