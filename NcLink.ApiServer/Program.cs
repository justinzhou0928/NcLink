using NcLink.Extensions;
using NcLink.ApiServer.Operations;
using NcLink.ApiServer.Models;
using NcLink;
using NcLink.Transport.Mqtt;

// NcLink.ApiServer —— 官方 apiServer 的 C# 可替换实现。
// 本进程不含协议逻辑，只做 HTTP 编解码与采样缓存；契约见 docs/api-server.md。

var builder = WebApplication.CreateBuilder(args);

var options = new ApiServerOptions();
builder.Configuration.GetSection(ApiServerOptions.SectionName).Bind(options);

// 允许用命令行/环境变量覆盖关键项，便于并列跑多个实例做对比
builder.WebHost.UseUrls($"http://0.0.0.0:{options.Port}");

builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));

// 传输与客户端：走本项目自己的 DI 扩展
builder.Services.AddNcLinkMqtt(o => o.BrokerAddress = options.BrokerAddress);
builder.Services.AddNcLink(o => o.RequestTimeout = TimeSpan.FromMilliseconds(options.DefaultRequestTimeoutMs));

// 服务端特有：采样缓存
builder.Services.AddSingleton<NcLink.ApiServer.Operations.ISampleCache,
    NcLink.ApiServer.Operations.SampleCache>();

// 控制器 + v1/v2 共用的实现（控制器保持薄，逻辑在服务里）
builder.Services.AddControllers();

// [ApiController] 默认把模型绑定失败变成 ValidationProblemDetails，那会破坏
// 官方那套 Spring 形状的 400 错误体。关掉它，400 由控制器自己给（ApiError.BadRequest）。
builder.Services.Configure<Microsoft.AspNetCore.Mvc.ApiBehaviorOptions>(o =>
    o.SuppressModelStateInvalidFilter = true);

// 响应形状必须与官方逐字段一致，因此关掉命名策略、保留显式 JsonPropertyName
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = null;
    o.SerializerOptions.DefaultIgnoreCondition =
        System.Text.Json.Serialization.JsonIgnoreCondition.Never;
    // 官方 JSON 里中文是原样的，默认编码器会写成 \uXXXX 而与之不同
    o.SerializerOptions.Encoder =
        System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
});

// 控制器走的是 MVC 的 JSON 选项，与上面 ConfigureHttpJsonOptions 是两套，
// 必须同样关掉命名策略并保留中文，否则控制器返回的 JSON 会与 v1 不一致。
builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.PropertyNamingPolicy = null;
    o.JsonSerializerOptions.DefaultIgnoreCondition =
        System.Text.Json.Serialization.JsonIgnoreCondition.Never;
    o.JsonSerializerOptions.Encoder =
        System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
});

// API 文档：由内置生成器从真实端点自动生成 OpenAPI 3。
builder.Services.AddOpenApi("v1", o =>
{
    o.AddDocumentTransformer<NcLink.ApiServer.ApiDocs.ApiDocumentTransformer>();
    o.AddOperationTransformer<NcLink.ApiServer.ApiDocs.MvcQuirksOperationTransformer>();
});

var app = builder.Build();

// 绑定失败（缺必填的表单/查询参数、请求体解析不了）时 Minimal API 会抛
// BadHttpRequestException，并直接吐出纯文本异常体。官方返回的是 Spring 形状的
// JSON 错误体，因此在这里统一收敛——形参才敢声明成非可空（文档里也就如实标必填）。
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (BadHttpRequestException e) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = e.StatusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(ApiError.Create(
            e.StatusCode,
            Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(e.StatusCode),
            context.Request.Path));
    }
});

// 官方对 404/405/415 这类空体错误返回 Spring 默认错误 JSON，不是空响应体。
// 实测 PUT/DELETE /v1/{sn}/data/ → 405 带体；GET /v1/sample/{sn}/data/ → 404 带体；
// 非 multipart 打上传端点 → 415 带体。
app.UseStatusCodePages(async context =>
{
    var response = context.HttpContext.Response;
    var status = response.StatusCode;

    // 用 BCL 的标准原因短语，而不是手写表——Spring 用的就是这些标准串。
    // 曾漏掉 415 而落到兜底的 "Error"，与官方的 "Unsupported Media Type" 不同。
    var error = Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status);

    response.ContentType = "application/json; charset=utf-8";
    await response.WriteAsJsonAsync(ApiError.Create(status, error, context.HttpContext.Request.Path));
});

// API 文档：MapOpenApi 一行就够（官方脚手架就是这么写的），
// Swagger UI 指过去即可。
app.MapOpenApi();

// 默认开（官方就无条件开），但生产建议关掉：微软文档把"不暴露交互式 UI"
// 列为安全最佳实践。关掉不影响 /openapi/v1.json 本身。
if (options.SwaggerUiEnabled)
    app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "NcLink ApiServer"));

// 路由由控制器上的 [Route] 声明，与厂家结构一一对应（对照表见 AGENTS.md）
app.MapControllers();

// 便于运维与对比验证
app.MapGet("/", () => Results.Text(
    "NcLink.ApiServer —— 官方 apiServer 的 C# 可替换实现\n"
    + "  GET/POST /v1/{device_id}/data/**\n"
    + "  GET/POST /v1/{device_id}/file/**\n"
    + "  GET/POST /v1/{device_id}/model\n"
    + "  POST     /v1/sample/{device_id}/sub/{channel_id}\n"
    + "  GET      /v1/sample/{device_id}/data/{sub_id}\n"
    + "  POST     /v1/sample/{device_id}/unsub/{sub_id}\n"
    + "  GET/POST /v2/{device_id}/data/**   （复刻官方 v2 的退化行为，见 docs/api-server.md）\n"
    + "  GET/POST /v2/{device_id}/file/**\n"
    + "  GET      /v2/{device_id}/model\n"
    + "  GET      /openapi/v1.json    （OpenAPI 3 规范）\n"
    + "  GET      /swagger            （可视化界面）\n",
    "text/plain; charset=utf-8"))
    .ExcludeFromDescription();

var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("NcLink.ApiServer");
NcLink.ApiServer.Logging.ApiServerLog.LogStarted(logger, $"http://0.0.0.0:{options.Port}", options.BrokerAddress);

await app.RunAsync();
