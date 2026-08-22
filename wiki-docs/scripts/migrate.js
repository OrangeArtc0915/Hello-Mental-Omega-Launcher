import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const SRC = path.resolve(__dirname, "../../github-wiki");
const DST = path.resolve(__dirname, "../docs");

// 目标结构:源文件 -> 目标文件(相对 docs/)
const FILES = {
  "Home.md": "index.md",
  // 标准版
  "HMOL-Home.md": "standard/index.md",
  "HMOL-Installation-Guide.md": "standard/installation.md",
  "HMOL-User-Guide.md": "standard/user-guide.md",
  "HMOL-Error-Codes.md": "standard/error-codes.md",
  "HMOL-Troubleshooting.md": "standard/troubleshooting.md",
  "HMOL-FAQ.md": "standard/faq.md",
  "HMOL-Contributing.md": "standard/contributing.md",
  // Wine 版
  "HMOL-Wine-Home.md": "wine/index.md",
  "HMOL-Wine-Project-Overview.md": "wine/project-overview.md",
  "HMOL-Wine-Installation-Guide.md": "wine/installation.md",
  "HMOL-Wine-User-Guide.md": "wine/user-guide.md",
  "HMOL-Wine-Error-Codes.md": "wine/error-codes.md",
  "HMOL-Wine-Error-Codes-Full.md": "wine/error-codes-full.md",
  "HMOL-Wine-Troubleshooting.md": "wine/troubleshooting.md",
  "HMOL-Wine-FAQ.md": "wine/faq.md",
  "HMOL-Wine-Contributing.md": "wine/contributing.md",
  // 联机模块
  "HMOL-Multiplayer-Home.md": "multiplayer/index.md",
  "HMOL-Multiplayer-Quick-Start.md": "multiplayer/quick-start.md",
  "HMOL-Multiplayer-Engines.md": "multiplayer/engines.md",
  "HMOL-Multiplayer-Hosting.md": "multiplayer/hosting.md",
  "HMOL-Multiplayer-Hall.md": "multiplayer/hall.md",
  "HMOL-Multiplayer-FileTransfer-HUD.md": "multiplayer/file-transfer-hud.md",
  "HMOL-Multiplayer-Toolbox.md": "multiplayer/toolbox.md",
  "HMOL-Multiplayer-FAQ.md": "multiplayer/faq.md",
  "HMOL-Multiplayer-Changelog.md": "multiplayer/changelog.md",
};

// 页面中文标题(用于 frontmatter)
const TITLES = {
  "Home.md": "HMOL 启动器 Wiki",
  "HMOL-Home.md": "HMOL 标准版",
  "HMOL-Installation-Guide.md": "安装指南",
  "HMOL-User-Guide.md": "使用说明",
  "HMOL-Error-Codes.md": "错误代码大全",
  "HMOL-Troubleshooting.md": "故障排查",
  "HMOL-FAQ.md": "常见问题 FAQ",
  "HMOL-Contributing.md": "贡献指南",
  "HMOL-Wine-Home.md": "HMOL Wine 版",
  "HMOL-Wine-Project-Overview.md": "项目概述",
  "HMOL-Wine-Installation-Guide.md": "安装指南",
  "HMOL-Wine-User-Guide.md": "使用说明",
  "HMOL-Wine-Error-Codes.md": "错误码速查",
  "HMOL-Wine-Error-Codes-Full.md": "错误码完整版",
  "HMOL-Wine-Troubleshooting.md": "问题排查",
  "HMOL-Wine-FAQ.md": "常见问题 FAQ",
  "HMOL-Wine-Contributing.md": "贡献指南",
  "HMOL-Multiplayer-Home.md": "HMOL 联机模块",
  "HMOL-Multiplayer-Quick-Start.md": "快速开始",
  "HMOL-Multiplayer-Engines.md": "四方案详解",
  "HMOL-Multiplayer-Hosting.md": "建房与加入",
  "HMOL-Multiplayer-Hall.md": "大厅与好友",
  "HMOL-Multiplayer-FileTransfer-HUD.md": "文件传输与 HUD",
  "HMOL-Multiplayer-Toolbox.md": "工具箱与设置",
  "HMOL-Multiplayer-FAQ.md": "常见问题",
  "HMOL-Multiplayer-Changelog.md": "更新日志",
};

