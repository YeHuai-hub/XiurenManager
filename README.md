# 写真资源管理器

Windows 桌面端写真下载与本地图片、视频管理工具，使用 .NET 8、WPF 和 WPF-UI 开发。

## 源码构建

环境要求：

- Windows 10/11 x64
- .NET 8 SDK
- 已登录的 BaiduPCS-Go（仅下载功能需要）
- 7-Zip 与 FFmpeg/ffprobe（解压和媒体检查功能需要）

```powershell
dotnet restore XiurenManager.sln
dotnet build XiurenManager.sln -c Release
dotnet run --project src\XiurenManager\XiurenManager.csproj
```

网站账号、密码、数据库、日志、标签、下载内容和发布产物均属于本机私人数据，已通过 `.gitignore` 排除，不会提交到仓库。首次运行后请在应用设置页填写本机环境信息。

## 私人版安装

- 标准安装程序输出到 `artifacts\installer`
- 默认安装目录：`E:\Apps\写真资源管理器`
- 私人配置、数据库、标签和日志继续保存在 `F:\秀人\_Tool`
- 升级或卸载程序不会删除 `F:\秀人` 下的媒体文件，也不会覆盖私人数据
- 构建命令：`powershell -ExecutionPolicy Bypass -File scripts\build-private-installer.ps1`

安装器构建另外需要 Inno Setup 7，以及放置于 `tools\ffmpeg` 的本机 FFmpeg 便携文件；这些第三方二进制不纳入源码仓库。

启动方式：双击 `启动写真自动下载工具.bat`，或直接运行 `publish-v3\XiurenManager.exe`。

`publish` 目录保留上一版 WinForms 稳定程序，必要时可作为回退使用。

默认目录：
- 工具目录：`F:\秀人\_Tool`
- 下载目录：`F:\秀人`
- 配置文件：`config\settings.json`
- 数据库：`data\xiuren.db`
- 日志目录：`logs`
- FFmpeg：`tools\ffmpeg\bin`
- 喜爱值：`data\favorites.json`

主要功能：
- 全站搜索：`https://260704.xiurentua.cc/?s=搜索内容`
- 分类内搜索：例如 `/tbgx?s=搜索内容`
- 跳过没有百度网盘链接的免费资源
- 通过本机已登录的 BaiduPCS-Go 下载会员资源
- 支持资源级并发下载，设置页里的“资源并发数”最大为 5
- 每套资源保存到 `F:\秀人\<模特名>\<页面标题>`
- 压缩包自动改名为页面标题
- 自动解压，解压成功后可自动删除原压缩包
- 下载完成前使用 ffprobe/ffmpeg 检查视频结构，并抽查开头、中段、结尾画面
- 统计页可后台检查全部视频完整性，损坏结果保存在 `data\video-check-*.json`
- 损坏视频会标记为待重新下载；检查本身不删除文件，重新下载时只替换确认损坏的文件
- 清理非图片、非视频文件，并保护 `_Tool` 工具目录不被误删
- 日志按单文件 20 MB 自动轮转，启动时按保留天数和总容量清理，日志页也可手动清理磁盘日志
- 统计全部模特和单个模特的本地资源情况，并显示损坏视频数量
- “媒体库”页以真实封面浏览本地套图，并在工具内播放图片和视频
- 套图卡片左上角的管理菜单可直接打开目录、重命名、移动、复制或删除到回收站；目标已存在时不会覆盖
- 查看器默认使用大画面；F11 或双击画布进入沉浸模式，图片支持 1–12 倍缩放和拖动
- 图片支持自动轮播，速度可在 0.5–30 秒/张之间调节，并自动跳过视频
- 每套写真可添加多个短标签；标签摘要会显示在套图封面，并与喜爱值一起保存在 `data\favorites.json`
- 媒体库筛选支持套图标题、编号、模特名、标签和多个关键词，并显示实时命中套数
- 支持上一项、下一项、播放暂停、进度拖动和音量调节
- 看完后由用户主动点击“看完本套 +1”，打开或播放不会自动加分
- 喜爱值可以重复累加，也可“撤销 1”；喜欢合集只显示喜爱值大于 0 的套图
- 统计页双击套图或点击“应用内浏览”，可直接进入相应套图

注意：
- 需要本机 BaiduPCS-Go 已经登录百度网盘账号。
- FFmpeg 为便携版，不写入系统目录，也不修改系统 PATH。
- 工具不会绕过网站会员、百度登录、验证码或风控限制。
- 如果修改过设置，重新打开工具后会自动读取 `config\settings.json`。
- 内嵌视频播放使用 VideoLAN LibVLCSharp/LibVLC，依赖许可见 `THIRD-PARTY-NOTICES.md`。
