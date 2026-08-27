# Changelog

版本号遵循[语义化版本](https://semver.org/lang/zh-CN/)（`主版本.次版本.修订号`），每次发布打对应的 Git tag（`vX.Y.Z`）。

## [1.0.1] - 2026-08-27

### 修复

- **严重**：点击"设置"会导致整个托盘应用崩溃退出。根因是 `SettingsWindow.xaml` 里用
  `Icon="..."` 通过 XAML 类型转换器加载图标，在动态 `new` 出来的窗口（非 `StartupUri`
  主窗口）上会在 `InitializeComponent()` 阶段抛 `XamlParseException`；WPF 对 UI 线程的
  未处理异常默认直接终止进程。改为在代码后置里用 `BitmapImage` + pack URI 手动设置图标。
- 补上全局异常兜底（`DispatcherUnhandledException` / `AppDomain.UnhandledException` /
  `TaskScheduler.UnobservedTaskException`）：往后任何界面层未预料的异常都只会记录到
  `%LOCALAPPDATA%\QuotaFlow\crash.log`，不会再让托盘图标整个消失。
- 设置页密钥输入框禁用输入法（`InputMethod.IsInputMethodEnabled="False"`），避免第三方
  输入法钩子在这类只需要 ASCII 字符的输入框上触发冲突。

### 优化

- 托盘面板卡片间距进一步收紧（页脚"刚刚更新"行的留白从两段叠加的 14px 降到 2px）。

## [1.0.0] - 2026-08-27

首个可用版本。

### 新增

- Claude / Codex / MiniMax / DeepSeek 四平台额度查询，真实接口联调通过（详见 `README.md`）。
- Windows 系统托盘常驻，弹出面板展示四张平台卡片，固定顺序 Claude → Codex → MiniMax → DeepSeek。
- 本机 OAuth 凭据只读复用（Claude Code / Codex CLI），不要求重新登录、不收集账号密码。
- MiniMax / DeepSeek API Key 经 Windows 凭据管理器加密存储；设置页支持通过 Windows Hello（或密码兜底验证）查看已保存的明文 Key。
- 缓存先行展示 + 后台刷新；同一平台并发刷新请求自动去重；单平台失败不影响其他平台。
- 浅色 / 深色 / 跟随系统三态主题，125%/150%/200% DPI 适配。
- 品牌图标：渐变圆角背景 + "额度环"设计，覆盖托盘图标、应用图标、窗口图标。
- 71 个 xUnit 单元测试，覆盖额度换算、凭据解析、错误分类、缓存往返、并发去重等场景。

### 已知限制

- Claude/Codex 用量接口为非公开接口，未来可能变化（见 `README.md` "已知风险"）。
- 托盘面板定位为启发式算法，非 Shell 层面精确坐标查询。
- 视觉效果为手写 Fluent 风格模拟，未使用系统级 Mica/Acrylic 材质。
