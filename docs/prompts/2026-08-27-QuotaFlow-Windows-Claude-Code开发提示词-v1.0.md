# QuotaFlow for Windows 11：Claude Code 完整开发提示词

> 文档日期：2026-08-27  
> 文档版本：v1.0  
> 产品名称：QuotaFlow  
> 文档类型：Windows 11 开发实施提示词

请在当前工作区开发一个全新的 **Windows 11 轻量托盘应用：QuotaFlow**。

本轮目标非常明确：只做 Claude、Codex、MiniMax、DeepSeek 四个平台的额度/余额查看，不加入聊天、模型切换、代理、路由、Provider 管理、用量分析、云同步或其他复杂功能。

> 产品定位：打开托盘即可在 2 秒内看清四个平台还剩多少额度、何时重置、数据是否最新。

---

## 1. 开工前要求

1. 先检查当前工作区结构、已有代码、Git 状态和项目说明文件。
2. 如果当前目录存在此前的其他项目，不要删除、覆盖或破坏原项目；请新建独立的 Windows 工程目录，例如 `QuotaFlow.Windows/`。
3. 检查是否存在 `AGENTS.md`、`CLAUDE.md` 或其他项目规范并遵守。
4. 检查 CC Switch 最新开源代码及许可证，重点研究其四个平台的额度查询实现、响应模型、认证来源、刷新和错误处理：
   - 项目：https://github.com/farion1231/cc-switch
   - Claude/Codex 官方订阅额度查询相关代码
   - MiniMax Coding Plan/Token Plan 查询相关代码
   - DeepSeek 余额查询相关代码
5. 可以借鉴公开实现和数据结构，但不要盲目复制；记录引用来源，遵守许可证。
6. 不要先假设接口和凭据文件位置。以当前已安装版本、CC Switch 最新实现和实际响应为准，先验证，再实现。
7. 不要向我索要 Claude、Codex 的账号密码，也不要创建网页登录抓取方案。

---

## 2. 技术方案

默认使用以下技术栈：

- **.NET 8**
- **WPF**
- MVVM 分层
- Windows 系统托盘常驻
- Windows 11 风格的 Fluent 视觉语言
- `HttpClientFactory` 或可测试的统一 HTTP 请求层
- JSON 使用 `System.Text.Json`
- 敏感数据使用 Windows Credential Manager 或 DPAPI
- 单元测试使用 xUnit

如果当前 Windows 环境已明确采用其他成熟桌面技术栈，只有在能显著降低风险时才允许调整，并在实施报告中说明原因。不要为追求新技术改用复杂的 WinUI 3/MSIX 架构；本工具优先追求稳定、轻量和容易生成独立安装包。

架构至少包含：

```text
QuotaFlow.Windows
├── App / Bootstrap
├── Models
├── Providers
│   ├── ClaudeQuotaProvider
│   ├── CodexQuotaProvider
│   ├── MiniMaxQuotaProvider
│   └── DeepSeekBalanceProvider
├── Authentication
├── Services
│   ├── RefreshCoordinator
│   ├── SecureCredentialStore
│   ├── LocalCache
│   └── SystemTrayService
├── ViewModels
├── Views
└── Tests
```

不要把四个平台的判断全部写在一个 ViewModel 或 Window code-behind 中。

---

## 3. 四个平台的数据获取

### 3.1 Claude Official

目标：显示 Claude Pro/Max/Claude Code 订阅的真实额度窗口。

优先研究并采用 CC Switch 已验证的方式：

- 识别本机 Claude Code 已登录的 OAuth 状态；
- 在不要求用户重新输入账号密码的前提下读取必要授权；
- 调用当前有效的 Claude 订阅用量接口；已知实现可能使用：

```http
GET https://api.anthropic.com/api/oauth/usage
Authorization: Bearer <Claude Code OAuth access token>
```

- 以上路径只是研究线索，必须结合 CC Switch 最新源码和实测确认，不能把未经验证的字段写死。
- 支持解析当前账户实际返回的窗口，例如：
  - `five_hour`
  - `seven_day`
  - 模型专属 7 天窗口（若存在）
  - `utilization`
  - `resets_at`
  - `extra_usage`（若存在）
