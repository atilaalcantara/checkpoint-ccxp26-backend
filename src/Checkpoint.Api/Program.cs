using System.Diagnostics;
using System.Text.Json;
using Checkpoint.Api;
using Dapper;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Npgsql;

DefaultTypeMap.MatchNamesWithUnderscores = true;
var builder = WebApplication.CreateBuilder(args);
var imageSettings = builder.Configuration.GetSection("Images").Get<ImageSettings>() ?? new();
builder.Services.AddSingleton(imageSettings);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = imageSettings.MaxFileBytes + 65536L);
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = imageSettings.MaxFileBytes + 65536L);
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.AddSingleton(services => NpgsqlDataSource.Create(Connection(services)));
builder.Services.AddDbContext<MigrationDb>((services, options) => options.UseNpgsql(Connection(services), npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "checkpoint")));
builder.Services.AddSingleton<Store>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddOptions<ProgressionSettings>()
    .BindConfiguration("Progression")
    .Validate(settings => settings.IsValid(), "Progression thresholds must satisfy 0 < FriendsAtXp < DatingAtXp < EngagedAtXp < MarriedAtXp.")
    .ValidateOnStart();
builder.Services.AddSingleton<Progression>();
builder.Services.AddSingleton<ImageProcessor>();
builder.Services.AddSingleton<ArtworkCatalog>();
builder.Services.AddScoped<ParticipationService>();
builder.Services.AddScoped<ResultsService>();
builder.Services.AddCors();
builder.Services.AddOptions<CorsOptions>().Configure<IConfiguration, IWebHostEnvironment>((options, configuration, environment) =>
{
    var origins = configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];
    if (!environment.IsDevelopment() && origins.Contains("*"))
        throw new InvalidOperationException("CORS requires explicit origins outside Development.");
    options.AddDefaultPolicy(policy =>
    {
        if (environment.IsDevelopment()) policy.AllowAnyOrigin();
        else policy.WithOrigins(origins);
        policy.WithMethods("GET", "PUT", "POST")
            .WithHeaders("Content-Type", "X-Visitor-Id")
            .WithExposedHeaders("Retry-After");
    });
});
builder.Services.AddOpenApi(options =>
{
    options.AddOperationTransformer(Documentation.AddHeaders);
});
var app = builder.Build();
var migrateOnly = args.Contains("--migrate-only", StringComparer.Ordinal);
app.Use(async (context, next) =>
{
    try
    {
        if (context.Request.Path.StartsWithSegments("/api/v1") &&
            context.Request.Path != "/api/v1/progress" && context.Request.Path != "/api/v1/photos/recent")
            context.Response.Headers.CacheControl = "no-store";
        await next(context);
    }
    catch (Exception ex) when (!context.Response.HasStarted)
    {
        if (ex is OperationCanceledException && context.RequestAborted.IsCancellationRequested) return;
        var error = ex switch
        {
            ApiException known => known,
            BadHttpRequestException bad when bad.StatusCode == 413 => new ApiException(413, "FILE_TOO_LARGE", "Arquivo maior que o limite permitido."),
            BadHttpRequestException or JsonException or InvalidDataException => new ApiException(400, "VALIDATION_ERROR", "Confira os dados enviados."),
            NpgsqlException or TimeoutException => new ApiException(503, "SERVICE_UNAVAILABLE", "Serviço temporariamente indisponível."),
            _ => new ApiException(500, "INTERNAL_ERROR", "Ocorreu um erro inesperado.")
        };
        // Do not log payloads, visitor identifiers, connection strings or request headers.
        if (error.Status >= 500) app.Logger.LogError("Request failed: {ErrorType}, {Code}, trace {TraceId}", ex.GetType().Name, error.Code, context.TraceIdentifier);
        context.Response.StatusCode = error.Status;
        if (error.Status is 503 or 429) context.Response.Headers.RetryAfter = "5";
        await context.Response.WriteAsJsonAsync(new ErrorResponse(new ErrorDetails(error.Code, error.Message, Activity.Current?.Id ?? context.TraceIdentifier)), context.RequestAborted);
    }
});
app.UseCors();

app.MapGet("/health/live", () => Results.Ok(new HealthResponse("ok")))
    .WithTags("Health").WithSummary("Check whether the process is alive").Produces<HealthResponse>();
