using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Providers;

namespace QuotaFlow.Windows.Tests.Providers;

public class DeepSeekBalanceProviderTests
{
    private static DeepSeekBalanceProvider CreateProvider() =>
        new(new HttpClient(), () => "fixture-key-not-real");

    [Fact]
    public void ParseResponse_ParsesBalanceAndCurrency()
    {
        var json = """
            {
              "is_available": true,
              "balance_infos": [
                { "currency": "CNY", "total_balance": "48.91", "granted_balance": "0.00", "topped_up_balance": "48.91" }
              ]
            }
            """;

        var snapshot = CreateProvider().ParseResponse(json);

        Assert.Equal(ProviderState.Available, snapshot.State);
        Assert.NotNull(snapshot.Balance);
        Assert.Equal(48.91m, snapshot.Balance!.Amount);
        Assert.Equal("CNY", snapshot.Balance.Currency);
        Assert.Equal(0.00m, snapshot.Balance.GrantedAmount);
        Assert.Equal(48.91m, snapshot.Balance.ToppedUpAmount);
    }

    [Fact]
    public void ParseResponse_NumericBalanceField_IsAlsoAccepted()
    {
        // 服务端可能返回数字而不是字符串，两种都要兼容。
        var json = """
            { "is_available": true, "balance_infos": [ { "currency": "USD", "total_balance": 12.5 } ] }
            """;

        var snapshot = CreateProvider().ParseResponse(json);

        Assert.Equal(12.5m, snapshot.Balance!.Amount);
    }

    [Fact]
    public void ParseResponse_NotAvailable_ReturnsCriticalWithGuidance()
    {
        var json = """
            { "is_available": false, "balance_infos": [ { "currency": "CNY", "total_balance": "0.00" } ] }
            """;

        var snapshot = CreateProvider().ParseResponse(json);

        Assert.Equal(ProviderState.Critical, snapshot.State);
        Assert.NotNull(snapshot.UserGuidance);
    }

    [Fact]
    public void ParseResponse_EmptyBalanceInfos_ReturnsProviderError()
    {
        var json = """{ "is_available": true, "balance_infos": [] }""";

        var snapshot = CreateProvider().ParseResponse(json);

        Assert.Equal(ProviderState.ProviderError, snapshot.State);
        Assert.Equal(ErrorCategory.ResponseFormat, snapshot.ErrorCategory);
    }

    [Fact]
    public async Task GetSnapshotAsync_NoApiKey_ReturnsNotConfigured()
    {
        var provider = new DeepSeekBalanceProvider(new HttpClient(), () => null);

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal(ProviderState.NotConfigured, snapshot.State);
    }
}