- UI 统一显示“剩余百分比”；如果接口返回的是已使用比例，必须正确转换：

```text
remaining = 100 - utilization
```

- 不存在的额度窗口不要伪造为 0%，应直接隐藏。
- OAuth 过期时显示“需要在 Claude Code 重新登录”，不要让 QuotaFlow 接收 Claude 密码。

### 3.2 Codex

目标：显示 ChatGPT/Codex 订阅的额度窗口及恢复时间。

- 研究 CC Switch 对 Codex Official/Codex OAuth 的最新实现。
- 优先复用本机 Codex CLI 已登录的 OAuth 状态。
- 不要使用 OpenAI Admin Key；它与 ChatGPT/Codex 订阅额度不是同一体系。
- 显示当前服务端实际返回的窗口，通常至少包括：
  - 5 小时窗口
  - 7 天窗口
  - 对应重置时间
- 若当前 Codex 版本只能提供部分字段，按实际能力降级，不得估造数据。
- 如果官方查询失败，可将 `/status` 或本地状态作为诊断/降级研究方向，但不要用脆弱的终端屏幕文本解析作为唯一长期实现，除非实测确认没有更稳定方案。

### 3.3 MiniMax

目标：显示 MiniMax Coding Plan/Token Plan 的额度窗口及恢复时间。

- 重点研究 CC Switch 当前版本的 `coding_plan`、MiniMax Provider、请求模板和解析器。
- 支持截图所示的典型信息：
  - 5 小时剩余百分比
  - 7 天剩余百分比
  - 各自重置倒计时
- 如果 MiniMax 必须使用普通 API Key、GroupId、套餐查询 URL或其他参数，在设置页只提供必要字段。
- 所有密钥只保存到 Windows 安全存储，不写入普通 JSON、源码、日志、测试快照或发布包。
- 接口和字段必须基于实测/上游实现，不允许虚构“官方公开 API”。

### 3.4 DeepSeek

目标：显示账户可用余额。

- 使用 DeepSeek 普通 API Key。
- 查询余额接口并显示：
  - 总可用余额
  - 币种
  - 更新时间
  - 若接口返回充值余额/赠送余额，可在展开详情显示，但托盘主页只突出总余额。
- 参考当前官方接口和 CC Switch 实现，已知常用入口为 `GET /user/balance`，必须实测响应结构。
- Key 通过设置页录入并存入 Windows 安全存储。

---

## 4. 统一数据模型

建立统一但不抹平平台差异的数据模型，例如：

```csharp
ProviderSnapshot
- ProviderId
- DisplayName
- State
- PrimaryMetric
- IReadOnlyList<QuotaWindow>
- LastUpdatedAt
- DataSource
- ErrorCategory
- UserGuidance

QuotaWindow
- Id
- DisplayName
- RemainingPercent
- UsedPercent
- ResetsAt
- RemainingDuration

BalanceMetric
- Amount
- Currency
```

状态至少区分：

- `Loading`
- `Available`
- `Low`
- `Critical`
- `NotConfigured`
- `AuthenticationExpired`
- `NetworkError`
- `RateLimited`
- `ProviderError`
- `Stale`

硬性规则：

- 未知数据不能显示为 `0%` 或 `0.00`。
- 旧缓存必须标记“数据可能已过期”。
- 401、403、429、超时、DNS/TLS 和响应格式变化必须分别处理。
- 单个平台失败不能影响其他三个平台显示。

---

## 5. UI 与交互设计

### 5.1 总体形态

应用主要形态是 **Windows 系统托盘弹窗**，不是传统的大型管理后台。

- 单击托盘图标：打开/关闭额度面板。
- 面板默认出现在任务栏托盘区域上方。
- 点击外部区域自动收起。
- 右键托盘图标：显示“打开面板、立即刷新、设置、退出”。
- 主窗口不在任务栏长期占位。
- 支持开机自动启动，默认关闭，由用户自行开启。

建议面板尺寸约 `420 × 560`，根据系统缩放自适应；四个平台尽量不滚动即可看完。

### 5.2 视觉风格

设计关键词：

> 明亮、克制、精致、原生、轻量、信息一眼可读。

