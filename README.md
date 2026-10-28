# GcoresLite

一个用 WPF + .NET 8 重写的机核播客桌面收听工具，只有一个窄条那么大。

> **非官方项目。与机核网（gcores.com）没有任何关系，未获其授权或认可。**  
> 仅调用其官网公开的、未登录状态即可访问的接口，不提供会员内容，不绕过任何付费限制。

![GcoresLite 主界面](docs/screenshot.png)

---

## 为什么有这个东西

官方和社区已有的桌面版基于 Electron，安装体积 272MB、常驻内存 300MB 以上，而日常使用其实只做一件事：找一期节目，听。

GcoresLite 把这件事拆到最小：**本体 5.6MB，启动 1 秒内出列表，常驻内存约 150MB**。

|       | Electron 版 | GcoresLite                  |
| ----- | ---------- | --------------------------- |
| 安装体积  | 272 MB     | 5.6 MB（框架依赖）/ 66 MB（单文件免安装） |
| 常驻内存  | 300 MB+    | ~150 MB                     |
| 启动到可用 | 2~3 秒      | < 1 秒                       |

## 功能

**收听**

- 三条内容线：最新、机核自制、入驻播客，滚动到底自动加载
- 会员专享节目自动隐藏（接口不支持服务端过滤，在客户端做），列表底部的"已隐藏 N 期"会如实告诉你
- mp3 / m4a 直接流式播放，支持 ±15 秒、拖动进度、自动连播
- 续播：关掉再打开，从上次听到的位置继续（听到结尾会重头开始）

**自建播单**

- 右键任意节目「加入播单」，可建多个、可命名
- 「播放全部」按顺序连播，右键可从播单移除

**随机电台**

- 跨三个来源取池，洗牌后连播，不重样
- 剩 4 首时后台自动续池，不打断当前播放
- 「换一个台」重新洗牌

**窗口**

- 大模式 380×620 / 小模式 380×88 / 极小模式 76×76（只显示封面，悬停出播放键）
- 三种模式共用同一个播放实例，切换不中断播放
- 托盘右键菜单可切换模式、播放暂停、下一首、打开数据目录
- 深浅色主题，标题栏月亮/太阳按钮切换，选择会被记住

## 系统要求

- Windows 10 / 11，x64
- **框架依赖版**：需要 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- **单文件版**：不需要任何运行时，双击即用

## 自己构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```bash
git clone <this-repo>
cd GcoresLite

dotnet build
dotnet run --project src/GcoresLite
```

发布产物：

```bash
# 框架依赖（体积小，需要目标机器装 .NET 8 Desktop Runtime）
dotnet publish src/GcoresLite/GcoresLite.csproj -c Release -r win-x64 \
  --self-contained false -o publish

# 单文件免安装（把运行时一起打包，约 66 MB）
dotnet publish src/GcoresLite/GcoresLite.csproj -c Release -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -o publish-single
```

`IncludeNativeLibrariesForSelfExtract` 必须开，否则 SQLite 的原生库解不出来。

## 数据存在哪

全部在 `%LOCALAPPDATA%\GcoresLite\`：

| 路径              | 内容           | 会不会无限增长               |
| --------------- | ------------ | --------------------- |
| `app.db`        | 播单、播放进度、收听历史 | 不会，随播单数量缓慢增长          |
| `cache\covers\` | 封面缩略图        | 不会，200 MB 封顶 + LRU 回收 |
| `cache\audio\`  | **始终为空**     | 音频流式播放，不落盘            |
| `logs\`         | 运行日志         | 保留 7 天，自动清理           |
| `settings.json` | 主题等偏好        | 固定大小                  |

删掉整个 `cache` 文件夹只会丢缓存，播单和进度不受影响。托盘菜单里有「打开数据目录」直达。

## 已知限制

- **不支持登录**，因此会员专享节目无法播放（这是设计上的取舍，不是缺陷）
- 接口是机核官网的公开 JSON:API，非官方文档化接口，**随时可能变更导致某天失效**
- 只在 Windows 上验证过；WPF 本身不跨平台
- 播放位置在刚开播的前几秒会有缓冲滞后，之后正常
- Win10 上窗口是直角（系统圆角需要 Win11）

## 图标

应用图标为项目自制，见 `src/GcoresLite/Assets/app.ico`（7 个尺寸：16/24/32/48/64/128/256）。

两个细节值得说明，都是踩过坑的结果：

- **16/24px 用简化版**：只保留字母 `G`。完整的 `G.FM` 在这个尺寸下会糊成一团，必须单独出图，不能靠缩放。
- **绿边宽度取 38/831**：这个比例等于原设计里 `G.FM` 中间那个句点的直径，所以描边和字重是同一个视觉尺度。小尺寸下它自然淡化，不会喧宾夺主。

图标可以用程序自带工具重新生成：

```bash
# 圆角方形图标 + 浅色底预览
GcoresLite.exe --roundicon <源图> <输出目录> [圆角比例] [绿边比例] [黑边比例]

# 真实尺寸对照图（深色/浅色背景各一行）
GcoresLite.exe --iconsizes <源图> <输出.png>

# 生成多尺寸 ico
GcoresLite.exe --appico <源图> <输出.ico>
```

## 技术栈

- .NET 8 / WPF，MVVM（CommunityToolkit.Mvvm）
- NAudio + 系统 Media Foundation 播放
- SQLite（Microsoft.Data.Sqlite）存播单与进度
- H.NotifyIcon.Wpf 托盘
- 手写 JSON:API 解析，全程容错（机核 2010 年的老数据字段缺失严重）

## 致谢

接口行为的探索参考了社区已有的 Electron 版「机核播客桌面小工具」（作者 yunyi，MIT）。  
本项目为独立的 C# 实现，未使用其任何代码。

## License

[MIT](LICENSE)

---

<sub>本项目仅用于学习 WPF 与桌面应用开发。所有节目内容的版权归机核网及各播客创作者所有，请支持正版。</sub>
