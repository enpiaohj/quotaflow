using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Services;

namespace QuotaFlow.Windows.Tests.Services;

/// <summary>
/// 跨设备迁移备份包的测试。
///
/// 这条路径同时承载两件敏感的事：把 API Key 写进文件，以及用文件内容覆盖用户已有配置。
/// 因此重点验证「口令错了必须干净地失败」——绝不能解出垃圾数据再去覆盖配置。
/// </summary>
public class SettingsBackupTests
{
    private static BackupPayload SamplePayload() => new()
    {
        Settings = new AppSettings
        {
            AutoRefreshIntervalMinutes = 10,
            Theme = ThemeMode.Dark,
            HiddenPlatforms = ["minimax"],
            CustomPlatforms =
            [
                new CustomPlatformSettings { Id = "custom-1", Name = "OpenCode GO", Endpoint = "https://example.test/q" },
            ],
        },
        Secrets = new Dictionary<string, string>
        {
            ["minimax:ApiKey"] = "mm-secret-value",
            ["deepseek:ApiKey"] = "ds-secret-value",
        },
    };

    [Fact]
    public void ExportThenImport_WithSamePassword_RoundTripsSettingsAndSecrets()
    {
        var exported = SettingsBackup.Export(SamplePayload(), "correct horse battery staple");

        var result = SettingsBackup.Import(exported, "correct horse battery staple");

        Assert.True(result.Succeeded);
        Assert.Equal(10, result.Payload!.Settings.AutoRefreshIntervalMinutes);
        Assert.Equal(ThemeMode.Dark, result.Payload.Settings.Theme);
        Assert.Equal(["minimax"], result.Payload.Settings.HiddenPlatforms);
        Assert.Equal("OpenCode GO", result.Payload.Settings.CustomPlatforms[0].Name);
        Assert.Equal("mm-secret-value", result.Payload.Secrets["minimax:ApiKey"]);
        Assert.Equal("ds-secret-value", result.Payload.Secrets["deepseek:ApiKey"]);
    }

    [Fact]
    public void Import_WrongPassword_FailsCleanlyWithoutReturningData()
    {
        var exported = SettingsBackup.Export(SamplePayload(), "right-password");

        var result = SettingsBackup.Import(exported, "wrong-password");

        Assert.False(result.Succeeded);
        Assert.Null(result.Payload);
        Assert.Equal(BackupImportFailure.WrongPasswordOrCorrupted, result.Failure);
    }

    [Fact]
    public void Import_TamperedCiphertext_IsRejectedByAuthenticationTag()
    {
        // AES-GCM 是 AEAD：内容被改过必须被标签校验挡下，而不是解出一堆垃圾再去覆盖配置。
        var exported = SettingsBackup.Export(SamplePayload(), "pw");
        var tampered = exported.Replace("\"ciphertext\": \"", "\"ciphertext\": \"A", StringComparison.Ordinal);

        var result = SettingsBackup.Import(tampered, "pw");

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Import_NotABackupFile_ReportsDistinctFailure()
    {
        // 让界面能提示"这不是备份文件"，而不是笼统地说密码错了。
        var result = SettingsBackup.Import("{\"hello\":\"world\"}", "pw");

        Assert.Equal(BackupImportFailure.NotABackupFile, result.Failure);
    }

    [Fact]
    public void Import_NotEvenJson_ReportsNotABackupFile()
    {
        var result = SettingsBackup.Import("这不是 JSON", "pw");

        Assert.Equal(BackupImportFailure.NotABackupFile, result.Failure);
    }

    [Fact]
    public void Import_NewerFormatVersion_ReportsUnsupportedVersion()
    {
        var exported = SettingsBackup.Export(SamplePayload(), "pw");
        var future = exported.Replace("\"version\": 1", "\"version\": 99", StringComparison.Ordinal);

        var result = SettingsBackup.Import(future, "pw");

        Assert.Equal(BackupImportFailure.UnsupportedVersion, result.Failure);
    }

    [Fact]
    public void Export_DoesNotLeakSecretsInPlaintext()
    {
        // 导出文件里绝不能出现密钥明文——这是整套设计成立的前提。
        var exported = SettingsBackup.Export(SamplePayload(), "pw");

        Assert.DoesNotContain("mm-secret-value", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("ds-secret-value", exported, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_SamePayloadTwice_ProducesDifferentCiphertext()
    {
        // 每次导出都用新的随机盐与 nonce；否则相同内容会产生相同密文，泄露"配置没变过"这类信息，
        // 更严重的是 GCM 在同一密钥下重用 nonce 会直接破坏其安全性。
        var payload = SamplePayload();

        var first = SettingsBackup.Export(payload, "pw");
        var second = SettingsBackup.Export(payload, "pw");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Export_EmptyPassword_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => SettingsBackup.Export(SamplePayload(), string.Empty));
    }

    [Fact]
    public void ExportThenImport_NoSecrets_StillWorks()
    {
        var payload = new BackupPayload { Settings = new AppSettings { AutoRefreshIntervalMinutes = 3 } };

        var result = SettingsBackup.Import(SettingsBackup.Export(payload, "pw"), "pw");

        Assert.True(result.Succeeded);
        Assert.Empty(result.Payload!.Secrets);
        Assert.Equal(3, result.Payload.Settings.AutoRefreshIntervalMinutes);
    }
}
