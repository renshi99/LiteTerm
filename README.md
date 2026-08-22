# LiteTerm

LiteTerm 是一款面向 Windows 的精简 SSH/SFTP 客户端。目前仓库已经完成 V0.0 技术验证骨架，重点验证 WPF、WebView2、xterm.js 与 SSH.NET 的完整终端链路。

## 当前能力

- WPF 主窗口和连接栏
- SSH 密码连接
- SSH 私钥连接（支持选择私钥文件和可选口令）
- 首次连接确认并持久化 SHA-256 主机指纹；后续不匹配时阻止连接
- 本地打包的 xterm.js 终端页面，不加载远程网页
- UTF-8 输入输出桥接
- 终端尺寸自适应并同步远程 PTY
- `Ctrl+F` 终端搜索、右键复制/粘贴/清屏菜单
- 1 MiB 有界终端输出缓冲；输出过快时保留最新内容并提示已丢弃较早数据
- 16ms 输出合并刷新，降低高速输出时的跨 WebView2 调用次数
- 连接状态提示、手动断开和窗口关闭资源释放
- SQLite 版本化服务器资料/DPAPI 凭据存储基础
- 已知主机记录使用 SQLite 保存，并兼容导入旧版 JSON 数据
- SSH 连接参数基础校验测试

当前连接界面尚未接入服务器资料的新增、编辑和凭据保存；底层 SQLite/DPAPI 仓储已经完成并通过测试。已确认的主机指纹会持久化，后续完全匹配时自动通过，不匹配时阻止连接。

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
3. 将现有 SQLite 服务器配置和 Windows DPAPI 凭据保护接入服务器管理界面。

完整路线见 [LiteTerm_开发计划.md](LiteTerm_开发计划.md)。
