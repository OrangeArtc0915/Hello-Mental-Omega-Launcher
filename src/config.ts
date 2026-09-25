import type {
	AnnouncementConfig,
	CommentConfig,
	ExpressiveCodeConfig,
	FooterConfig,
	FullscreenWallpaperConfig,
	LicenseConfig,
	MusicPlayerConfig,
	NavBarConfig,
	ProfileConfig,
	SakuraConfig,
	SidebarLayoutConfig,
	SiteConfig,
} from "./types/config";
import { HMOL, HMOL_LICENSE_URL } from "./constants/hmol";
import { LinkPreset } from "./types/config";

// 移除i18n导入以避免循环依赖

// 定义站点语言
const SITE_LANG = "zh_CN";

export const siteConfig: SiteConfig = {
	title: "HMOL 启动器",
	subtitle: HMOL.name,

	keywords: [
		"HMOL",
		"Hello Mental Omega Launcher",
		"心灵终结",
		"Mental Omega",
		"MO 启动器",
		"红色警戒2",
		"命令与征服",
	],

	lang: SITE_LANG,

	themeColor: {
		hue: 35, // 主题色的默认色相，范围从 0 到 360。例如：红色：0，青色：200，蓝绿色：250，粉色：345
		fixed: false, // 对访问者隐藏主题色选择器
	},

	// 个人博客模块的页面已从官网移除，仅保留数据与组件源码（改回时需一并恢复页面）
	featurePages: {
		anime: false,
		diary: false,
		friends: false,
		projects: false,
		skills: false,
		timeline: false,
		albums: false,
	},

	navbarTitle: {
		text: "HMOL",
		icon: "/brand/icon-128.png", // 启动器程序图标（由 AppIcon.ico 派生）
	},

	bangumi: {
		userId: "your-bangumi-id", // 在此处设置你的Bangumi用户ID，可以设置为 "sai" 测试
	},

	anime: {
		mode: "local", // 番剧页面模式："bangumi" 使用Bangumi API，"local" 使用本地配置
	},

	// 文章列表布局配置
	postListLayout: {
		// 默认布局模式："list" 列表模式（单列布局），"grid" 网格模式（双列布局）
		defaultMode: "grid",
		// 是否允许用户切换布局
		allowSwitch: true,
	},

	banner: {
		enable: true, // 是否启动Banner壁纸模式

		// 支持单张图片或图片数组，当数组长度 > 1 时自动启用轮播
		src: {
			desktop: ["/brand/bg-desktop.jpg"], // 桌面横幅图片（docs/111.jpg）
			mobile: ["/brand/bg-mobile.jpg"], // 移动横幅图片（docs/comment_*.jpg）
		}, // 使用本地横幅图片

		position: "center", // 等同于 object-position，仅支持 'top', 'center', 'bottom'。默认为 'center'

		carousel: {
			enable: false, // 为 true 时：为多张图片启用轮播。为 false 时：从数组中随机显示一张图片

			interval: 5, // 轮播间隔时间（秒）
		},

		waves: {
			enable: true, // 是否启用水波纹效果(这个功能比较吃性能)
			performanceMode: true, // 性能模式：减少动画复杂度(性能提升40%)
			mobileDisable: false, // 移动端禁用
		},

		// PicFlow API支持(智能图片API)
		imageApi: {
			enable: false, // 启用图片API
			url: "http://domain.com/api_v2.php?format=text&count=4", // API地址，返回每行一个图片链接的文本
		},
		// 这里需要使用PicFlow API的Text返回类型,所以我们需要format=text参数
		// 项目地址:https://github.com/matsuzaka-yuki/PicFlow-API
		// 请自行搭建API

		homeText: {
			enable: true,
			title: HMOL.name,

			subtitle: [
				HMOL.tagline,
				"多实例管理 · 资源包一键安装 · 备份还原",
				"内建联机组网 · 界面可自定义 · 扩展即 JSON",
			],
			typewriter: {
				enable: true, // 启用副标题打字机效果

				speed: 100, // 打字速度（毫秒）
				deleteSpeed: 50, // 删除速度（毫秒）
				pauseTime: 2000, // 完全显示后的暂停时间（毫秒）
			},
		},

		credit: {
			enable: false, // 显示横幅图片来源文本

			text: "Describe", // 要显示的来源文本
			url: "", // （可选）原始艺术品或艺术家页面的 URL 链接
		},

		navbar: {
			transparentMode: "semifull", // 导航栏透明模式："semi" 半透明加圆角，"full" 完全透明，"semifull" 动态透明
		},
	},
	toc: {
		enable: true, // 启用目录功能
		depth: 3, // 目录深度，1-6，1 表示只显示 h1 标题，2 表示显示 h1 和 h2 标题，依此类推
	},
	generateOgImages: false, // 启用生成OpenGraph图片功能,注意开启后要渲染很长时间，不建议本地调试的时候开启
	favicon: [
		// 站点图标 = 启动器程序图标（由 src/HMOL.App/Assets/AppIcon.ico 派生）
		{ src: "/brand/icon-32.png", sizes: "32x32" },
		{ src: "/brand/icon-128.png", sizes: "128x128" },
		{ src: "/brand/icon-180.png", sizes: "180x180" },
		{ src: "/brand/icon-192.png", sizes: "192x192" },
		{ src: "/brand/favicon.ico", sizes: "any" },
	],

	// 字体配置
	font: {
		zenMaruGothic: {
			enable: true, // 启用全局圆体适合日语和英语，对中文适配一般
		},
		hanalei: {
			enable: false, // 启用 Hanalei 字体作为全局字体，适合中文去使用
		},
	},
	showLastModified: true, // 控制“上次编辑”卡片显示的开关
};
export const fullscreenWallpaperConfig: FullscreenWallpaperConfig = {
	enable: true, // 启用全屏壁纸功能,非Banner模式下生效
	src: {
		desktop: ["/brand/bg-desktop.jpg"], // 桌面壁纸
		mobile: ["/brand/bg-mobile.jpg"], // 移动壁纸
	}, // 使用本地壁纸图片
	position: "center", // 壁纸位置，等同于 object-position
	carousel: {
		enable: false, // 启用轮播
		interval: 6, // 轮播间隔时间（秒）
	},
	zIndex: -1, // 层级，确保壁纸在背景层
	opacity: 0.8, // 壁纸透明度
	blur: 1, // 背景模糊程度
};

