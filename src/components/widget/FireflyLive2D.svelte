<script>
import { onMount, onDestroy } from "svelte";
import { pioConfig } from "@/config";
import { url } from "../../utils/url-utils";

const base = import.meta.env.BASE_URL;
const coreUrl = `${base}live2d/Core/live2dcubismcore.min.js`;
// 看板娘模型（相对 public 的路径，取自 pioConfig.models 首项，未配置时回退到流萤默认）
const modelUrl = url(
	(pioConfig.models?.[0]) || "live2d/firefly/FileReferences_Moc_0.model3.json",
);

let containerEl;
let canvasEl;
let model = null;
let app = null;
let dialogText = "";
let dialogVisible = false;
let dialogTimer = null;
let dragData = null;
let isLoaded = false;
let menuVisible = false;
let lastHitTime = 0;

// pixi 相关库只在客户端加载（其模块顶层引用了 window，不能在 SSR 中静态导入）
let PIXI = null;
let Live2DModel = null;

const dialog = pioConfig.dialog || {};
const touchMsgs = dialog.touch || ["Do not touch me~"];

function loadScript(src) {
	return new Promise((resolve, reject) => {
		const existed = document.querySelector(`script[src="${src}"]`);
		if (existed) {
			resolve();
			return;
		}
		const s = document.createElement("script");
		s.src = src;
		s.onload = () => resolve();
		s.onerror = () => reject(new Error(`Failed to load ${src}`));
		document.head.appendChild(s);
	});
}

function showDialog(text, duration = 3500) {
	dialogText = text;
	dialogVisible = true;
	clearTimeout(dialogTimer);
	dialogTimer = setTimeout(() => {
		dialogVisible = false;
	}, duration);
}

function findMotionIndex(group, name) {
	const defs = model?.internalModel?.motionManager?.definitions?.[group] || [];
	return defs.findIndex((d) => d.Name === name);
}

function findExpressionIndex(name) {
	const defs = model?.internalModel?.motionManager?.expressionManager?.definitions || [];
	return defs.findIndex((d) => d.Name === name);
}

async function playMotion(group, name, priority = 3 /* MotionPriority.FORCE */) {
	if (!model) return;
	const idx = findMotionIndex(group, name);
	if (idx < 0) return;
	try {
		await model.motion(group, idx, priority);
	} catch (e) {
		console.warn("Firefly motion error:", e);
	}
}

async function handleHit(names) {
	if (!model || !names?.length) return;
	// 记录命中时间，用于区分"部位互动点击"与"菜单点击"
	lastHitTime = Date.now();
	// 多个命中区域重叠时，按优先级处理一个（动作带语音的区域优先，饮料"回正"最后）
	const priority = ["蛋糕", "左侧后发", "刘海", "右侧后发", "饮料"];
	const name = [...names].sort(
		(a, b) => priority.indexOf(a) - priority.indexOf(b),
	)[0];
	if (!name) return;
	switch (name) {
		case "蛋糕":
			await playMotion("表情组", "使一颗心免于哀伤（点击）");
			showDialog("使一颗心免于哀伤", 5000);
			break;
		case "左侧后发":
			await playMotion("表情组", "点燃星海（点击）");
			showDialog("点燃星海，照亮你的旅途", 5000);
			break;
		case "刘海": {
			const i = findExpressionIndex("expression1.exp3");
			if (i >= 0) model.expression(i);
			showDialog("墨镜一戴，谁也不爱~");
			break;
		}
		case "右侧后发": {
			const i = findExpressionIndex("expression2.exp3");
			if (i >= 0) model.expression(i);
			showDialog("猫耳登场，可爱加倍~");
			break;
		}
		case "饮料": {
			const i = findExpressionIndex("expression00.exp3");
			if (i >= 0) model.expression(i);
			showDialog(dialog.welcome || "欢迎光临~");
			break;
		}
		default:
			showDialog(touchMsgs[Math.floor(Math.random() * touchMsgs.length)]);
	}
}

function onPointerDown(e) {
	if (pioConfig.mode !== "draggable" || !containerEl) return;
	const rect = containerEl.getBoundingClientRect();
	dragData = {
		startX: e.clientX,
		startY: e.clientY,
		rectLeft: rect.left,
		rectTop: rect.top,
		dragging: false,
	};
}

function onPointerMove(e) {
	if (!dragData) return;
	const dx = e.clientX - dragData.startX;
	const dy = e.clientY - dragData.startY;
	if (!dragData.dragging && Math.hypot(dx, dy) > 6) {
		dragData.dragging = true;
	}
	if (dragData.dragging && containerEl) {
		containerEl.style.left = `${dragData.rectLeft + dx}px`;
		containerEl.style.top = `${dragData.rectTop + dy}px`;
	}
}

