# LiteTerm

LiteTerm 是一款面向 Windows 的精简 SSH/SFTP 客户端。目前仓库已经完成 V0.0 技术验证骨架，重点验证 WPF、WebView2、xterm.js 与 SSH.NET 的完整终端链路。

## 当前能力

- WPF 主窗口和连接栏
- SSH 密码连接
- 首次连接前显示并确认 SHA-256 主机指纹
- 本地打包的 xterm.js 终端页面，不加载远程网页
- UTF-8 输入输出桥接
- 终端尺寸自适应并同步远程 PTY
- 16ms 输出合并刷新，降低高速输出时的跨 WebView2 调用次数
- 连接状态提示、手动断开和窗口关闭资源释放
- SSH 连接参数基础校验测试

当前版本不会保存服务器或密码，主机指纹也会在每次连接时重新确认。

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

1. 使用测试 Linux 主机验证 `vim`、`top`、`tail -F` 和中文宽字符。
2. 增加私钥选择界面与已知主机指纹持久化。
3. 引入 SQLite 服务器配置和 Windows DPAPI 凭据保护。
4. 增加终端搜索、复制粘贴菜单和更严格的输出背压。

完整路线见 [LiteTerm_开发计划.md](LiteTerm_开发计划.md)。
