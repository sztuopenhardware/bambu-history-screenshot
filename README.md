# Bambu History Screenshot（拓竹打印历史自动截图工具）

一款轻量、零依赖的 Windows 桌面工具，用于对 **拓竹 Bambu Studio** 的「打印历史」卡片进行高清批量截图，并自动按照 **`日期-名称.png`** 格式命名保存（例如 `2026-11-19-球箱垫块_plate_8.png`）。

---

## 核心特性

- **精准提取，零 OCR 错字**：基于 Windows UI Automation 直接读取 Bambu Studio 打印历史页面（WebView2）中的原始模型名称、打印日期、盘号、耗时、设备和状态，并按卡片坐标精确裁剪，完全避免中英混排 OCR 乱码。
- **自动按 `日期-名称` 命名**：默认命名模板 `{date}-{name}`，自动清洗 Windows 非法文件名字符，自动区分打印历史与「最近打开的 `.3mf` 文件」。
- **单页勾选 & 自动滚动批量截图**：
  - **扫描当前页 → 截取勾选卡片**：先预览识别结果与目标文件名，按需勾选后一键截图。
  - **自动滚动批量截图**：自动隐藏本工具并前置 Bambu Studio，逐屏去重截图并自动向下/向右滚动（按 **ESC** 或点击悬浮条可随时停止）。
- **多维度筛选**：支持按打印状态（打印完成 / 正在打印 / 已取消）、日期范围、关键词筛选；支持自定义外边距（默认 `10 px`）及重名策略（自动加序号 / 跳过已存在 / 覆盖）。
- **云端耗材同步（国区 / 国际区）**：内置拓竹云端登录（下拉选择 **中国大陆 `api.bambulab.cn`（默认）** 或 **国际区 `api.bambulab.com`**，支持 **手机短信验证码登录**、账号密码登录（含邮箱/短信二次校验与 2FA）及 Token 备用登录），自动拉取每次打印的 **耗材种类（如 `PLA`、`PLA / PETG`）** 与 **耗材重量（g）**，并智能匹配本地卡片与已有截图。
- **导出 Excel（`.xlsx`）**：一键导出标准 Excel 工作簿（兼容 Microsoft Excel / WPS / LibreOffice），6 列依次为 **日期 | 名字 | 打印状态 | 耗材种类 | 耗材重量(g) | 截图**，内嵌高清 PNG 截图、自动计算耗材重量合计（`=SUM(...)`）并冻结首行表头。
- **单文件便携运行**：纯 C#（.NET Framework 4.x）编写，Windows 10/11 双击 `BambuHistoryScreenshot.exe` 即可运行，无需安装 Python 或额外运行库。

---

## 快速开始

### 方式一：直接下载运行（推荐）
1. 前往 [Releases 页面](https://github.com/sztuopenhardware/bambu-history-screenshot/releases) 下载最新的 `BambuHistoryScreenshot.exe`。
2. 打开 **Bambu Studio** 并进入「主页（打印历史）」或左侧「打印历史」页面，确保卡片已加载完成。
3. 双击运行 `BambuHistoryScreenshot.exe`：
   - 点击 **扫描当前页** → 勾选目标卡片 → 点击 **截取勾选卡片**；
   - 或直接点击 **自动滚动批量截图**，按 **ESC** 可随时中止。
4. 点击 **打开保存目录** 查看生成的 PNG 图片。

### 方式二：从源码一键编译
无需安装 Visual Studio 或 .NET SDK，使用 Windows 自带的 `.NET Framework 4.x` 编译器即可一键编译：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

编译完成后会在项目根目录生成 `BambuHistoryScreenshot.exe`。

---

## 支持的命名占位符

| 占位符 | 说明 | 示例 |
| --- | --- | --- |
| `{date}` | 打印日期（YYYY-MM-DD） | `2026-11-19` |
| `{date_compact}` | 紧凑日期（YYYYMMDD） | `20261119` |
| `{time}` | 打印时间（HHmm） | `1951` |
| `{datetime}` | 日期与时间 | `2026-11-19_1951` |
| `{name}` | 模型名称 | `球箱垫块_plate_8` |
| `{plate}` | 盘号 | `第8盘` |
| `{device}` | 打印机设备名 | `B10 X1-Carbon(X1 Carbon)` |
| `{status}` | 打印状态 | `正在打印` |

---

## 目录结构

```text
bambu-history-screenshot/
├── src/
│   ├── Program.cs              # 入口与高 DPI（Per-Monitor V2）适配
│   ├── NativeMethods.cs        # Win32 窗口定位、前置、WebView2 唤醒与滚轮控制
│   ├── BambuUiaScanner.cs      # 三级卡片识别（DOM 容器 / 图像锚点 / 视觉边框）与字段解析
│   ├── BambuCloudClient.cs     # 拓竹云端打印历史客户端（国区/国际区登录、耗材种类与克重解析）
│   ├── CloudLoginForm.cs       # 云端账号登录窗口（区域下拉、验证码/2FA/Token 备用）
│   ├── ScreenshotCapturer.cs   # 屏幕捕获、边距裁剪与视觉灰框精定位
│   ├── ExcelExporter.cs        # 零依赖标准 .xlsx 导出（6列排版、耗材合计与内嵌 PNG 截图）
│   ├── Models.cs               # 数据模型、配置持久化、命名模板与重名策略
│   └── MainForm.cs             # WinForms 主界面、实时预览与自动滚动调度
├── build.ps1                   # Windows 零依赖一键编译脚本
├── 使用说明.md                  # 中文使用说明
└── LICENSE                     # MIT 开源协议
```

---

## 故障排查

- 如果未检测到卡片，请确保 Bambu Studio 已正常显示打印历史页面且未最小化。
- 可点击工具右上角的 **窗口诊断** 按钮生成 `uia_debug.txt`，便于排查页面布局差异。

## License

[MIT License](LICENSE)
