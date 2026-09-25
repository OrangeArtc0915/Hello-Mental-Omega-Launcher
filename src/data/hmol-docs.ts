/**
 * 文档区（`src/content/docs/` 内容集合）在官网里的共用定义与工具。
 *
 * 事实来源：
 * - 文档正文 = `src/content/docs/**`（Astro 内容集合 `docs`，schema 见 `src/content/config.ts`）
 * - 页面路由 = `/docs/<slug>/`（由 `src/pages/docs/[...slug].astro` 生成）
 * - 章节划分与顺序 = 下面的 `DOC_SECTIONS`，各页用 frontmatter 的 `section` 字段归属章节
 *
 * 同时提供 markdown → HTML 的渲染工具（marked 已是本项目既有依赖，未新增任何依赖），
 * 并把文档里的站内绝对链接（如 `/docs/faq/`）补上部署 base。
 */
import { marked } from "marked";
import { url } from "../utils/url-utils";

/** 一个章节（文档首页按此顺序分组，也是文档页「上一篇 / 下一篇」的章节顺序） */
export interface DocSection {
	id: string;
	title: string;
	/** 一句话说明，用在文档首页的章节标题下 */
	desc: string;
}

export const DOC_SECTIONS: DocSection[] = [
	{
		id: "start",
		title: "入门",
		desc: "这是一个什么程序、能做什么、界面上都有什么。",
	},
	{
		id: "install",
		title: "安装",
		desc: "系统要求、下载渠道、解压与首次启动、升级与卸载。",
	},
	{
		id: "usage",
		title: "使用",
		desc: "六个页面的功能说明与日常使用流程。",
	},
	{
		id: "multiplayer",
		title: "联机",
		desc: "主程序内置的「联机」页：组网方案、建房与加入、大厅、文件传输与工具箱。",
	},
	{
		id: "troubleshoot",
		title: "排查",
		desc: "常见故障的排查顺序与日志查看方法。",
	},
	{
		id: "faq",
		title: "常见问题",
		desc: "按主题汇总的高频问答。",
	},
	{
		id: "errors",
		title: "错误信息",
		desc: "界面实际会出现的提示信息与对应处理方式。",
	},
	{
		id: "contributing",
		title: "贡献",
		desc: "可接受与不接受的贡献方式、反馈渠道。",
	},
];

/**
 * 文档页 slug：内容集合里 `xxx.md` → `xxx`，`multiplayer/index.md` → `multiplayer`。
 * 直接用内容集合的 `id`（相对 `src/content/docs/` 的路径），保证路由与目录结构一一对应。
 */
export function docSlug(id: string): string {
	return id.replace(/\.mdx?$/i, "").replace(/(^|\/)index$/, "");
}

/** 拼一个文档页地址（带上部署 base）；`slug` 留空即文档首页，`hash` 传 `#xxx` */
export function docsUrl(slug = "", hash = ""): string {
	return `${url(slug ? `/docs/${slug}/` : "/docs/")}${hash}`;
}

/** 章节定义（找不到时返回 undefined，由调用方决定怎么显示） */
export function docSection(id: string): DocSection | undefined {
	return DOC_SECTIONS.find((section) => section.id === id);
}

/** 把文档里站点内的绝对链接（`/docs/faq/`）补上部署 base，外部链接原样保留 */
export function rewriteDocLinks(html: string): string {
	const siteBase = import.meta.env.BASE_URL;
	return html.replace(/href="(\/(?!\/)[^"]*)"/g, (whole, href: string) => {
		if (href.startsWith(siteBase)) return whole; // 已是带 base 的站内地址
		return `href="${url(href)}"`;
	});
}

/** 渲染文档片段为 HTML（用于 FAQ 答案等取自文档的正文） */
export function renderDocMarkdown(md: string): string {
	const html = marked.parse(md, { async: false, gfm: true }) as string;
	return rewriteDocLinks(html);
}

/** 渲染单行文档片段为 HTML（用于标题行） */
export function renderDocInline(md: string): string {
	const html = marked.parseInline(md, { async: false, gfm: true }) as string;
	return rewriteDocLinks(html);
}
