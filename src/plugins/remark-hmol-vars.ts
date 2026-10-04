import { visit } from "unist-util-visit";
import { hasHmolToken, replaceHmolTokens } from "../utils/hmol-vars.ts";

/**
 * 把 Markdown 正文里的版本占位符替换成构建时读到的真实值。
 *
 * 版本号只在 `src/HMOL.Core/App/AppInfo.cs` 维护一处，文档里写占位符即可，
 * 免得每次发版都要回头改一堆散落的 `v1.x.y`、改漏了就出现「文档还写着老版本」。
 *
 * 正文、行内代码（`code`）与围栏代码块都替换——文档里的「分享文本示例」「问题反馈模板」
 * 本来就是给用户照抄的，里面也该是当前版本号。
 *
 * 占位符与替换规则见 `src/utils/hmol-vars.ts`（FAQ 聚合页那条自建渲染管线用的是同一个函数）。
 */
export function remarkHmolVars() {
	return (tree: unknown) => {
		visit(tree as never, ["text", "inlineCode", "code"] as never, (node: { value?: string }) => {
			if (typeof node.value === "string" && hasHmolToken(node.value)) {
				node.value = replaceHmolTokens(node.value);
			}
		});
	};
}
