import { defineCollection, z } from "astro:content";
import { glob } from "astro/loaders";

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
const resourcesCollection = defineCollection({
	// 从项目根 Resource 目录自动读取：Resource/<分类文件夹>/*.md
	loader: glob({
		pattern: ["**/*.md", "!#TOOLS/**"],
		base: "./Resource",
	}),
	schema: z.object({
		title: z.string(),
		icon: z.string().default(""),
		description: z.string().default(""),
		link: z.string().optional().default(""),
		image: z.string().optional().default(""),
		tags: z.array(z.string()).default([]),
		order: z.number().optional().default(0),
		author: z.string().default(""),
		gameVersion: z.string().default(""),
		version: z.string().default(""),
		mode: z.enum(["", "single", "multi", "both"]).default(""),
		links: z
			.array(z.object({ name: z.string(), url: z.string() }))
			.default([]),
	}),
});
export const collections = {
	posts: postsCollection,
	spec: specCollection,
	resources: resourcesCollection,
};
