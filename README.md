# VRlibrary（PICO XR 智能图书馆）部署与快捷工具指南

基于 **Unity `2022.3.42f1c1`**、**URP**、**XR Interaction Toolkit `2.6.5`** 与 **PICO Unity Integration SDK `2.5.3`** 开发的 VR 沉浸式智能图书馆应用（主场景：`Assets/_Scenes/SmartLibrary.unity`），包含一二层馆藏阅览室、全文检索跨层传送、阅读面板与书签持久化、日夜渐变与灯光控制、星空露台影院与全链路自动化冒烟测试工具链。

---

## 一、环境与部署指南

### 1. 基础环境要求
- **Unity 版本**：`2022.3.42f1c1`（见 `ProjectSettings/ProjectVersion.txt`）
- **Unity 模块**：**Android Build Support**（勾选其自带的 **OpenJDK 11**、**Android SDK & NDK Tools**；`Assets/Editor/Build/VRLibraryAndroidTools.cs` 已针对 Unity 2022.3 内置工具链适配）
- **目标设备**：PICO Neo 3（企业版 / 消费版）或 PICO 4（`minSdk 29`、ARM64、IL2CPP）
- **已内置 SDK**：`Assets/sdk/PICO Unity Integration SDK 2.5.3`（无需外网另行下载）

### 2. 两种获取与启动方式

#### 方式 A：使用谷歌云盘完整工程包（解压即用，含最新 APK 与视频片源）
1. 解压 `VRlibrary_Complete_20261125.zip`（已剔除 `Library/`、`Temp/`、`Obj/`、`Logs/` 等本地缓存）。
2. **直接安装体验真机 APK**：
   - 包内已含最新构建成品 `Builds/VRlibrary.apk`。
   - PICO 头显开启 **开发者模式 → USB 调试** 并连上电脑后，直接运行：
     ```powershell
     adb install -r Builds/VRlibrary.apk
     ```
     或在打开 Unity 工程后执行菜单 **`打包PICO真机/③ 安装APK并启动到真机`**。
3. **打开 Unity 工程开发/修改**：
   - 通过 Unity Hub（`2022.3.42f1c1`）添加解压目录 `VRlibrary/`，首次打开会自动重建 `Library/`（约 1~3 分钟）。
   - 打开主场景 `Assets/_Scenes/SmartLibrary.unity` 即可运行或二次构建。

#### 方式 B：通过 GitHub 仓库克隆部署
1. 克隆仓库：
   ```bash
   git clone https://github.com/dingyinpc/VRLibrary.git
   ```
2. **补全大体积影片资源（重要）**：
   - 因 GitHub 单文件 `100 MB` 限制及 `.gitignore` 约定，仓库**不包含** `Builds/*.apk` 及 `Assets/StreamingAssets/*.mp4`（含 `123.3 MB` 的 `bisai_zuopin.mp4` 等 4 部露台影院影片）。
   - 如需在编辑器或打包 APK 中完整体验**星空露台影院**，请从谷歌云盘完整工程包内拷贝以下 4 个视频文件放入 `Assets/StreamingAssets/`：
     - `bisai_zuopin.mp4`
     - `xinghe_fangxian2.mp4`
     - `xunzhao_mishi_de_wo.mp4`
     - `xunzhao_mishi_de_wo_trailer.mp4`
   - 拷入后可在 Unity 中执行 **`VRLibrary/体验·⑭ 复核(只报告)`** 确认片单 `Assets/Resources/filmography.json` 与片源 `4/4` 匹配在位。

---

## 二、「打包PICO真机」快捷菜单与真机部署流程

本工程在 Unity 编辑器顶部独立注册了 **`打包PICO真机`** 菜单（源码位于 `Assets/Editor/Build/`），把 PICO SDK 清单注入、JDK/SDK 路径修复、Neo 3 兼容档切换、一键打包与真机安装全部做成了快捷操作：