- Windows 11 Fluent 风格。
- 浅色模式优先，同时适配深色模式和系统主题。
- 使用圆角面板、轻微层次阴影和柔和边框。
- 背景保持干净，不使用大面积高饱和渐变。
- 四个平台使用各自品牌识别色作为小面积强调，不把整张卡染色。
- 字体使用 Segoe UI Variable，数字采用便于比较的排版。
- 所有内容支持 125%、150%、200% DPI。
- 颜色不能成为唯一状态表达，必须同时有文字或图标。

### 5.3 顶部区域

```text
QuotaFlow                        [刷新]
AI 额度状态                     刚刚更新
```

- 左侧为产品名和简短说明。
- 右侧为统一刷新按钮。
- 刷新时按钮旋转，但界面继续显示旧缓存，不出现整页空白。

### 5.4 平台卡片

固定顺序：

1. Claude
2. Codex
3. MiniMax
4. DeepSeek

Claude/Codex/MiniMax 卡片参考：

```text
┌────────────────────────────────────────┐
│ [Logo] Claude                 正常  ··· │
│                                        │
│ 5 小时      100%      进度条           │
│                       2小时20分后重置   │
│ 7 天         32%      进度条           │
│                       3天6小时后重置    │
│                                        │
│ 5 分钟前更新                        ↻  │
└────────────────────────────────────────┘
```

DeepSeek 卡片参考：

```text
┌────────────────────────────────────────┐
│ [Logo] DeepSeek               正常  ··· │
│                                        │
│              ¥ 48.91                   │
│               可用余额                 │
│                                        │
│ 5 分钟前更新                        ↻  │
└────────────────────────────────────────┘
```

视觉优先级：

1. 剩余百分比或余额数字；
2. 5 小时/7 天窗口名称；
3. 重置倒计时；
4. 更新时间和刷新按钮。

进度颜色规则：

- 剩余 `> 30%`：品牌强调色或绿色；
- 剩余 `10%～30%`：橙色；
- 剩余 `< 10%`：红色；
- 未知：灰色占位并显示原因；
- 不要用红色表示正常的“已使用”部分。

默认只显示核心指标。点击卡片可展开服务器返回的其他窗口或诊断信息，但不要跳转到复杂详情页。

### 5.5 设置页

设置只保留必要内容：

- 自动刷新间隔：5/10/15/30 分钟，默认 5 分钟；
- 启动时刷新；
- 开机启动；
- 跟随系统/浅色/深色；
- MiniMax 所需凭据；
- DeepSeek API Key；
- Claude、Codex 本机登录状态检测；
- 清除缓存和安全凭据；
- 关于与开源许可。

Claude 和 Codex 设置项显示“已检测到本机登录”或“未检测到，请先运行对应 CLI 登录”，不提供密码输入框。

---

## 6. 刷新与缓存

- 应用启动后先在 100ms 内显示本地缓存，再后台刷新。
- 默认每 5 分钟刷新一次。
- 支持全部刷新和单卡刷新。
- 四个平台并行请求，但每个平台内部避免并发重复请求。
- 设置合理超时、取消和退避重试，避免快速刷新触发限流。
- 刷新失败时保留最后一次成功数据，并同时显示错误状态。
- 倒计时在本地按秒/分钟更新，不要为了倒计时反复请求服务器。
- 缓存只保存展示数据，不保存 OAuth Token 或 API Key。

---

## 7. 安全要求

这是硬性要求：

1. 不保存 Claude、OpenAI、MiniMax 或 DeepSeek 的登录密码。
2. 不把 OAuth Token、API Key、Authorization Header 写入日志。
3. 不把密钥写入 `appsettings.json`、普通配置文件、源码或测试数据。
4. 日志中的请求 URL应去除敏感查询参数。
5. HTTP 解码失败时不要原样记录完整响应体。
6. 错误对象和 UI 不得包含 Token、Cookie 或 Authorization 内容。
7. 发布脚本必须排除 `APIKeys.txt`、`*.local.*`、日志、缓存和本地凭据。
8. 不上传本地 Claude/Codex 登录凭据到任何自建服务器；所有查询由本机直接请求对应官方服务。
9. 对读取本地 OAuth 凭据的路径、权限、用途和风险写入 README。

---

## 8. 本轮不做

