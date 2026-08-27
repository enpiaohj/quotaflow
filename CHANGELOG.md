# Changelog

版本号遵循[语义化版本](https://semver.org/lang/zh-CN/)（`主版本.次版本.修订号`），每次发布打对应的 Git tag（`vX.Y.Z`）。

## [1.0.3] - 2026-08-27

### 新增

- **配置加密持久化**：`%LOCALAPPDATA%\QuotaFlow\settings.json` 改为 DPAPI（`DataProtectionScope.CurrentUser`）加密信封落盘，磁盘上只出现 `schemaVersion` / `cipher` / `payload`，不含任何明文配置。信封带 `schemaVersion`，旧版明文配置（v1.0.0~1.0.2）与旧版信封在加载时自动迁移，并保留旧文件快照 `settings.json.v0.bak` / `settings.json.vN.bak` 后再重写。
- **接口地址可配置**：设置页新增"接口地址（高级）"卡片，Claude / Codex / MiniMax / DeepSeek 四个平台的接口地址可在不重编译的情况下覆盖（留空使用内置默认），覆盖项同样加密持久化，保存后重启应用生效。
- **面板实时时钟**：托盘面板顶部新增实时日期/时间显示，格式支持"日期 + 周几 + 第几周 + 时间"（默认）、"日期 + 周几 + 时间"、"日期 + 时间"、"仅时间（实时）"四种，可在设置页"外观 → 日期显示格式"切换并持久化。
- **面板标题显示版本号**：产品名后自动显示当前版本号，与设置页"关于"同源（取自程序集版本），避免 UI 文本手工维护造成漂移。
- **诊断启动开关**：`--show-panel` 启动参数可在启动时直接展示面板且失焦不自动收起，供自动化检查使用；不带该参数行为与之前完全一致。

### 优化

- 设置页"关于"的版本号改为从程序集动态读取。

## [1.0.2] - 2026-08-27

### 修复

- **严重**：点击"眼睛"查看明文 Key 时，Windows Hello 未配置的机器上不弹任何验证框。
  根因有两层：① 当 Hello 报告"可用"但实际未给当前用户配置 PIN/生物识别时，
  `RequestVerificationAsync` 不弹 UI 直接返回，旧代码就此判定失败、从不降级到密码验证——
  现在统一降级到系统密码验证；② 密码验证用的 `CredUIPromptForWindowsCredentials` 因传了
  `hwndParent` 和 `CREDUIWIN_EXCLUDE_CERTIFICATES` 标志，在本机直接返回
  `ERROR_INVALID_PARAMETER` 导致弹窗根本不出现——现已移除这两处，凭据框正常居中弹出。
- 密码验证结果区分"已取消 / 验证失败"，不再把用户主动取消也算作失败。

### 优化

- 服务端新出现的未识别额度窗口（如 `nimbus_quill`）默认不再显示成英文名占位。
  新增设置项"显示未识别的新额度窗口"（默认关闭），开启后以中文标签（如"其他额度（nimbus_quill）"）
  展示，便于排查。
- 识别 Claude 新增的 `seven_day_omelette`（Claude Design 设计额度）窗口，中文显示为"7 天 (Design)"。

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