| 菜单路径 | 源码文件 | 作用说明 |
| --- | --- | --- |
| `打包PICO真机/⓪ 修复PICO设置注册(清单注入)` | `VRLibraryPicoSettingsFix.cs` | 校验并自动注册 `PXR_Settings.asset` 到 `EditorBuildSettings`，含损坏 `MonoScript` 缓存自愈；防止打出的 APK 缺失 `pvr.app.type=vr` 等声明而被真机当成 2D 面板应用。 |
| `打包PICO真机/① 配置Android打包设置` | `VRLibraryBuildTools.cs` | 切换 Android 平台、设置包名 `com.vrlibrary.xr`、配置 IL2CPP + ARM64 + `minSdk 29`，并按 `vrlib.glesOnly` 配置图形 API（`OpenGLES3` 或 `Vulkan + OpenGLES3`）。 |
| `打包PICO真机/② 一键打包APK` | `VRLibraryBuildTools.cs` | 依次自动执行 ①、SDK 预检、`⓪ 修复PICO设置注册`，随后构建输出到 `Builds/VRlibrary.apk`，并在 `Logs/build-report.txt` 输出构建报告。 |
| `打包PICO真机/③ 安装APK并启动到真机` | `VRLibraryBuildTools.cs` | 自动查找 Unity 内置 `adb.exe`，检测设备连接并执行 `adb install -r Builds/VRlibrary.apk`，安装后自动拉起应用主入口。 |
| `打包PICO真机/PICO3 兼容档·应用(关ASW/MRC/晚锁存/锐化)` | `VRLibraryPico3Compat.cs` | 针对 PICO Neo 3 切为 `OpenGLES3` 单栈并关闭 ASW / MRC / 晚锁存 / 锐化，避免启动黑屏卡死在渲染设备初始化。 |
| `打包PICO真机/PICO3 兼容档·回退(恢复PICO4特性)` | `VRLibraryPico3Compat.cs` | 切回 `Vulkan + OpenGLES3` 并恢复 PICO 4 增强特性。 |
| `打包PICO真机/修复 Android 工具链路径(JDK/SDK/NDK)` | `VRLibraryAndroidTools.cs` | 将 JDK / SDK / NDK 路径显式指回当前 Unity 2022.3 自带目录（避免误指向其它 Unity 版本的 JDK 17），报告输出至 `Logs/android-tools.txt`。 |
| `打包PICO真机/修复·禁用高版本 cmdline-tools(需JDK17)` | `VRLibraryBuildTools.cs` | 将 Android SDK 中 `Pkg.Revision >= 12` 的 `cmdline-tools` 移至 `_disabled-cmdline-tools` 并清空 Unity 缓存，解决 `latest installed SDK on the system is 0` 报错。 |
| `打包PICO真机/诊断·Android SDK 实际状态` | `VRLibraryAndroidTools.cs` | 检查 JDK/SDK/NDK 实际路径、已安装 Android API 平台与 `sdkmanager` 状态，输出至 `Logs/android-diagnose.txt`。 |

> **真机重打包建议**：若切换过 SDK 或修改过 `Assets/Plugins/Android/`，可先删除 `Library/Bee/Android/Prj` 目录，再点击 **`打包PICO真机/② 一键打包APK`** 确保 Gradle 工程彻底重建。

---

## 三、「一键导入文档」与内容批处理工具

工程内置了 3 套面向藏书文档与影院片单的批量导入/接线快捷入口：

### 1. 一键导入 Markdown 文档为馆藏书（`Assets/Editor/SolutionDocImporter.cs`）
- **菜单入口**：
  - **`VRLibrary/导入方案文档为馆藏书`**：读取作者默认方案目录（`E:\Unity xianmu\VR\XR学习知识库\VRlibrary实现方案`）下的全部 `.md` 文件批量上架；
  - **`VRLibrary/导入文件夹书籍为馆藏书(自选文件夹)`**：**跨电脑推荐使用**——弹窗自选任意包含 `.md` 文档的文件夹一键导入并保存场景。
