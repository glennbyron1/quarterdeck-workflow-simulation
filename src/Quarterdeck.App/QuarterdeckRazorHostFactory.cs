using System.Net;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Quarterdeck.Simulation.Tests")]

namespace Quarterdeck.App;

/// <summary>
/// Simulation Razor shell. The current builder binds loopback and maps
/// simulation page handlers. A commissioned Windows host may reuse the
/// visual design only through a separately reviewed live composition and
/// access policy; it must not call this factory as its live entry point.
/// </summary>
public static class QuarterdeckRazorHostFactory
{
    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls("http://127.0.0.1:5080");
        builder.Services.AddRazorPages()
            .AddApplicationPart(typeof(QuarterdeckRazorHostFactory).Assembly);
        return builder;
    }

    public static WebApplication Build(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var app = builder.Build();
        app.Use((context, next) => RequireLocalSimulationRequestAsync(context, next));
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers["Content-Security-Policy"] =
                    "default-src 'self'; base-uri 'none'; object-src 'none'; " +
                    "frame-ancestors 'none'; form-action 'self'; script-src 'self'; " +
                    "style-src 'self'; img-src 'self'; connect-src 'self'; media-src 'none'";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["X-Frame-Options"] = "DENY";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers["Cache-Control"] = "no-store";
                return Task.CompletedTask;
            });
            await next();
        });
        app.UseStatusCodePagesWithReExecute("/Error", "?statusCode={0}");
        app.UseStaticFiles();
        app.UseRouting();
        app.MapRazorPages();
        return app;
    }

    internal static async Task RequireLocalSimulationRequestAsync(
        HttpContext context, Func<Task> next)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote) ||
            !string.Equals(context.Request.Host.Value, "127.0.0.1:5080", StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("simulation_loopback_required");
            return;
        }

        await next();
    }
}
