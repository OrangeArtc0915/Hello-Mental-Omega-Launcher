import base64
import io
import json
import os
import re
import sys
import tkinter as tk
from tkinter import filedialog, messagebox, ttk

from PIL import Image, ImageOps

QQ_GROUPS = [
    ("HMOL官方1群", "1034243331", "https://qm.qq.com/q/1034243331"),
    ("HMOL官方2群", "1092671790", "https://qm.qq.com/q/1092671790"),
]

CATEGORY_NAMES = {
    "ini": "INI包",
    "mission": "任务包",
    "map": "地图包",
    "beautification": "美化包",
    "music": "音乐包",
    "voice": "语音包",
    "plugin": "插件包",
    "GameFile": "游戏实例",
}

IMG_EXT = {".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"}

FONT = ("Microsoft YaHei UI", 10)
FONT_SMALL = ("Microsoft YaHei UI", 9)
BG = "#f5f6f8"


def resource_dir():
    if getattr(sys, "frozen", False):
        exe_dir = os.path.dirname(sys.executable)
    else:
        exe_dir = os.path.dirname(os.path.abspath(__file__))
    return os.path.dirname(exe_dir)


def clean_filename(name):
    name = name.strip()
    name = re.sub(r'[<>:"/\\|?*]', "_", name)
    return name or "未命名"


def yaml_str(value):
    return json.dumps(str(value), ensure_ascii=False)


def compress_image(path, max_side=1280, quality=80):
    im = Image.open(path)
    im = ImageOps.exif_transpose(im)
    im.thumbnail((max_side, max_side), Image.Resampling.LANCZOS)
    if im.mode in ("RGBA", "LA", "P"):
        im = im.convert("RGBA")
        bg = Image.new("RGB", im.size, (255, 255, 255))
        bg.paste(im, mask=im.split()[-1])
        im = bg
    else:
        im = im.convert("RGB")
    buf = io.BytesIO()
    im.save(buf, format="JPEG", quality=quality, optimize=True)
    return buf.getvalue(), "image/jpeg"


def build_md(title, description, author, gamever, version, mode, tags,
             links, intro, screenshot_paths):
    lines = ["---"]
    lines.append(f"title: {yaml_str(title)}")
    lines.append(f"description: {yaml_str(description)}")
    lines.append(f"author: {yaml_str(author)}")
    lines.append(f"gameVersion: {yaml_str(gamever)}")
    lines.append(f"version: {yaml_str(version)}")
    lines.append(f"mode: {yaml_str(mode)}")
    if links:
        lines.append("links:")
        for name, url in links:
            lines.append(f"  - name: {yaml_str(name)}")
            lines.append(f"    url: {yaml_str(url)}")
    lines.append(f"tags: {json.dumps(tags, ensure_ascii=False)}")
    lines.append("order: 0")
    lines.append("---")
    lines.append("")

    if intro:
        lines.append(intro)
        lines.append("")

    if screenshot_paths:
        lines.append("## 截图")
        lines.append("")
        for i, path in enumerate(screenshot_paths, 1):
            data, mime = compress_image(path)
            b64 = base64.b64encode(data).decode("ascii")
            lines.append(f"![截图{i}](data:{mime};base64,{b64})")
            lines.append("")

    lines.append("---")
    lines.append("")
    lines.append("如需反馈问题或交流，欢迎加入 HMOL 官方 QQ 群：1群 1034243331，2群 1092671790。")
    lines.append("")

    return "\n".join(lines)


