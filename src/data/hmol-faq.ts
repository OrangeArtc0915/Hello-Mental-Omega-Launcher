/**
 * /faq/ 页面数据：**在构建时直接解析仓库里的问答文档**，页面不手工誊抄答案，
 * 避免与文档各处口径不一致。解析失败只跳过该来源并打印警告，不会让构建失败。
 *
 * 来源（全部为仓库内真实文件）：
 * - `src/content/posts/hmol-faq.md`                站内文章：常见问题
 * - `src/content/docs/faq.md`                      文档「常见问题」章
 * - `src/content/docs/multiplayer/faq.md`          文档「联机 → 常见问题」页
 * 另附 `src/content/docs/error-codes.md` 的「提示信息」分区索引。
 */
import fs from "node:fs";
import path from "node:path";
import { docsUrl, renderDocInline, renderDocMarkdown } from "./hmol-docs";
import { url } from "../utils/url-utils";

export interface FaqItem {
	/** 纯文本问题（用于锚点/搜索） */
	question: string;
	/** 渲染后的问题 HTML（问题里可能有 `code`） */
	questionHtml: string;
	/** 渲染后的答案 HTML */
	answerHtml: string;
}

export interface FaqGroup {
	id: string;
	/** 形如「常见问题 FAQ · 安装与启动」 */
	title: string;
	/** 该组所属来源（文档页面） */
	docName: string;
	docHref: string;
	items: FaqItem[];
}

interface FaqSource {
	id: string;
	/** 来源显示名 */
	label: string;
	/** 完整文本对应的地址 */
	docName: string;
	docHref: string;
	/** 相对 Web 目录的文件路径 */
	file: string;
}

const SOURCES: FaqSource[] = [
	{
		id: "post",
		label: "站内文章",
		docName: "《HMOL 常见问题解答》",
		docHref: url("/posts/hmol-faq/"),
		file: "src/content/posts/hmol-faq.md",
	},
	{
		id: "faq",
		label: "常见问题 FAQ",
		docName: "常见问题 FAQ",
		docHref: docsUrl("faq"),
		file: "src/content/docs/faq.md",
	},
	{
		id: "multiplayer",
		label: "联机 · 常见问题",
		docName: "联机 · 常见问题",
		docHref: docsUrl("multiplayer/faq"),
		file: "src/content/docs/multiplayer/faq.md",
	},
];