app.MapGet("/health/ready", async (Store store, ImageProcessor processor, CancellationToken ct) =>
{
    if (!processor.Ready) throw new ApiException(503, "SERVICE_UNAVAILABLE", "Serviço temporariamente indisponível.");
    await using var connection = await store.Open(ct);
    await Store.One<int>(connection, "SELECT id FROM checkpoint.community_progress WHERE id=1", null, ct);
    return Results.Ok(new HealthResponse("ok"));
}).WithTags("Health").WithSummary("Check database and AVIF codec readiness")
    .Produces<HealthResponse>().Produces<ErrorResponse>(503);
app.MapGet("/api/v1/progress", async (Store store, ArtworkCatalog artwork, CancellationToken ct) =>
{
    await using var connection = await store.Open(ct);
    var progress = await Store.One<CommunityRow>(connection, "SELECT * FROM checkpoint.community_progress WHERE id=1", null, ct);
    return Results.Ok(new ProgressResponse(progress.TotalXp, progress.State.Revision, progress.UpdatedAtUtc, artwork.Current(progress.TotalXp), artwork.Previous(progress.TotalXp)));
}).WithTags("Community").WithSummary("Get community progress and current artwork")
    .WithDescription("Public. Includes only the current level artwork and already unlocked levels. revision is a decimal string; compare it as an integer to avoid displaying stale state.")
    .Produces<ProgressResponse>().Produces<ErrorResponse>(503);
app.MapPut("/api/v1/participation", async (HttpContext context, ParticipationService service, CancellationToken ct) =>
    Results.Ok(await service.Participate(Headers.Visitor(context), ct)))
    .WithTags("Participation").WithSummary("Start or resume a participation")
    .WithDescription("Creates or resumes up to three stable poll assignments. waiting_for_polls means fewer than three active polls are currently available. No aggregate results are returned.")
    .Produces<ParticipationResponse>().Produces<ErrorResponse>(400).Produces<ErrorResponse>(503);
app.MapPost("/api/v1/votes", async (HttpContext context, VoteRequest request, ParticipationService service, CancellationToken ct) =>
{
    var visitor = Headers.Visitor(context);
    if (request.PollId == Guid.Empty || request.OptionId == Guid.Empty) throw new ApiException(400, "VALIDATION_ERROR", "Informe enquete e alternativa.");
    var result = await service.Vote(visitor, request, ct);
    return Results.Json(result, statusCode: 201);
}).WithTags("Participation").WithSummary("Submit an assigned poll vote")
    .WithDescription("A confirmed vote awards 1 community XP. A repeated vote returns 409 ALREADY_VOTED without additional XP. After a lost response, call PUT /api/v1/participation to recover the saved answer. 400: invalid visitor or option; 404: participation or poll not found; 409: poll not assigned or already answered.")
    .Produces<VoteResponse>(201).Produces<ErrorResponse>(400).Produces<ErrorResponse>(404).Produces<ErrorResponse>(409).Produces<ErrorResponse>(503);
app.MapGet("/api/v1/results", async (HttpContext context, ResultsService service, CancellationToken ct) =>
    Results.Ok(await service.Get(Headers.Visitor(context), ct)))
    .WithTags("Results").WithSummary("Get all poll results")
    .WithDescription("Requires three confirmed votes. Returns all visible polls with at least one vote, alphabetically by title (Portuguese, case-insensitive), with poll ID as a stable tie-breaker. Each request reads fresh counts; the frontend can rank polls by votes. No cursor or pagination is used. 403 RESULTS_LOCKED; 404 PARTICIPATION_NOT_FOUND.")
    .Produces<ResultsResponse>().Produces<ErrorResponse>(400).Produces<ErrorResponse>(403).Produces<ErrorResponse>(404).Produces<ErrorResponse>(503);
app.MapGet("/api/v1/photos/recent", async (Store store, CancellationToken ct) =>
{
    await using var connection = await store.Open(ct);
    var photos = (await Store.Query<PhotoRow>(connection, "SELECT id,thumbnail,created_at_utc FROM checkpoint.photos ORDER BY created_at_utc DESC,id LIMIT 3", null, ct)).ToArray();
    return Results.Ok(new RecentPhotosResponse(photos.FirstOrDefault()?.CreatedAtUtc ?? DateTime.UnixEpoch,
        photos.Select(photo => new RecentPhoto(photo.Id, photo.CreatedAtUtc, "Foto enviada por visitante", new ImageData("image/avif", 360, 330, Convert.ToBase64String(photo.Thumbnail)))).ToArray()));
}).WithTags("Photos").WithSummary("Get up to three recent photos")
    .WithDescription("Public. Returns confirmed photos newest first as Base64 AVIF thumbnails; originals and visitor IDs are never returned.")
    .Produces<RecentPhotosResponse>().Produces<ErrorResponse>(503);
