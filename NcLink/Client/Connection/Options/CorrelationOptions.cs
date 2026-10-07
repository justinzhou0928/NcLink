namespace NcLink.Client.Connection.Options;

/// <summary>
/// 请求-响应关联的语义设置。
///
/// <b>超时不在这里</b>——请求超时的唯一来源是
/// <see cref="NcLinkClientOptions.RequestTimeout"/>，关联器构造时由管线读入
/// （单次调用可用 <c>timeout</c> 参数覆盖）。
/// 两个类各写一份超时会导致"改了不生效"的困惑，因此只保留一处。
/// </summary>
public sealed class CorrelationOptions
{
    /// <summary>
    /// 设备在线状态记录的有效期。
    ///
    /// 用来区分两种失败语义：
    /// <list type="bullet">
    /// <item>窗口<b>内</b>见过该设备 → 报"超时"（设备在线但本次没回应）</item>
    /// <item>窗口<b>外</b>从未见过 → 报"设备不可用"（SN 错误或设备未接入）</item>
    /// </list>
    /// </summary>
    public TimeSpan ActivityWindow { get; set; } = TimeSpan.FromMinutes(5);
}