function onPointerUp(e) {
	const wasDragging = dragData?.dragging || false;
	dragData = null;
	// 点击菜单自身不触发开关
	if (e.target?.closest?.(".firefly-menu")) return;
	// 单击（未拖拽）且最近 300ms 内未触发部位互动 → 弹出/切换功能菜单
	if (!wasDragging && Date.now() - lastHitTime > 300) {
		toggleMenu();
	}
}

function toggleMenu(force) {
	menuVisible = typeof force === "boolean" ? force : !menuVisible;
}

// 返回首页
function goHome() {
	toggleMenu(false);
	showDialog(dialog.home || "Click here to go back to homepage!", 2000);
	const home = url("/");
	// 优先使用 swup 无刷新导航，回退到整页跳转
	setTimeout(() => {
		if (window.swup?.navigate) {
			window.swup.navigate(window.location.origin + home);
		} else {
			window.location.href = home;
		}
	}, 600);
}

// 初始化渲染环境（Cubism Core + Pixi + 渲染器），仅执行一次
async function ensureRenderer() {
	if (app) return true;

	// 1. 加载 Live2D Cubism Core（Cubism 3 运行时）
	if (!window.Live2DCubismCore) {
		try {
			await loadScript(coreUrl);
		} catch (e) {
			console.error("Failed to load Live2D Cubism Core:", e);
			return false;
		}
	}
	if (!window.Live2DCubismCore) {
		console.error("Live2DCubismCore is not available");
		return false;
	}

	// 2. 动态加载 PixiJS 与 Live2D 渲染库（仅客户端）
	try {
		const pixi = await import("pixi.js");
		const l2d = await import("pixi-live2d-display/cubism4");
		PIXI = pixi;
		Live2DModel = l2d.Live2DModel;
		// 注册 Ticker，让模型可以随时间自动更新（眨眼/待机动画/物理）
		Live2DModel.registerTicker(PIXI.Ticker);
	} catch (e) {
		console.error("Failed to load pixi libraries:", e);
		return false;
	}

	// 3. 创建 Pixi 渲染器
	app = new PIXI.Application({
		view: canvasEl,
		backgroundAlpha: 0,
		autoStart: true,
		antialias: true,
		autoDensity: true,
		resolution: window.devicePixelRatio || 1,
	});
	return true;
}

// 加载看板娘模型并适配容器
async function loadModel() {
	if (!app || !Live2DModel) return;

	// 卸载旧模型
	if (model) {
		model.off("hit", handleHit);
		try {
			model.destroy({ children: true });
		} catch (e) {
			// 忽略销毁异常
		}
		model = null;
	}
	app.stage.removeChildren();

	// 4. 加载模型
	model = await Live2DModel.from(modelUrl, {
		autoInteract: true,
		idleMotionGroup: "Tick2",
		motionPreload: "IDLE",
	});
	// 预加载两个带语音的点击动作，保证点击时动作与声音及时触发
	for (const [group, motionName] of [
		["表情组", "使一颗心免于哀伤（点击）"],
		["表情组", "点燃星海（点击）"],
	]) {
		const idx = findMotionIndex(group, motionName);
		if (idx >= 0) {
			model.internalModel.motionManager
				.loadMotion(group, idx)
				.catch(() => {});
		}
	}
	// 用外层容器做缩放定位，保持模型自身坐标不变（否则点击命中的坐标换算会失效）
	const wrapper = new PIXI.Container();
	wrapper.addChild(model);
	app.stage.addChild(wrapper);

	// 5. 计算角色实际可见边界（用 getDrawableVertices 获取像素坐标，与显示/命中坐标一致）
	const [canvasW, canvasH] = model.internalModel.getSize();
	let minX = 0, minY = 0, maxX = canvasW, maxY = canvasH;
	try {
		const drawables = model.internalModel.coreModel.getModel().drawables;
		if (drawables && drawables.count > 0) {
			let bx1 = Infinity, by1 = Infinity, bx2 = -Infinity, by2 = -Infinity;
			for (let i = 0; i < drawables.count; i++) {
				if (drawables.opacities[i] <= 0) continue;
				const vp = model.internalModel.getDrawableVertices(i);
				if (!vp || !vp.length) continue;
				for (let j = 0; j < vp.length; j += 2) {
					const x = vp[j];
					const y = vp[j + 1];
					if (x < bx1) bx1 = x;
					if (x > bx2) bx2 = x;
					if (y < by1) by1 = y;
					if (y > by2) by2 = y;
				}
			}
			if (bx1 <= bx2 && by1 <= by2) {
				minX = bx1;
				minY = by1;
				maxX = bx2;
				maxY = by2;
			}
		}
	} catch (e) {
		console.warn("Failed to compute character bounds:", e);
	}
	// 留一点边距，避免动画（头发/裙摆）被裁掉
	const pad = Math.max(20, (maxY - minY) * 0.03);
	minX -= pad;
	minY -= pad;
	maxX += pad;
	maxY += pad;

	// 6. 按配置高度缩放并定位（像素坐标：原点在画布左上角，Y 轴向下）
	// 手机端按比例缩小（约 75%），避免超出窄屏
	const isMobile = window.innerWidth <= 768;
	const targetHeight = (pioConfig.height || 300) * (isMobile ? 0.75 : 1);
	const scale = targetHeight / (maxY - minY);
	wrapper.scale.set(scale);
	wrapper.x = -minX * scale;
	wrapper.y = -minY * scale;
	const cw = (maxX - minX) * scale;
	const ch = targetHeight;
	containerEl.style.width = `${cw}px`;
	containerEl.style.height = `${ch}px`;
	app.renderer.resize(cw, ch);

	// 初始位置
	const isLeft = pioConfig.position !== "right";
	const left = isLeft ? 0 : Math.max(0, window.innerWidth - cw - 12);
	const top = Math.max(0, window.innerHeight - ch - 12);
	containerEl.style.left = `${left}px`;
	containerEl.style.top = `${top}px`;
	isLoaded = true;

	// 7. 点击互动
	model.on("hit", handleHit);
}