app.MapPost("/api/v1/photos", async (HttpContext context, ParticipationService service, ImageProcessor processor, ImageSettings settings, CancellationToken ct) =>
{
    var visitor = Headers.Visitor(context);
    if (!context.Request.HasFormContentType) throw new ApiException(400, "VALIDATION_ERROR", "Envie um formulário multipart com o campo file.");
    var form = await context.Request.ReadFormAsync(ct);
    if (form.Files.Count != 1 || form.Files[0].Name != "file" || form.Count != 0)
        throw new ApiException(400, "VALIDATION_ERROR", "Envie exatamente um arquivo no campo file.");
    var file = form.Files[0];
    if (file.Length > settings.MaxFileBytes) throw new ApiException(413, "FILE_TOO_LARGE", "Arquivo maior que o limite permitido.");
    if (file.Length == 0) throw new ApiException(422, "INVALID_IMAGE", "Arquivo vazio.");
    using var buffer = new MemoryStream((int)file.Length);
    await file.CopyToAsync(buffer, ct);
    var result = await service.Photo(visitor, buffer.ToArray(), processor, ct);
    return Results.Json(result, statusCode: 201);
}).WithTags("Photos").WithSummary("Upload one photo")
    .WithDescription("Send exactly one multipart file field named file. JPEG, PNG, WebP, AVIF, HEIC/HEIF and JPEG 2000 are accepted when supported by the installed codec. Defaults: 10 MiB and 40 megapixels. Awards 2 XP once. A repeated upload returns 409 PHOTO_ALREADY_SUBMITTED. After a lost response, call PUT /api/v1/participation and check photoStatus; it does not identify the saved file. 413: FILE_TOO_LARGE; 415: UNSUPPORTED_IMAGE_FORMAT; 422: INVALID_IMAGE or IMAGE_DIMENSIONS_EXCEEDED; 503: PROCESSING_BUSY or SERVICE_UNAVAILABLE.")
    .Accepts<PhotoUploadRequest>("multipart/form-data")
    .Produces<PhotoResponse>(201).Produces<ErrorResponse>(400).Produces<ErrorResponse>(404).Produces<ErrorResponse>(409).Produces<ErrorResponse>(413).Produces<ErrorResponse>(415).Produces<ErrorResponse>(422).Produces<ErrorResponse>(503);

if (app.Environment.IsDevelopment())
{
    // OpenAPI document is a development aid only; /openapi/v1.json. Full contract: Frontend/CONTRATO_API.md.
    app.MapOpenApi();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "Checkpoint API v1"));
    app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
}

using (var scope = app.Services.CreateScope())
{
    _ = scope.ServiceProvider.GetRequiredService<Progression>();
    if (migrateOnly || app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
        await scope.ServiceProvider.GetRequiredService<MigrationDb>().Database.MigrateAsync();
    if (app.Configuration.GetValue<bool>("Database:SeedDemo"))
    {
        if (!app.Environment.IsDevelopment()) throw new InvalidOperationException("Demo seed is only allowed in Development.");
        await DemoSeed.Run(scope.ServiceProvider.GetRequiredService<Store>(), CancellationToken.None);
    }
    if (migrateOnly) return;
    var processor = scope.ServiceProvider.GetRequiredService<ImageProcessor>();
    processor.Probe();
    if (processor.Ready) scope.ServiceProvider.GetRequiredService<ArtworkCatalog>().Load();
}
await app.RunAsync();

static string Connection(IServiceProvider services) => services.GetRequiredService<IConfiguration>().GetConnectionString("Checkpoint")
    ?? throw new InvalidOperationException("Set ConnectionStrings__Checkpoint using environment or user secrets.");

public static class Headers
{
    public static Guid Visitor(HttpContext context) => Parse(context.Request.Headers["X-Visitor-Id"].ToString(), "INVALID_VISITOR_ID");
    public static Guid Parse(string value, string code)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || value.Length != 36 || value[14] != '4' || !"89abAB".Contains(value[19]))
            throw new ApiException(400, code, "Identificador UUID v4 inválido.");
        return id;
    }
}

public partial class Program;
