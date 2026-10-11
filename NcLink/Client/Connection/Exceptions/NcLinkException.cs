namespace NcLink.Client.Connection.Exceptions;

/// <summary>
/// NC-Link 所有异常的共同基类。
///
/// 分类约定：
/// <list type="bullet">
/// <item><see cref="NcLinkRequestException"/> —— 请求本身有问题（超时、路径不存在、参数非法）</item>
/// <item><see cref="NcLinkDeviceException"/> —— 设备或连接侧的问题（设备不可用、设备返回失败、未连接）</item>
/// </list>
/// 调用方通常只需 <c>catch (NcLinkException e)</c>；需要区分时再按子类分支。
/// </summary>
public abstract class NcLinkException : Exception
{
    protected NcLinkException(string message) : base(message)
    {
    }

    protected NcLinkException(string message, Exception? innerException) : base(message, innerException)
    {
    }

    /// <summary>相关设备 SN（与该设备无关时为 null）。</summary>
    public string? DeviceId { get; init; }

    /// <summary>相关主题（与主题无关时为 null）。</summary>
    public string? Topic { get; init; }
}

#region 请求侧

/// <summary>请求侧问题：请求未被满足，但连接与设备本身正常。</summary>
public abstract class NcLinkRequestException : NcLinkException
{
    protected NcLinkRequestException(string message) : base(message)
    {
    }

    protected NcLinkRequestException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}

/// <summary>等待设备响应超时（设备在线，但本次没有回应）。</summary>
public sealed class NcLinkTimeoutException : NcLinkRequestException
{
    public NcLinkTimeoutException(string topic, TimeSpan timeout, string? deviceId = null)
        : base($"等待设备响应超时（{timeout.TotalMilliseconds:0} ms）：{topic}")
    {
        Timeout = timeout;
        Topic = topic;
        DeviceId = deviceId;
    }

    /// <summary>本次使用的超时时长。</summary>
    public TimeSpan Timeout { get; }
}

/// <summary>模型里找不到该路径／标识（调用方给错了地址）。</summary>
public sealed class NcLinkModelPathNotFoundException : NcLinkRequestException
{
    public NcLinkModelPathNotFoundException(string path, string deviceId)
        : base($"设备 {deviceId} 的模型中不存在该路径：{path}")
    {
        Path = path;
        DeviceId = deviceId;
    }

    public string Path { get; }
}

/// <summary>请求参数非法（在发出前就被拦下）。</summary>
public sealed class NcLinkInvalidRequestException : NcLinkRequestException
{
    public NcLinkInvalidRequestException(string message)
        : base(message)
    {
    }
}

#endregion

#region 设备侧

/// <summary>设备侧或连接侧问题。</summary>
public abstract class NcLinkDeviceException : NcLinkException
{
    protected NcLinkDeviceException(string message) : base(message)
    {
    }

    protected NcLinkDeviceException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}

/// <summary>设备从未在本会话出现过：SN 错误，或设备未接入 Broker。</summary>
public sealed class NcLinkDeviceUnavailableException : NcLinkDeviceException
{
    public NcLinkDeviceUnavailableException(string deviceId)
        : base($"设备未连接到 MQTT Broker 或 SN 码错误：{deviceId}")
    {
        DeviceId = deviceId;
    }
}

/// <summary>设备受理了请求但返回失败（<c>NG</c>）。</summary>
public sealed class NcLinkDeviceRejectedException : NcLinkDeviceException
{
    public NcLinkDeviceRejectedException(string deviceId, string? itemId, string? reason)
        : base($"设备 {deviceId} 拒绝了操作"
               + (string.IsNullOrWhiteSpace(itemId) ? string.Empty : $"（数据项 {itemId}）")
               + (string.IsNullOrWhiteSpace(reason) ? string.Empty : $"，原因：{reason}"))
    {
        DeviceId = deviceId;
        ItemId = itemId;
        Reason = reason;
    }

    /// <summary>被拒绝的数据项 id。</summary>
    public string? ItemId { get; }

    /// <summary>设备给出的原因。</summary>
    public string? Reason { get; }
}

/// <summary>传输未连接或连接已断开。</summary>
public sealed class NcLinkNotConnectedException : NcLinkDeviceException
{
    public NcLinkNotConnectedException(string endpoint)
        : base($"尚未连接到 MQTT Broker：{endpoint}")
    {
        Endpoint = endpoint;
    }

    public string Endpoint { get; }
}

/// <summary>响应无法解析（协议不符或链路脏数据）。</summary>
public sealed class NcLinkProtocolException : NcLinkDeviceException
{
    public NcLinkProtocolException(string message, string? topic = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Topic = topic;
    }
}

#endregion
