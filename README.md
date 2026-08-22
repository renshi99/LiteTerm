# LiteTerm

LiteTerm 是一款面向 Windows 的精简 SSH/SFTP 客户端。目前项目处于 V0.2 服务器管理阶段，SSH 终端纵向链路、SQLite/DPAPI 持久化和已知主机校验已经建立。

## 当前能力

- WPF 主窗口和连接栏
- SSH 密码连接
- SSH 私钥连接（支持选择私钥文件和可选口令）
- 首次连接确认并持久化 SHA-256 主机指纹；后续不匹配时阻止连接
- 本地打包的 xterm.js 终端页面，不加载远程网页
- UTF-8 输入输出桥接
- 终端尺寸自适应并同步远程 PTY
- `Ctrl+F` 终端搜索、右键复制/粘贴/清屏菜单
- 可即时预览并持久化的终端文字色/背景色设置，支持系统可视化颜色面板、`#RRGGBB` 精确输入及深色、纯黑、柔和灰黑和浅色预设
- 1 MiB 有界终端输出缓冲；输出过快时保留最新内容并提示已丢弃较早数据
- 16ms 输出合并刷新，降低高速输出时的跨 WebView2 调用次数
- 连接状态提示、手动断开和窗口关闭资源释放
- SQLite 版本化服务器资料/DPAPI 凭据存储基础
- 服务器资料新增、编辑、复制、删除、分组、多关键词搜索和快捷连接
- 按名称或组内最近连接时间排序，连接成功后更新最近连接时间
- 快捷连接成功后可自动保存资料，并为重复连接生成名称后缀
- 已知主机记录使用 SQLite 保存，并兼容导入旧版 JSON 数据
- SSH 连接参数基础校验测试

服务器公开资料与 DPAPI 保护后的凭据通过同一 SQLite 事务保存。已确认的主机指纹会持久化，后续完全匹配时自动通过，不匹配时阻止连接。终端兼容性和服务器管理完整交互仍需使用测试服务器人工验收。

## 开发环境

- Windows 10/11 x64
- .NET 10 SDK（仓库通过 `global.json` 使用 10.0.400 功能带）
- Microsoft Edge WebView2 Runtime

## 构建与测试

```powershell
dotnet restore LiteTerm.sln --configfile NuGet.Config
dotnet build LiteTerm.sln --no-restore
dotnet test LiteTerm.sln --no-build --no-restore
```

真实 SSH 集成测试仅在显式提供临时环境变量时运行，凭据不会写入仓库：

```powershell
$env:LITETERM_TEST_SSH_HOST = "测试主机"
$env:LITETERM_TEST_SSH_USERNAME = "测试用户"
$env:LITETERM_TEST_SSH_PASSWORD = "测试密码"
dotnet test tests/LiteTerm.Tests/LiteTerm.Tests.csproj --filter "Category=Integration"
```

运行：

```powershell
dotnet run --project src/LiteTerm.App/LiteTerm.App.csproj
```

## 项目结构

```text
src/LiteTerm.App             WPF 界面、WebView2 和终端静态资源
src/LiteTerm.Core            会话接口、状态和连接模型
src/LiteTerm.Infrastructure  SSH.NET 会话实现
tests/LiteTerm.Tests         核心逻辑测试
```

## 下一阶段

1. 使用测试 Linux 主机通过 LiteTerm 实际验证连接、中文宽字符、`vim`、`top`、`tail -F`、搜索及复制粘贴。
2. 验证连接超时、拒绝连接、远端断开和连续连接/释放行为。
3. 使用可清理的专用测试资料验证服务器 CRUD、复制、排序、重启恢复和密码/私钥快速连接。

完整路线见 [LiteTerm_开发计划.md](LiteTerm_开发计划.md)。
