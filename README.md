# QuotaFlow

> Private Project · Windows 11 系统托盘 AI 额度监控

## 产品简介

一眼看清多个 AI 平台还剩多少额度、何时重置、数据是否新鲜。不做聊天、模型切换、
代理路由等其他功能。常驻系统托盘，支持托盘弹出 / 悬浮窗口 / 桌面看板三种显示模式。

## 核心功能

- **内置平台**：Claude、Codex、MiniMax、DeepSeek、阿里云百炼 Token Plan（个人版）、火山方舟 Coding Plan。
- **自定义平台**：运行时添加任意额度接口，配置取值路径即可，无需改代码或发版。
- **额度窗口**：按平台展示多个时间窗口的剩余/已用百分比与重置倒计时；余额型平台展示金额。
- **跨设备迁移**：口令加密的备份包导出/导入（PBKDF2 + AES-256-GCM），导出前需身份验证。
- **诊断报告**：一键生成已脱敏的运行状态报告，便于反馈问题。
- **安全**：密钥只存 Windows 凭据管理器；配置 DPAPI 加密落盘；**绝不把未知数据显示成 0%**。

## 当前版本

`v0.12.1` — 版本号定义于 `QuotaFlow.Windows.App/QuotaFlow.Windows.App.csproj` 的 `<Version>`。

仍处于 `0.x`：走向 1.0 的条件见 `docs/2026-09-01-QuotaFlow后续优化计划-v1.0.md`。

## 系统要求

- Windows 11（最低支持 Windows 10 1809 / 10.0.17763）
- 发布产物为自包含单文件，**目标机器无需安装 .NET 运行时**
- 阿里云百炼一键登录需要 WebView2 Runtime（Windows 11 已内置）

## 开发环境

- .NET SDK 8.0
- 目标框架：Core `net8.0-windows` / App `net8.0-windows10.0.19041.0`（仅为调用 Windows Hello）

## 项目结构

```
QuotaFlow/                         （仓库根 = 项目根）
├── QuotaFlow.Windows.sln
├── QuotaFlow.Windows.Core/        平台无关的业务逻辑（可单独跑单测，不依赖 WPF）
│   ├── Models/                    ProviderSnapshot / QuotaWindow / BalanceMetric / 枚举
│   ├── Authentication/            Claude/Codex 本机 OAuth 凭据读取
│   ├── Providers/                 各内置平台的查询实现 + IQuotaProvider
│   └── Services/                  SecureCredentialStore / LocalCache / RefreshCoordinator / AppSettingsStore
├── QuotaFlow.Windows.App/         WPF 宿主：托盘、面板、设置页（ViewModels/Views）
├── QuotaFlow.Windows.Tests/       xUnit 单元测试
└── QuotaFlow.Windows.PocConsole/  阶段 A 用的命令行验证工具（真实联调 + 存取密钥）
```


## 已知行为：Claude 偶发限流

Claude 的用量接口有服务端限流，而 Claude Code CLI 与本应用共用同一份配额，
因此日常使用中偶发 429 是正常现象，不代表出错。

被限流时的行为（v0.10.2 起）：

- 卡片**保留上次查到的真实数据**继续显示，不会清空成空白或 0%；
- 状态显示「请求过于频繁」，提示给出**实时倒计时**：
  `查询过于频繁，约 3 分钟后自动重试（期间显示的是上次查到的数据）`；
- 等待时长以服务端 `Retry-After` 为准，本地兜底 3 分钟，取两者中更长的；
- 该冷却**跨重启有效**——限流是远端配额的状态，不会因为重启应用就复位。

「上次更新」的分钟数在此期间会持续变大，这是如实反映数据新鲜度，属于预期表现。

## 构建 / 测试 / 运行

```powershell
dotnet build QuotaFlow.Windows.sln -c Release
dotnet test QuotaFlow.Windows.Tests/QuotaFlow.Windows.Tests.csproj
dotnet run --project QuotaFlow.Windows.App
```

## 发布独立可执行文件

```powershell
dotnet publish QuotaFlow.Windows.App/QuotaFlow.Windows.App.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none `
  -o publish/win-x64
