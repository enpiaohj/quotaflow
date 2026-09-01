using QuotaFlow.Windows.Core.Models;

namespace QuotaFlow.Windows.Core.Services;

/// <summary>
/// 自定义平台定义是否完整可用。
///
/// 校验不通过的定义<b>不写盘</b>，并由界面点名提示是哪一个——静默丢弃会让用户以为保存成功了。
/// 从 <c>SettingsViewModel</c> 下沉到 Core，以便直接测试各条规则。
/// </summary>
public static class CustomPlatformValidator
{
    public static bool IsValid(CustomPlatformSettings def)
    {
        if (def is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(def.Name) ||
            string.IsNullOrWhiteSpace(def.Endpoint) ||
            !Uri.TryCreate(def.Endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        // 选了"自定义请求头"鉴权却没填头名，请求发出去必然失败，等同未配置。
        if (def.AuthKind == CustomAuthKind.CustomHeader && string.IsNullOrWhiteSpace(def.HeaderName))
        {
            return false;
        }

        if (def.QuotaWindows.Count == 0)
        {
            return false;
        }

        var seenWindowNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var window in def.QuotaWindows)
        {
            if (string.IsNullOrWhiteSpace(window.Name) || string.IsNullOrWhiteSpace(window.ValuePath))
            {
                return false;
            }

            if (!seenWindowNames.Add(window.Name))
            {
                return false; // 同一平台内窗口名称重复，面板上无法区分
            }

            // "已使用数值 / 剩余数值"要换算成百分比，必须知道额度上限（路径或固定值二选一）；
            // 缺了它只能算出一个没有意义的数字，宁可不显示也不能显示错的。
            if (window.DataKind is CustomDataKind.UsedValue or CustomDataKind.RemainingValue &&
                string.IsNullOrWhiteSpace(window.LimitPath) && window.FixedLimit is null)
            {
                return false;
            }
        }

        return true;
    }
}