- **批处理机制**：
  - **幂等更新**：按书名匹配，场景里已有同名书只更新正文与书屏标题；新书自动寻找书架中层视平线占位槽（优先 `Bookshelves` / `Shelves2F` 中 `level 0/1` 占位书）。
  - **自动清洗与分页**：自动清理 Markdown 标记并按 **每 400 字插入 `\f`** 自动分页，同步写入书体 `TitleText`（每 6 字自动换行）并调用 `VRLibraryFix.FitOne` 自适应字号，最后自动保存场景。
- **命令行无界面批处理**：
  ```powershell
  "<Unity安装目录>\Editor\Unity.exe" -batchmode -projectPath "<工程路径>" -executeMethod SolutionDocImporter.ImportDefault -quit
  ```

### 2. 一键导入 JSON 馆藏书单（`Assets/Editor/BookDataImporter.cs`）
- **数据文件位置**：`Assets/Data/bookdata.json`
- **格式示例**（支持顺序写入或通过 `"slot"` 精确指定 `Book_架_层_序` 节点）：
  ```json
  [
    { "title": "示例图书", "author": "作者名", "summary": "正文内容..." },
    { "slot": "Book_0_0_1", "title": "指定书位示例", "author": "作者", "summary": "指定对应书架格子。" }
  ]
  ```
- **菜单入口**：**`VRLibrary/导入馆藏数据(JSON)`**（支持 `-executeMethod BookDataImporter.ImportFromMenu`）。

### 3. 露台影院片单接入与复核（`Assets/Editor/VRLibraryFilmImport.cs`）
- **配置与目录**：片单位于 `Assets/Resources/filmography.json`，视频文件位于 `Assets/StreamingAssets/*.mp4`。
- **菜单入口**：
  - **`VRLibrary/体验·⑭ 放映片单接线(改场景)`**：绑定首部片源并统一 `videoVolume = 0.50`，同时检查全部片源完整性并保存场景。
  - **`VRLibrary/体验·⑭ 复核(只报告)`**：只读检查片单与视频文件是否一一对应（报告输出至 `Logs/film-import.txt`）。

---

## 四、体检、性能整改、冒烟测试与 CLI 自动化工具

### 1. 一键验收与全面体检菜单（`VRLibrary/`）
- **`VRLibrary/一键落地全部体检整改(改场景+资产+烘焙)`**（`VRLibraryOptimizeApply.cs`）：幂等执行坏资产清理、重新挂载 SDK 2.5.3 `PXR_Manager`、禁用冲突 `GraphicRaycaster`、禁用冗余 XR Rig 子节点、迁移程序化贴图为 ASTC 6×6 PNG、校验 `ReflectionProbe-0.exr` 与光照贴图完整性，并输出 `Logs/optimize-apply.txt` 与全套审计报告。
- **`VRLibrary/一键验证贴图账与清单`**（`VRLibraryOptimizeApply.cs`）：校对 Android 贴图账（`Logs/perf-tex.txt`）并执行 PICO Manifest 预注入诊断（`Logs/pico-manifest-probe.txt`）。
- **`VRLibrary/冒烟测试(进Play自动跑)`**（`VRLibrarySmokeMenu.cs`）：自动进入 Play 模式跑完阅读面板/翻页/检索跨层传送/灯光开关/日夜切换/露台影院播控等全链路 38 项自动化测试，输出 `Logs/runtime-smoke.txt` 与实机视角截图 `Captures/reader-layout.png`。
- **`VRLibrary/露台示例片预览(进Play,播完自动退出)`**：进入 Play 自动播放露台视频便于录制/截图验收。
- **只读报告类菜单**：
  - `VRLibrary/审计全场景(写报告)` → `Logs/scene-audit.txt`
  - `VRLibrary/全场合理化体检(只报告)` → `Logs/sanity-audit.txt`
  - `VRLibrary/性能·静态体检(只报告)` → `Logs/perf-static.txt`
  - `VRLibrary/性能·贴图账(只报告)` → `Logs/perf-tex.txt`
  - `VRLibrary/诊断·PICO清单注入` → `Logs/pico-manifest-probe.txt`

