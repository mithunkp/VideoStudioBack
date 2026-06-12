using Microsoft.AspNetCore.Http.Features;
using backend.Services;

var builder = WebApplication.CreateBuilder(args);

const long MaxVideoUploadBytes = 4L * 1024 * 1024 * 1024;

builder.WebHost.ConfigureKestrel(options =>
{
    // Video processing requests can remain open while FFmpeg renders the result.
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(30);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(2);
    options.Limits.MaxRequestBodySize = MaxVideoUploadBytes;
    options.Limits.MinRequestBodyDataRate = null;
});

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = MaxVideoUploadBytes;
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartHeadersLengthLimit = int.MaxValue;
    options.MemoryBufferThreshold = 1024 * 1024;
});

// Configure CORS for Angular frontend
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngularClient",
        policy =>
        {
            policy.AllowAnyOrigin()
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        });
});

// Register API Key Store (singleton — all services share the same in-memory key state)
builder.Services.AddHttpClient(); // IHttpClientFactory used by ApiKeyStoreService
builder.Services.AddSingleton<IApiKeyStore, ApiKeyStoreService>();

// Register services for Dependecy Injection
builder.Services.AddTransient<IFFmpegService, FFmpegService>();
builder.Services.AddTransient<IVideoProcessingOrchestrator, VideoProcessingOrchestrator>();

// Register HttpClients for API Integrations
builder.Services.AddHttpClient<IGroqTranscriptionService, GroqTranscriptionService>();
builder.Services.AddHttpClient<IGeminiHookService, GeminiHookService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors("AllowAngularClient");

// Serve static files (e.g. from wwwroot/shorts)
app.UseStaticFiles();

app.UseAuthorization();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "viral-studio-backend" }));
app.MapFallbackToFile("index.html");

app.Run();
