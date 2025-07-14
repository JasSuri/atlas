using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;
using ProtectedMCPServer.Tools;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Get configuration values from appsettings.json or environment variables
var tenantId = builder.Configuration["AzureAd:TenantId"] ?? "8b047ec6-6d2e-481d-acfa-5d562c09f49a";
var clientId = builder.Configuration["AzureAd:ClientId"] ?? "5e00b345-a805-42a0-9caa-7d6cb761c668";
var clientSecret = builder.Configuration["AzureAd:ClientSecret"]; // Should be set in Azure App Service configuration
var instance = builder.Configuration["AzureAd:Instance"] ?? "https://login.microsoftonline.com/";

// Configure URLs - use localhost for development, auto-detect for production
var isDevelopment = builder.Environment.IsDevelopment();

builder.Services.AddAuthentication(options =>
{
    options.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.Authority = $"{instance}{tenantId}/v2.0";
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidAudience = clientId,
        ValidIssuer = $"{instance}{tenantId}/v2.0",
        NameClaimType = "name",
        RoleClaimType = "roles"
    };

    options.MetadataAddress = $"{instance}{tenantId}/v2.0/.well-known/openid-configuration";

    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = context =>
        {
            var name = context.Principal?.Identity?.Name ?? "unknown";
            var email = context.Principal?.FindFirstValue("preferred_username") ?? "unknown";
            Console.WriteLine($"Token validated for: {name} ({email})");
            return Task.CompletedTask;
        },
        OnAuthenticationFailed = context =>
        {
            Console.WriteLine($"Authentication failed: {context.Exception.Message}");
            return Task.CompletedTask;
        },
        OnChallenge = context =>
        {
            Console.WriteLine($"Challenging client to authenticate with Entra ID");
            return Task.CompletedTask;
        }
    };
})

.AddMcp(options =>
{
    // Default configuration - will be updated dynamically
    options.ResourceMetadata = new()
    {
        Resource = new Uri("https://localhost/"),
        ResourceDocumentation = new Uri("https://docs.example.com/api/weather"),
        AuthorizationServers = { new Uri($"{instance}{tenantId}/v2.0") },
        ScopesSupported = [$"api://{clientId}/user.read"],
    };
});

// Add a post-configure options to dynamically set the Resource URL based on HTTP context
builder.Services.AddSingleton<IPostConfigureOptions<McpAuthenticationOptions>>(serviceProvider =>
    new PostConfigureOptions<McpAuthenticationOptions>(null, options =>
    {
        var httpContextAccessor = serviceProvider.GetService<IHttpContextAccessor>();
        if (httpContextAccessor?.HttpContext != null && options.ResourceMetadata != null)
        {
            var request = httpContextAccessor.HttpContext.Request;
            var scheme = request.Scheme;
            var host = request.Host.Value;
            var baseUrl = $"{scheme}://{host}";
            
            options.ResourceMetadata.Resource = new Uri($"{baseUrl}/");
        }
    }));

builder.Services.AddAuthorization();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<MsGraphAPITool>();

builder.Services.AddMcpServer()
    .WithHttpTransport(options =>
    {
        // ModelContextProtocol.AspNetCore
        options.Stateless = true;
    })
    .WithTools<MsGraphAPITool>();

// Configure HttpClientFactory for Microsoft Graph API
builder.Services.AddHttpClient("GraphApi", client =>
{
    client.BaseAddress = new Uri("https://graph.microsoft.com");
    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("mcp-graph-tool", "1.0"));
});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Use the default MCP policy name that we've configured
app.MapMcp().RequireAuthorization();

// Always show startup info
Console.WriteLine("Starting MCP server with authorization");

if (isDevelopment)
{
    app.Run("http://localhost:7071");
}
else
{
    app.Run();
}