### 2. 场景搭建与专项优化快捷菜单速览（`VRLibrary/`）
- **场景与空间改造**：`一键搭建智能图书馆场景`、`搭建星空露台(会改场景)`、`装小地图与菜单(会改场景并自动保存)`、`打通二楼回廊(改场景)`、`二楼玻璃廊道·改造(改场景)`、`在二层回廊布设传送锚点`、`楼梯·重建双跑楼梯`、`拔高·执行`、`Carpet 换上地毯`、`TechFloor 铺到 2m 地砖`。
- **渲染与性能优化**：`性能·① 静态合批补标`、`性能·③ 应用像素档位`、`性能·④ 开固定视场渲染 FFR`、`性能·⑥ 光照烘焙全流程`、`性能·⑦ 应用真机三件套(Multiview + 72Hz + 晚锁存)`、`性能·⑨ 书本改成点击阅读`、`性能·⑬ GPU瘦身`、`程序化贴图/迁移.asset为ASTC压缩PNG`、`灯色/替换为 白青+极光蓝+芍药粉`。
- **交互体验调优**：`体验·⑩ 转头改成按住持续转` / `防眩晕环强度减半`、`体验·⑫ 阅读面板布局`、`体验·⑬ 开始菜单加「退出图书馆」按钮`、`观影·锁定站位与目录朝前`。

### 3. `Tools/` 命令行自动化与桥接脚本
当 Unity 编辑器打开时，`UnityBridgeServer.cs` 会启动本地 WebSocket 桥（默认端口 `6401`，可通过 `Unity Copilot/Restart Bridge Server` 重启），可用 Node.js 直接驱动编辑器：
- `node Tools/bridge-scan.mjs`：扫描本机所有活着的 Unity Bridge 端口与工程归属。
- `node Tools/refresh.mjs`：触发 `AssetDatabase.Refresh` 重新编译脚本。
- `node Tools/compile-check.mjs`：结合 `Library/Bee/tundra.log.json` 与 `Library/ScriptAssemblies/*.dll` 验证真实编译结果。
- `node Tools/unity-bridge.mjs <action> [jsonParams]`：直接调用 `ping`、`getSceneHierarchy`、`runMenuItem`、`saveScene`、`captureScreenshot` 等动作（示例：`node Tools/unity-bridge.mjs runMenuItem '{"menuPath":"打包PICO真机/② 一键打包APK"}'`）。
- `node Tools/perf-menu.mjs <short>`：用预设短名跑体检/性能/冒烟菜单（如 `static`、`tex`、`smoke`、`stereo-apply` 等；注意其中 `apk`/`apk-install` 短名若触发请优先直接用顶层菜单名 `打包PICO真机/② 一键打包APK`）。
- `node Tools/shot.mjs [文件名]`：截取当前编辑器 Scene 视图保存到 `Captures/`。
- `Tools/apk-strip-pxr.ps1`：旧版 SDK 3.3.0 应急剔除 `libPxrPlatform.so` 并重签名的 PowerShell 脚本（SDK 2.5.3 正常打包无需使用）。

---

## 五、馆内基本交互说明（进馆参考）
- **导览与移动**：入口与中央导览台提供一键传送（一层大厅 / 二楼环廊 / 星空影院 / 图书检索 / 灯光开关 / 日夜切换 / 退出图书馆）；支持左手摇杆平移与瞬移锚点传送。
- **看书与阅读面板**：射线瞄准任意书本并扣下扳机即可打开阅读面板；面板支持实时跟随头部视角、上一页/下一页、跨会话 JSON 书签保存/跳转，以及在面板范围外扣扳机「指外即关」。
- **星空露台影院**：从二楼环廊或导览台进入露台观影席；首次入座弹出观影须知，确认后弹出片单目录（支持选片、暂停/继续、快进/快退 5 秒、倍速切换及回大厅/回二楼传送）。