// 内部链接: GitHub Wiki 页面名 -> VitePress 路径
const LINKS = {
  "Home": "/",
  "HMOL-Home": "/standard/",
  "HMOL-Installation-Guide": "/standard/installation",
  "HMOL-User-Guide": "/standard/user-guide",
  "HMOL-Error-Codes": "/standard/error-codes",
  "HMOL-Troubleshooting": "/standard/troubleshooting",
  "HMOL-FAQ": "/standard/faq",
  "HMOL-Contributing": "/standard/contributing",
  "HMOL-Wine-Home": "/wine/",
  "HMOL-Wine-Project-Overview": "/wine/project-overview",
  "HMOL-Wine-Installation-Guide": "/wine/installation",
  "HMOL-Wine-User-Guide": "/wine/user-guide",
  "HMOL-Wine-Error-Codes": "/wine/error-codes",
  "HMOL-Wine-Error-Codes-Full": "/wine/error-codes-full",
  "HMOL-Wine-Troubleshooting": "/wine/troubleshooting",
  "HMOL-Wine-FAQ": "/wine/faq",
  "HMOL-Wine-Contributing": "/wine/contributing",
  "HMOL-Multiplayer-Home": "/multiplayer/",
  "HMOL-Multiplayer-Quick-Start": "/multiplayer/quick-start",
  "HMOL-Multiplayer-Engines": "/multiplayer/engines",
  "HMOL-Multiplayer-Hosting": "/multiplayer/hosting",
  "HMOL-Multiplayer-Hall": "/multiplayer/hall",
  "HMOL-Multiplayer-FileTransfer-HUD": "/multiplayer/file-transfer-hud",
  "HMOL-Multiplayer-Toolbox": "/multiplayer/toolbox",
  "HMOL-Multiplayer-FAQ": "/multiplayer/faq",
  "HMOL-Multiplayer-Changelog": "/multiplayer/changelog",
};

// 已删除页面的链接, 做定向处理(保留文本, 去掉链接)
const BROKEN = [
  "HMOL-Wine-Security-and-License",
  "HMOL-Wine-Features",
];

function fixLinks(content) {
  // 处理已删除页面的链接: [文本](页面#锚点) -> 纯文本
  for (const page of BROKEN) {
    content = content.replace(
      new RegExp(`\\]?\\(${page.replace(/-/g, "\\-")}[^)]*\\)`, "g"),
      "",
    );
    content = content.replace(/\[([^\]]+)\]\(\)/g, "$1");
  }
  // 处理正常内部链接: ](Page) 或 ](Page#anchor)
  content = content.replace(/\]\(([A-Za-z0-9-]+?)(#[^)]*)?\)/g, (m, name, anchor) => {
    if (LINKS[name]) {
      return `](${LINKS[name]}${anchor || ""})`;
    }
    if (name === "Home") {
      return `](/${anchor || ""})`;
    }
    return m; // 未知链接(外部URL等)保持不变
  });
  return content;
}

let count = 0;
for (const [srcFile, dstRel] of Object.entries(FILES)) {
  const srcPath = path.join(SRC, srcFile);
  if (!fs.existsSync(srcPath)) {
    console.warn(`跳过(源文件不存在): ${srcFile}`);
    continue;
  }
  let content = fs.readFileSync(srcPath, "utf-8");
  content = fixLinks(content);

  const title = TITLES[srcFile] || srcFile.replace(/\.md$/, "");
  const fm = `---\ntitle: ${title}\n---\n\n`;
  // 去掉文件原有开头 H1(标题由 frontmatter 提供)
  content = content.replace(/^#\s+.+\n\n?/, "");

  const dstPath = path.join(DST, dstRel);
  fs.mkdirSync(path.dirname(dstPath), { recursive: true });
  fs.writeFileSync(dstPath, fm + content, "utf-8");
  count++;
  console.log(`已迁移: ${srcFile} -> docs/${dstRel}`);
}
console.log(`\n完成, 共迁移 ${count} 个文件 -> ${DST}`);
