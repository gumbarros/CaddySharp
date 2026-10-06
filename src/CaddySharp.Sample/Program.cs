namespace CaddySharp.Sample;

public sealed class GreetingService
{
    public string Text => "hello from DI";
}

public static class AppBootstrap
{
    private static int CallbackCount;
    private static int CancelCount;
    private static int StartupCount;

    public static void Configure(WebApplicationBuilder builder)
    {
        Interlocked.Increment(ref StartupCount);
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1 << 20);
        builder.Services.AddSingleton<GreetingService>();
    }

    public static void Map(WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok", pid = Environment.ProcessId }));
        app.MapGet("/text", () => "hello caddysharp");
        app.MapGet("/json", () => Results.Json(new { name = "CaddySharp", ok = true }));
        app.MapPost("/echo", async (HttpRequest request) =>
        {
            using var ms = new MemoryStream();
            await request.Body.CopyToAsync(ms);
            return Results.Bytes(ms.ToArray(), "application/octet-stream");
        });
        app.MapMethods("/inspect/{**rest}", ["GET", "POST", "PUT", "HEAD"],
            (HttpRequest r) => Results.Json(new
            {
                method = r.Method, path = r.Path.Value, query = r.QueryString.Value, host = r.Host.Value,
                scheme = r.Scheme, headers = r.Headers.ToDictionary(x => x.Key, x => x.Value.ToArray())
            }));
        app.MapGet("/callbacks", (HttpContext c) =>
        {
            c.Response.OnStarting(() =>
            {
                c.Response.Headers.Append("X-Order", "first");
                return Task.CompletedTask;
            });
            c.Response.OnStarting(() =>
            {
                c.Response.Headers.Append("X-Order", "second");
                return Task.CompletedTask;
            });
            c.Response.OnCompleted(() =>
            {
                Interlocked.Increment(ref CallbackCount);
                return Task.CompletedTask;
            });
            return "callbacks";
        });
        app.MapGet("/large", () => Results.Bytes(new byte[5 * 1024 * 1024]));
        app.MapGet("/empty", Results.NoContent);
        app.MapGet("/cookies", (HttpContext c) =>
        {
            c.Response.Cookies.Append("a", "1");
            c.Response.Cookies.Append("b", "2");
            return "cookies";
        });
        app.MapGet("/callback-count", () => CallbackCount);
        app.MapGet("/wait", async (HttpContext c) =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), c.RequestAborted);
                return "done";
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref CancelCount);
                throw;
            }
        });
        app.MapGet("/startup-count", () => StartupCount);
        app.MapGet("/cancel-count", () => CancelCount);
        app.MapGet("/error", () => { throw new InvalidOperationException("controlled sample error"); });
        app.MapGet("/config",
            (IConfiguration c, GreetingService s) =>
                Results.Json(new { greeting = s.Text, value = c["SampleSettings:Value"] }));
    }
}

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Host.UseCaddyServer();
        AppBootstrap.Configure(builder);
        var app = builder.Build();
        AppBootstrap.Map(app);
        app.Run();
    }
}
