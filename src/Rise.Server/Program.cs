using System.IO;
using Destructurama;
using FastEndpoints.Swagger;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Rise.Persistence;
using Rise.Persistence.Triggers;
using Rise.Server.Identity;
using Rise.Server.Processors;
using Rise.Services;
using Rise.Services.Identity;
using Serilog.Events;

var migrateOnly = args.Any(argument =>
    string.Equals(argument, "--migrate", StringComparison.OrdinalIgnoreCase));

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting RISE web application");
    var builder = WebApplication.CreateBuilder(args);

    var dataProtection = builder.Services
        .AddDataProtection()
        .SetApplicationName("Rise");
    var keysPath = builder.Configuration["DataProtection:KeysPath"];
    if (!string.IsNullOrWhiteSpace(keysPath))
    {
        dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
    }

    builder.Services
        .AddSerilog((_, loggerConfiguration) => loggerConfiguration
            .ReadFrom.Configuration(builder.Configuration)
            .Destructure.UsingAttributes())
        .AddIdentity<IdentityUser, IdentityRole>()
        .AddEntityFrameworkStores<ApplicationDbContext>()
        .Services.AddDbContext<ApplicationDbContext>(options =>
        {
            var connectionString = builder.Configuration.GetConnectionString("DatabaseConnection") ??
                throw new InvalidOperationException(
                    "Connection string 'DatabaseConnection' was not configured.");
            options.UseNpgsql(connectionString);
            options.EnableDetailedErrors();
            if (builder.Environment.IsDevelopment())
            {
                options.EnableSensitiveDataLogging();
            }

            options.UseTriggers(triggerOptions =>
                triggerOptions.AddTrigger<EntityBeforeSaveTrigger>());
        })
        .ConfigureApplicationCookie(options =>
        {
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        })
        .AddHttpContextAccessor()
        .AddScoped<ISessionContextProvider, HttpContextSessionProvider>()
        .AddApplicationServices()
        .AddAuthorization()
        .AddFastEndpoints(options =>
        {
            options.IncludeAbstractValidators = true;
            options.Assemblies = [typeof(Rise.Shared.Products.ProductRequest).Assembly];
        })
        .SwaggerDocument(options =>
        {
            options.DocumentSettings = settings => settings.Title = "RISE API";
        });

    builder.Services
        .AddHealthChecks()
        .AddDbContextCheck<ApplicationDbContext>("database", tags: ["ready"]);

    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                                   ForwardedHeaders.XForwardedProto |
                                   ForwardedHeaders.XForwardedHost;
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
    });

    var app = builder.Build();

    if (migrateOnly)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await dbContext.Database.MigrateAsync();

        if (builder.Configuration.GetValue("SeedDemoData", false))
        {
            var dbSeeder = scope.ServiceProvider.GetRequiredService<DbSeeder>();
            await dbSeeder.SeedAsync();
        }

        Log.Information("Database migration completed successfully");
        return;
    }

    if (app.Environment.IsDevelopment())
    {
        await using var scope = app.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await dbContext.Database.MigrateAsync();
        var dbSeeder = scope.ServiceProvider.GetRequiredService<DbSeeder>();
        await dbSeeder.SeedAsync();
    }

    app.UseForwardedHeaders();
    if (app.Environment.IsDevelopment())
    {
        app.UseHttpsRedirection();
    }

    app.UseBlazorFrameworkFiles()
        .UseStaticFiles()
        .UseDefaultExceptionHandler()
        .UseAuthentication()
        .UseAuthorization()
        .UseFastEndpoints(options =>
        {
            options.Endpoints.Configurator = endpoint =>
            {
                endpoint.DontAutoSendResponse();
                endpoint.PreProcessor<GlobalRequestLogger>(Order.Before);
                endpoint.PostProcessor<GlobalResponseSender>(Order.Before);
                endpoint.PostProcessor<GlobalResponseLogger>(Order.Before);
            };
        })
        .UseSwaggerGen();

    app.MapHealthChecks("/health/live", new HealthCheckOptions
    {
        Predicate = _ => false
    }).AllowAnonymous();
    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = registration => registration.Tags.Contains("ready")
    }).AllowAnonymous();
    app.MapFallbackToFile("index.html");
    await app.RunAsync();
}
catch (HostAbortedException)
{
    // EF Core stops the host after design-time service discovery. This is not
    // an application failure and should not be logged as fatal.
    Log.Debug("Host stopped after design-time service discovery");
}
catch (Exception exception)
{
    Log.Fatal(exception, "An unhandled exception occurred during bootstrapping");
}
finally
{
    await Log.CloseAndFlushAsync();
}
