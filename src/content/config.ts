import { defineCollection, z } from "astro:content";

const postsCollection = defineCollection({
	schema: z.object({
		title: z.string(),
		published: z.date(),
		updated: z.date().optional(),
		draft: z.boolean().optional().default(false),
		description: z.string().optional().default(""),
		image: z.string().optional().default(""),
		tags: z.array(z.string()).optional().default([]),
		category: z.string().optional().nullable().default(""),
		lang: z.string().optional().default(""),
		pinned: z.boolean().optional().default(false),
		author: z.string().optional().default(""),
		sourceLink: z.string().optional().default(""),
		licenseName: z.string().optional().default(""),
		licenseUrl: z.string().optional().default(""),

		/* Page encryption fields */
		encrypted: z.boolean().optional().default(false),
		password: z.string().optional().default(""),

		

		/* For internal use */
		prevTitle: z.string().default(""),
		prevSlug: z.string().default(""),
		nextTitle: z.string().default(""),
		nextSlug: z.string().default(""),
	}),
});
const specCollection = defineCollection({
	schema: z.object({}),
});
/**
 * 文档集合：`src/content/docs/**` 下的 Markdown 就是官网文档正文，
 * 由 `src/pages/docs/[...slug].astro` 渲染到 `/docs/<slug>/`
 * （`xxx.md` → `/docs/xxx/`，`multiplayer/index.md` → `/docs/multiplayer/`）。
 */
const docsCollection = defineCollection({
	schema: z.object({
		title: z.string(),
		description: z.string().optional().default(""),
		/** 所属章节 id，取值见 src/data/hmol-docs.ts 的 DOC_SECTIONS */
		section: z.string(),
		/** 全书顺序，决定文档首页的排列与文档页的「上一篇 / 下一篇」 */
		order: z.number(),
	}),
});
export const collections = {
	posts: postsCollection,
	spec: specCollection,
	docs: docsCollection,
};