// 导航按官网路由契约配置：首页 / 下载 / 文档 / 更新日志 / 常见问题 / 关于
export const navBarConfig: NavBarConfig = {
	links: [
		LinkPreset.Home,
		{
			name: "下载",
			url: "/download/",
			icon: "material-symbols:download",
		},
		{
			name: "文档",
			url: "/docs/",
			icon: "material-symbols:menu-book",
		},
		{
			name: "更新日志",
			url: "/changelog/",
			icon: "material-symbols:new-releases",
		},
		{
			name: "常见问题",
			url: "/faq/",
			icon: "material-symbols:help",
		},
		{
			name: "关于",
			url: "/about/",
			icon: "material-symbols:info",
		},
	],
};

export const profileConfig: ProfileConfig = {
	avatar: "/brand/author-avatar.jpg", // docs/1414893933180198206.suf.jpg
	name: HMOL.author,
	bio: HMOL.tagline,
	typewriter: {
		enable: false,
		speed: 80,
	},
	links: [
		{
			name: "GitHub",
			icon: "fa6-brands:github",
			url: HMOL.github,
		},
		{
			name: "Gitee",
			icon: "simple-icons:gitee",
			url: HMOL.gitee,
		},
		{
			name: "QQ 群",
			icon: "fa6-brands:qq",
			url: HMOL.qqGroupUrl,
		},
	],
};