```

产出 `publish/win-x64/QuotaFlow.exe`（约 70MB，自包含 .NET 运行时，目标机器不需要预装 .NET）。如果目标机器已装 .NET 8 Desktop Runtime，去掉 `--self-contained true` 和 `-p:PublishSingleFile=true` 可以得到几百 KB 的框架依赖版本。

## 安全：本机凭据读取说明（对应产品文档 §7.9）

### Claude

- **路径**：`%USERPROFILE%\.claude\.credentials.json`
- **读取方式**：普通文件读取（当前用户权限即可，不需要管理员权限），只解析 JSON 结构，取出 `accessToken` 用于当次 HTTP 请求，不写入任何日志、缓存或异常消息。
- **用途**：作为 `Authorization: Bearer <token>` 调用 `GET https://api.anthropic.com/api/oauth/usage`（非公开接口，参考 [cc-switch](https://github.com/farion1231/cc-switch) 源码并结合本机实测确认）。
- **风险**：
  1. 该文件本身由 Claude Code CLI 以明文 JSON 形式落地，QuotaFlow 只读不写，风险边界与直接运行 `claude` 命令一致，不额外扩大暴露面。
  2. `/api/oauth/usage` 是非公开接口，官方随时可能调整路径或响应结构；已做防御式解析（未知字段跳过、缺失窗口隐藏），接口变化会体现为"服务异常"状态而不是崩溃或伪造数据。
  3. 进程内存中会短暂持有 access token（仅在发起单次 HTTP 请求期间），不落盘、不进日志。

### Codex

- **路径**：`%USERPROFILE%\.codex\auth.json`
- **读取方式**：同上，普通文件读取；只有 `auth_mode == "chatgpt"`（ChatGPT OAuth 登录）时才有可用的 `access_token`，API Key 模式（`auth_mode == "apikey"`）会被识别为"未配置"，不会误用 API Key 冒充订阅凭据。
- **用途**：调用 `GET https://chatgpt.com/backend-api/wham/usage`（非公开接口，同样参考 cc-switch 并结合本机实测确认）。
- **风险**：与 Claude 一致——只读、不落盘、接口非公开可能变化、已做防御式解析。

### MiniMax / DeepSeek

- 不读取任何本机 CLI 凭据文件；由用户在设置页手动填写 API Key。
- Key 只通过 Windows 凭据管理器（`Advapi32.dll` 的 `CredWrite`/`CredRead`/`CredDelete`，`CRED_TYPE_GENERIC`）存取，目标名分别为 `QuotaFlow:minimax:ApiKey` / `QuotaFlow:deepseek:ApiKey`，不写入 `settings.json`、缓存文件、日志或测试代码。

### 火山方舟 Coding Plan

- 额度查询走火山引擎**控制面 OpenAPI**（`open.volcengineapi.com`，`Action=GetCodingPlanUsage`，
  账号未订阅时回退探测 `GetAFPUsage`），不是推理网关（`ark.cn-beijing.volces.com`）的 Bearer
  Token，所以用 Access Key ID / Secret Access Key 做 Signature V4 的火山变体签名，而不是简单的
  API Key 请求头。
- Access Key ID / Secret Access Key 同样只经 Windows 凭据管理器存取，目标名
  `QuotaFlow:volcengine-ark:AccessKeyId` / `QuotaFlow:volcengine-ark:SecretAccessKey`；Secret
  Access Key 不出现在日志、异常消息或配置文件中。
