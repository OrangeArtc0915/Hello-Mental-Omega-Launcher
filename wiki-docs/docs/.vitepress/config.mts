import { defineConfig } from "vitepress";

// 与主站 (Astro) 一起部署：主站 base 为 /Hello-Mental-Omega-Launcher/，wiki 位于其下的 /wiki/ 子路径
const base = "/Hello-Mental-Omega-Launcher/wiki/";

export default defineConfig({
  base,
  // 相对 docs 目录输出到主站 dist/wiki，与主站合并部署
  outDir: "../../dist/wiki",
  lang: "zh-CN",
  title: "HMOL 启动器 Wiki",
  description: "Hello Mental Omega Launcher (HMOL) 官方 Wiki - 标准版 / Wine 版 / 联机模块",

  head: [
    ["link", { rel: "icon", type: "image/svg+xml", href: `${base}hmol-logo.svg` }],
  ],

  themeConfig: {
    logo: "/hmol-logo.svg",
    siteTitle: "HMOL 启动器 Wiki",

    nav: [
      { text: "首页", link: "/" },
      { text: "标准版", link: "/standard/" },
      { text: "Wine 版", link: "/wine/" },
      { text: "联机模块", link: "/multiplayer/" },
      { text: "GitHub", link: "https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher" },
    ],

    sidebar: {
      "/standard/": [
        {
          text: "HMOL 标准版",
          collapsed: false,
          items: [
            { text: "标准版首页", link: "/standard/" },
            { text: "安装指南", link: "/standard/installation" },
            { text: "使用说明", link: "/standard/user-guide" },
            { text: "错误代码大全", link: "/standard/error-codes" },
            { text: "故障排查", link: "/standard/troubleshooting" },
            { text: "常见问题 FAQ", link: "/standard/faq" },
            { text: "贡献指南", link: "/standard/contributing" },
          ],
        },
      ],
      "/wine/": [
        {
          text: "HMOL Wine 版",
          collapsed: false,
          items: [
            { text: "Wine 版首页", link: "/wine/" },
            { text: "项目概述", link: "/wine/project-overview" },
            { text: "安装指南", link: "/wine/installation" },
            { text: "使用说明", link: "/wine/user-guide" },
            { text: "错误码速查", link: "/wine/error-codes" },
            { text: "错误码完整版", link: "/wine/error-codes-full" },
            { text: "问题排查", link: "/wine/troubleshooting" },
            { text: "常见问题 FAQ", link: "/wine/faq" },
            { text: "贡献指南", link: "/wine/contributing" },
          ],
        },
      ],
      "/multiplayer/": [
        {
          text: "HMOL 联机模块",
          collapsed: false,
          items: [
            { text: "联机模块首页", link: "/multiplayer/" },
            { text: "快速开始", link: "/multiplayer/quick-start" },
            { text: "四方案详解", link: "/multiplayer/engines" },
            { text: "建房与加入", link: "/multiplayer/hosting" },
            { text: "大厅与好友", link: "/multiplayer/hall" },
            { text: "文件传输与 HUD", link: "/multiplayer/file-transfer-hud" },
            { text: "工具箱与设置", link: "/multiplayer/toolbox" },
            { text: "常见问题", link: "/multiplayer/faq" },
            { text: "更新日志", link: "/multiplayer/changelog" },
          ],
        },
      ],
    },

    outline: { label: "本页目录", level: [2, 3] },

    docFooter: { prev: "上一页", next: "下一页" },

    returnToTopLabel: "返回顶部",
    sidebarMenuLabel: "菜单",
    darkModeSwitchLabel: "外观",
    lightModeSwitchTitle: "切换到浅色模式",
    darkModeSwitchTitle: "切换到深色模式",

    search: {
      provider: "local",
      options: {
        translations: {
          button: { buttonText: "搜索文档", buttonAriaLabel: "搜索文档" },
          modal: {
            displayDetails: "显示详细列表",
            resetButtonTitle: "清空搜索条件",
            backButtonTitle: "关闭搜索",
            noResultsText: "未找到相关结果",
            footer: { selectText: "选择", navigateText: "切换", closeText: "关闭" },
          },
        },
      },
    },

    footer: {
      message: "本启动器与 EA、《红色警戒 2》开发团队或 Mental Omega 开发团队均无任何关联。",
      copyright: "© 2026 HMOL Contributors. All Rights Reserved.",
    },

    externalLinkIcon: true,
  },
});
