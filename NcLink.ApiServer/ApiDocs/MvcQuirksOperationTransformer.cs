using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace NcLink.ApiServer.ApiDocs;

/// <summary>
/// 修正 MVC 生成 OpenAPI 时与官方契约不符的表现。
///
/// <b>为什么需要它</b>：MVC 对同样的路由/参数会生成与官方契约
/// （Springfox 的 Swagger 2.0）不同形状的文档，这属于"框架产物 vs 契约"
/// 的差异，只能在这里收口。
///
/// <b>multipart 上传的请求体</b>：MVC 把 <c>[FromForm] IFormFile</c> 展开成了
/// <c>IFormFile</c> 自身的属性（<c>ContentType</c>/<c>Headers</c>/<c>Length</c>…），
/// 内容类型还写成 <c>application/x-www-form-urlencoded</c>。
/// 真实契约是三个表单字段：<c>path</c> / <c>key</c> / <c>value</c>（文件）。
/// <b>实测必要</b>：摘掉这个转换器，对比套件在两个上传端点上立刻报不一致。
///
/// 判据只看<b>路由与 HTTP 方法</b>，不含端点清单，因此不会漂移。
///
/// 这里曾经还剥掉 catch-all 生成的 <c>rest</c> 路径参数，已删除：
/// .NET 10 的生成器<b>不再</b>把 <c>{**rest}</c> 生成为参数
/// （实测我们的规范里一处都没有），那段代码成了空操作。
/// 真要回归也不必担心——对比套件会逐字段比参数。
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

        if (isPost && route.Contains("/file", StringComparison.Ordinal))
            FixUploadRequest(operation);

        return Task.CompletedTask;
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