export const licenseConfig: LicenseConfig = {
	enable: true,
	name: `HMOL · ${HMOL.license}`,
	url: HMOL_LICENSE_URL,
};

export const expressiveCodeConfig: ExpressiveCodeConfig = {
	// 注意：某些样式（如背景颜色）已被覆盖，请参阅 astro.config.mjs 文件。
	// 请选择深色主题，因为此博客主题目前仅支持深色背景
	theme: "github-dark",
};

export const commentConfig: CommentConfig = {
	enable: false, // 启用评论功能。当设置为 false 时，评论组件将不会显示在文章区域。
	twikoo: {
		envId: "https://twikoo.vercel.app",
		lang: "en", // 设置 Twikoo 评论系统语言为英文
	},
};

export const announcementConfig: AnnouncementConfig = {
	title: "HMOL",
	content: `当前版本 ${HMOL.versionDisplay}（${HMOL.name}）。本软件为专有软件，保留所有权利；请仅从官方渠道下载。`,
	closable: false,
	link: {
		enable: true,
		text: "前往下载",
		url: "/download/",
		external: false,
	},
	link2: {
		enable: true,
		text: "查看文档",
		url: "/docs/",
		external: false,
	},
	link3: {
		enable: true,
		text: "加入 QQ 群",
		url: HMOL.qqGroupUrl,
		external: true,
	},
};

// 音乐播放器属于原个人博客模块，官网下线（保留配置项，改回 true 即可恢复）
export const musicPlayerConfig: MusicPlayerConfig = {
	enable: false,
	mode: "local",
};

export const footerConfig: FooterConfig = {
	enable: false, // 是否启用Footer HTML注入功能
};

// 直接编辑 FooterConfig.html 文件来添加备案号等自定义内容

/**
 * 侧边栏布局配置
 * 用于控制侧边栏组件的显示、排序、动画和响应式行为
 */
export const sidebarLayoutConfig: SidebarLayoutConfig = {
	// 是否启用侧边栏功能（恢复主题原版的侧栏，只保留资料卡与公告）
	enable: true,

	// 侧边栏位置：左侧或右侧
	position: "left",

	// 侧边栏组件配置列表（只保留官网用得上的资料卡与公告；
	// 分类/标签是博客向组件，已关闭，代码保留可随时改回）
	components: [
		{
			// 组件类型：用户资料组件
			type: "profile",
			// 是否启用该组件
			enable: true,
			// 组件显示顺序（数字越小越靠前）
			order: 1,
			// 组件位置："top" 表示固定在顶部
			position: "top",
			// CSS 类名，用于应用样式和动画
			class: "onload-animation",
			// 动画延迟时间（毫秒），用于错开动画效果
			animationDelay: 0,
		},
		{
			// 组件类型：公告组件
			type: "announcement",
			// 是否启用该组件（现在通过统一配置控制）
			enable: true,
			// 组件显示顺序
			order: 2,
			// 组件位置："top" 表示固定在顶部
			position: "top",
			// CSS 类名
			class: "onload-animation",
			// 动画延迟时间
			animationDelay: 50,
		},
		{
			// 组件类型：分类组件
			type: "categories",
			// 是否启用该组件（博客向，官网关闭）
			enable: false,
			// 组件显示顺序
			order: 3,
			// 组件位置："sticky" 表示粘性定位，可滚动
			position: "sticky",
			// CSS 类名
			class: "onload-animation",
			// 动画延迟时间
			animationDelay: 150,
			// 响应式配置
			responsive: {
				// 折叠阈值：当分类数量超过5个时自动折叠
				collapseThreshold: 5,
			},
		},
		{
			// 组件类型：标签组件
			type: "tags",
			// 是否启用该组件（博客向，官网关闭）
			enable: false,
			// 组件显示顺序
			order: 5,
			// 组件位置："sticky" 表示粘性定位
			position: "sticky",
			// CSS 类名
			class: "onload-animation",
			// 动画延迟时间
			animationDelay: 250,
			// 响应式配置
			responsive: {
				// 折叠阈值：当标签数量超过20个时自动折叠
				collapseThreshold: 20,
			},
		},
	],

	// 默认动画配置
	defaultAnimation: {
		// 是否启用默认动画
		enable: true,
		// 基础延迟时间（毫秒）
		baseDelay: 0,
		// 递增延迟时间（毫秒），每个组件依次增加的延迟
		increment: 50,
	},

	// 响应式布局配置
	responsive: {
		// 断点配置（像素值）
		breakpoints: {
			// 移动端断点：屏幕宽度小于768px
			mobile: 768,
			// 平板端断点：屏幕宽度小于1024px
			tablet: 1024,
			// 桌面端断点：屏幕宽度小于1280px
			desktop: 1280,
		},
		// 不同设备的布局模式
		//hidden:不显示侧边栏(桌面端)   drawer:抽屉模式(移动端不显示)   sidebar:显示侧边栏
		layout: {
			// 移动端：抽屉模式
			mobile: "sidebar",
			// 平板端：显示侧边栏
			tablet: "sidebar",
			// 桌面端：显示侧边栏
			desktop: "sidebar",
		},
	},
};

