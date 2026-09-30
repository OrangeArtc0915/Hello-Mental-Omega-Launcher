# HMOL 旧 Python 实现 → 新 C# 实现 迁移映射清单

> 本文档只做映射记录，不改任何代码。
> 旧实现：`f:\ra2\HMOL\HMOL\HMOL_QT\`（PySide6）＋ `f:\ra2\HMOL\HMOL\HMOL联机模块\`（PyQt/tkinter）
> 新实现：`f:\ra2\HMOL\HMOL\src\HMOL.Core` + `src\HMOL.App`（C# / .NET 8 / WPF）
> 行号一律以文件内实际行号为准，未读到的部分标「未确认」。
>
> ⚠️ **本表状态以当前代码为准**（最后核对：**2026-09-25**）。第 2 节状态列已按 `src\` 当前代码逐条复核；
> 第 1.1 节的规模数字仍是 **2026-09-23 19:55 的历史快照**，仅作规模参考，不代表当前行数。
> 若后续继续开发，请以最新代码为准重新核对状态列。

## 0. 统计口径与状态定义

**行数统计命令（实测执行，不是估算）**

```powershell
# 旧：统计两个目录顶层 .py（含空行，Get-Content 的 .Count 为准确行数）
Get-ChildItem 'f:\ra2\HMOL\HMOL\HMOL_QT'    -File -Filter *.py | % { (Get-Content -LiteralPath $_.FullName).Count }
Get-ChildItem 'f:\ra2\HMOL\HMOL\HMOL联机模块' -File -Filter *.py | % { (Get-Content -LiteralPath $_.FullName).Count }
# 新：统计 src 下全部 .cs / .xaml，并排除 bin、obj（避免把 MSBuild 生成的 .g.cs 算进来）
Get-ChildItem 'f:\ra2\HMOL\HMOL\src' -Recurse -File |
  ? { $_.Extension -in '.cs','.xaml' -and $_.FullName -notmatch '\\(bin|obj)\\' } |
  % { (Get-Content -LiteralPath $_.FullName).Count }