async function init() {
	if (!pioConfig.enable) return;
	if (pioConfig.hiddenOnMobile && window.innerWidth <= 768) return;

	if (!(await ensureRenderer())) return;
	try {
		await loadModel();
	} catch (e) {
		console.error("Firefly Live2D init error:", e);
		return;
	}
	// 8. 欢迎语
	showDialog(dialog.welcome || "欢迎来到HMOL官方网站", 5000);
}

onMount(() => {
	init().catch((e) => console.error("Firefly Live2D init error:", e));
});

onDestroy(() => {
	if (app) {
		app.destroy(true, { children: true, texture: true });
		app = null;
	}
	model = null;
	clearTimeout(dialogTimer);
});
</script>

{#if pioConfig.enable}
	<div
		class="firefly-container"
		class:loaded={isLoaded}
		bind:this={containerEl}
		on:pointerdown={onPointerDown}
		on:pointermove={onPointerMove}
		on:pointerup={onPointerUp}
		on:pointercancel={onPointerUp}
	>
		{#if menuVisible}
			<div class="firefly-menu">
				<button class="firefly-menu-item" on:click={goHome}>🏠 返回首页</button>
			</div>
		{/if}
		{#if dialogVisible}
			<div class="firefly-dialog">{dialogText}</div>
		{/if}
		<canvas bind:this={canvasEl}></canvas>
	</div>
{/if}

<style>
	.firefly-container {
		position: fixed;
		z-index: 52;
		cursor: grab;
		user-select: none;
		pointer-events: none;
		opacity: 0;
		transition: opacity 0.5s ease;
		-webkit-user-select: none;
	}

	.firefly-container.loaded {
		pointer-events: auto;
		opacity: 1;
	}

	.firefly-container:active {
		cursor: grabbing;
	}

	.firefly-container canvas {
		width: 100%;
		height: 100%;
		display: block;
	}

	.firefly-dialog {
		position: absolute;
		bottom: calc(100% + 0.5em);
		left: 50%;
		transform: translateX(-50%);
		min-width: 8em;
		max-width: 14em;
		font-size: 0.8em;
		line-height: 1.5;
		color: #333;
		background: rgba(255, 255, 255, 0.95);
		padding: 0.6em 0.9em;
		border-radius: 1em;
		box-shadow: 0 2px 12px rgba(0, 0, 0, 0.12);
		border: 1px solid rgba(0, 0, 0, 0.06);
		word-break: break-all;
		white-space: pre-wrap;
		pointer-events: none;
	}

	.firefly-menu {
		position: absolute;
		bottom: calc(100% + 0.5em);
		left: 50%;
		transform: translateX(-50%);
		display: flex;
		flex-direction: column;
		gap: 0.35em;
		min-width: 7em;
		font-size: 0.8em;
		background: rgba(255, 255, 255, 0.95);
		padding: 0.4em;
		border-radius: 0.8em;
		box-shadow: 0 2px 12px rgba(0, 0, 0, 0.12);
		border: 1px solid rgba(0, 0, 0, 0.06);
		pointer-events: auto;
	}

	.firefly-menu-item {
		display: flex;
		align-items: center;
		gap: 0.4em;
		width: 100%;
		padding: 0.4em 0.7em;
		border: none;
		border-radius: 0.5em;
		background: transparent;
		color: #333;
		font-size: 1em;
		line-height: 1.4;
		text-align: left;
		cursor: pointer;
		white-space: nowrap;
	}

	.firefly-menu-item:hover {
		background: rgba(0, 0, 0, 0.06);
	}
</style>
