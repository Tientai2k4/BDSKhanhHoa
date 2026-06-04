using BDSKhanhHoa.Data;
using BDSKhanhHoa.Helpers;
using BDSKhanhHoa.Services;
using BDSKhanhHoa.Services.AI;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using System.IO.Compression;

var builder = WebApplication.CreateBuilder(args);

// MVC
builder.Services.AddControllersWithViews();

// DATABASE
builder.Services.AddDbContext<ApplicationDbContext>(options =>
{
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        sqlOptions =>
        {
            sqlOptions.CommandTimeout(120);
            sqlOptions.EnableRetryOnFailure(
                maxRetryCount: 3,
                maxRetryDelay: TimeSpan.FromSeconds(5),
                errorNumbersToAdd: null
            );
        }
    );

    // Chỉ bật khi cần soi SQL. Bình thường để tắt cho nhẹ.
    // if (builder.Environment.IsDevelopment())
    // {
    //     options.EnableDetailedErrors();
    //     options.EnableSensitiveDataLogging();
    //     options.LogTo(
    //         Console.WriteLine,
    //         new[] { DbLoggerCategory.Database.Command.Name },
    //         LogLevel.Information
    //     );
    // }
});

builder.Services.AddHttpContextAccessor();

// CACHE
builder.Services.AddMemoryCache();

// SESSION
builder.Services.AddDistributedMemoryCache();

builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.Name = ".BDSKhanhHoa.Session";
    options.Cookie.SameSite = SameSiteMode.Lax;
});

// NÉN RESPONSE: chỉ dùng khi publish/production.
// Không bật Development để tránh BrowserLink/BrowserRefresh báo lỗi Content-Encoding: br.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});

builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
{
    options.Level = CompressionLevel.Fastest;
});

builder.Services.Configure<GzipCompressionProviderOptions>(options =>
{
    options.Level = CompressionLevel.Fastest;
});

// EMAIL SERVICE
builder.Services.AddScoped<IEmailService, EmailSender>();

// AUDIT LOG
builder.Services.AddScoped<IAuditLogService, AuditLogService>();

// AUTHENTICATION
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = GoogleDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
    options.Cookie.Name = "BDSKhanhHoa_UserAuth";
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromDays(7);
    options.SlidingExpiration = true;
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
})
.AddGoogle(options =>
{
    options.ClientId = builder.Configuration["Authentication:Google:ClientId"];
    options.ClientSecret = builder.Configuration["Authentication:Google:ClientSecret"];
    options.CallbackPath = "/signin-google";
});

// AI CHATBOT - CHỈ DÙNG GEMINI, KHÔNG DÙNG OLLAMA
builder.Services.Configure<AIProviderSettings>(
    builder.Configuration.GetSection("AIProviderSettings"));

builder.Services.AddHttpClient<GeminiAIClient>();

builder.Services.AddScoped<IAIModelClient, GeminiAIClient>();
builder.Services.AddScoped<ChatbotService>();

var app = builder.Build();

// PIPELINE
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

// Middleware đo request chậm
app.Use(async (context, next) =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();

    await next();

    sw.Stop();

    if (sw.ElapsedMilliseconds >= 1000)
    {
        var endpoint = context.GetEndpoint()?.DisplayName ?? "Không xác định endpoint";

        var logger = context.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("SlowRequest");

        logger.LogWarning(
            "SLOW REQUEST {Method} {Path}{QueryString} => {StatusCode} in {Elapsed}ms | Endpoint: {Endpoint}",
            context.Request.Method,
            context.Request.Path,
            context.Request.QueryString,
            context.Response.StatusCode,
            sw.ElapsedMilliseconds,
            endpoint
        );
    }
});

app.UseHttpsRedirection();

if (!app.Environment.IsDevelopment())
{
    app.UseResponseCompression();
}

app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var path = ctx.File.PhysicalPath ?? "";

        if (path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".woff", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers.CacheControl = "public,max-age=604800";
        }
    }
});

app.UseRouting();

app.UseSession();

app.UseAuthentication();

app.UseAuthorization();

// ROUTE
app.MapControllerRoute(
    name: "areas",
    pattern: "{area:exists}/{controller=Home}/{action=Index}/{id?}");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
