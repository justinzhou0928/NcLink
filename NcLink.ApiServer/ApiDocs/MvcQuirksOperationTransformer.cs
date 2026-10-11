using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace NcLink.ApiServer.ApiDocs;

/// <summary>
/// 修正 MVC 生成 OpenAPI 时与官方契约不符的两处表现。
///
/// <b>为什么需要它</b>：Minimal API 与 MVC 对同样的路由/参数会生成不同的文档，
/// 而官方契约（Springfox 的 Swagger 2.0）有它自己的形状。这两处属于
/// "框架产物 vs 契约"的差异，只能在这里收口：
///
/// <list type="number">
/// <item><b>catch-all 的 <c>rest</c> 路径参数</b>。MVC 会把
///   <c>{**rest}</c> 生成成一个<b>必填路径参数</b>，而它只是"吃掉剩余路径"的语法，
///   不是调用方要填的参数——官方文档里也没有它。</item>
/// <item><b>multipart 上传的请求体</b>。MVC 把 <c>[FromForm] IFormFile</c> 展开成了
///   <c>IFormFile</c> 自身的属性（<c>ContentType</c>/<c>Headers</c>/<c>Length</c>…），
///   内容类型还写成 <c>application/x-www-form-urlencoded</c>。
///   真实契约是三个表单字段：<c>path</c> / <c>key</c> / <c>value</c>（文件）。</item>
/// </list>
///
/// 两处判据都只看<b>路由与 HTTP 方法</b>，不含端点清单，因此不会漂移。
/// </summary>
internal sealed class MvcQuirksOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var route = "/" + (context.Description.RelativePath ?? string.Empty).TrimStart('/');
        var isPost = string.Equals(
            context.Description.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase);

        RemoveCatchAllParameter(operation);

        if (isPost && route.Contains("/file", StringComparison.Ordinal))
            FixUploadRequest(operation);

        return Task.CompletedTask;
    }

    /// <summary><c>{**rest}</c> 只负责匹配剩余路径，不是调用方要填的参数。</summary>
    private static void RemoveCatchAllParameter(OpenApiOperation operation)
    {
        var parameters = operation.Parameters;
        if (parameters is null) return;

        for (var i = parameters.Count - 1; i >= 0; i--)
            if (parameters[i] is { In: ParameterLocation.Path, Name: "rest" })
                parameters.RemoveAt(i);
    }

    /// <summary>把 MVC 展开坏了的 multipart 请求体还原成三个表单字段。</summary>
    private static void FixUploadRequest(OpenApiOperation operation)
    {
        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["multipart/form-data"] = new OpenApiMediaType
                {
                    Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Object,
                        Required = new HashSet<string> { "path", "key", "value" },
                        Properties = new Dictionary<string, IOpenApiSchema>
                        {
                            ["path"] = new OpenApiSchema { Type = JsonSchemaType.String },
                            ["key"] = new OpenApiSchema { Type = JsonSchemaType.String },
                            ["value"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" }
                        }
                    }
                }
            }
        };
    }
}
