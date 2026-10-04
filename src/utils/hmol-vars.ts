import { hmol } from "../data/hmol-info";

/**
 * 文档里的版本占位符 → 构建时的真实值。
 *
 * 版本号只在 `src/HMOL.Core/App/AppInfo.cs` 维护一处；文档（含 FAQ 聚合页这类自建渲染管线）
 * 统一调用这里替换，免得每次发版都要回头改一堆散落的 `v1.x.y`。
 *
 * 支持：
 * - `{{HMOL_VERSION}}`     → v1.5.3
 * - `{{HMOL_VERSION_NUM}}` → 1.5.3
 * - `{{HMOL_ZIP}}`         → HMOL-v1.5.3-win-x64.zip
 */
const TOKENS: ReadonlyArray<readonly [string, string]> = [
	["{{HMOL_VERSION}}", hmol.versionDisplay],
	["{{HMOL_VERSION_NUM}}", hmol.version],
	["{{HMOL_ZIP}}", hmol.zipName],
];

/** 是否含有需要替换的占位符（避免无谓地反复扫描长文本）。 */
export function hasHmolToken(value: string): boolean {
	return value.includes("{{HMOL_");
}

export function replaceHmolTokens(value: string): string {
	let result = value;
	for (const [token, text] of TOKENS) {
		if (result.includes(token)) result = result.split(token).join(text);
	}
	return result;
}