export const sakuraConfig: SakuraConfig = {
	enable: false, // 默认关闭樱花特效
	sakuraNum: 21, // 樱花数量
	limitTimes: -1, // 樱花越界限制次数，-1为无限循环
	size: {
		min: 0.5, // 樱花最小尺寸倍数
		max: 1.1, // 樱花最大尺寸倍数
	},
	opacity: {
		min: 0.3, // 樱花最小不透明度
		max: 0.9, // 樱花最大不透明度
	},
	speed: {
		horizontal: {
			min: -1.7, // 水平移动速度最小值
			max: -1.2, // 水平移动速度最大值
		},
		vertical: {
			min: 1.5, // 垂直移动速度最小值
			max: 2.2, // 垂直移动速度最大值
		},
		rotation: 0.03, // 旋转速度
		fadeSpeed: 0.03, // 消失速度，不应大于最小不透明度
	},
	zIndex: 100, // 层级，确保樱花在合适的层级显示
};

// Pio 看板娘配置（原个人博客模块，官网下线：enable 改回 true 即可恢复）
export const pioConfig: import("./types/config").PioConfig = {
	enable: false, // 启用看板娘
	models: ["live2d/firefly/FileReferences_Moc_0.model3.json"], // 看板娘模型路径（相对 public 的路径）
	position: "left", // 默认位置在右侧
	width: 280, // 默认宽度
	height: 280, // 默认高度
	mode: "draggable", // 默认为可拖拽模式
	hiddenOnMobile: false, // 默认在移动设备上隐藏
	dialog: {
		welcome: "欢迎来到 HMOL 官方网站", // 欢迎词
		touch: [
			"呜……不要乱摸啦～",
			"再碰我就要生气啦！",
			"那里不可以碰哦～",
			"痒痒的，快住手啦！",
		], // 触摸提示
		close: "QWQ 下次再见啦~", // 关闭提示
		link: HMOL.github, // 关于链接
	},
};

// 导出所有配置的统一接口
export const widgetConfigs = {
	profile: profileConfig,
	announcement: announcementConfig,
	music: musicPlayerConfig,
	layout: sidebarLayoutConfig,
	sakura: sakuraConfig,
	fullscreenWallpaper: fullscreenWallpaperConfig,
	pio: pioConfig, // 添加 pio 配置
} as const;

export const umamiConfig = {
	enabled: false, // 是否显示Umami统计
	apiKey: import.meta.env.UMAMI_API_KEY || "api_xxxxxxxx", // API密钥优先从环境变量读取，否则使用配置文件中的值
	baseUrl: "https://api.umami.is", // Umami Cloud API地址
	scripts: `
<script defer src="XXXX.XXX" data-website-id="ABCD1234"></script>
  `.trim(), // 上面填你要插入的Script,不用再去Layout中插入
} as const;
