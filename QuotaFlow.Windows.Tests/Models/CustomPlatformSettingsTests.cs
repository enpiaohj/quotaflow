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
        var settings = new CustomPlatformSettings { Id = "custom-1", Name = "OpenCode" };

        Assert.Equal(CustomAuthKind.BearerKey, settings.AuthKind);
        Assert.Equal(CustomDataKind.UtilizationPercent, settings.DataKind);
        Assert.Equal("CNY", settings.Currency);
        Assert.Null(settings.HeaderName);
        Assert.Null(settings.ResetsAtPath);
        Assert.Equal("", settings.ValuePath);
    }
}