```

**编译验证（实测执行，2026-09-23 19:4x）**

```
dotnet build f:\ra2\HMOL\HMOL\HMOL.sln -c Debug
→ HMOL.Core -> bin\Debug\net8.0-windows\HMOL.Core.dll
→ HMOL.App  -> bin\Debug\net8.0-windows\HMOL.dll
→ 已成功生成。0 个警告 0 个错误。
```

> 注：验证用的 `bin`/`obj` 已被并行进行的构建重新生成（`obj\...\*.g.cs` 可见）。**这不是本文档产生的改动**，故未再清理，以免打断正在跑的构建。

**状态定义**

| 状态 | 含义 |
| --- | --- |
| `已迁移` | 新代码已实现，且在上面的 `dotnet build` 中可编译通过 |
| `未迁移` | 旧有、新没有，且按既定范围**应当保留** → 待办 |
| `已删除` | 按既定范围明确删除（MSAL/Xbox、OneDrive 与网盘下载、DLC 体系、资源下载页、死代码） |

---

## 1. 总览

### 1.1 规模（实测，快照时点 2026-09-23 19:55）

| 项目 | 文件数 | 行数 | 备注 |
| --- | --- | --- | --- |
| 旧：`HMOL_QT\*.py` | 11 | **64276** | 其中 `hmol_repkg.py` 40161 行是内嵌 RePKG.exe 的 Base64 数据，手写代码实为 24115 行 |
| 旧：`HMOL_QT\HMOL_qt.py` | 1 | **18982** | 单文件主程序（任务书写的 18982 行与实测一致） |
| 旧：`HMOL联机模块\*.py` | 17 | **8471** | 其中主程序 `HMOL联机模块.py` 4186 行 |
| **旧实现合计** | **28** | **72747** | 剔除内嵌二进制后 **32586** 行手写代码 |
| 新：`src\HMOL.Core\**\*.cs` | 27 | 4699 | 逻辑层（实例/包/备份/启动/日志/IO），本快照内未再变动 |
| 新：`src\HMOL.App\**\*.cs` | 30 | 4424 | UI 层（外壳/页面/控件/主题/动画/服务） |
| 新：`src\**\*.xaml` | 15 | 2098 | 窗口与页面布局 |
| **新实现合计** | **72** | **11221** | `dotnet build` 0 警告 0 错误 |

进度口径：`11221 / 72747 = 15.4%`；与旧手写代码相比 `11221 / 32586 = 34.4%`。

### 1.2 主表状态分布（按条目数，不是行数）

> 下表为 **2026-09-25 按当前代码复核后**的分布（旧快照为：已迁移 60 / 未迁移 54 / 已删除 40）。

| 状态 | 条数 | 占比 |
| --- | --- | --- |
| `已迁移` | **105** | 68.2% |
| `未迁移`（待办） | **8** | 5.2% |
| `已删除` | **41** | 26.6% |
| 合计 | **154** | 100% |

### 1.3 一句话结论

- **逻辑层迁移得比较完整**：实例 CRUD、包安装/精确卸载、安装记录、备份/还原、启动器、日志、压缩包解压、路径安全、回滚机制都已在 `HMOL.Core` 落地并有对应旧函数注释。
- **UI 层已完成全部 6 个功能页**：`MainWindow`（页面缓存 + 导航 + 玻璃化）、`PageHome`（实例卡 / 启动 / 运行态 / 小组件）、`PageInstances`（实例增删改查 + 导入导出 + 备份还原）、`PagePackages`（包列表 / 导入 / 安装 / 卸载）、`PageMultiplayer`（组网 / 大厅 / 对端 / 工具箱）、`PageLog`、`PageSettings`、`AboutWindow`，以及通用对话框与游戏会话中枢。
- **原「占位」部分已全部落地**：包管理页与联机页均已实现；壁纸包导入（`Appearance\WallpaperPackageService.cs`，内嵌 RePKG.exe）、BGM（`App\Services\BgmPlayer.cs`）、主页背景（`Views\BackgroundLayer.xaml`）、自定义布局编辑器（`Layout\` + `Windows\LayoutEditorWindow.xaml`）、扩展模块（`Extensions\`）均已在 C# 侧实现。
- **联机模块已接入**：EasyTier / n2n 引擎、大厅（MQTT）、房间聊天、文件传输、游戏内 HUD、系统托盘、进程守护、网络工具箱（含 TAP 安装 / WinIPBroadcast）、公共节点、分享文本、收藏队友、配置持久化都已在 `HMOL.Core\Multiplayer\` + 联机页落地；ZeroTier / awl / MQTT 之外的旧选项按既定范围删除。
- **仍是待办**：彩蛋系统（M087）、教程页（M074）、反馈对话框（M072）、包目录浏览器与预览对话框（M034/M035）、EULA 启动门（M055，新版无）、日志敏感信息过滤（M062）。
- **在线更新器已按《自动更新设计.md》落地**：不再走清单文件 / CDN 镜像，改为直查 Releases（GitHub 网页 + Gitee API 双源）；只替换 exe 本体，由 `%TEMP%\HMOL-update-{pid}.cmd` 在旧进程退出后完成替换，失败留 `locked`/`timeout`/`broken` 标记供下次启动提示。旧版 `HMOL_updater.py`（1025 行）的清单 / 镜像 / Wine 变体 / `cleanup` 清理四件套均未照搬；`App.xaml.cs` 原 `--apply-update` / `--wait-pid` / `--target` 参数解析已删除。

---

## 2. 主表：一一映射

编号说明：`M001`…`M153`（共 153 条），状态列取值严格为 `已迁移` / `未迁移` / `已删除`。

### 2.1 主窗口外壳与主题

| 编号 | 功能块 | 旧实现（文件 + 行号 + 类/函数） | 新实现（文件 + 类/方法） | 状态 |
| --- | --- | --- | --- | --- |
| M001 | 主窗口外壳 / 页面切换 / 侧栏导航 | `HMOL_QT/HMOL_qt.py:9461` `class MainWindow`、`10014` `_build_ui`、`10170` `_build_sidebar`、`10574` `_switch_page`、`10654` `_switch_to_subpage` | `src/HMOL.App/Windows/MainWindow.xaml(.cs)` `MainWindow.SwitchToPage/GetPage`、`Controls/NavItem.cs`、`Pages/NavPages.cs` | 已迁移 |
| M002 | 主题配色与 QSS 生成 | `HMOL_qt.py:1147` `build_qss`、`1605` `build_qss`（重载）、`9930` `_apply_theme`、`10004` `apply_theme_mode`、`9720` `_resolve_theme` | `src/HMOL.App/Theme/ThemeService.cs` `Apply/BuildBrushes`、`Theme/ThemeColors.cs`、`Resources/Colors.xaml` | 已迁移 |
| M003 | 跟随系统深浅色 | `HMOL_qt.py:3393` `detect_system_theme` | `Theme/ThemeService.cs` `IsSystemInDarkMode`＋5 秒轮询 `EnsureSystemWatcher` | 已迁移 |
| M004 | 渐变主题与强调色 | `HMOL_qt.py:10415` `_current_gradient`、`10421` `_apply_gradient_theme`、`10443` `set_gradient_theme`、`10470` `_gradient_btn_style`、`11948` `_settings_pick_accent` | `Theme/ThemeService.cs` `AccentTheme`（Default/Blue/Green/Purple 共 4 套，`BuildWindowGradient`/`BuildCardCover`） | 已迁移 |
| M005 | 玻璃化 / 亚克力背景 | `HMOL_qt.py:2098` `class AcrylicHelper`（`2156` `enable_acrylic`、`2195` `disable_acrylic`） | `src/HMOL.App/Interop/WindowInterop.cs` `TryExtendFrameIntoClientArea`（DWM 玻璃延伸） | 已迁移 |
| M006 | 圆角窗体 + 自绘投影 + 标题栏拖拽 | `HMOL_qt.py:9461` `MainWindow`（自绘标题栏）、`16262` `resizeEvent` | `Windows/MainWindow.xaml`（`PanBack`/`RectForm` 圆角裁剪 + `DropShadowEffect`）＋`Interop/WindowInterop.cs` | 已迁移 |
| M007 | 图标资源（emoji → SVG） | `HMOL_qt.py:3440` `extract_emoji_icon`、`3461` `strip_leading_emoji` | `Controls/Svg/SvgIcon.cs`、`SvgIconLoader.cs`、`SvgPathParser.cs`＋`Assets/IconPacks/lucide/*.svg` | 已迁移 |
| M008 | 窗口透明度设置 | `HMOL_qt.py:9619`/`9666` `window_opacity`（配置键） | `Core/App/Settings.cs` `WindowOpacity`（0.5–1.0）＋`Pages/PageSettings.xaml` 滑条 `SldOpacity`＋`Windows/MainWindow.xaml.cs` `ApplyAppearance` | 已迁移 |
| M009 | 单实例运行 | 旧版无此逻辑（无 `QLocalServer`/`QLockFile`/`QSharedMemory` 匹配） | `src/HMOL.App/App.xaml.cs:13,39` `Mutex "HMOL.SingleInstance"` | 已迁移 |
| M154 | 通用确认 / 文本输入 / 进度对话框（编号在末尾补充） | `HMOL_qt.py:110-111` 导入 `QMessageBox`/`QInputDialog`/`QProgressDialog`；用例 `4121`/`4825` `QInputDialog.getText`、`4181`/`4844`/`11056` `QMessageBox.question`、`4194`/`4427`/`11234` `QProgressDialog`、`872` `_safe_close_progress` | `src/HMOL.App/Windows/ChoiceWindow.xaml(.cs)`（`Ask`）、`TextInputWindow.xaml(.cs)`、`ProgressWindow.xaml(.cs)`（`SetDetail:56`、`Finish:65`、`OnCancelClick:79`） | 已迁移 |

### 2.2 实例管理

| 编号 | 功能块 | 旧实现（文件 + 行号 + 类/函数） | 新实现（文件 + 类/方法） | 状态 |
| --- | --- | --- | --- | --- |
| M010 | 实例数据模型 | `HMOL_qt.py:2665` `class GameInstance`（`2678` `to_dict`、`2688` `from_dict`、`2722` `get_instance_dir`、`2729` `get_config_path`、`2732` `get_install_records_dir`） | `src/HMOL.Core/Instances/GameInstance.cs` | 已迁移 |
| M011 | 实例增删改查与校验 | `HMOL_qt.py:2737` `class InstanceManager`（`2793` `add_instance`、`2811` `remove_instance`、`2827` `rename_instance`、`2841` `update_instance`、`2869` `get_instance_list`、`2872` `set_current_instance`、`2883` `open_instance_directory`） | `Core/Instances/InstanceManager.cs` `Add/Remove/Rename/Update/SetCurrent/DirectoryOf` | 已迁移 |
| M012 | 实例持久化与加载 | `HMOL_qt.py:2749` `load_instances`、`2776` `save_instances`、`2780` `_save_instance_config`、`2768` `_ensure_instance_dirs` | `Core/Instances/InstanceStore.cs` `Load/Save/Upsert/Delete/SetCurrent` | 已迁移 |
| M013 | 实例体积统计（带缓存） | `HMOL_qt.py:3334` `get_instance_size`、`3366` `format_size` | `Core/Instances/InstanceManager.cs` `GetSize`＋`Core/Backup/BackupService.cs` `FormatSize` | 已迁移 |
| M014 | 实例导出（zip） | `HMOL_qt.py:2897` `export_instance`、`3027` `_export_to_zip` | `Core/Instances/InstanceArchive.cs` `Export` | 已迁移 |
| M015 | 实例导出（7z / rar） | `HMOL_qt.py:3085` `_export_to_7z`（依赖 py7zr）、`3125` `_export_to_rar`（依赖 WinRAR CLI） | `Core/Instances/InstanceArchive.cs` `Export`（`.zip` 用 .NET 自带压缩、`.7z` 走 `Core/IO/SevenZipTool.cs` 调随包的 `runtime\7zip\7za.exe`）；**rar 导出按范围删除** | 已迁移 |
| M016 | 导出格式选择对话框 | `HMOL_qt.py:7191` `class ExportFormatDialog` | 并入系统 `SaveFileDialog` 的格式过滤器（`Pages/PageInstances.xaml.cs:398-407` `OnExportClick`，`EnsureExportExtension:384` 按所选过滤器补后缀） | 已迁移 |
| M017 | 实例导入 | `HMOL_qt.py:3201` `import_instance`、`3322` `_handle_name_collision` | `Core/Instances/InstanceArchive.cs` `Import/ResolveName` | 已迁移 |
| M018 | 实例管理页 UI（列表 / 增删 / 导出导入 / 打开目录） | `HMOL_qt.py:3972` `class InstanceManagementDialog`（`4038`–`4521`）、`10877` `_build_instance_page`、`11006`–`11323` `_inline_*` 系列 | `src/HMOL.App/Pages/PageInstances.xaml(.cs)`（747 行 code-behind）：`RefreshSizesAsync:158`、`OnNewInstanceClick:196`、`OnEditClick:214`、`OnRenameClick:233`、`OnDeleteClick:270`、`OnOpenDirClick:299`、`OnUseClick:314`、`OnLaunchClick:324`、`OnExportClick:347`、`OnImportInstanceClick:408` | 已迁移 |
| M019 | 新增 / 重命名实例对话框 | `HMOL_qt.py:3787` `class AddInstanceDialog`、`15891` `_build_add_instance_subpage`、`15955` `_build_rename_instance_subpage`、`3916` `_submit` | `src/HMOL.App/Windows/InstanceEditorWindow.xaml(.cs)`（`Validate:84`、`OnSaveClick:70`）、重命名走 `Windows/TextInputWindow.xaml(.cs)` | 已迁移 |
| M020 | 实例卡片与主页启动按钮 | `HMOL_qt.py:6041` `class InstanceListItemWidget`、`6239` `class InstanceLaunchButton`（`6448` `_on_launch_clicked`）、`6467` `class BackgroundHomePage` | `src/HMOL.App/Pages/PageHome.xaml(.cs)`（`RefreshCurrentInstance:95`、`UpdateLaunchState:113`、`OnSwitchRowClick:160`、`OnLaunchClick:169`）＋`Services/GameSessionHub.cs`（单会话 + 日志转投 `ActivityLog`） | 已迁移 |

### 2.3 插件包安装卸载

| 编号 | 功能块 | 旧实现（文件 + 行号 + 类/函数） | 新实现（文件 + 类/方法） | 状态 |
| --- | --- | --- | --- | --- |
| M021 | 包类型定义（7 类 → 4 类） | `HMOL_qt.py:9726` `_get_package_configs`（ini/map/mission/voice/plugin/beautification/music） | `Core/Packages/PackageType.cs` `PackageType`＋`PackageTypes.Specs`（ini/map/mission/plugin） | 已迁移 |
| M022 | 包目录约定 + `mod`→`ini` 迁移 | `HMOL_qt.py:9741` `_get_package_dirs`、`9758` `get_package_dir` | `Core/Packages/PackageType.cs` `EnsureDirectories`、`MigrateLegacyIniDirectory` | 已迁移 |
| M023 | 可用包列表与可见性判定 | `HMOL_qt.py:3663` `_refresh_available`、`3729` `_is_valid_package_directory`、`3748` `_is_valid_package_file` | `Core/Packages/PackageLibrary.cs` `List/Find/IsValidDirectory` | 已迁移 |
| M024 | 已安装包列表 | `HMOL_qt.py:3696` `_refresh_installed` | `Core/Instances/GameInstance.cs` `InstalledOf/AddInstalled/RemoveInstalled/ClearInstalled` | 已迁移 |
| M025 | 包安装 | `HMOL_qt.py:16462` `_install_package`、`16479` `_install_package_impl` | `Core/Packages/PackageInstaller.cs` `Install`、`Describe`、`ResolveTargetFiles` | 已迁移 |
| M026 | 安装冲突扫描 | `HMOL_qt.py:9892` `scan_install_conflicts` | `Core/Packages/PackageInstaller.cs` `ScanConflicts` | 已迁移 |
| M027 | 文件 / 目录复制 | `HMOL_qt.py:9802` `copy_files` | `Core/IO/DirectoryCopier.cs` `Copy/CopyFile/MovePath/CopyDirectory` | 已迁移 |
| M028 | 包卸载（精确 / 全量 / 地图） | `HMOL_qt.py:16983` `_uninstall_package`、`17031` `_uninstall_map_package`、`17106` `_uninstall_package_selective`、`17286` `_uninstall_package_full` | `Core/Packages/PackageInstaller.cs` `Uninstall/UninstallMap/UninstallSelective/UninstallFull` | 已迁移 |
| M029 | 从管理器目录移除包 | `HMOL_qt.py:17441` `_remove_package` | `Core/Packages/PackageLibrary.cs` `Remove/InUseBy` | 已迁移 |
| M030 | 导入包到管理器目录 | `HMOL_qt.py:17499` `_import_package` | `Core/Packages/PackageLibrary.cs` `Import` | 已迁移 |
| M031 | 压缩包内容预览 | `HMOL_qt.py:17585` `_peek_package_contents` | `Core/Packages/PackageLibrary.cs` `Peek`＋`ArchiveExtractor.ListEntries` | 已迁移 |
| M032 | 解压（zip / 7z / rar） | `HMOL_qt.py:19` `import zipfile`、`46-50` `py7zr`、`56-62` `rarfile`；`5325` `_scan_zip`、`5336` `_scan_7z`、`5359` `_scan_rar` | `Core/Packages/ArchiveExtractor.cs`（SharpCompress，含 tar/gz，路径穿越校验） | 已迁移 |
| M033 | 包管理器页 UI | `HMOL_qt.py:3525` `class PackageManagerTab`（`3549`–`3787`） | `src/HMOL.App/Pages/PagePackages.xaml(.cs)`：`RefreshAsync:183`、`OnImportPackageClick:244`、`OnInstallClick:328`、`OnUninstallClick:446`、`OnRemovePackageClick:300`、`OnOpenPackageDirClick:226` | 已迁移 |
| M034 | 包目录浏览器（重命名 / 删除 / 预览） | `HMOL_qt.py:4531` `class PackageDirectoryBrowserDialog`（`4628`–`4928`） | 无 | 未迁移 |
| M035 | 包预览对话框（图标 / 说明 / 文本） | `HMOL_qt.py:5282` `class PackagePreviewDialog`（`5299`–`5522`） | 无 | 未迁移 |
| M036 | 包下载（网盘） | `HMOL_qt.py:17602` `_download_package`、`3647` `_on_download` | 无 | 已删除 |
| M037 | 打开包目录 | `HMOL_qt.py:3650` `_on_open_dir`、`17617` `_open_package_dir` | `Core/IO/ShellHelper.cs` `OpenFolder` | 已迁移 |

### 2.4 安装记录与回滚

| 编号 | 功能块 | 旧实现（文件 + 行号 + 类/函数） | 新实现（文件 + 类/方法） | 状态 |
| --- | --- | --- | --- | --- |
| M038 | 安装记录读写 | `HMOL_qt.py:6982` `get_install_record_path`、`6991` `save_install_record`、`7023` `load_install_record`、`7036` `list_install_records`、`7077` `delete_install_record` | `Core/Packages/InstallRecordStore.cs` `PathOf/Save/Load/List/Delete/DeleteAll` | 已迁移 |
| M039 | 安装记录数据结构 | `HMOL_qt.py:6991` `save_install_record` 字段（package_type/package_name/source_archive/install_time/files/original_snapshot） | `Core/Packages/InstallRecord.cs` | 已迁移 |
| M040 | 覆盖前文件快照 | `HMOL_qt.py:7089` `snapshot_existing_files` | `Core/Packages/InstallRecordStore.cs` `SnapshotExistingFiles` | 已迁移 |
| M041 | 文件哈希 / 目录统计 | `HMOL_qt.py:7127` `_compute_file_hash`、`6937` `_dir_size`、`6959` `_dir_file_count`、`7103` `list_target_files`、`885` `_copyfile_chunked` | `Core/Backup/BackupService.cs` `ComputeHash`；`Core/IO/DirectoryCopier.cs` `GetSize/CountFiles/ListFiles` | 已迁移 |
| M042 | 部分失败回滚（隔离区 + .bak + 逆序撤销） | 旧版无此机制（`_install_package_impl` 失败即放弃本次） | `Core/IO/OperationJournal.cs` `BackupExisting/MoveToQuarantine/Commit/Rollback` | 已迁移 |

### 2.5 备份还原

| 编号 | 功能块 | 旧实现（文件 + 行号 + 类/函数） | 新实现（文件 + 类/方法） | 状态 |
| --- | --- | --- | --- | --- |
| M043 | 备份列表 / 名称校验 / 元数据 / 体积格式化 | `HMOL_qt.py:6845` `list_game_backups`、`6822` `is_valid_backup_name`、`6901` `get_original_backup_path`、`6906` `get_game_backup_path`、`6925` `_read_backup_meta`、`6912` `_format_size` | `Core/Backup/BackupService.cs` `List/IsValidName/OriginalBackupPath/PathOf/ReadInfo/FormatSize`＋`Backup/BackupInfo.cs` | 已迁移 |
| M044 | 用户备份 | `HMOL_qt.py:17659` `_backup_game_impl` | `Core/Backup/BackupService.cs` `Backup` | 已迁移 |
| M045 | 原版备份（`backup/MO`） | `HMOL_qt.py:18154` `_backup_original_game` | `Core/Backup/BackupService.cs` `BackupOriginal` | 已迁移 |
| M046 | 还原（备份覆盖游戏目录） | `HMOL_qt.py:17921` `_restore_game` | `Core/Backup/BackupService.cs` `Restore` | 已迁移 |
| M047 | 备份完整性抽样校验 | `HMOL_qt.py:7142` `_verify_backup_integrity` | `Core/Backup/BackupService.cs` `Verify` | 已迁移 |
| M048 | 备份 / 还原 UI（备份列表、创建备份、备份原版、还原确认、进度窗） | `HMOL_qt.py:17645` `_backup_game`、`17921` `_restore_game`、`18154` `_backup_original_game`（对话框部分） | `src/HMOL.App/Pages/PageInstances.xaml(.cs)` 备份区块：`RefreshBackupsAsync:483`、`OnBackupRowClick:516`、`OnBackupClick:531`、`OnBackupOriginalClick:588`、`OnRestoreClick:639`、`OnDeleteBackupClick:681`；进度用 `Windows/ProgressWindow.xaml(.cs)` | 已迁移 |

### 2.6 启动游戏

| 编号 | 功能块 | 旧实现（文件 + 行号 + 类/函数） | 新实现（文件 + 类/方法） | 状态 |
| --- | --- | --- | --- | --- |
| M049 | 启动游戏进程 | `HMOL_qt.py:18306` `_launch_game`（`18312` 三个候选 exe、`18326` `subprocess.Popen`） | `Core/Launch/GameLauncher.cs` `TryLaunch` | 已迁移 |
| M050 | 游戏输出捕获与等级着色 | 旧版 Popen 不重定向输出（`18326`），无捕获 | `Core/Launch/GameProcessSession.cs` `LineReceived/Emit/Classify`＋`Launch/GameLogLine.cs` | 已迁移 |
| M051 | 游戏进程退出跟踪 / 强杀 | 旧版不跟踪进程（`18326` 起完即返回） | `Core/Launch/GameProcessSession.cs` `Exited/Kill/WaitForExitAsync/ExitCode` | 已迁移 |
| M052 | 游戏目录识别 | `HMOL_qt.py:9761` `is_mo_directory` | `Core/Games/GameLocator.cs` `IsMoDirectory/FindExecutable/NormalizePath` | 已迁移 |
| M053 | 依赖库检查与提示 | `HMOL_qt.py:18642` `check_dependencies`、`5979` `class DependencyWarningDialog`、`40-62` PIL/py7zr/rarfile 探测 | 无（新版自包含单文件，无外部运行时依赖） | 已删除 |
| M054 | 未处理异常 / 启动错误提示 | `HMOL_qt.py:18800` `_show_startup_error`、`18824` `_install_fatal_error_hook`、`18829` `_fatal_handler` | `src/HMOL.App/App.xaml.cs` `OnUnhandledException` | 已迁移 |
| M055 | EULA 启动门 | `HMOL_qt.py:18660` `_show_eula_full`、`18754` `_check_eula_accepted`、`18795` `show_eula_viewer`；配置键 `eula_accepted`/`eula_accepted_version`（`9655`） | 无 | 未迁移 |
| M056 | 设置项「启动时自动检测游戏路径」 | `HMOL_qt.py:9613`/`9649` `auto_detect_path`（只有开关，无实现） | `Core/Games/GameLocator.cs` `FindCandidates`（限深度 / 限耗时 / 跳过系统目录）＋`Core/App/Settings.cs` `AutoDetectGamePath`＋`Pages/PageSettings.xaml` 「游戏路径」卡（开关 + 立即检测 + 添加为实例）＋`App.xaml.cs` `SuggestGamePathAsync`（启动后台建议，仅提示不改配置） | 已迁移 |

### 2.7 日志

| 编号 | 功能块 | 旧实现（文件 + 行号 + 类/函数） | 新实现（文件 + 类/方法） | 状态 |
| --- | --- | --- | --- | --- |
| M057 | 日志写入 / 分级 / 模块名 | `HMOL_qt.py:673` `_log_writer_worker`、`707` `_start_log_writer`、`714-723` `log_info/warn/error/debug`、`767` `_log`、`782` `get_logs` | `Core/Logging/Log.cs` `Logger.Write/Log.Info/Warn/Error/Fatal` | 已迁移 |
| M058 | 日志导出 TXT / CSV | `HMOL_qt.py:938` `export_logs_txt`、`944` `export_logs_csv` | `src/HMOL.App/Pages/PageLog.xaml.cs` `Export/BuildTxt/BuildCsv` | 已迁移 |
| M059 | 旧日志清理 | `HMOL_qt.py:952` `cleanup_old_logs`、`671` `_LOG_MAX_DAYS=7` | `Core/Logging/Log.cs` `CleanupOldFiles`＋`PageLog.CleanupOldFiles` | 已迁移 |
| M060 | 日志查看页（三视图 + 操作栏） | `HMOL_qt.py:12237` `_build_log_viewer_card`、`12329` `_log_refresh_view`、`12348` `_log_export_txt`、`12364` `_log_export_csv`、`12380` `_log_cleanup` | `src/HMOL.App/Pages/PageLog.xaml(.cs)` | 已迁移 |
| M061 | 日志文件命名与目录 | `HMOL_qt.py:670` `_LOG_FILE_PATH`＝程序目录 `logs/`、`677` `HMOL_<日期>.log` | `Core/App/Paths.cs` `Log`（`Data/Log`）＋`Logger.CreateFile`（`HMOL-yyyy-M-d-HHmmssfff.log`） | 已迁移 |
| M062 | 日志敏感信息过滤 | `HMOL_qt.py:726` `_sanitize_log_message` | 无 | 未迁移 |
| M063 | 内存环形日志缓冲 | `HMOL_qt.py:667` `_LOG_BUFFER`（上限 5000） | `Core/Logging/ActivityLog.cs`（上限 `Capacity = 500`） | 已迁移 |

### 2.8 设置

| 编号 | 功能块 | 旧实现（文件 + 行号 + 类/函数） | 新实现（文件 + 类/方法） | 状态 |
| --- | --- | --- | --- | --- |
| M064 | 设置持久化 | `HMOL_qt.py:9593` `_load_config`、`9625` `save_config`、`9639` `_do_save_config`；文件 `HMOL_QT/HMOL_config.json` | `Core/App/Settings.cs`＋`SettingsStore.cs`；文件 `Data/Settings.json` | 已迁移 |
| M065 | 主题 / 强调色设置项 | `HMOL_qt.py:4928` `SettingsDialog._build_theme_tab`（`4966`）、`11674` `_inline_update_theme_status`、`11685` `_inline_preview_theme` | `src/HMOL.App/Pages/PageSettings.xaml.cs` `OnThemeModeClick/OnAccentClick` | 已迁移 |
| M066 | 联机昵称（本地自定义） | 旧版昵称来自 Xbox 玩家代号（`14751` `_save_manual_gamertag`）＋联机模块 `config.py:37` `nickname` | `Core/App/Settings.cs:35` `Nickname`＋`PageSettings.xaml.cs` `CommitNickname` | 已迁移 |
| M067 | 设置对话框其余 tab（通用 / 关于） | `HMOL_qt.py:4928` `SettingsDialog._build_general_tab`（`5013`）、`_build_about_tab`（`5031`）、`5053` `_save` | `Pages/PageSettings.xaml(.cs)`（外观 / 昵称 / 天气城市 / 常用网站 / 背景 / 音乐 / 布局 / 扩展 / 开机自启 / 游戏路径 / 程序更新 / 关于与存储）＋`Windows/AboutWindow.xaml(.cs)` | 已迁移 |
| M068 | 壁纸 / BGM / 背景相关设置项 | `HMOL_qt.py:11332` `_build_settings_page` 内的背景与 BGM 控件组 | `Pages/PageSettings.xaml`（`CardBackground` / `CardMusic`）+ `Core/App/Settings.cs` `BackgroundSettings`/`BgmSettings` + `App/Services/BgmPlayer.cs` | 已迁移 |

### 2.9 关于

| 编号 | 功能块 | 旧实现（文件 + 行号 + 类/函数） | 新实现（文件 + 类/方法） | 状态 |
| --- | --- | --- | --- | --- |
| M069 | 关于窗口（版本 / QQ 群 / GitHub） | `HMOL_qt.py:5062` `class AboutDialog`、`5096` `build_widget`、`5271` `_copy_qq_to_clipboard`、`5651` `_open_qq`、`5659` `_open_github` | `src/HMOL.App/Windows/AboutWindow.xaml(.cs)`＋`Core/App/AppInfo.cs` | 已迁移 |
| M070 | 关于页（页面形态） | `HMOL_qt.py:15078` `_build_about_page`、`15090` `_wrap_subpage` | 改为独立窗口 `AboutWindow`（`MainWindow.xaml.cs:233` `OnAboutNavClick`） | 已迁移 |

### 2.10 上传 / 反馈

| 编号 | 功能块 | 旧实现（文件 + 行号 + 类/函数） | 新实现（文件 + 类/方法） | 状态 |
| --- | --- | --- | --- | --- |
| M071 | 上传对话框（资源 / DLC 上传社区） | `HMOL_qt.py:16353` `_show_upload_dialog`、`16433` `_open_upload_url`、`16455` `_open_dlc_upload_wizard` | 无 | 已删除 |
| M072 | 反馈对话框 / 反馈子页 | `HMOL_qt.py:5522` `class FeedbackDialog`（`5539`–`5664`）、`15844` `_build_feedback_subpage` | 无 | 未迁移 |
| M073 | GitHub Issues / QQ 群跳转 | `HMOL_qt.py:5651` `_open_qq`、`5655` `_open_qch`、`5659` `_open_github` | `Windows/AboutWindow.xaml.cs` `OnOpenGitHubClick/OnOpenIssuesClick/OnJoinGroupClick`＋`AppInfo` | 已迁移 |

### 2.11 教程

| 编号 | 功能块 | 旧实现（文件 + 行号 + 类/函数） | 新实现（文件 + 类/方法） | 状态 |
| --- | --- | --- | --- | --- |
| M074 | 教程页（分节 / hero / 导航 / Markdown 渲染） | `HMOL_qt.py:15469` `_build_tutorial_subpage`、`15534` `_tutorial_hero_style`、`15545` `_tutorial_nav_style`、`15565` `_on_tutorial_section_changed`、`15595` `_render_tutorial_block`、`15733` `_render_markdown_block`、`15832` `_refresh_tutorial_subpage_style` | 无 | 未迁移 |

### 2.12 壁纸

| 编号 | 功能块 | 旧实现（文件 + 行号 + 类/函数） | 新实现（文件 + 类/方法） | 状态 |
| --- | --- | --- | --- | --- |
| M075 | 背景管理器（图片 / 视频 / GIF、填充模式、模糊、暗化） | `HMOL_qt.py:2221` `class BackgroundManager`（`2242` `set_background`、`2254` `set_fill_mode`、`2259` `set_blur_radius`、`2263` `set_darken_opacity`、`2267` `set_video_muted`、`6651` `_get_blurred_pixmap`、`6668` `_draw_image_bg`） | `Core/Appearance/BackgroundService.cs`（类型判定 / 导入 / 校验）＋`src/HMOL.App/Views/BackgroundLayer.xaml(.cs)`（图片 / 动图 / 视频渲染 + 模糊 + 暗化；**填充模式未做**） | 已迁移 |
| M076 | 壁纸包（mpkg）导入 + RePKG 解包 | `HMOL_qt.py:12030` `_settings_import_mpkg`、`12115` `_finish_mpkg_import`、`12016` `_find_bg_media_in_dir`、`11983` `_find_repkg_exe`、`11991` `_ensure_repkg_exe` | `Core/Appearance/WallpaperPackageService.cs` `ImportAsync`（释放内嵌工具 → RePKG extract → 选取媒体文件）＋`PageSettings.OnImportWallpaperClick` | 已迁移 |
| M077 | 内嵌 RePKG.exe | `HMOL_QT/hmol_repkg.py`（40161 行，`REPKG_EXE_B64` 于 8 行）；`HMOL_QT/tool/wallpaper/RePKG.exe`、`tool/generate_repkg_embed.py` | `Core/Resources/RePKG.exe`（作为内嵌资源，运行时释放到 `Data\Cache\RePKG.exe`，见 `WallpaperPackageService`） | 已迁移 |
| M078 | 背景配置持久化（`background{}`） | `HMOL_qt.py:2305` `BackgroundManager.to_config`、`2316` `from_config` | `Core/App/Settings.cs` `BackgroundSettings`（FileName/BlurRadius/DimPercent/FadeMs/FromWallpaperPackage） | 已迁移 |

### 2.13 BGM

| 编号 | 功能块 | 旧实现（文件 + 行号 + 类/函数） | 新实现（文件 + 类/方法） | 状态 |
| --- | --- | --- | --- | --- |
| M079 | BGM 播放器（播放列表 / 随机 / 音量 / 上下曲） | `HMOL_qt.py:2329` `class BgmManager`（`2384` `set_playlist`、`2417/2425` `shuffle`/`volume`、`2455` `play_pause`、`2464/2467` `next`/`prev`） | `src/HMOL.App/Services/BgmPlayer.cs`（NAudio：播放 / 暂停 / 上下曲 / 随机 / 循环 / 音量 / 失败跳过） | 已迁移 |
| M080 | BGM 设置控件与状态标签 | `HMOL_qt.py:12143` `_settings_add_bgm`、`12155` `_settings_remove_bgm`、`12164` `_bgm_control`、`12177` `_settings_bgm_shuffle_changed`、`12182` `_settings_bgm_volume_changed`、`12188` `_bgm_update_track_label`、`11690` `_inline_browse_music` | `Pages/PageSettings.xaml(.cs)` `CardMusic` 区块：`OnBgmAddClick`/`OnBgmRemoveRowClick`/`OnBgmPlayPauseClick`/`OnBgmVolumeChanged`/`OnBgmToggleClick` | 已迁移 |
| M081 | BGM 配置持久化（`bgm{}`） | `HMOL_qt.py:9660-9665` `bgm: {playlist, volume, shuffle, current_index}` | `Core/App/Settings.cs` `BgmSettings`（Enabled/Playlist/Volume/Shuffle/Loop/CurrentIndex） | 已迁移 |

### 2.14 主页背景

| 编号 | 功能块 | 旧实现（文件 + 行号 + 类/函数） | 新实现（文件 + 类/方法） | 状态 |
| --- | --- | --- | --- | --- |
| M082 | 主页背景选择 / 重置 / 加载 / 存储 | `HMOL_qt.py:11937` `_apply_home_background`、`18332` `_get_home_background_dir`、`18341` `_choose_home_background`、`18391` `_reset_home_background`、`18407` `_load_saved_home_background`、`12200` `_inline_choose_background`、`12206` `_inline_reset_background` | `Pages/PageSettings.xaml.cs`：`OnPickBackgroundClick`/`OnImportWallpaperClick`/`OnClearBackgroundClick`/`ApplyBackgroundImport`＋`Windows/MainWindow.ApplyBackground` | 已迁移 |
| M083 | 主页背景绘制（图片 / 视频循环 / GIF） | `HMOL_qt.py:6467` `class BackgroundHomePage`（`6539` `set_background`、`6590` `_start_video`、`6605` `_on_video_frame`、`6668` `_draw_image_bg`、`6713` `_draw_video_bg`、`6744` `paintEvent`） | `src/HMOL.App/Views/BackgroundLayer.xaml(.cs)`（铺满主窗口，图片 / 动图 / 视频循环 + 模糊 + 暗化 + 淡入） | 已迁移 |
| M084 | 主页背景配置键 | `HMOL_qt.py:9607`/`9614` `home_background_path` | `Core/App/Settings.cs` `BackgroundSettings.FileName`（素材复制进 `Data\backgrounds`，原文件删除也不影响） | 已迁移 |

### 2.15 自定义布局编辑器

| 编号 | 功能块 | 旧实现（文件 + 行号 + 类/函数） | 新实现（文件 + 类/方法） | 状态 |
| --- | --- | --- | --- | --- |
| M085 | 布局描述符收集 / 编辑器入口 / 方案管理 | `HMOL_qt.py:11712` `_ensure_layout_editor`、`11811` `_refresh_layout_current_label`、`11827` `_open_layout_editor`、`11902` `_refresh_descriptors_geometry`、`11914` `_open_layout_scheme_manager`、`10103` `_apply_sidebar_state_to_widget` | `Core/Layout/LayoutStore.cs`＋`App/Layout/LayoutElements.cs`＋`Windows/MainWindow.ApplyLayout/ApplyScheme`＋设置页 `OnOpenLayoutEditorClick/OnResetLayoutClick` | 已迁移 |
| M086 | 布局编辑器模块（拖拽 / 对齐 / 方案保存） | `HMOL_QT/HMOL_custom_layout.py`（2320 行：`LayoutManager`、`ElementDescriptor`、`open_layout_editor`、`open_scheme_manager`） | `Core/Layout/LayoutScheme.cs`（方案 / 条目模型）＋`src/HMOL.App/Windows/LayoutEditorWindow.xaml(.cs)`（调整顺序 / 显隐 / 重命名，多方案保存） | 已迁移 |

### 2.16 彩蛋

| 编号 | 功能块 | 旧实现（文件 + 行号 + 类/函数） | 新实现（文件 + 类/方法） | 状态 |
| --- | --- | --- | --- | --- |
| M087 | 彩蛋系统（裂隙生成器 / 铁幕装置 / 心灵终结仪 / 沃克网入侵 / 退出逆转） | `HMOL_qt.py:18432` `_init_easter_eggs`、`18464` `eventFilter`、`18472` `_ee_trigger_gap_generator`、`18514` `_ee_trigger_iron_curtain`、`18571` `_ee_on_version_click`、`18579` `_ee_trigger_proselyte`、`18617` `_ee_volknet_intrusion`、`9677`（退出逆转）、`10580`（主页五连击） | 无 | 未迁移 |

### 2.17 在线更新

| 编号 | 功能块 | 旧实现（文件 + 行号 + 类/函数） | 新实现（文件 + 类/方法） | 状态 |
| --- | --- | --- | --- | --- |
| M088 | 更新清单获取（多 CDN 镜像） | `HMOL_QT/HMOL_updater.py:90-94` `MANIFEST_PATHS`、`97-108` `GITHUB_MIRRORS`、`196-236` `UpdateChecker.fetch_raw/check` | `src/HMOL.Core/Updater/LauncherUpdater.cs` `CheckAsync`（双源：GitHub 走 `releases/latest` 302 取 tag → `releases/expanded_assets/{tag}` 抠资产；Gitee 走 API `releases/latest` + `attach_files`；按用户线路偏好决定顺序、单源不可达自动换源）＋`Updater/HttpDownloader.cs`（内存缓存 5 分钟 + 重试）。**无清单文件、无 CDN 镜像** | 已迁移 |
| M089 | 版本比较与 major 强制更新 | `HMOL_updater.py:125` `_parse_version`、`138` `is_newer`、`142` `is_major_update`、`173-191` `UpdateInfo.from_json` | `Core/Updater/SemVer.cs` `Compare/IsNewer/Normalize`（忽略 v 前缀、补零、纯数字按数值比较）；**取消 major 强制更新**，新版本一律可「稍后」 | 已迁移 |
| M090 | 更新包下载（流式 / 续传 / SHA256 校验） | `HMOL_updater.py:242-372` `UpdateDownloader.run/_download_with_retry` | `Core/Updater/ResumableDownloader.cs`（Range 续传写 `.part`，重试 1/3/7 秒 + `Retry-After`，磁盘空间预检）＋`LauncherUpdater.ValidateStaged/TryExtractExecutable`（zip 资产解出 exe）。校验改为**「≥5MB + 文件头 `MZ`」**，不再用 SHA256 | 已迁移 |
| M091 | 更新对话框 UI | `HMOL_updater.py:398-860` `UpdateDialog`（`548` `show_no_update`、`573` `show_update_info`、`599` `_show_progress`、`778` `check_then_show`） | `src/HMOL.App/Services/LauncherUpdateFlow.cs`（`CheckAsync` / `StartupCheckAsync` / `InstallAsync`）复用现有 `Windows/ChoiceWindow.xaml(.cs)`（三选：现在更新 / 稍后 / 打开发布页）＋`Windows/ProgressWindow.xaml(.cs)`（可取消） | 已迁移 |
| M092 | 替换 exe 并重启（bat 助手） | `HMOL_updater.py:894` `generate_helper_bat`、`1001` `apply_update_and_restart` | `LauncherUpdater.WriteInstallScript/BuildScriptText/StartScript`（写 `%TEMP%\HMOL-update-{pid}.cmd`，`chcp 65001` + UTF-8 无 BOM；`tasklist /FI "PID eq {pid}"` 轮询上限 90 秒 → 换 exe → 成功删备份，失败回滚并留 `locked`/`timeout`/`broken` 标记）；`App.xaml.cs` 启动时 `CleanupStaleFiles` + `TakePendingFailureNote` 收尾。原 `--apply-update`/`--wait-pid`/`--target` 维护分支已删除 | 已迁移 |
| M093 | 静默检查（24h 节流） | `HMOL_qt.py:16191` `_silent_check_update`、`16199` `_do_silent_check`；`HMOL_updater.py:875/886` `check_throttle_ok`/`mark_checked` | `Settings.CheckUpdateOnStartup`＋`App.xaml.cs:158` `Dispatcher.BeginInvoke(ApplicationIdle, LauncherUpdateFlow.StartupCheckAsync)`（失败/已最新一律静默）；`Settings.LastUpdateCheck` 只作展示，**无 24h 节流** | 已迁移 |
| M094 | 手动检查更新入口 | `HMOL_qt.py:16230` `_on_manual_check_update`、`16246` `apply_launcher_update` | `Pages/PageSettings.xaml(.cs)` `CardUpdate`：自动检查开关、线路选择（Auto/GitHub/Gitee）、三态按钮「检查启动器更新 → 下载并更新到 vX.Y.Z → 打开下载页」、`LabUpdateLastCheck` / `LabUpdateStatus` | 已迁移 |
| M095 | 更新清单文件 | `HMOL_QT/update/HMOL.update.json`、`HMOL.wine.update.json` | 无（新版**不设清单文件**，改为发布页约定：资产名须以 `HMOL` 开头，优先 `.exe`、退化 `.zip`，见 `LauncherUpdater.PickAsset`） | 已删除 |
| M096 | 运行变体检测（Windows / Wine） | `HMOL_updater.py:56-89` `detect_variant` | 无（新版不支持 Wine 变体） | 已删除 |

### 2.18 联机模块

| 编号 | 功能块 | 旧实现（文件 + 行号 + 类/函数） | 新实现（文件 + 类/方法） | 状态 |
| --- | --- | --- | --- | --- |
| M097 | 联机主界面（建房 / 加入 / 状态栏 / 日志） | `HMOL联机模块/HMOL联机模块.py:222` `class HMOLApp`（`2906`–`4074` 共 14 个对话框类；`main()` 在 `4151`） | `src/HMOL.App/Pages/PageMultiplayer.xaml(.cs)`（组网 / 大厅 / 对端三块 + 网络工具箱）＋`Windows/Multiplayer/*` 各独立窗口 | 已迁移 |
| M098 | EasyTier 引擎 | `HMOL联机模块/engine_easytier.py:160` `class EasyTierEngine`（`78` `cleanup_stale_easytier`、`99` `tun_ips`、`126` `_parse_peer_table`） | `Core/Multiplayer/EasyTierEngine.cs`（`EngineProcessBase` 派生，含清理残留 / 解析 peer 表） | 已迁移 |
| M099 | n2n 引擎（edge + supernode） | `HMOL联机模块/engine_n2n.py:98` `class N2NEngine`、`228` `class SupernodeServer` | `Core/Multiplayer/N2nEngine.cs`＋`Core/Multiplayer/SupernodeServer.cs` | 已迁移 |
| M100 | ZeroTier 引擎 | `HMOL联机模块/engine_zerotier.py:252` `class ZeroTierEngine`、`212-252` `api_*`（Central API 建网/授权） | 无 | 已删除 |
| M101 | Anywherelan（awl）引擎 | `HMOL联机模块/engine_awl.py:93` `class AWLEngine` | 无 | 已删除 |
| M102 | ZeroTier 工具箱对话框 | `HMOL联机模块/HMOL联机模块.py:3353` `class ZtToolDialog` | 无 | 已删除 |
| M103 | MQTT 大厅（信标 / 在线列表 / 大厅聊天 / 公开房间） | `HMOL联机模块/hall.py:195` `class Hall`、`45` `class _HallChat`、`25` `HALL_BROKERS`（broker.emqx.io / test.mosquitto.org）、`26-30` 主题前缀 | `Core/Multiplayer/HallClient.cs`（MQTTnet，多方 broker 轮询 + `HallChat`）＋`Pages/PageMultiplayer.xaml(.cs)` 大厅子页 | 已迁移 |
| M104 | 房间聊天 + 房主踢人 | `HMOL联机模块/chat.py:33` `class ChatChannel`、`167` `send_text`、`192` `announce_owner`、`198` `kick`、`214` `unkick`、`236` `_owner_valid` | `Core/Multiplayer/RoomChat.cs`＋`Windows/Multiplayer/MultiplayerChatWindow.xaml(.cs)` | 已迁移 |
| M105 | 在线文件传输 | `HMOL联机模块/filetrans.py:44` `class FileTransfer`、`14` `FILE_PORT = 17800`、`15` `MAX_FILE_SIZE = 500MB` | `Core/Multiplayer/FileTransfer.cs`＋`Windows/Multiplayer/MultiplayerFileWindow.xaml(.cs)` | 已迁移 |
| M106 | 游戏内 HUD 浮窗 | `HMOL联机模块/hud.py:24` `class GameHUD` | `src/HMOL.App/Windows/Multiplayer/MultiplayerHudWindow.xaml(.cs)`＋`Services/MultiplayerHub.cs`（位置跨会话保存：`MultiplayerSettings.HudLeft/HudTop`） | 已迁移 |
| M107 | 系统托盘 | `HMOL联机模块/tray.py:77` `class SysTray`、`43` `class NOTIFYICONDATAW` | `src/HMOL.App/Interop/TrayIcon.cs`＋`App.xaml.cs` `InitializeTray`（含托盘菜单） | 已迁移 |
| M108 | 进程守护 + 开机自启 | `HMOL联机模块/guard.py:43` `class ProcessGuard`、`116` `auto_start_command`、`123` `auto_start_set`、`152` `auto_start_enabled` | `Core/Multiplayer/ProcessGuard.cs`＋`Core/Multiplayer/AutoStartManager.cs`＋设置页「开机自启」卡 | 已迁移 |
| M109 | 网络工具箱（PING / 带宽 / NAT / 防火墙 / 跃点） | `HMOL联机模块/toolkit.py:220` `ping`、`242` `tcp_latency`、`259/298` `tcp_bandwidth_*`、`322/358` `udp_bandwidth_*`、`441` `nat_type`、`468/482/491` `firewall_*`、`31` `sanitize_text`、`43/68` `chat_encrypt`/`chat_decrypt`、`175` `chat_policy` | `Core/Multiplayer/NetworkToolkit.cs`（`PingAsync`/`TcpLatencyAsync`/`Tcp|UdpBandwidth*`/`NatTypeAsync`/`FirewallStateAsync`/`SetFirewallAsync`/`SetInterfaceMetricAsync`）＋`Core/Multiplayer/ChatCrypt.cs` | 已迁移 |
| M110 | TAP 驱动检测与安装 / WinIPBroadcast | `HMOL联机模块/toolkit.py:500` `tap_count`、`513` `tap_install`、`535` `winipbroadcast_ensure`；资源 `HMOL联机模块/resources/tap/`（`tap0901.sys`、`OemVista.inf`、`tapinstall.exe`） | `NetworkToolkit.TapCountAsync/InstallTapDriverAsync/EnsureWinIpBroadcastAsync`；资源 `runtime/tap/`、`runtime/winipbroadcast/`；提权执行 `ElevatedTasks`；入口：联机页「n2n 专属工具 → 安装 TAP 驱动」「网络工具箱 → 广播转发服务」 | 已迁移 |
| M111 | 引擎运行资源（easytier / n2n） | `HMOL联机模块/resources/easytier/`（`easytier-core.exe`、`easytier-cli.exe`、`wintun.dll`）、`resources/n2n/`（`edge.exe`、`supernode.exe`） | `runtime/easytier/`、`runtime/n2n/`＋`Core/Multiplayer/RuntimeLocator.cs`（校验与定位） | 已迁移 |
| M112 | awl 运行资源 | `HMOL联机模块/resources/awl/`（`awl.exe`、`config_awl.json`、`wintun.dll`、`pkg/awl.exe`） | 无 | 已删除 |
| M113 | 内置公共节点列表 | `HMOL联机模块/nodes.py:10` `N2N_PUBLIC_NODES`、`20` `EASYTIER_PUBLIC_NODES`、`36-61` 选项/取值函数 | `Core/Multiplayer/NodeCatalog.cs` | 已迁移 |
| M114 | 分享文本与二维码 | `HMOL联机模块/share.py:19` `build_share_text`、`42` `parse_share_text`、`93` `qr_matrix`、`117` `qrcode_available` | `Core/Multiplayer/ShareCode.cs` `Build/Parse`＋`Windows/Multiplayer/MultiplayerShareWindow.xaml(.cs)`（**二维码绘制未做**，见 `ShareCode.cs:28` 注释） | 已迁移 |
| M115 | 收藏队友 | `HMOL联机模块/friends.py:14` `_load_raw`、`25` `list_friends`、`32` `upsert`、`55` `remove`、`69` `touch` | `Core/Multiplayer/FriendStore.cs`＋联机页「对端 / 好友」区块 | 已迁移 |
| M116 | 联机模块配置持久化 | `HMOL联机模块/config.py:46` `class Config`、`15` `CONFIG_FILE = 'hmol-LJ-DLC_config.json'`、`18-43` `DEFAULTS` | `Core/Multiplayer/MultiplayerSettings.cs`＋`MultiplayerSettingsStore.cs`（落盘 `Data\multiplayer.json`） | 已迁移 |
| M117 | 联机模块 EULA 门 | `HMOL联机模块/HMOL联机模块.py:3877` `class EulaDialog`、`310` `_show_eula_gate` | 无 | 未迁移 |
| M118 | 联机模块防篡改自校验（integrity.json） | `HMOL联机模块/HMOL联机模块.py:313` `_verify_integrity`；`HMOL联机模块/dist/integrity.json` | `Core/Security/IntegrityChecker.cs`＋`Security/StartupSecurityCheck.cs`（`App.RunSecuritySelfCheck` 调用，与旧版一致只告警不退出） | 已迁移 |
| M119 | HMOL 集成参数契约（`--hmol-*`） | `HMOL联机模块/hmol_integration.py:15` `parse_hmol_args`、`49` `check_hmol_login`、`83` `get_xbox_username` | 无 | 已删除 |
| M120 | 整程序提权（uac_admin） | `HMOL联机模块/HMOL联机模块.spec:78` `uac_admin=True`、`HMOL联机模块.py:70` `is_admin` | `src/HMOL.App/app.manifest:21` `asInvoker`＋按需提权已实现：`App.xaml.cs` `OnStartup` 识别 `--elevated` 并执行 `ElevatedTasks.RunAsync`，`Core/Multiplayer/ElevationHelper.cs` 用 `runas` 拉起、结果文件回传 | 已删除 |

### 2.19 微软账号 / MSAL / Xbox

| 编号 | 功能块 | 旧实现（文件 + 行号 + 类/函数） | 新实现（文件 + 类/方法） | 状态 |
| --- | --- | --- | --- | --- |
| M121 | 账号管理（设备码登录 / Graph / Xbox） | `HMOL_qt.py:8607` `class AuthManager`（`8700` `start_device_flow`、`8719` `poll_device_flow`、`8793` `_graph_get`、`8876` `_acquire_xbox_token`、`8965` `get_xbox_gamertag`、`9007` `get_xbox_friends`） | 无 | 已删除 |
| M122 | 登录对话框（设备码 / 离线模式） | `HMOL_qt.py:9067` `class LoginDialog`（`9242` `_do_login`、`9291` `_poll_flow`、`9328` `_do_offline`） | 无 | 已删除 |
| M123 | 账号页（头像 / 玩家代号 / Xbox 好友列表） | `HMOL_qt.py:14440` `_build_account_page`、`14674` `_async_load_account`、`14702` `_on_account_loaded`、`14751` `_save_manual_gamertag`、`14778`–`15066` 好友加载/排序/筛选/头像、`15066` `_do_logout` | 无 | 已删除 |
| M124 | 启动时强制登录门 | `HMOL_qt.py:18901-18953` `main()` 内 MSAL 分支 | 无 | 已删除 |
| M125 | MSAL 常量配置 | `HMOL_qt.py:655-661` `MSAL_CLIENT_ID`/`MSAL_AUTHORITY`/`MSAL_SCOPES`/`GRAPH_API_BASE` | 无 | 已删除 |
| M126 | 令牌缓存（加密落盘） | `HMOL_qt.py:657` `MSAL_CACHE_FILE = "msal_token_cache.enc"`、`8619` `_init_app`、`8656` `_save_cache`、`8749` `acquire_token_silent` | 无 | 已删除 |
| M127 | 离线模式降级分支 | `HMOL_qt.py:9478` `MainWindow.__init__(auth_manager, is_offline)`、`9333` `_start_network_check`、`7341` `_check_network_available` | 无 | 已删除 |

### 2.20 网盘下载（OneDrive / 资源下载页 / 笨蛋广场）

| 编号 | 功能块 | 旧实现（文件 + 行号 + 类/函数） | 新实现（文件 + 类/方法） | 状态 |
| --- | --- | --- | --- | --- |
| M128 | OneDrive 浏览（Graph + SharePoint 双通道） | `HMOL_qt.py:7383` `class OneDriveBrowser`（`7444` `_get_token`、`7455` `_graph_req`、`7533` `_sp_init_session`、`7612` `_sp_list_folder`、`7738` `_graph_list_folder`、`7776` `search_folder`） | 无 | 已删除 |
| M129 | 下载悬浮窗（后台下载进度） | `HMOL_qt.py:8316` `class DownloadFloatingWidget`（`8429` `update_progress`、`8476` `_on_cancel`） | 无 | 已删除 |
| M130 | 下载信息对话框（文件名 / 大小 / 保存位置） | `HMOL_qt.py:8523` `class DownloadInfoDialog` | 无 | 已删除 |
| M131 | 分片并发下载与断点续传 | `HMOL_qt.py:7886` `download_file`、`7956` `_od_calc_segments`、`8033` `_od_segmented_download`、`8172` `_od_stream_download`、`8240` `_sp_download_file` | 无 | 已删除 |
| M132 | 下载源页面（多线路 / 分页 / 搜索 / 属性） | `HMOL_qt.py:12493` `_od_show_line_selector`、`12550` `_od_open_line`、`12567` `_build_onedrive_page`、`12751` `_make_od_header_row`、`12771` `_make_od_item_row`、`12888`–`13177` `_od_*` 系列 | 无 | 已删除 |
| M133 | 下载源 URL 配置（混淆存储） | `HMOL_qt.py:976-1048` 各下载源 `url`（`deobfuscate_string`） | 无 | 已删除 |
| M134 | 游戏分类页 / 占位页 | `HMOL_qt.py:12410` `_build_game_category_page`、`12392` `_build_placeholder_page` | 无 | 已删除 |
| M135 | OneDrive 分享链接校验 | `HMOL_qt.py:7411` `_validate_onedrive_url`、`7358` `_share_url_to_token`、`7367` `_parse_share_url` | 无 | 已删除 |
| M136 | OneDrive 下载基准脚本与文档 | `HMOL_QT/scripts/od_download_bench.py`、`HMOL_QT/docs/onedrive-download-benchmark.md` | 无 | 已删除 |

### 2.21 DLC 体系

| 编号 | 功能块 | 旧实现（文件 + 行号 + 类/函数） | 新实现（文件 + 类/方法） | 状态 |
| --- | --- | --- | --- | --- |
| M137 | DLC 打包向导 | `HMOL_qt.py:5664` `class PackageWizardDialog`（`5710` `_build_meta_page`、`5822` `_validate_meta`、`5905` `_gen_skeleton`、`5931` `_pack`、`5970` `_open_community_upload`） | 无 | 已删除 |
| M138 | DLC 页面 / 设置卡 / JSON 规范说明 | `HMOL_qt.py:13733` `_build_dlc_page`、`13846` `_dlc_toc_navigate`、`13855` `_build_dlc_settings_card`、`13939` `_show_dlc_json_spec`、`13997` `_dlc_btn_style` | 无 | 已删除 |
| M139 | DLC 列表 / 卡片 / 启动 | `HMOL_qt.py:14029` `_dlc_refresh`、`14081` `_scan_dlc_folder`、`14137` `_parse_dlc_json`、`14245` `_make_dlc_card`、`14370` `_dlc_launch`、`14428` `_dlc_open_folder` | 无 | 已删除 |
| M140 | DLC 下载后处理流水线（校验 / 解压 / 日志） | `HMOL_qt.py:13480` `_dlc_log`、`13494` `_dlc_verify_archive`、`13532` `_dlc_extract_after_download`、`13691` `_dlc_scan_pending_archives`、`13408` 下载后自动解压 | 无 | 已删除 |
| M141 | DLC 文件夹监听与路径 | `HMOL_qt.py:14009` `_dlc_folder_path`、`14012` `_dlc_setup_watcher`、`14025` `_dlc_on_folder_changed` | 无 | 已删除 |
| M142 | `DLC.json` 体系与 DLC 目录 | `HMOL_QT/DLC/Example_MyTheme/DLC.json`、`DLC-wine.json`、`DLC/dlc_operations.log`；`HMOL联机模块/DLC.json` | 无 | 已删除 |
| M143 | 导航项「程序DLC下载」 | `HMOL_qt.py:1046` 资源条目、`10086`–`10088`、`10160`、`10307`、`10320-10330`、`13205-13215`、`13425` | 无 | 已删除 |
| M144 | DLC 安装后重启程序 | `HMOL_qt.py:13722` `_restart_application`、`13426` 重启提示 | 无 | 已删除 |

### 2.22 死代码 / 工具链

| 编号 | 功能块 | 旧实现（文件 + 行号 + 类/函数） | 新实现（文件 + 类/方法） | 状态 |
| --- | --- | --- | --- | --- |
| M145 | 速率限制器（死代码） | `HMOL_QT/rate_limiter.py`（195 行）；`HMOL_qt.py:643-648` 导入，`get_login_limiter`/`get_api_limiter` 全文无调用点 | 无 | 已删除 |
| M146 | 输入验证模块（基本未接入） | `HMOL_QT/input_validation.py`（269 行）；`HMOL_qt.py:634-640` 导入，仅 `5866` 处用到 `sanitize_filename` | 无（`Core/IO/PathGuard.cs` 覆盖路径/名称安全校验） | 已删除 |
| M147 | 字符串混淆与加密工具 | `HMOL_QT/crypto_utils.py`（394 行）；`HMOL_qt.py:595-598` 导入、`655-658`/`976-1048`/`5973`/`16395` 使用；`HMOL_QT/obfuscate.py`（76 行） | 无（混淆改由 `build-tools/confuserex/obfuscate.ps1` 承担） | 已删除 |
| M148 | 反调试 / 完整性校验 | `HMOL_QT/anti_debug.py`（298 行：`is_debugger_present`、`check_tampering`、`check_suspicious_environment`、`verify_runtime_integrity`）；调用点 `HMOL_qt.py:626-630`、`18864-18872` | `Core/Security/`：`DebuggerDetector.cs`、`SandboxDetector.cs`、`IntegrityChecker.cs`、`StartupSecurityCheck.cs`（`App.RunSecuritySelfCheck`，只写日志不退出） | 已迁移 |
| M149 | 发布前安全审计脚本 | `HMOL_QT/security_audit.py`（164 行） | 无 | 已删除 |
| M150 | 临时文件清理脚本 | `HMOL_QT/cleanup_temp.py`（392 行） | 无 | 已删除 |
| M151 | 打包 / 构建脚本 | `HMOL_QT/HMOL.spec`、`build.bat`、`build_enhanced.bat`、`clean.bat`、`test_clean.bat` | `build.bat`（读 `AppInfo.cs` 版本 + 混淆 + 单文件打包）、`build-tools/confuserex/obfuscate.ps1` | 已迁移 |
| M152 | 内嵌 RePKG 数据模块 | `HMOL_QT/hmol_repkg.py`（40161 行） | 改为把 `RePKG.exe` 作为内嵌资源放进 `Core/Resources/`（运行时释放到 `Data\Cache`，见 `WallpaperPackageService`），不再内嵌 Base64 数据模块 | 已迁移 |
| M153 | 主程序未使用的托盘导入（死导入） | `HMOL_qt.py:114` `QSystemTrayIcon`（全文仅此一处，无使用） | 无 | 已删除 |

> 说明：M152 与 M077 都指 `hmol_repkg.py`，分别从「壁纸功能」与「死代码/工具链」两个视角列出：M077 是功能依赖，M152 是模块本身。

### 2.23 新版新增（旧版无对应实现）

以下项在新代码中存在、旧版没有，不占用上面的状态统计：

| 新实现 | 说明 |
| --- | --- |
| `Core/IO/PathGuard.cs` | 统一的路径穿越防护（绝对路径 / `..` / 盘符 / 落点越界），旧版靠 `input_validation` 但基本没接入 |
| `Core/IO/OperationJournal.cs` | 操作级回滚（隔离区搬运 + `.bak-<时间戳>` + 逆序撤销），旧版没有 |
| `Core/IO/ProgressSpan.cs` | 阶段进度映射（0–0.3 解压 / 0.3–0.95 复制） |
| `src/HMOL.App/Services/GameSessionHub.cs` | 游戏会话中枢：全局只允许一个会话，输出转投 `ActivityLog`，退出后释放（主页与实例页共用） |
| `src/HMOL.App/Windows/ChoiceWindow.xaml(.cs)` | 统一的多选项确认窗（旧版对应 `QMessageBox.question`，但布局与按钮语义是新的） |
| `src/HMOL.App/Windows/TextInputWindow.xaml(.cs)` | 统一的单行文本输入 + 校验回调（旧版对应 `QInputDialog.getText`） |
| `src/HMOL.App/Windows/InstanceEditorWindow.xaml(.cs)` | 实例新建/编辑窗口，带实时校验（旧版为 `AddInstanceDialog`，此处单列是因为它同时承担了「编辑」职责，旧版是编辑入口分散在实例对话框内） |
| `App.xaml.cs` 单实例互斥量 | 旧版无单实例判断 |
| `Core/Apps/Paths.cs` `HMOL_DATA` 环境变量 | 数据目录可重定向（便携/测试） |

---

## 3. 删除清单

> 每条写明：删了什么 / 旧位置 / 删除理由（引用既定范围）。

| # | 删了什么 | 旧位置 | 删除理由（对应既定范围） |
| --- | --- | --- | --- |
| D01 | 微软账号整套：`AuthManager`、`LoginDialog`、账号页、Xbox 好友列表 | `HMOL_qt.py:8607`、`9067`、`14440`–`15066` | 范围明确「删除微软账号 / MSAL / Xbox 全链」 |
| D02 | MSAL 常量与令牌缓存 | `HMOL_qt.py:655-661`、`657`、`8619`、`8656` | 同上；令牌缓存已无用途 |
| D03 | 启动时强制登录门与离线模式降级 | `HMOL_qt.py:18901-18953`、`9478`、`9333` | 同上；新版无账号概念，直接进主界面 |
| D04 | OneDrive 浏览（Graph + SharePoint） | `HMOL_qt.py:7383`（`7444`–`8298`） | 范围明确「删除 OneDrive 与所有网盘下载」 |
| D05 | 分片并下发下载与断点续传 | `HMOL_qt.py:7886`、`7956`、`8033`、`8172`、`8240` | 同上 |
| D06 | 下载悬浮窗与下载信息对话框 | `HMOL_qt.py:8316`、`8523` | 同上 |
| D07 | 下载源页面与全部 `_od_*` 逻辑 | `HMOL_qt.py:12493`–`13177` | 同上 |
| D08 | 下载源 URL 配置（含混淆字符串） | `HMOL_qt.py:976-1048` | 同上；这些 URL 只服务于网盘下载 |
| D09 | 游戏分类页 / 占位页 | `HMOL_qt.py:12410`、`12392` | 属于「资源下载页」 |
| D10 | OneDrive 分享链接解析与校验 | `HMOL_qt.py:7411`、`7358`、`7367` | 同上 |
| D11 | 下载基准脚本与基准文档 | `HMOL_QT/scripts/od_download_bench.py`、`HMOL_QT/docs/onedrive-download-benchmark.md` | 同上 |
| D12 | 包下载入口（走网盘） | `HMOL_qt.py:17602`、`3647` | 范围明确「删除资源下载页」 |
| D13 | DLC 打包向导 | `HMOL_qt.py:5664`（`5710`–`5979`） | 范围明确「删除 DLC 整套（打包向导 / `DLC.json` 体系）」 |
| D14 | DLC 页面 / 设置卡 / JSON 规范 / 导航项 | `HMOL_qt.py:13733`、`13855`、`13939`、`13846`、`10160`、`10307`、`10320-10330` | 同上 |
| D15 | DLC 列表 / 卡片 / 启动 / 打开目录 | `HMOL_qt.py:14029`、`14081`、`14137`、`14245`、`14370`、`14428` | 同上 |
| D16 | DLC 下载后处理流水线（校验/解压/日志/扫描） | `HMOL_qt.py:13480`、`13494`、`13532`、`13691` | 同上；依赖 DLC 下载渠道 |
| D17 | DLC 文件夹监听 | `HMOL_qt.py:14009`、`14012`、`14025` | 同上 |
| D18 | `DLC.json` 文件与 `DLC/` 目录 | `HMOL_QT/DLC/Example_MyTheme/DLC.json`、`DLC-wine.json`、`DLC/dlc_operations.log`；`HMOL联机模块/DLC.json` | 同上 |
| D19 | `--hmol-*` 参数契约 | `HMOL联机模块/hmol_integration.py:15` `parse_hmol_args`（`--hmol-root`/`--hmol-instance`/`--hmol-gamertag`）、`49` `check_hmol_login`（检查 MSAL 缓存）、`83` `get_xbox_username` | 范围明确「删除 `--hmol-*` 参数契约」；该契约的存在前提（DLC 由 HMOL 拉起来 + 微软账号）已一并删除 |
| D20 | DLC 安装后重启程序 | `HMOL_qt.py:13722`、`13426` | 同上 |
| D21 | 上传对话框（资源 / DLC 上传社区） | `HMOL_qt.py:16353`、`16433`、`16455` | 属于「DLC 整套」与「上传」；社区上传页是 DLC 分发链路 |
| D22 | **ZeroTier 引擎（四套引擎之一，裁掉）** | `HMOL联机模块/engine_zerotier.py:252` `ZeroTierEngine`、`212` `api_create_network`、`233` `api_get_network`、`238` `api_list_members`、`246` `api_authorize_member`；`95` `download_msi`、`111` `install_service` | 范围明确「保留 EasyTier + n2n；删除 ZeroTier」 |
| D23 | **Anywherelan（awl）引擎（四套引擎之一，裁掉）** | `HMOL联机模块/engine_awl.py:93` `AWLEngine`；资源 `HMOL联机模块/resources/awl/`（`awl.exe`、`config_awl.json`、`wintun.dll`、`pkg/awl.exe`） | 范围明确「保留 EasyTier + n2n；删除 Anywherelan」 |
| D24 | ZeroTier 工具箱对话框 | `HMOL联机模块/HMOL联机模块.py:3353` `ZtToolDialog` | 随 ZeroTier 引擎一起裁掉 |
| D25 | 整程序提权（`uac_admin=True`） | `HMOL联机模块/HMOL联机模块.spec:78`；`HMOL联机模块.py:70` `is_admin`、`306-307` 权限告警 | 范围明确「`uac_admin=True` 整程序提权 → `asInvoker` 按需提权」；新清单 `src/HMOL.App/app.manifest:21` 已是 `asInvoker` |
| D26 | 速率限制器（死代码） | `HMOL_QT/rate_limiter.py`（195 行）；`HMOL_qt.py:643-648` | 范围明确「死代码（`rate_limiter` 等）」；`get_login_limiter`/`get_api_limiter` 全文无调用 |
| D27 | 输入验证模块 | `HMOL_QT/input_validation.py`（269 行）；`HMOL_qt.py:634-640`、`5866` | 范围「死代码」；唯一使用点在已删除的 DLC 向导里，且能力已被 `PathGuard` 覆盖 |
| D28 | 字符串混淆/加密工具 | `HMOL_QT/crypto_utils.py`（394 行）、`HMOL_QT/obfuscate.py`（76 行）；`HMOL_qt.py:595-598`、`655-658`、`976-1048`、`5973`、`16395` | 使用面集中在已删除的 MSAL 配置与下载 URL；新版混淆由 ConfuserEx 承担 |
| D29 | 发布前安全审计脚本 | `HMOL_QT/security_audit.py`（164 行） | 面向旧 Python 代码库的审计规则（检测 `.py` 里的硬编码密钥），对新 C# 仓库无意义 |
| D30 | 临时文件清理脚本 | `HMOL_QT/cleanup_temp.py`（392 行） | 独立开发脚本，不属于程序功能（未在 `HMOL_qt.py` 中被引用） |
| D31 | 旧的 PyInstaller 打包链 | `HMOL_QT/HMOL.spec`、`build_enhanced.bat`、`clean.bat`、`test_clean.bat` | 范围「死代码」；新版构建由 `build.bat` + ConfuserEx 取代（`build.bat` 本身在 M151 记为已迁移） |
| D32 | 主程序未使用的托盘导入 | `HMOL_qt.py:114` `QSystemTrayIcon` | 范围「死代码」；旧版主程序没有托盘实现，导入即无用 |
| D33 | 依赖检查对话框（随依赖消失） | `HMOL_qt.py:18642`、`5979`、`40-62` | 旧版需要用户自备 PIL/py7zr/rarfile；新版自包含，检查无对象 |

---

## 4. 未迁移待办清单（按优先级）

优先级：**P0** = 不补则核心流程走不通；**P1** = 保留范围内的功能缺口，用户可感知；**P2** = 保留范围内的次要功能；**P3** = 收尾/合规类。

### P0

| 编号 | 要做什么 | 旧实现位置 | 预估涉及的新文件 | 依赖关系 |
| --- | --- | --- | --- | --- |
| T03 | 插件包页真实界面：可用/已安装双列表、安装/卸载/导入/移除/预览按钮、进度与结果提示 | `HMOL_qt.py:3525` `PackageManagerTab`、`3549-3787` | `Pages/PagePackages.xaml(.cs)` | 依赖已就绪的 `PackageLibrary` / `PackageInstaller`；进度接入可用现成的 `ProgressWindow`（`Windows/ProgressWindow.xaml.cs`） |
| T05 | 联机模块接入：EasyTier + n2n 引擎（进程管理、虚拟 IP、对端表、清理残留进程） | `engine_easytier.py:160`、`engine_n2n.py:98`/`228` | 新增 `HMOL.Core/Multiplayer/EasyTierEngine.cs`、`N2nEngine.cs`、`SupernodeServer.cs` | 依赖资源文件（`resources/easytier`、`resources/n2n`）+ TAP 驱动（T10）；引擎进程输出需接入 `ActivityLog` |
| T06 | 联机页面骨架：建房 / 加入 / 节点选择 / 状态与日志面板 | `HMOL联机模块.py:222` `HMOLApp` | `Pages/PageMultiplayer.xaml(.cs)` | 依赖 T05、T07、T13 |

> 原 P0 的 T01（实例页）、T02（主页）、T04（备份/还原 UI）已在本轮并行开发中落地：分别见主表 `M018` / `M020` / `M048`。为便于对照旧待办编号，此处删除对应行而不重新编号。

### P1

| 编号 | 要做什么 | 旧实现位置 | 预估涉及的新文件 | 依赖关系 |
| --- | --- | --- | --- | --- |
| T07 | MQTT 大厅：在线列表、大厅聊天、公开房间、邀请（含 MQTT 主题与共享密钥） | `hall.py:195` `Hall`、`45` `_HallChat`、`25` `HALL_BROKERS`、`26-34` | 新增 `Multiplayer/HallClient.cs`（MQTT 库待定） | 需先确定 .NET MQTT 客户端库；与 T05 解耦（大厅不依赖虚拟网卡） |
| T08 | 房间聊天 / 房主踢人 / HUD 浮窗 | `chat.py:33` `ChatChannel`（`198` `kick`、`214` `unkick`）、`hud.py:24` `GameHUD` | 新增 `Multiplayer/RoomChat.cs`、`Multiplayer/GameHud.cs` | 依赖 T05（要虚拟 IP 才能单播）；HUD 需置顶无边框窗口 + 拖动记忆 |
| T09 | 文件传输（TCP 17800，单文件上限 500MB，头部 JSON + 二进制流） | `filetrans.py:44` | 新增 `Multiplayer/FileTransfer.cs`、`Windows/FileTransferWindow.xaml(.cs)` | 依赖 T05 |
| T10 | TAP 驱动按需提权：检测网卡 → 不足时提示 → `runas` 以 `--elevated tap-install` 重启自身 → 提权实例装驱动后退出 | `toolkit.py:500` `tap_count`、`513` `tap_install`；提权需求见 `HMOL联机模块.spec:78` | `App.xaml.cs`（`--elevated` 分支落地）、新增 `Core/IO/Elevation.cs`、`Core/Multiplayer/TapDriver.cs` | `app.manifest` 已是 `asInvoker`（`src/HMOL.App/app.manifest:21`），提权实例分流已在 `App.xaml.cs:87` 留好入口（当前只写日志） |
| T11 | 进程守护与托盘：守护 `edge.exe`/`easytier-core.exe`/`supernode.exe`，异常退出自动重启（防抖 + 频率上限）；托盘菜单显示/退出 | `guard.py:43` `ProcessGuard`、`tray.py:77` `SysTray` | 新增 `Multiplayer/ProcessGuard.cs`、`Interop/TrayIcon.cs` | 依赖 T05；WPF 可用 `System.Windows.Forms.NotifyIcon` 或 P/Invoke `Shell_NotifyIcon` |
| T12 | 网络工具箱：PING、TCP/UDP 测速、NAT 类型（STUN）、一键防火墙开关、网卡跃点、WinIPBroadcast | `toolkit.py:220`、`242`、`259/298`、`322/358`、`441`、`468/482/491`、`535` | 新增 `Multiplayer/NetworkToolkit.cs`、`Pages/PageToolbox.xaml(.cs)` | 防火墙/服务操作需要提权（与 T10 同一套提权通道） |
| T13 | 联机配置持久化 + 本地自定义昵称落到底层 | `config.py:46` `Config`、`18-43` `DEFAULTS`；昵称 `HMOL联机模块.py:300-302` | 新增 `Multiplayer/MultiplayerSettings.cs`（或扩展 `Core/App/Settings.cs`） | 昵称设置页已存在（`Settings.Nickname`），需要与联机模块对齐 |
| T15 | 壁纸：背景管理器（图片/视频/GIF、填充模式、模糊、暗化）+ mpkg 导入 | `HMOL_qt.py:2221` `BackgroundManager`、`12030` `_settings_import_mpkg`、`11968` `_settings_browse_bg_file`、`12124`/`12132` 应用/重置 | 新增 `HMOL.App/Appearance/BackgroundService.cs`、`Controls/BackgroundLayer.cs`、`Pages/PageAppearance.xaml(.cs)`；`MediaElement` 播放视频 | **RePKG.exe 替代方案需先拍板**（见 6.1）：旧版靠内嵌 40161 行的 Base64 RePKG（`hmol_repkg.py`）解 `.pkg`，新版需要「内嵌资源 + 落地执行」「外部工具由用户提供」「自己实现 pkg 解析」三选一 |
| T16 | BGM：播放列表、随机、音量、上下曲、状态标签 | `HMOL_qt.py:2329` `BgmManager`、`12143-12188` | 新增 `HMOL.App/Appearance/BgmPlayer.cs`（`MediaPlayer`）、接入 `Pages/PageAppearance` | 依赖 T15 的外观页骨架；配置键需新增到 `Core/App/Settings.cs` |
| T17 | 主页背景：选择/重置/复制到本地目录/加载恢复、图片与视频绘制 | `HMOL_qt.py:11937`、`18332`、`18341`、`18391`、`18407`、`6467` | 复用 T15 的 `BackgroundService`＋`BackgroundLayer`；`Settings.HomeBackgroundPath` | 依赖 T15 |

> 原 P1 的 T14（在线更新器：清单获取 → 版本比较 → 下载 → 替换 exe 并重启）已按《自动更新设计.md》落地：**不走清单 / CDN 镜像，改为直查 Releases**，实现见主表 `M088`–`M094`。为便于对照旧待办编号，此处删除对应行而不重新编号。

### P2

| 编号 | 要做什么 | 旧实现位置 | 预估涉及的新文件 | 依赖关系 |
| --- | --- | --- | --- | --- |
| T18 | 自定义布局编辑器：元素描述符收集、拖拽/对齐、方案保存与切换、侧栏状态应用 | `HMOL_qt.py:11712`、`11811`、`11827`、`11902`、`11914`、`10103`；模块 `HMOL_custom_layout.py`（2320 行） | 新增 `HMOL.App/Layout/LayoutManager.cs`、`Layout/ElementDescriptor.cs`、`Controls/LayoutEditorLayer.cs`、`Windows/SchemeManagerWindow.xaml` | 工作量最大的 UI 项；建议放在所有页面稳定之后 |
| T19 | 教程页（分节导航 + Markdown 渲染） | `HMOL_qt.py:15469`、`15534-15832` | 新增 `Pages/PageTutorial.xaml(.cs)` | 需要选 Markdown 渲染方案（如 Markdig），先在 `HMOL.Core.csproj` 加包 |
| T20 | 反馈入口 | `HMOL_qt.py:5522` `FeedbackDialog`、`15844` `_build_feedback_subpage` | 新增 `Windows/FeedbackWindow.xaml(.cs)` | 需先确认反馈收集方式（旧版走 QQ / GitHub，是否仍保留「表单」待拍板，见 6.5） |
| T21 | 设置页补全（旧「通用」/「关于」tab 的剩余项：日志目录、清理、数据目录、卸载等） | `HMOL_qt.py:5013` `_build_general_tab`、`5031` `_build_about_tab`、`5053` `_save` | `Pages/PageSettings.xaml(.cs)` 扩展 | 其中「检查更新」已在 `CardUpdate` 落地（M093/M094），不再依赖 T14（T14 已完成）；其余项仍待补 |
| T24 | 节点列表与分享（公共节点、分享文本、二维码） | `nodes.py:10/20`、`share.py:19/42/93/117` | `Multiplayer/NodeCatalog.cs`、`Multiplayer/ShareCode.cs` | 依赖 T05（节点是引擎参数）；二维码需引入生成库 |
| T25 | 收藏队友 | `friends.py:14/25/32/55/69` | `Multiplayer/FriendStore.cs` | 依赖 T07/T08（队友身份来自大厅/房间） |
| T26 | 联机模块 EULA 门 + `integrity.json` 自校验 | `HMOL联机模块.py:3877`/`310`；`313` `_verify_integrity` | 并入 T31（EULA）与 T30（完整性） | 与主程序合规项合并处理 |

> 原 P2 的 T22（7z / rar 导出）与 T23（导出格式选择对话框）已落地：7z 导出走随包的 `runtime\7zip\7za.exe`（`Core/IO/SevenZipTool.cs`），格式选择并入系统 `SaveFileDialog` 的过滤器，rar 写入按范围取消，见主表 `M015`/`M016`。此处删除对应行而不重新编号。

### P3

| 编号 | 要做什么 | 旧实现位置 | 预估涉及的新文件 | 依赖关系 |
| --- | --- | --- | --- | --- |
| T27 | 反调试：调试器检测（`IsDebuggerPresent` / `CheckRemoteDebuggerPresent` / NtQueryInformationProcess / 父进程名）＋沙箱特征检测 | `anti_debug.py:51` `is_debugger_present`、`147` `check_suspicious_environment`、`236` `verify_runtime_integrity`；调用点 `HMOL_qt.py:18864-18872` | 新增 `HMOL.App/Security/AntiDebug.cs`（P/Invoke `kernel32`/`ntdll`）、`Security/SandboxCheck.cs`；在 `App.xaml.cs` 启动流程调用 | 需要在「严格/非严格」之间先拍板（见 6.4）；P/Invoke 清单可与 `Interop/WindowInterop.cs` 合并 |
| T28 | 文件完整性校验落地（HMAC-SHA256 + `.sig`） | `anti_debug.py:203` `check_tampering`、`39` `_file_hmac` | `Security/IntegrityCheck.cs` | 旧版因仓库无 `.sig` 文件而实际不生效（见 7.1），新版需先确定「签什么、密钥放哪」 |
| T29 | 日志敏感信息过滤 | `HMOL_qt.py:726` `_sanitize_log_message` | `Core/Logging/Log.cs` 内加过滤器 | 无 |
| T30 | 窗口透明度设置项 | `HMOL_qt.py:9619`/`9666` | `Settings.WindowOpacity`＋`MainWindow.xaml` 绑定 | WPF 整窗透明度需 `AllowsTransparency` 或分层窗口，注意与 DWM 玻璃的兼容（见 6.3） |
| T31 | EULA 首次启动确认（含拒绝时清理数据） | `HMOL_qt.py:18660`、`18754`、`18795` | `Windows/EulaWindow.xaml(.cs)`＋`Settings.EulaAccepted` | 是否仍然需要，建议由用户拍板（见 6.5） |
| T32 | 旧配置迁移（`HMOL_config.json` → `Data/Settings.json`） | `HMOL_qt.py:9593`/`9639` 的键集合 | `Core/App/SettingsStore.cs` 增加一次性迁移 | 目前只有「包目录 `mod`→`ini`」（`PackageType.cs:137`）与「实例里的 7 类包并成 4 类」（`GameInstance.cs:118`）做了迁移 |

---

## 5. 关键差异与风险

### 5.1 实例目录布局（不可逆）

| 项 | 旧版 | 新版 |
| --- | --- | --- |
| 数据根 | 程序目录（`get_program_base_path`） | `<exe 目录>\Data`，可用环境变量 `HMOL_DATA` 重定向（`Core/App/Paths.cs:36-62`） |
| 实例配置 | `instances/<名称消毒后>/config.json`（`HMOL_qt.py:2722-2730`、`2749-2757`） | `Data/instances/<id>.json`（`Paths.cs:64`、`InstanceStore.cs:46-59`） |
| 实例数据 | `instances/<名称>/install_records`、`instances/<名称>/backup/Mental_Omega`（`HMOL_qt.py:2726-2734`） | `Data/instances/<id>/install_records`（`GameInstance.cs:49-53`） |
| 实例 Id | `instance_%Y%m%d_%H%M%S_{hash(name)}`（`HMOL_qt.py:2672`） | `instance_<时间戳>_<8 位随机>`（`GameInstance.cs:157-162`） |
| 改名影响 | 改名要搬目录（目录名即实例名） | 改名只改一个字段，目录不动 |

风险：新版**不读**旧版 `instances/` 目录（`InstanceStore.Load` 只扫 `Data/instances/*.json`）。若要让老用户无痛升级，需要写一次性迁移（扫描旧 `instances/*/config.json` → 生成 `<id>.json`）。当前范围说「数据格式可重新设计，不需要迁移旧数据」，但**这只是数据格式不用迁移，不等于目录不用迁移**——建议明确记录：老用户需要重新添加实例。

### 5.2 更新方式：旧版清单 v1 vs 新版「直查 Releases」

旧清单 `HMOL_QT/update/HMOL.update.json` 实测字段：

| 字段 | 说明 | 读取位置 |
| --- | --- | --- |
| `version` | 远端版本号 | `HMOL_updater.py:176` |
| `release_date` | 发布日期 | `HMOL_updater.py:181` |
| `min_version` | 清单里有，代码未读取（`UpdateInfo.from_json` 无对应字段） | `HMOL.update.json:4` |
| `variant` | 清单里有，代码未读取 | `HMOL.update.json:6` |
| `mandatory` | 是否强制更新（与 `is_major` 取或） | `HMOL_updater.py:178,182` |
| `exe.url` / `exe.size` / `exe.sha256` | 单文件 exe 的下载地址、大小、校验值 | `HMOL_updater.py:175,183-185` |
| `changelog_zh` / `changelog_en` | 更新说明 | `HMOL_updater.py:186-187` |
| `cleanup` | `reset_config` / `clear_logs` / `clear_cache` / `clear_backups` 四个布尔 | `HMOL_updater.py:188`，消费点 `957-982` |

**新版不设清单文件。** 实现见 `src/HMOL.Core/Updater/LauncherUpdater.cs`（`CheckAsync` / `PickAsset` / `ValidateStaged` / `BuildScriptText`）与 `src/HMOL.App/Services/LauncherUpdateFlow.cs`，两版差异如下：

| 项 | 旧版 v1 | 新版 |
| --- | --- | --- |
| 版本来源 | 清单 `HMOL.update.json`，多 CDN 镜像轮询（`MANIFEST_PATHS` / `GITHUB_MIRRORS`） | GitHub：`releases/latest` 的 302 `Location` 里抠 `/tag/<tag>`；Gitee：API `releases/latest` 取 `tag_name` |
| 下载地址 | 清单里的 `exe.url` | GitHub：`releases/expanded_assets/{tag}` 页面正则抠 `href`；Gitee：`releases/{id}/attach_files` 取 `browser_download_url` |
| 资产约定 | 无（清单直接给地址） | 文件名必须以 `HMOL` 开头，优先 `.exe`、退化 `.zip`（`PickAsset`） |
| 校验 | `exe.size` + `exe.sha256` | **≥5MB + 文件头 `MZ`**（`ValidateStaged`）；不做 SHA256（发布页不提供摘要） |
| 更新方式 | 换 exe，且 `cleanup` 可删 `backup/`、`logs/`、`cache/` 并重置配置 | 只换 exe；无 `cleanup` 概念，**绝不碰 `Data` 下任何数据** |
| 强制更新 | `mandatory` / `is_major` 决定是否强制 | 取消，新版本一律可「稍后」 |
| Wine 变体 | `HMOL.wine.update.json` + `detect_variant` | 不做（仅 Windows） |
| 落盘文件 | 清单 + 更新包 | `HMOL.exe.new` / `.new.zip` / `.old`(…`.old4`) / `.update-failed.txt`（启动时 `CleanupStaleFiles` 清理） |
| 失败可观测性 | 无（助手 bat 失败即静默） | 脚本必须留 `locked` / `timeout` / `broken` ASCII 标记，下次启动翻译成中文提示并跳过本次自动检查 |
| 已删除的旧接口 | — | `App.xaml.cs` 的 `--apply-update <zip> --target <dir>`（解 zip 覆盖目录）与「维护模式」分支已删除，与「只换 exe」语义冲突 |

- 旧版 B10 的隐患（`cleanup` 默认全 True，缺字段就删 `backup/`、`logs/`、`cache/` 并重置配置）在新实现里**从根上规避**：新版脚本只有「换文件 / 回滚 / 删备份」三件事，没有任何清理开关。
- 仍需仓库侧配合：两个源目前都还没有 Release（`AppInfo.Version` 为 `1.0.0`），要真正走通「下载 → 校验 → 替换 → 重启」，需先发一个 `v<版本>` 的 Release——GitHub 传 zip 与裸 exe，Gitee 只传 zip（单附件上限 100MB）。

### 5.3 包类型 7 类 → 4 类（不可逆）

- 旧：`ini` / `map` / `mission` / `voice` / `plugin` / `beautification` / `music`（`HMOL_qt.py:9726-9739`），实例里用 7 个键分别记录已安装包（`HMOL_qt.py:2673-2676`）。
- 新：`ini` / `map` / `mission` / `plugin`（`Core/Packages/PackageType.cs:52-58`），`voice`/`beautification`/`music` 并入 `plugin`。
- 已做兼容：`GameInstance.MigrateLegacyPackages`（`GameInstance.cs:118-144`）会把旧 7 类键并进 4 类，`InstanceArchive.Import` 也会调用它（`InstanceArchive.cs:239`）。
- **不可逆点**：合并后无法再区分「这个包原来是语音还是美化」，旧实例（或老用户手工放的文件）一旦走过一次迁移，就回不到 7 类视图。若将来想重新分开，只能靠包管理器目录名（`packages/voice` 之类）反推，而新版 `EnsureDirectories` 只建 4 个目录。
- 补充：旧版 INI 包目录名是 `mod`，新版为 `ini`；`PackageTypes.MigrateLegacyIniDirectory`（`PackageType.cs:137-153`）与旧版 `_get_package_dirs`（`HMOL_qt.py:9744-9751`）行为一致。

### 5.4 提权模型：整程序提权 → 按需提权

| 项 | 旧版 | 新版 |
| --- | --- | --- |
| 主程序 | Python 打包的 `HMOL_qt.py` 未声明提权（未找到 `uac_admin` 或 manifest 相关声明） | `src/HMOL.App/app.manifest:21` `asInvoker`（双击不弹 UAC） |
| 联机模块 | `HMOL联机模块.spec:78` `uac_admin=True` → **整个插件包始终以管理员运行** | 计划：主程序 `runas` 以 `--elevated <task>` 重启自身，只让提权实例做需要管理员的事（`App.xaml.cs:87`） |
| 现状 | — | `App.xaml.cs:29-36`：识别到 `--elevated` 后**只写日志就 `Shutdown()`**，任务未实现 |

风险：旧联机模块里凡是「默认有管理员权限」才成立的功能（关防火墙 `toolkit.py:482`、TAP 安装 `513`、WinIPBroadcast `535`、ZeroTier MSI 安装 `111`）在按需提权模式下都要能正确触发提权、并把结果回传给主实例。ZeroTier MSI 安装已随引擎删除，剩下四项都需要走 T10 的提权通道。

### 5.5 其他行为差异

| 主题 | 旧版 | 新版 | 备注 |
| --- | --- | --- | --- |
| 日志文件 | `logs/HMOL_2026-08-10.log`（按天，`HMOL_qt.py:677`） | `Data/Log/HMOL-2026-9-23-192828382.log`（按启动 + 8MB 切分，`Log.cs:134`） | `PageLog.DateOfFile`（`PageLog.xaml.cs:316-320`）按 `-` 切分取日期，与新命名匹配 |
| 内存日志容量 | 5000 条（`HMOL_qt.py:668`） | 500 条（`ActivityLog.cs:33`） | 切走页面再回来只保留最近 500 行 |
| 日志模块名 | 手工传 `module` 字符串（`log_info("Updater", ...)`） | `CallerFilePath` 自动取文件名（`Log.cs:221-226`） | 名字会变成 `GameLauncher`/`PackageInstaller` 这类类名 |
| 覆盖文件 | 直接覆盖 | 覆盖前留 `<文件>.bak-<时间戳>`（`OperationJournal.cs:51-74`） | 会在游戏目录里留下 `.bak-*` 文件，卸载时不会自动清理这些备份 |
| 卸载时目录替换 | 直接删目标目录再铺新内容 | 整体搬进隔离区，失败可搬回（`PackageInstaller.cs:141`） | 跨卷时会退化为复制+删除（`DirectoryCopier.cs:172-192`） |
| 解压支持 | zip/7z/rar，rar 依赖系统 `unrar`/WinRAR | zip/7z/rar/tar/gz，按文件头嗅探（`ArchiveExtractor.cs:244-272`） | 把 7z 改名成 .zip 也能解（旧版靠扩展名） |
| 实例体积统计 | 目录 mtime + 缓存（`HMOL_qt.py:3334`） | 同策略（`InstanceManager.cs:172-194`） | 行为一致 |
| 游戏进程日志 | 不捕获（`HMOL_qt.py:18326`） | 捕获 stdout/stderr，按关键字着色（`GameProcessSession.cs:164-179`） | `GameLauncher.cs:35` 有 TODO：未指定输出编码，中文 Windows 下可能出现乱码 |
| 更新方式 | 清单文件 + 多 CDN 镜像（`HMOL_updater.py`） | 直查 Releases（`LauncherUpdater.CheckAsync`，GitHub 网页 + Gitee API 双源） | 见 5.2；新版无 SHA256 校验、无强制更新 |

---

## 6. 需要用户拍板的事项

### 6.1 壁纸的 RePKG.exe 怎么办（P1-1 / T15）

旧版把 RePKG.exe 以 Base64 内嵌进 `hmol_repkg.py`（40161 行），运行时释放到 `tool/wallpaper/RePKG.exe` 再调用（`HMOL_qt.py:11983-12013`）。C# 侧三个方向：

1. **照搬内嵌**：把 RePKG.exe 作为嵌入资源，运行时释放到 `Data/Cache` 或 exe 同目录再执行。改动最小，但等于把一个第三方闭源二进制继续打包进发行物（体积 + 杀软误报 + 授权问题）。
2. **要求用户自备**：只认 `tool/wallpaper/RePKG.exe`，找不到就提示用户下载。彻底去掉内嵌，但要重新设计 UX。
3. **自己实现 pkg 解析**：Wallpaper Engine 的 `.pkg` 格式有公开社区实现，但工作量大、且随版本变化。

需要确认：**发行包里是否允许继续携带第三方 RePKG.exe**。

### 6.2 更新方式（已定，P1 / T14）

原「新版 v2 清单字段」问题已由《自动更新设计.md》定稿解决：**不再有清单文件**，改为直查 Releases；只换 exe，没有 `cleanup` 清理开关，也不做 Wine 变体。实现与逐项差异见 5.2、主表 M088–M096。

唯一待办落在仓库侧而非代码侧：两个源目前都没有 Release，需要先发一个 `v<版本>` 的 Release（GitHub 传 zip + 裸 exe；Gitee 只传 zip，单附件上限 100MB），才能端到端验证「下载 → 校验 → 替换 → 重启」。另外 `HMOL.exe.update-failed.txt` 的标记文件契约、`broken` 回滚分支的不可达性见 7 的 B14/B15。

### 6.3 窗口透明度与 DWM 玻璃的取舍（P3 / T30）

旧版用 Qt 的 `window_opacity`（`HMOL_qt.py:9619`）。新版主窗口已经走「DWM 框架延伸 + `Background=null` + 外圈留白自绘投影」（`MainWindow.xaml.cs:46-67`）。WPF 里再叠加整窗透明度需要 `AllowsTransparency=True`（会禁用部分 DWM 特性、影响性能），或者改成分层窗口。需要确认：**这个设置项是否还保留**。

### 6.4 反调试的强度（P3 / T27、T28）

旧版是「非严格模式」：`verify_runtime_integrity(strict=False)`（`HMOL_qt.py:18867`）只打印告警；且完整性校验因为仓库里没有 `.sig` 文件（Glob `**/*.sig` 在 `HMOL_QT` 下无结果）实际不生效（`anti_debug.py:223-225`）。需要确认：新 C# 版是「仅检测并记日志」还是「检测到调试器就拒绝启动」；以及完整性校验签什么文件、密钥放哪里（旧版靠环境变量 `HMOL_INTEGRITY_KEY` + 内置混淆密钥兜底，`anti_debug.py:26-36`）。

### 6.5 EULA 与反馈入口是否保留（P2/P3 / T20、T31）

旧版有强制 EULA 门 + 拒绝时清理数据（`HMOL_qt.py:18882-18899`），联机模块也有自己的 EULA 门（`HMOL联机模块.py:3877`）。新版完全没有 EULA/合规相关实现。同时旧版「反馈」有独立表单（`FeedbackDialog`）。需要确认：新版是否继续做 EULA 门与反馈表单，还是只保留「GitHub Issues + QQ 群」跳转（`AboutWindow` 已有）。

---

## 7. 旧版已知 Bug / 死代码（避免新实现重复）

| # | 问题 | 证据（文件 + 行号） | 影响 / 新实现注意 |
| --- | --- | --- | --- |
| B01 | `build_qss` 在同一模块重复定义，后者覆盖前者 | `HMOL_qt.py:1147` `def build_qss(theme, gradient=None)` 与 `1605` `def build_qss(theme, gradient=None, accent_color=None)` | 1147 行那份（含 1148–1604 共约 450 行 QSS 生成逻辑）是死代码；改样式时若改到 1147 那份会「没反应」。新版 `ThemeService.BuildBrushes` 单点定义，注意别在别处再写第二套配色表 |
| B02 | `MainWindow._on_home_instance_changed` 重复定义 | `HMOL_qt.py:10703` 与 `16122`（同在 `MainWindow` 类内） | 生效的是 16122；10703 是死代码。同类问题在新版 `MainWindow.xaml.cs` 只有一个入口，勿再引入同名重载 |
| B03 | `input_validation` 基本未接入 | 导入 `HMOL_qt.py:634-640`；全文唯一使用点 `5866`（DLC 向导里 `sanitize_filename`）；标志 `INPUT_VALIDATION_AVAILABLE`（638/640）从未被读取 | `safe_path_join`、`validate_package_path`、`check_zip_bomb`、`validate_url`、`is_safe_member_path` 全程未用 → 包路径/zip bomb 防护形同虚设。新版必须真正接入 `PathGuard`（已有，且 `ArchiveExtractor` 逐条目做了 `PathGuard.TryResolve` 校验） |
| B04 | `rate_limiter` 是纯死代码 | 导入 `HMOL_qt.py:643-648`；`get_login_limiter` / `get_api_limiter` 全文无调用；标志 `RATE_LIMITER_AVAILABLE`（646/648）从未被读取；模块自身 195 行 | 登录失败锁定、API 限流、QQ 喊话限频都没生效。新版不再有登录，若要保留「QQ 喊话限频」需单独设计 |
| B05 | 联机模块开机自启分支失效 | `guard.py:116-120` `auto_start_command()` 写成 `"... --autostart"`；但 `HMOL联机模块.py` 全文搜不到 `--autostart` 解析（唯一命中在 `guard.py` 自己），`main()`（`4151`）也未处理该参数 | 写进 HKCU Run 键后，开机只会正常弹出主窗口，不会「静默启动到托盘」。新版若要做自启，必须真正实现参数分支（`--autostart` → 最小化到托盘） |
| B06 | 主程序导入未使用的托盘类 | `HMOL_qt.py:114` `QSystemTrayIcon`（全文唯一出现处） | 死导入；旧版主程序没有托盘功能（托盘只在联机模块里用 ctypes 实现）。新版若要给主程序加托盘，别以为旧版有现成实现 |
| B07 | 全局配置里的 `installed_packages` 是死字段 | 读取时被丢弃 `HMOL_qt.py:9606`；写入注释明确「installed_packages 是 dead code,不再写入」（`9644`） | 已安装包信息只存在于实例目录的 install_records。新版 `Settings` 里同样不应放实例数据（现在也没有） |
| B08 | 实例 id 用 `hash(name)` 生成 | `HMOL_qt.py:2672` `f"instance_{...}_{hash(name)}"` | Python 的 `hash()` 在同一次运行内稳定，但进程间受 `PYTHONHASHSEED` 影响会变。旧版靠把 id 落盘规避，但如果 `config.json` 丢了 id 字段会重新生成一个不稳定 id。新版用时间戳 + GUID（`GameInstance.cs:157-162`），无此问题 |
| B09 | rar 导出会「降级成 zip」 | `HMOL_qt.py:3125-3130` 注释：优先调 WinRAR/rar CLI，不可用时降级为生成 `.zip` 并提示用户手动改名 | 会产生「扩展名与实际格式不一致」的文件。新版若做 rar 导出，宁可直接报错也别静默改格式 |
| B10 | 更新助手 bat 的清理默认值是 True（危险默认值） | `HMOL_updater.py:957` `clearup.get("clear_logs", True)`、`963` `clear_cache`、`971` `clear_backups`、`977` `reset_config` | 清单缺 `cleanup` 字段时会删掉 `backup/`、`logs/`、`cache/` 并重置配置。**新版不适用**：新实现没有 `cleanup`，脚本只做「换文件 / 回滚 / 删备份」（见 5.2） |
| B11 | 防篡改校验实际不生效 | `anti_debug.py:223-225`：无 `.sig` 文件就 `continue`（跳过校验）；`Glob **/*.sig` 在 `HMOL_QT` 下无任何结果 | 完整性校验只打印「通过」。新版要真做校验，需先解决「签名文件从哪来、怎么随包发布」 |
| B12 | 反调试是「只告警」模式 | `HMOL_qt.py:18866-18867` `verify_runtime_integrity(strict=False)`；`anti_debug.py:271-273` strict 才返回 False | 检测到调试器/沙箱也照常启动。新版若要「检测到就拒绝」，需要明确产品口径（见 6.4） |
| B13 | 新代码注释与旧实现不符（文档准确性） | `src/HMOL.Core/Instances/InstanceArchive.cs:69` 注释写「旧版的 7z / rar 导出未实现」，但旧版 `HMOL_qt.py:3085` / `3125` 是有实现的（7z 依赖 `py7zr`，rar 依赖 WinRAR CLI） | 只是注释不准确，不影响编译；但会误导后来人以为「7z/rar 导出可以不做」。建议改注释或在 T22 里明确取舍 |
| B14 | 更新脚本的退出码不可依赖（新实现实测） | `LauncherUpdater.BuildScriptText` 生成的 `%TEMP%\HMOL-update-{pid}.cmd`；实测 2026-09-25 | 批处理执行 `del /f /q "%~f0"` 自我删除后，其后的 `exit /b N` 不再执行、cmd 退出码变成 1（正常路径与失败路径实测都是 1）。因此**唯一可靠的成功/失败契约是 `HMOL.exe.update-failed.txt` 标记文件**，不能用退出码判定；本次按设计文档保留脚本原文未改 |
| B15 | `broken` 回滚分支实际不可达（新实现实测） | `LauncherUpdater.BuildScriptText` 的 `:rollback` / `:retry` / `:broken` 段；实测 2026-09-25 | 在 `del %BACKUP%` + `if exist %BACKUP% goto retry` 的保护下，要走进 `broken` 需同时锁住 TARGET、STAGED、`%BACKUP%`，而这会先落入 `:locked`。属防御性分支，未能构造用例，保留以防极端场景 |

---

## 附：本次未确认项清单

| 项 | 说明 |
| --- | --- |
| 新版更新方式与更新清单 | 已定：不走清单 / CDN 镜像，改为直查 Releases（见 5.2、6.2）。**仓库尚无 Release，真实「下载 → 校验 → 替换 → 重启」链路未端到端验证** |
| 新版是否计划做 EULA / 反馈表单 / 窗口透明度 | 旧版有、新版无，属于范围外或待拍板，见 6.3 / 6.5 |
| `HMOL_QT/logs/HMOL_2026-08-10.log`、`output/HMOL LTS v3.2.0.exe`、`HMOL联机模块/dist/HMOL联机模块.exe` 等产物是否随重构删除 | 属于仓库清理决策，不在本次既定删除范围内，未做判断 |
| 旧版主程序是否有其他未列出的侧栏页面 | 已覆盖 `_build_*_page` 系列（home `10667` / package `10748` / instance `10877` / settings `11332` / onedrive `12567` / dlc `13733` / account `14440` / about `15078` + 各 subpage）；若有遗漏属未确认 |
| 并行开发带来的时效性 | `src\` 在本文撰写期间持续变化（63 文件/8940 行 → 72 文件/11221 行），第 2 节状态列与第 4 节待办清单在提交时点之后可能已继续推进。本快照对应 **2026-09-23 19:55**，状态列的判定依据是当时的文件内容与一次成功的 `dotnet build`。其中 **§2.17 在线更新（M088–M096）与 §1.2 / §1.3 / §5.2 / §6.2 / 第 7 节 B10/B14/B15 已于 2026-09-25 按《自动更新设计.md》的落地结果单独复核**，其余章节仍是 09-23 快照 |