class App:
    def __init__(self, root):
        self.root = root
        root.title("HMOL 资源文档生成器")
        root.geometry("660x800")
        root.minsize(600, 560)
        root.configure(bg=BG)

        self.resource = resource_dir()
        self.links = []
        self.screenshots = []

        self._build_ui()
        self._load_categories()

    def _build_ui(self):
        container = ttk.Frame(self.root, padding=16)
        container.pack(fill="both", expand=True)

        title_lb = ttk.Label(container, text="HMOL 资源文档生成器", font=("Microsoft YaHei UI", 15, "bold"))
        title_lb.pack(anchor="w", padx=2, pady=(0, 2))
        desc_lb = ttk.Label(
            container,
            text="填写信息后一键生成 md 文档，放入对应分类文件夹即可在网站上显示。",
            font=FONT_SMALL,
            foreground="#666",
        )
        desc_lb.pack(anchor="w", padx=2, pady=(0, 8))

        # 滚动区域
        scroll_wrap = ttk.Frame(container)
        scroll_wrap.pack(fill="both", expand=True)

        self.canvas = tk.Canvas(scroll_wrap, highlightthickness=0, bg=BG)
        self.scrollbar = ttk.Scrollbar(scroll_wrap, orient="vertical", command=self.canvas.yview)
        self.inner = ttk.Frame(self.canvas)

        self._window_id = self.canvas.create_window((0, 0), window=self.inner, anchor="nw")
        self.canvas.configure(yscrollcommand=self.scrollbar.set)
        self.inner.bind("<Configure>", self._on_inner_configure)
        self.canvas.bind("<Configure>", self._on_canvas_configure)
        self.canvas.bind_all("<MouseWheel>", self._on_mousewheel)

        self.canvas.pack(side="left", fill="both", expand=True)
        self.scrollbar.pack(side="right", fill="y")

        pad = {"padx": 10, "pady": 6}
        inner = self.inner

        # 基本信息
        basic = ttk.LabelFrame(inner, text=" 基本信息 ", padding=10)
        basic.pack(fill="x", **pad)

        ttk.Label(basic, text="分类 *", font=FONT).grid(row=0, column=0, sticky="w", padx=4, pady=3)
        self.cat_var = tk.StringVar()
        self.cat_combo = ttk.Combobox(basic, textvariable=self.cat_var, state="readonly", font=FONT, width=26)
        self.cat_combo.grid(row=0, column=1, sticky="w", padx=4, pady=3)

        ttk.Label(basic, text="标题 *", font=FONT).grid(row=1, column=0, sticky="w", padx=4, pady=3)
        self.title_var = tk.StringVar()
        ttk.Entry(basic, textvariable=self.title_var, font=FONT, width=34).grid(row=1, column=1, sticky="w", padx=4, pady=3)

        ttk.Label(basic, text="作者", font=FONT).grid(row=2, column=0, sticky="w", padx=4, pady=3)
        self.author_var = tk.StringVar()
        ttk.Entry(basic, textvariable=self.author_var, font=FONT, width=34).grid(row=2, column=1, sticky="w", padx=4, pady=3)

        # 详细信息
        detail = ttk.LabelFrame(inner, text=" 详细信息 ", padding=10)
        detail.pack(fill="x", **pad)

        ttk.Label(detail, text="游戏版本", font=FONT).grid(row=0, column=0, sticky="w", padx=4, pady=3)
        self.gamever_var = tk.StringVar()
        ttk.Entry(detail, textvariable=self.gamever_var, font=FONT, width=20).grid(row=0, column=1, sticky="w", padx=4, pady=3)

        ttk.Label(detail, text="版本", font=FONT).grid(row=0, column=2, sticky="w", padx=8, pady=3)
        self.ver_var = tk.StringVar()
        ttk.Entry(detail, textvariable=self.ver_var, font=FONT, width=16).grid(row=0, column=3, sticky="w", padx=4, pady=3)

        ttk.Label(detail, text="运行方式", font=FONT).grid(row=1, column=0, sticky="w", padx=4, pady=3)
        self.mode_var = tk.StringVar(value="")
        ttk.Radiobutton(detail, text="单人", value="single", variable=self.mode_var).grid(row=1, column=1, sticky="w", padx=4, pady=3)
        ttk.Radiobutton(detail, text="联机", value="multi", variable=self.mode_var).grid(row=1, column=2, sticky="w", padx=8, pady=3)
        ttk.Radiobutton(detail, text="单人+联机", value="both", variable=self.mode_var).grid(row=1, column=3, sticky="w", padx=8, pady=3)

        ttk.Label(detail, text="标签", font=FONT).grid(row=2, column=0, sticky="w", padx=4, pady=3)
        self.tags_var = tk.StringVar()
        ttk.Entry(detail, textvariable=self.tags_var, font=FONT, width=34).grid(row=2, column=1, columnspan=3, sticky="w", padx=4, pady=3)
        ttk.Label(detail, text="多个标签用逗号分隔", font=FONT_SMALL, foreground="#888").grid(row=3, column=1, columnspan=3, sticky="w", padx=4, pady=0)

        # 简介
        intro = ttk.LabelFrame(inner, text=" 简介 ", padding=10)
        intro.pack(fill="x", **pad)
        self.intro_text = tk.Text(intro, height=5, font=FONT, wrap="word")
        self.intro_text.pack(fill="x", padx=4, pady=3)

        # 相关链接
        link_frame = ttk.LabelFrame(inner, text=" 相关链接 ", padding=10)
        link_frame.pack(fill="x", **pad)

        self.link_list = tk.Listbox(link_frame, height=4, font=FONT, exportselection=False)
        self.link_list.grid(row=0, column=0, columnspan=4, sticky="ew", padx=4, pady=3)

        self.link_name_var = tk.StringVar()
        self.link_url_var = tk.StringVar()
        ttk.Entry(link_frame, textvariable=self.link_name_var, font=FONT_SMALL, width=14).grid(row=1, column=0, sticky="w", padx=4, pady=3)
        ttk.Entry(link_frame, textvariable=self.link_url_var, font=FONT_SMALL, width=26).grid(row=1, column=1, sticky="w", padx=4, pady=3)
        ttk.Button(link_frame, text="添加", command=self._add_link).grid(row=1, column=2, padx=4, pady=3)
        ttk.Button(link_frame, text="删除选中", command=self._del_link).grid(row=1, column=3, padx=4, pady=3)
        ttk.Button(link_frame, text="添加 QQ 群", command=self._add_qq).grid(row=2, column=0, columnspan=2, sticky="w", padx=4, pady=3)
        ttk.Label(link_frame, text="名称 + 链接，如：B站 https://...", font=FONT_SMALL, foreground="#888").grid(row=2, column=2, columnspan=2, sticky="w", padx=4, pady=3)
        link_frame.columnconfigure(1, weight=1)

        # 截图
        shot_frame = ttk.LabelFrame(inner, text=" 截图 ", padding=10)
        shot_frame.pack(fill="x", **pad)

        self.shot_list = tk.Listbox(shot_frame, height=4, font=FONT, exportselection=False)
        self.shot_list.grid(row=0, column=0, columnspan=4, sticky="ew", padx=4, pady=3)

        ttk.Button(shot_frame, text="选择图片", command=self._pick_images).grid(row=1, column=0, padx=4, pady=3)
        ttk.Button(shot_frame, text="删除选中", command=self._del_shot).grid(row=1, column=1, padx=4, pady=3)
        ttk.Label(shot_frame, text="选择图片后自动压缩（最长边 1280px）并内嵌到 md 文档", font=FONT_SMALL, foreground="#888").grid(row=1, column=2, columnspan=2, sticky="w", padx=4, pady=3)

        # 底部
        bottom = ttk.Frame(inner)
        bottom.pack(fill="x", **pad)
        ttk.Button(bottom, text="生成 md 文档", command=self._generate).pack(side="left", padx=4)
        self.status_var = tk.StringVar(value="就绪")
        ttk.Label(bottom, textvariable=self.status_var, font=FONT_SMALL, foreground="#888").pack(side="left", padx=10)

    def _on_inner_configure(self, event):
        self.canvas.configure(scrollregion=self.canvas.bbox("all"))

    def _on_canvas_configure(self, event):
        self.canvas.itemconfigure(self._window_id, width=event.width)

    def _on_mousewheel(self, event):
        widget = self.root.winfo_containing(event.x_root, event.y_root)
        if widget is not None and isinstance(widget, tk.Text):
            return
        self.canvas.yview_scroll(int(-1 * (event.delta / 120)), "units")

    def _load_categories(self):
        if not os.path.isdir(self.resource):
            messagebox.showerror("错误", f"未找到 Resource 目录：\n{self.resource}\n\n请把本程序放在 Resource\\#TOOLS 文件夹内运行。")
            return
        folders = []
        try:
            for name in os.listdir(self.resource):
                if name.startswith("#") or name.startswith("."):
                    continue
                full = os.path.join(self.resource, name)
                if os.path.isdir(full):
                    folders.append(name)
        except OSError as e:
            messagebox.showerror("错误", f"读取 Resource 目录失败：{e}")
            return
        folders.sort()
        items = []
        for f in folders:
            cn = CATEGORY_NAMES.get(f, f)
            items.append(f"{cn} ({f})" if cn != f else f)
        self.cat_combo["values"] = items
        if items:
            self.cat_combo.current(0)

    def _add_link(self):
        name = self.link_name_var.get().strip()
        url = self.link_url_var.get().strip()
        if not name or not url:
            messagebox.showwarning("提示", "请填写链接名称和链接地址。")
            return
        self.links.append((name, url))
        self.link_list.insert("end", f"{name} - {url}")
        self.link_name_var.set("")
        self.link_url_var.set("")

    def _del_link(self):
        sel = self.link_list.curselection()
        if not sel:
            return
        idx = sel[0]
        self.link_list.delete(idx)
        del self.links[idx]

    def _add_qq(self):
        existing = {name for name, _ in self.links}
        for name, num, url in QQ_GROUPS:
            if name not in existing:
                self.links.append((name, url))
                self.link_list.insert("end", f"{name}（{num}） - {url}")

    def _pick_images(self):
        files = filedialog.askopenfilenames(
            title="选择截图",
            filetypes=[("图片", "*.png *.jpg *.jpeg *.webp *.gif *.bmp")],
        )
        for f in files:
            ext = os.path.splitext(f)[1].lower()
            if ext not in IMG_EXT:
                continue
            if f in self.screenshots:
                continue
            try:
                data, _ = compress_image(f)
            except Exception as e:
                messagebox.showerror("错误", f"图片处理失败：{f}\n{e}")
                continue
            self.screenshots.append(f)
            orig = os.path.getsize(f) / 1024
            comp = len(data) / 1024
            self.shot_list.insert("end", f"{os.path.basename(f)}  {orig:.0f}KB → {comp:.0f}KB")

    def _del_shot(self):
        sel = self.shot_list.curselection()
        if not sel:
            return
        idx = sel[0]
        self.shot_list.delete(idx)
        del self.screenshots[idx]

    def _generate(self):
        title = self.title_var.get().strip()
        if not title:
            messagebox.showwarning("提示", "请填写标题。")
            return
        combo = self.cat_combo.current()
        if combo < 0:
            messagebox.showwarning("提示", "请选择分类。")
            return
        value = self.cat_combo["values"][combo]
        folder = value[value.rfind("(") + 1:].rstrip(")") if "(" in value else value

        author = self.author_var.get().strip()
        gamever = self.gamever_var.get().strip()
        version = self.ver_var.get().strip()
        mode = self.mode_var.get()
        tags = [t.strip() for t in re.split(r"[,，]", self.tags_var.get()) if t.strip()]

        intro = self.intro_text.get("1.0", "end").strip()
        description = intro.splitlines()[0] if intro else ""

        try:
            content = build_md(
                title=title,
                description=description,
                author=author,
                gamever=gamever,
                version=version,
                mode=mode,
                tags=tags,
                links=self.links,
                intro=intro,
                screenshot_paths=self.screenshots,
            )
        except Exception as e:
            messagebox.showerror("错误", f"图片处理失败：{e}")
            return

        out_dir = os.path.join(self.resource, folder)
        os.makedirs(out_dir, exist_ok=True)
        out_path = os.path.join(out_dir, f"{clean_filename(title)}.md")
        if os.path.exists(out_path):
            if not messagebox.askyesno("确认", f"文件已存在：\n{out_path}\n\n是否覆盖？"):
                return

        try:
            with open(out_path, "w", encoding="utf-8") as fh:
                fh.write(content)
        except OSError as e:
            messagebox.showerror("错误", f"写入失败：{e}")
            return

        self.status_var.set(f"已生成：{out_path}")
        messagebox.showinfo("完成", f"md 文档已生成：\n{out_path}\n\n构建网站后即可在资源库中查看。")
        if messagebox.askyesno("完成", "是否打开所在文件夹？"):
            os.startfile(out_dir)


def main():
    root = tk.Tk()
    App(root)
    root.mainloop()


if __name__ == "__main__":
    main()
