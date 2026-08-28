using QuotaFlow.Windows.Core.Models;

namespace QuotaFlow.Windows.Tests.Models;

public class CustomPlatformSettingsTests
{
    [Fact]
    public void CredentialKeyName_DerivesFromFixedId()
    {
        var settings = new CustomPlatformSettings { Id = "custom-3" };

        // 凭据键名由固定 Id 派生：改名只改 Name，Id 稳定则凭据稳定。
        Assert.Equal("custom:custom-3:ApiKey", settings.CredentialKeyName);
    }

    [Fact]
    public void Defaults_AreSensible()
    {
        var settings = new CustomPlatformSettings { Id = "custom-1", Name = "OpenCode GO" };

        Assert.Equal(CustomAuthKind.BearerKey, settings.AuthKind);
        Assert.Null(settings.HeaderName);
        Assert.Empty(settings.QuotaWindows);

        // 旧字段（v1.0.5 单窗口格式）只用于迁移读取，新建实例上应为空/null。
        Assert.Null(settings.ValuePath);
        Assert.Null(settings.ResetsAtPath);
        Assert.Null(settings.DataKind);
        Assert.Null(settings.Currency);
    }

    [Fact]
    public void QuotaWindowSettings_Defaults_AreSensible()
    {
        var window = new CustomQuotaWindowSettings { Id = "window-1", Name = "5 小时" };

        Assert.Equal(CustomDataKind.UtilizationPercent, window.DataKind);
        Assert.Equal(CustomResetTimeKind.Auto, window.ResetTimeKind);
        Assert.Null(window.ResetsAtPath);
        Assert.Null(window.LimitPath);
        Assert.Null(window.FixedLimit);
        Assert.Null(window.Unit);
        Assert.Equal(0, window.SortOrder);
        Assert.Equal("", window.ValuePath);
    }
}
