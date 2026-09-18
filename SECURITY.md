# 安全策略

## 报告安全漏洞

如果你发现了 QuotaFlow 的安全漏洞，请**不要**在公开 Issue 中披露细节。

优先使用 GitHub 的私密漏洞报告渠道：

1. 打开仓库 `enpiaohj/quotaflow` 的 **Security** 标签页；
2. 选择 **Report a vulnerability**（私密安全建议，Private Vulnerability Reporting）；
3. 描述问题、复现步骤和影响范围。

如果无法使用该渠道，也可以通过仓库主页显示的作者联系方式私下联系。

## 处理承诺

- 收到报告后尽快（通常 72 小时内）确认，并在修复期间与报告者保持沟通。
- 修复发布后，在对应版本的 `CHANGELOG.md` 中如实记录（不包含可被利用的细节）。

## 范围说明

QuotaFlow 是本地运行的应用，安全设计要点（也是审计时的重点）：

- 所有密钥（API Key、Console Cookie、SEC_TOKEN、AK/SK）只存 Windows 凭据管理器，
  绝不落盘、不写日志、不进配置文件；
- 配置以 DPAPI（当前用户）加密信封落盘；
- 唯一的对外请求是向各 AI 平台官方额度/余额接口查询数据，无遥测。

不在范围内：本机已被完整入侵场景下的凭据保护（DPAPI 与凭据管理器以当前 Windows
用户为信任边界）；用户主动导出的 `*.qfbackup` 备份包在口令强度不足时被爆破的风险
（界面已明确提示）。