- 该控制面网关没有面向个人开发者的公开文档；签名算法与响应字段依据两份独立开源实现交叉核对：
  [lordqyxz/dsh-ark-quota](https://github.com/lordqyxz/dsh-ark-quota)（MIT）与
  [farion1231/cc-switch](https://github.com/farion1231/cc-switch)（MIT）。官方接口不返回
  Lite/Pro 套餐类型，因此不会在界面上臆造"Pro"字样，仅在用户手动确认后可自定义显示名。

### 通用安全约定

- `%LOCALAPPDATA%\QuotaFlow\cache.json` 只保存展示用的百分比/重置时间等数据（`ProviderSnapshot` 模型本身没有能装 token/key 的字段），可以随意删除或分享截图，不会泄露凭据。
- `%LOCALAPPDATA%\QuotaFlow\settings.json`（刷新间隔、主题、接口地址覆盖等非敏感配置）以 DPAPI（`DataProtectionScope.CurrentUser`）加密信封落盘：磁盘上只有 `schemaVersion` / `cipher` / `payload`，不含任何明文值。旧版明文配置首次启动会自动迁移，并保留 `settings.json.v0.bak` 快照。DPAPI 与当前 Windows 用户绑定，换用户/重装后旧文件无法解密时安全落回默认值（不覆盖原文件）。
- 四个平台的接口地址默认内置；设置页"接口地址（高级）"可在接口变更时覆盖（同样加密存储），留空即用内置默认。
- HTTP 请求失败（401/403/429/超时/DNS/TLS/响应格式变化）会被分类为不同的错误状态展示给用户，错误文案不包含 `Authorization`、`Bearer` 或原始响应体。
- 发布产物（`dotnet publish` 输出）不包含任何密钥、凭据、缓存或日志文件——这些都只在运行时生成于 `%LOCALAPPDATA%\QuotaFlow`。

## 版本管理

语义化版本（`主版本.次版本.修订号`），每次发布打 Git tag（`vX.Y.Z`），变更记录见 [`CHANGELOG.md`](./CHANGELOG.md)。

## 致谢与许可证引用

Claude / Codex / MiniMax 三个平台的接口路径、请求头和响应字段结构，参考了 [cc-switch](https://github.com/farion1231/cc-switch)（MIT License，作者 Jason Young）的实现，并结合本机实测响应交叉确认，未直接复制源码。DeepSeek 余额接口参考官方文档并同样与 cc-switch 实现交叉确认。

## 已知风险 / 后续改进方向

1. **非公开 OAuth 用量接口的兼容性**：Claude 的 `/api/oauth/usage` 和 Codex 的 `/backend-api/wham/usage` 都不是官方文档化接口，未来可能变化。当前用防御式解析（未知字段展示、缺失字段隐藏、解析失败归类为"服务异常"）作为缓冲，但接口如果发生结构性变化（不只是加字段）仍需要更新代码。
2. **托盘弹出面板的定位是启发式的**：根据任务栏所在屏幕边缘估算弹出位置，不是通过 Shell 层面精确查询托盘图标坐标（`Shell_NotifyIconGetRect`），多显示器或非常规任务栏位置下可能不是像素级贴合。
3. **视觉效果是"手写 Fluent 风格"而非真正的 Mica/Acrylic 材质**：用圆角 + 阴影 + 浅色/深色双色板模拟 Windows 11 视觉语言，没有引入 WinUI3/WPF-UI 之类的库来获取系统级亚克力效果，符合"不为追新技术引入复杂架构"的要求，但视觉保真度不是像素级还原系统组件。v1.3.0 的显示模式功能里，"云母/亚克力"材质选项同样受此限制：面板窗口用 `WindowStyle="None" AllowsTransparency="True"` 实现圆角/阴影，这种逐像素 Alpha 分层窗口与 DWM 的系统级材质合成天然冲突，即使真实调用了 `DwmSetWindowAttribute` 也不会产生可见的系统材质效果，只能用不同色调的半透明背景色近似；悬浮窗口/桌面看板也暂时没有专门的悬停展开工具栏或进入/退出动画。
4. **DPAPI 与当前 Windows 用户绑定**：`settings.json` 的加密密钥派生自当前 Windows 用户，换账号登录或系统重装后旧文件无法解密——应用会安全落回默认值并保留原文件（不覆盖、不报错），重新在设置页保存即可。配置文件本身不含任何密钥，可接受。
5. **Claude/Codex 只识别对应 CLI 的登录，不识别桌面客户端**：Claude 只读取 Claude Code CLI 写的 `~/.claude/.credentials.json`；Codex 只读取 Codex CLI 写的 `~/.codex/auth.json` 且要求 `auth_mode == "chatgpt"`。**Claude Desktop / ChatGPT Desktop 客户端不会写这两个文件**，即使用桌面版登录了同一个账号，QuotaFlow 也检测不到——这是范围内的设计限制，不是 bug：CLI 工具的凭据文件是特意做成可被第三方程序读取的机读格式，桌面客户端的会话存储没有这层约定，贸然猜测/读取会既不可靠也超出"只读复用官方 CLI 已落地凭据"这条安全边界。**closing/退出 CLI 进程不影响检测**——凭据是登录时一次性写入磁盘的文件，QuotaFlow 直接读文件，不依赖 CLI 进程是否在运行；只有真正登出、或者凭据本身过期，才会导致检测不到。日常使用建议：只要曾经用对应 CLI 登录过一次（哪怕平时都用桌面版），文件就会一直留在磁盘上，无需让 CLI 保持运行。

## Repository 规则

本仓库的分支、提交、版本、发布、安全与备份规则见 [`CLAUDE.md`](CLAUDE.md)。要点：

- 一个产品一个仓库；本仓库只放 QuotaFlow。
- 默认分支 `main`，**禁止强制推送**。
- Commit 遵循 Conventional Commits，发布提交为 `release: QuotaFlow vX.Y.Z`。
- Tag 格式 `vX.Y.Z`。
- 构建产物进 GitHub Releases，不进 Git 历史。
- 密钥、Token、Cookie、`.env`、`*.qfbackup` 一律不得提交。
