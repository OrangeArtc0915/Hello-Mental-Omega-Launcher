/**
 * 更新日志数据：直接复用 `src/content/posts/hmol-changelog.md`（posts 集合），
 * 不在页面里另写一份版本历史。
 *
 * - 版本号：优先从标题里抓 `vX.Y[.Z]`（当前标题 → v1.0.0），标题里抓不到时回退到 slug
 * - 日期：frontmatter 的 `published`
 * - 要点：正文里的二/三级小标题原文（不加工、不压缩，原文说啥就是啥）
 *
 * 仓库里目前有几篇就算几篇；没有的版本不会出现在页面上（缺的版本留空）。
 */
import { getCollection } from "astro:content";
import { url } from "../utils/url-utils";

export interface ChangelogEntry {
	slug: string;
	title: string;
	/** 形如 "v1.0.0"，抓不到则留空 */
	version: string;
	/** frontmatter 的 published，格式化后的 YYYY-MM-DD */
	dateText: string;
	published: Date;
	updatedText: string;
	description: string;
	tags: string[];
	/** 正文里的小标题，按出现顺序 */
	highlights: string[];
	/** 站内全文地址（文章页） */
	href: string;
}

function pickVersion(title: string, slug: string): string {
	const matched = /v?(\d+\.\d+(?:\.\d+)?)/i.exec(title) ?? /v(\d+\.\d+(?:\.\d+)?)/i.exec(slug);
	return matched ? `v${matched[1]}` : "";
}

function pickHighlights(body: string): string[] {
	const headings = [...body.matchAll(/^#{2,3}\s+(.+?)\s*$/gm)].map((m) =>
		m[1].trim(),
	);
	return [...new Set(headings)];
}

function formatDate(date: Date): string {
	return date.toISOString().slice(0, 10);
}

async function loadChangelog(): Promise<ChangelogEntry[]> {
	const posts = await getCollection("posts", ({ data, slug }) => {
		// 只取更新日志：文件名为 hmol-changelog.md（版本化命名如 hmol-v1.1-changelog.md 也认）
		if (!/^hmol-(?:.*-)?changelog$/.test(slug)) return false;
		return data.draft !== true;
	});

	return posts
		.map((post) => ({
			slug: post.slug,
			title: post.data.title,
			version: pickVersion(post.data.title, post.slug),
			dateText: formatDate(post.data.published),
			published: post.data.published,
			updatedText: post.data.updated ? formatDate(post.data.updated) : "",
			description: post.data.description ?? "",
			tags: post.data.tags ?? [],
			highlights: pickHighlights(post.body ?? ""),
			href: url(`/posts/${post.slug}/`),
		}))
		.sort((a, b) => b.published.getTime() - a.published.getTime());
}

export const changelogEntries: ChangelogEntry[] = await loadChangelog();

/** 最新一篇更新日志的日期（没有更新日志时留空） */
export const latestChangelogDate: string =
	changelogEntries[0]?.dateText ?? "";
