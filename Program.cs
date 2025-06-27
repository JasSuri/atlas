using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;
using ProtectedMCPServer.Tools;
using System.Net.Http.Headers;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

// Get configuration values from appsettings.json or environment variables
var tenantId = builder.Configuration["AzureAd:TenantId"] ?? "8b047ec6-6d2e-481d-acfa-5d562c09f49a";
var clientId = builder.Configuration["AzureAd:ClientId"] ?? "5e00b345-a805-42a0-9caa-7d6cb761c668";
var clientSecret = builder.Configuration["AzureAd:ClientSecret"]; // Should be set in Azure App Service configuration
var instance = builder.Configuration["AzureAd:Instance"] ?? "https://login.microsoftonline.com/";

// Configure URLs - use localhost for development, auto-detect for Azure
var isDevelopment = builder.Environment.IsDevelopment();
var serverUrl = "http://localhost:7071/"; // Always use localhost:7071 for local development

// Override serverUrl for production/Azure
if (!isDevelopment || builder.Configuration["ASPNETCORE_ENVIRONMENT"] == "Production")
{
    serverUrl = null; // Let Azure determine the URL
}

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
    options.ProtectedResourceMetadataProvider = context =>
    {
        // Get the current request's base URL for dynamic URL generation in Azure
        var request = context.Request;
        var baseUrl = $"{request.Scheme}://{request.Host}";
        
        var metadata = new ProtectedResourceMetadata
        {
            Resource = new Uri($"{baseUrl}/"),
            BearerMethodsSupported = { "header" },
            ResourceDocumentation = new Uri("https://docs.example.com/api/mcp"),
            AuthorizationServers = { new Uri($"{instance}{tenantId}/v2.0") }
        };

        metadata.ScopesSupported.AddRange([
            $"api://{clientId}/user.read"
        ]);

        return metadata;
    };
});

builder.Services.AddAuthorization();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<WeatherTools>();
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

if (!string.IsNullOrEmpty(serverUrl))
{
    Console.WriteLine($"Local development URL: {serverUrl}");
    Console.WriteLine($"PRM Document URL: {serverUrl}.well-known/oauth-protected-resource");
    Console.WriteLine("Press Ctrl+C to stop the server");
    app.Run(serverUrl);
}
else
{
    Console.WriteLine("Production mode - using default Azure URLs");
    Console.WriteLine("PRM Document URL: /.well-known/oauth-protected-resource");
    Console.WriteLine("Press Ctrl+C to stop the server");
    app.Run();
}
