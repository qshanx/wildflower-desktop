# 当前结构与实施约定

Windows 宿主正在实现，未通过真实桌面验收。共享花园来自已运行的 Mac Canvas 原型。首版独立安装，不要求用户另装 Lively。

## 模块与状态

- `themes/pond-garden/` 负责画面和种植规则。入口 `flower-meadow.html`，`meadowDesktop(action,x,y)` 是兼容输入接口。
- `src/windows/` 使用 .NET 10 WinForms + Evergreen WebView2。`DesktopHost` 隔离 Explorer/WorkerW、桌面输入与遮挡。管理窗口负责预览、应用、停用与更新设置。
- `ThemeStore` 管理版本化资源、包校验与激活目录；`ProgramUpdates` 通过 Velopack 管理软件包。两者更新源固定到公共 GitHub Releases `update-feed`，无客户端密钥。
- 花园状态仍由页面 `pond-garden-v2` localStorage 权威保存，Windows 使用固定 `https://pond.wildflower.local/` origin 与 `%LocalAppData%/WildflowerDesktopData/WebView2` 数据目录。安装目录、主题包目录与用户数据分开；不因更换资源路径而更换 origin，不清空数据。
- Mac 原型保存在 `src/macos/`；历史运行包不会作为 Windows 源码。

## 采用的边界

候选 A 是自有薄宿主，候选 B 是 Lively 集成。选择 A，以便独立安装和更新；借鉴公开接口知识，避免复制 GPL 实现。WorkerW 为 Windows 桌面内部机制，必须实测 Windows 11 不同桌面结构，失败时明确提示，不能用置顶全屏窗口冒充。

`ThemeStore` 入口为 `string ActiveDirectory`、`Task<bool> StageUpdateAsync()`、`ActivateStaged()`、`Rollback()`。宿主先结束鼠标操作并暂停、切换目录，页面 ready 后才确认；失败恢复旧资源。

`ProgramUpdates` 入口为 `Task<string?> DownloadAsync()`、`void ApplyAndRestart()`，后台检查下载，安全时点应用，保留手动检查与自动更新开关。按架构独立 `win-x64` / `win-arm64` channel，包校验由 Velopack负责。

主题清单 `theme.json`：`schema=1,id=pond-garden,version,minHostVersion,saveSchema=2,url,size,sha256`。HTTPS 地址仅允许本仓库发布资产；按字节数与 SHA256 校验，解压拒绝路径越界、链接和超限内容。版本目录 staging 完成后原子激活；至少保留上一版本。

程序固定源 `https://github.com/qshanx/wildflower-desktop/releases/download/update-feed/`；theme.json 独立发布，可不更改程序版本。GitHub 负责文件分发，不保存用户花园。更新源不可用时继续原版本。

## 验证

逻辑测试与 Windows CI 仅证明相应代码路径。真正桌面输入、遮挡停刷和内存以 Windows VM 安装版记录为准。内存包含宿主及其 WebView2 整组进程；ARM64 VM 和原生 x64 的结论不混用。发布前必须明确未验项，未通过版本不得进入稳定更新源。
