using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace NcLink.ApiServer.Engine.ApiDocs;

/// <summary>
/// 补上生成器推不出来的文档级信息：标题与分组标签。
///
/// 这里**只放端点自己推不出来的东西**。路径、参数、请求体、响应 schema 全部由
/// 内置生成器从真实端点反射得到；端点一改，文档自动跟着变。
///
/// 刻意<b>不</b>复刻官方 Springfox 输出的这些内容：
/// <list type="bullet">
/// <item>在 data 端点上声明的 6 个查询参数（<c>index</c>/<c>key</c>/<c>length</c>/
///   <c>offset</c>/<c>operation</c>/<c>value</c>）——<b>实测官方实现根本不读它们</b>
///   （query 与 body 冲突时以 body 为准）。声明一个不生效的参数是误导。</item>
/// <item>统一的 401/403/404 响应——我们并不返回它们，照抄就是假声明。</item>
/// <item><c>securityDefinitions</c> 里的 <c>Authorization</c>——官方声明了但实测不校验，
///   我们也不校验；声明一个不生效的授权方案同样是误导。</item>
/// </list>
/// 文档格式（Swagger 2.0 vs OpenAPI 3）<b>不需要与官方一致</b>——它是给人看的，
/// 不是线上契约。要一致的是"怎么调用"，那由行为面比对保证。
/// </summary>
internal sealed class ApiDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Info ??= new OpenApiInfo();
        document.Info.Title = "NcLink ApiServer RESTful APIs";
        document.Info.Version = "1.0";
        document.Info.Description =
            "官方 apiServer 的可替换实现。线上契约与官方一致（见 docs/api-server.md）。";

        document.Tags = new HashSet<OpenApiTag>
        {
            new() { Name = "v1 数据接口", Description = "Data Api" },
            new() { Name = "v2 数据接口", Description = "Data Api 2" }
        };

        return Task.CompletedTask;
    }
}
