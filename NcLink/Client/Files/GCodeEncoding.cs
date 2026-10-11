using System.Text;

namespace NcLink.Client.Files;

/// <summary>
/// G 代码文本编码。
///
/// 协议要求：数控系统 G 代码默认编码为 <b>GB2312</b>；含中文的程序若用其它编码下发会乱码，
/// 从设备取回的字节也须按 GB2312 解码。
///
/// 注意：.NET Core 起不自带 GB2312，必须注册 <see cref="CodePagesEncodingProvider"/>。
/// 这里在静态构造中只注册一次。
/// </summary>
internal static class GCodeEncoding
{
    private const string DefaultEncodingName = "GB2312";

    private static readonly Encoding Instance = CreateEncoding();

    private static Encoding CreateEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(DefaultEncodingName);
    }

    /// <summary>按 GB2312 解码 G 代码字节。</summary>
    public static string Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Instance.GetString(bytes);
    }

    /// <summary>按 GB2312 编码 G 代码文本。</summary>
    public static byte[] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Instance.GetBytes(text);
    }

    /// <summary>取该文本按 GB2312 编码后的字节数。</summary>
    public static int GetByteCount(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Instance.GetByteCount(text);
    }
}