/** 去掉问题前的编号与 Q 前缀：`Q1. xxx` / `Q: xxx` / `Q12：xxx` */
function normalizeQuestion(raw: string): string {
	return raw
		.trim()
		.replace(/^#{1,6}\s*/, "")
		.replace(/^\**Q\d*\**\s*[:：.、]?\s*/i, "")
		.trim();
}

/** 去掉答案尾部只由分隔线/换行组成的部分 */
function cleanAnswer(lines: string[]): string {
	const trimmed = [...lines];
	const isSeparator = (line: string) =>
		/^\s*(?:\*\s*\*\s*\*+|-{3,}|_{3,}|<br\s*\/?>)\s*$/.test(line);
	while (trimmed.length > 0 && (trimmed[0].trim() === "" || isSeparator(trimmed[0]))) {
		trimmed.shift();
	}
	while (
		trimmed.length > 0 &&
		(trimmed[trimmed.length - 1].trim() === "" ||
			isSeparator(trimmed[trimmed.length - 1]))
	) {
		trimmed.pop();
	}
	return trimmed.join("\n").trim();
}

/** 把一篇 FAQ markdown 解析成「二级标题分组 + 问答条目」 */
function parseFaq(
	md: string,
): { section: string; items: { q: string; a: string }[] }[] {
	const groups: { section: string; items: { q: string; a: string }[] }[] = [];
	let section: string | null = null;
	let current: { q: string; lines: string[] } | null = null;

	const flushItem = () => {
		if (!current || !section) {
			current = null;
			return;
		}
		const answer = cleanAnswer(current.lines);
		if (answer) {
			groups[groups.length - 1].items.push({ q: current.q, a: answer });
		}
		current = null;
	};

	for (const line of md.split(/\r?\n/)) {
		// 只认 `## `（二级）作为分组；`# ` 一级标题（文档大标题）跳过
		const h2 = /^##\s+(.+?)\s*$/.exec(line);
		if (h2) {
			flushItem();
			section = h2[1];
			groups.push({ section, items: [] });
			continue;
		}
		if (/^#\s+/.test(line)) {
			flushItem();
			continue;
		}

		const h3 = /^###\s+(.+?)\s*$/.exec(line);
		const qBold1 = /^\*\*Q\*\*\s*[:：]\s*(.+?)\s*$/.exec(line);
		const qBold2 = /^\*\*Q\s*[:：]\s*(.+?)\*\*\s*$/.exec(line);
		const questionRaw = h3?.[1] ?? qBold1?.[1] ?? qBold2?.[1];

		if (questionRaw !== undefined && section) {
			flushItem();
			const q = normalizeQuestion(questionRaw);
			if (q) {
				current = { q, lines: [] };
				continue;
			}
		}

		if (current) current.lines.push(line);
	}
	flushItem();

	return groups;
}

function loadFaqGroups(source: FaqSource): FaqGroup[] {
	const abs = path.resolve(process.cwd(), source.file);
	try {
		const md = fs.readFileSync(abs, "utf-8");
		const parsed = parseFaq(md);
		const groups = parsed
			.filter((g) => g.items.length > 0)
			.map((g) => ({
				id: `${source.id}-${g.section}`,
				title: `${source.label} · ${g.section}`,
				docName: source.docName,
				docHref: source.docHref,
				items: g.items.map((item) => ({
					question: item.q,
					questionHtml: renderDocInline(item.q),
					answerHtml: renderDocMarkdown(item.a),
				})),
			}));
		if (groups.length === 0) {
			console.warn(`[hmol-faq] ${source.file} 没有解析出任何问答，请检查格式`);
		}
		return groups;
	} catch (error) {
		console.warn(
			`[hmol-faq] 读取 ${abs} 失败，已跳过该来源：${(error as Error).message}`,
		);
		return [];
	}
}

/** 全部问答分组（按来源顺序：站内文章 → 文档常见问题 → 文档联机常见问题） */
export const faqGroups: FaqGroup[] = SOURCES.flatMap(loadFaqGroups);

/** 分组总数与条目总数，用于页面头部统计 */
export const faqStats = {
	groups: faqGroups.length,
	items: faqGroups.reduce((sum, g) => sum + g.items.length, 0),
};

export interface ErrorCodeCategory {
	/** 分区自带的错误代码（如 `E12.01`）；当前实现绝大多数提示没有代码，这时用通用标记 */
	code: string;
	category: string;
	/** 分区里的「代码位置」一行（没有就留空） */
	scene: string;
	href: string;
}

/**
 * 标题锚点：与 Astro 渲染标题时用的 rehype-slug（github-slugger）规则一致 ——
 * 小写 → 去掉标点与符号 → 空格转 `-`，这样链接能落到对应小节。
 * 本页分区标题都是纯中文（无空格与标点），因此与文档正文里的锚点同名。
 */
function headingSlug(text: string): string {
	return text
		.toLowerCase()
		.replace(/[^\p{L}\p{M}\p{N}\s-]/gu, "")
		.replace(/ /g, "-");
}

/**
 * 提示信息索引：取自 `src/content/docs/error-codes.md`（该页现名为「错误信息与解决」）。
 * - 分类：正文里的一级分区标题，如 `## 启动与门锁`（跳过「📑 目录」与附录；锚点按 rehype-slug 规则推算）
 * - 代码：分区内保留的错误代码（如 `E12.01`）。当前 C# 实现绝大多数提示没有错误代码，
 *   这时用「提示」这个通用标记，**不臆造代码**
 * - 来源：分区里的 `**代码位置**：src/...` 一行，便于定位这条提示由哪段逻辑发出
 * 每条只做到分区级跳转，具体提示与处理方式仍在文档页里。
 */
function loadErrorCodeIndex(): ErrorCodeCategory[] {
	const abs = path.resolve(process.cwd(), "src/content/docs/error-codes.md");
	try {
		const md = fs.readFileSync(abs, "utf-8");

		const result: ErrorCodeCategory[] = [];
		const seen = new Set<string>();

		// 按 `## ` 分区切块，这样能取到「该分区内有没有错误代码 / 代码位置」
		for (const block of md.split(/\r?\n(?=##\s)/)) {
			const heading = /^##\s+(.+?)\s*$/m.exec(block);
			if (!heading) continue;

			const category = heading[1].replace(/[`*#]/g, "").trim();
			if (category.length === 0) continue;

			// 目录与附录不是提示分区
			if (/目录|附录/.test(category)) continue;
			if (seen.has(category)) continue;
			seen.add(category);

			const code = /`(E\d+(?:\.\d+)?)`/.exec(block)?.[1] ?? "提示";
			const scene = /\*\*代码位置\*\*[：:]\s*`?([^`\n]+?)`?\s*$/m.exec(block)?.[1]?.trim() ?? "";

			result.push({
				code,
				category,
				scene,
				href: docsUrl("error-codes", `#${headingSlug(category)}`),
			});
		}

		if (result.length === 0) {
			console.warn("[hmol-faq] 没有从 docs/error-codes.md 解析到提示分区");
		}
		return result;
	} catch (error) {
		console.warn(
			`[hmol-faq] 读取 ${abs} 失败，提示分区索引留空：${(error as Error).message}`,
		);
		return [];
	}
}

export const errorCodeIndex: ErrorCodeCategory[] = loadErrorCodeIndex();

/** 提示信息文档在新文档路由下的地址 */
export const errorCodeDocLinks = {
	errorCodes: docsUrl("error-codes"),
} as const;
