# Perelegans

> 用于追踪视觉小说的游玩时间的 Windows 桌面应用。

![演示](./images/image_01.png)

## ✨ 功能特性

### 🎮 游玩时间追踪
- 基于进程监控自动记录游玩时长，无需手动操作
- 可自定义监控轮询间隔
- 实时检测游戏运行状态并显示指示器
- 完整的游玩会话历史记录

### 📚 游戏库管理
- 从运行中的进程快速添加游戏
- 支持游戏状态标记：在玩 / 已弃 / 已通关 / 计划中
- 数据库备份与还原

### 🔍 元数据抓取
集成三大数据源，一键补全游戏信息：
- **[VNDB](https://vndb.org/)**
- **[Bangumi](https://bangumi.tv/)**
- **[ErogameSpace](https://erogamescape.dyndns.org)**

自动获取标题、品牌 / 开发商、发售日期、标签、官方网站等信息。

### 🖼️ 封面管理
- 从元数据源搜索并选择高质量封面图
- 支持本地缓存封面，离线可用

### 🤖 AI 游戏推荐
- 基于你的游戏库与玩家口味画像，利用 VNDB 数据智能推荐新作
- 接入 OpenAI 兼容 API，生成个性化推荐理由
- 可自定义 API 地址、密钥与模型

### 📊 游玩统计
- 可视化游玩时间分布
- 游玩时段趋势分析

### 🎨 个性化
- 亮色 / 暗色 / 跟随系统三种主题模式
- MahApps.Metro 现代化 UI 风格
- 支持三种语言：简体中文 · English · 日本語

### 🔧 实用功能
- 系统托盘最小化，后台静默监控
- 开机自启动
- HTTP 代理设置
- 单实例运行保护

## 📦 安装

### 从 Release 下载（推荐）

前往 [Releases](../../releases) 页面下载最新版本的压缩包：

### 从源码构建

**前置要求：**
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

```bash
# 克隆仓库
git clone https://github.com/Shizuku-in/Perelegans.git
cd Perelegans

# 构建
dotnet build src/Perelegans/Perelegans.csproj

# 运行
dotnet run --project src/Perelegans/Perelegans.csproj
```

## 🚀 快速上手

1. **启动应用** — 运行 `Perelegans.exe`
2. **添加游戏** — 先启动你的游戏，然后在应用中点击「从进程添加」选择对应的进程
3. **补全信息** — 右键游戏卡片，选择「获取元数据」自动从 VNDB / Bangumi / 批评空间抓取信息与封面
4. **自动追踪** — 保持应用在后台运行，它会自动监控进程并累计游玩时间
5. **查看统计** — 在主界面查看各游戏的总游玩时长，或打开统计面板查看详细数据

## 🏗️ 技术栈

| 类别 | 技术 |
|------|------|
| 框架 | .NET 8 / WPF |
| UI 库 | [MahApps.Metro](https://mahapps.com/) |
| MVVM | [CommunityToolkit.Mvvm](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/) |
| 数据库 | [EF Core](https://learn.microsoft.com/ef/core/) + SQLite |
| 图表 | [LiveCharts2](https://livecharts.dev/) |
| 网页解析 | [HtmlAgilityPack](https://html-agility-pack.net/) |
| CI/CD | GitHub Actions |

## 📁 项目结构

```
src/Perelegans/
├── App.xaml(.cs)          # 应用入口，服务初始化，单实例 & 托盘管理
├── Models/                # 数据模型（Game, PlaySession, AppSettings 等）
├── Data/                  # EF Core DbContext
├── Services/              # 业务逻辑层
│   ├── DatabaseService           # 数据库 CRUD
│   ├── ProcessMonitorService     # 进程监控 & 游玩时间记录
│   ├── VndbService               # VNDB API 客户端
│   ├── BangumiService            # Bangumi API 客户端
│   ├── ErogameSpaceService       # ErogameSpace 爬虫
│   ├── RecommendationService     # 游戏推荐引擎
│   ├── AiRecommendationService   # AI 推荐理由生成
│   ├── CoverArtService           # 封面图获取 & 缓存
│   ├── ThemeService              # 主题切换
│   ├── TranslationService        # 多语言翻译
│   ├── SettingsService           # 设置读写
│   └── StartupRegistrationService # 开机自启注册
├── ViewModels/            # MVVM ViewModel 层
├── Views/                 # XAML 界面
├── Controls/              # 自定义控件（MasonryPanel 瀑布流布局）
├── Converters/            # 值转换器
├── Themes/                # 自定义主题资源字典
└── i18n/                  # 国际化资源文件（.resx）
```

## 📄 许可证

本项目基于 [MIT 许可证](LICENSE) 开源。