以下功能全部排除，防止项目膨胀：

- 多模型聊天
- API 调用测试
- 模型切换
- Claude Code/Codex Provider 路由
- 本地代理或反向代理
- 账号批量轮换
- Token 成本分析
- 历史趋势图表
- iOS、Android、macOS 版本
- 云同步
- Widget
- 团队共享
- 自动充值
- 网页抓取和截图 OCR

如果实现四个平台查询必须共用少量认证/HTTP 基础设施，可以实现，但不得扩展成通用 Provider 管理器。

---

## 9. 实施顺序

严格按以下顺序推进：

### 阶段 A：真实查询 POC

1. Claude 本机 OAuth 检测与额度查询。
2. Codex 本机 OAuth 检测与额度查询。
3. MiniMax 额度查询。
4. DeepSeek 余额查询。
5. 为真实响应建立脱敏 fixture。

若某个平台失败，不要伪造成功；定位原因并提供清晰降级状态，同时继续完成其他平台。

### 阶段 B：工程化

1. Provider 接口与统一数据模型。
2. 安全凭据存储。
3. 缓存、刷新协调和错误分类。
4. 单元测试。

### 阶段 C：托盘与漂亮 UI

1. 托盘生命周期。
2. 四张平台卡片。
3. 进度条、倒计时、加载和异常状态。
4. 设置页。
5. 浅色、深色和 DPI 适配。

### 阶段 D：验证与发布

1. Debug/Release 构建。
2. Windows 11 真机运行验证。
3. 打包为可直接运行或安装的 x64 版本。
4. 检查发布目录不包含任何凭据和缓存。

---

## 10. 测试要求

至少覆盖：

- Claude `utilization` 到剩余百分比转换；
- Claude 缺少某个额度窗口时正确隐藏；
- Codex 5 小时/7 天窗口映射；
- MiniMax 窗口字段映射与重置时间；
- DeepSeek 多余额字段合计/选取逻辑；
- 401、403、429、超时和格式变化；
- OAuth 过期；
- 单个平台失败不影响其他平台；
- 缓存先显示、后台刷新；
- 旧数据 `Stale` 标记；
- 错误和日志中不出现 Authorization、Bearer、API Key；
- 剩余百分比始终限制在 `0...100`；
- 时区和重置倒计时正确；
- 重复点击刷新不会产生请求风暴。

不要使用真实 Token 写测试；所有 fixture 必须脱敏。

---

## 11. 验收标准

满足以下条件才算完成：

1. Windows 11 登录后可启动并常驻系统托盘。
2. 单击托盘图标立即显示面板。
3. 已配置并登录的四个平台能显示真实额度/余额。
4. Claude、Codex、MiniMax 正确显示当前存在的额度窗口、剩余比例和重置时间。
5. DeepSeek 正确显示余额与币种。
6. 数据来源、更新时间和异常状态表达清晰。
7. 无数据时不显示为 0。
8. 四个平台任意一个失败不会拖垮整个面板。
9. 不保存账号密码，发布物中不包含密钥、OAuth Token、缓存或敏感日志。
10. UI 在浅色、深色、125% 和 150% 缩放下没有裁切、重叠和模糊。
11. Release 构建通过，核心测试全部通过。
12. 提供可运行的 Windows x64 发布包。

---

## 12. 完成后输出报告

完成后请输出：

1. 项目目录与技术栈；
2. 四个平台分别采用的真实查询路径；
3. Claude/Codex 本机 OAuth 凭据来自哪里、如何安全读取（不要输出真实值）；
4. MiniMax 和 DeepSeek 需要用户配置什么；
5. 各平台实际支持的字段及无法支持的字段；
6. UI 页面和托盘交互说明；
7. 缓存、刷新和错误分类设计；
8. 安全检查结果；
9. 测试数量与通过结果；
10. Release 构建和安装包路径；
11. 已知风险，尤其是非公开 OAuth 用量接口未来变化的兼容策略；
12. 下一步只列真正必要的改进，不主动扩展产品范围。

开发过程中请自主完成检查、实现、测试和修复。只有遇到必须由我提供的 MiniMax/DeepSeek 凭据或无法从现有环境判断的关键选择时才询问；不得为了展示效果伪造额度数据。
