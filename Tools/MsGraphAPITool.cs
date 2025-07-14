using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using System.Text;

namespace ProtectedMCPServer.Tools;

[McpServerToolType]
public sealed class MsGraphAPITool
{    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IConfiguration _configuration;

    public MsGraphAPITool(IHttpClientFactory httpClientFactory, IHttpContextAccessor httpContextAccessor, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _httpContextAccessor = httpContextAccessor;
        _configuration = configuration;
    }

    // Helper method to get the bearer token from the authorization header
    private string? GetBearerToken()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null)
        {
            return null;
        }

        // Try to get the Authorization header from the request
        if (httpContext.Request.Headers.TryGetValue("Authorization", out var authHeaderValues))
        {
            var authHeader = authHeaderValues.FirstOrDefault();
            if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return authHeader["Bearer ".Length..]; // Extract just the token part
            }
        }

        return null;
    }    // Method to exchange the incoming token for a Graph API token using On-Behalf-Of flow
    private async Task<string?> GetGraphApiTokenAsync(string userToken)
    {
        try
        {
            var tenantId = _configuration["AzureAd:TenantId"];
            var clientId = _configuration["AzureAd:ClientId"];
            var clientSecret = _configuration["AzureAd:ClientSecret"];

            if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            {
                throw new InvalidOperationException("TenantId, ClientId, and ClientSecret must be configured for On-Behalf-Of flow");
            }

            var client = _httpClientFactory.CreateClient();
            var tokenEndpoint = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";

            var formParams = new List<KeyValuePair<string, string>>
            {
                new("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"),
                new("client_id", clientId),
                new("client_secret", clientSecret),
                new("assertion", userToken),
                new("scope", "https://graph.microsoft.com/.default"),
                new("requested_token_use", "on_behalf_of")
            };

            var formContent = new FormUrlEncodedContent(formParams);
            
            var response = await client.PostAsync(tokenEndpoint, formContent);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Token exchange failed: {response.StatusCode} - {responseContent}");
            }

            using var jsonDoc = JsonDocument.Parse(responseContent);
            var accessToken = jsonDoc.RootElement.GetProperty("access_token").GetString();
            
            return accessToken;
        }
        catch (Exception ex)
        {            throw new InvalidOperationException($"Failed to exchange token for Graph API access: {ex.Message}", ex);
        }
    }

    [McpServerTool, Description("Get current user information from Microsoft Graph API /me endpoint using On-Behalf-Of flow.")]
    public async Task<string> GetCurrentUserFromGraph()
    {
        var userToken = GetBearerToken();
        if (string.IsNullOrEmpty(userToken))
        {
            return "❌ No Bearer token found in the request. Make sure you're authenticated.";
        }

        try
        {
            // Exchange the user token for a Graph API token using On-Behalf-Of flow
            var graphToken = await GetGraphApiTokenAsync(userToken);
            if (string.IsNullOrEmpty(graphToken))
            {
                return "❌ Failed to obtain Graph API token through On-Behalf-Of flow.";
            }

            var client = _httpClientFactory.CreateClient("GraphApi");
            
            // Add the Graph API token to the request
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", graphToken);

            // Make the request to Microsoft Graph /me endpoint
            var response = await client.GetAsync("/v1.0/me");
            
            if (!response.IsSuccessStatusCode)
            {
                return $"❌ Microsoft Graph API request failed: {response.StatusCode} - {response.ReasonPhrase}";
            }

            var jsonContent = await response.Content.ReadAsStringAsync();
              // Parse and format the JSON response for better readability
            using var jsonDocument = JsonDocument.Parse(jsonContent);
            var user = jsonDocument.RootElement;

            var userInfo = $"""
                === Microsoft Graph User Information (via On-Behalf-Of) ===
                Display Name: {user.GetProperty("displayName").GetString()}
                Email: {user.GetProperty("mail").GetString() ?? user.GetProperty("userPrincipalName").GetString()}
                User Principal Name: {user.GetProperty("userPrincipalName").GetString()}
                ID: {user.GetProperty("id").GetString()}
                Job Title: {(user.TryGetProperty("jobTitle", out var jobTitle) ? jobTitle.GetString() : "Not specified")}
                Department: {(user.TryGetProperty("department", out var dept) ? dept.GetString() : "Not specified")}
                Office Location: {(user.TryGetProperty("officeLocation", out var office) ? office.GetString() : "Not specified")}
                Mobile Phone: {(user.TryGetProperty("mobilePhone", out var mobile) ? mobile.GetString() : "Not specified")}
                Business Phone: {(user.TryGetProperty("businessPhones", out var phones) && phones.GetArrayLength() > 0 ? phones[0].GetString() : "Not specified")}
                """;

            return userInfo;
        }        catch (HttpRequestException ex)
        {
            return $"❌ HTTP request error: {ex.Message}";
        }
        catch (JsonException ex)
        {
            return $"❌ JSON parsing error: {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            return $"❌ Configuration error: {ex.Message}";
        }        catch (Exception ex)
        {
            return $"❌ Unexpected error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Make a universal Microsoft Graph API call to any endpoint using On-Behalf-Of flow.")]
    public async Task<string> CallGraphApiEndpoint(
        [Description("The Graph API endpoint to call (e.g., '/v1.0/me', '/v1.0/users', '/v1.0/me/messages'). Include the version prefix.")] string endpoint,
        [Description("HTTP method to use (GET, POST, PUT, PATCH, DELETE). Defaults to GET.")] string method = "GET",
        [Description("Optional request body for POST, PUT, or PATCH methods. Should be a JSON string.")] string? body = null)
    {
        var userToken = GetBearerToken();
        if (string.IsNullOrEmpty(userToken))
        {
            return "❌ No Bearer token found in the request. Make sure you're authenticated.";
        }

        // Validate endpoint format
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return "❌ Endpoint parameter is required. Example: '/v1.0/me'";
        }

        // Ensure endpoint starts with /
        if (!endpoint.StartsWith("/"))
        {
            endpoint = "/" + endpoint;
        }

        // Validate HTTP method
        var validMethods = new[] { "GET", "POST", "PUT", "PATCH", "DELETE" };
        method = method.ToUpper();
        if (!validMethods.Contains(method))
        {
            return $"❌ Invalid HTTP method '{method}'. Supported methods: {string.Join(", ", validMethods)}";
        }

        try
        {
            // Exchange the user token for a Graph API token using On-Behalf-Of flow
            var graphToken = await GetGraphApiTokenAsync(userToken);
            if (string.IsNullOrEmpty(graphToken))
            {
                return "❌ Failed to obtain Graph API token through On-Behalf-Of flow.";
            }

            var client = _httpClientFactory.CreateClient("GraphApi");
            // Add the Graph API token to the request
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", graphToken);

            // Prepare content for methods that support a body
            StringContent? content = null;
            if (method is "POST" or "PUT" or "PATCH")
            {
                content = new StringContent(string.IsNullOrEmpty(body) ? "{}" : body, System.Text.Encoding.UTF8, "application/json");
            }

            // Make the request to the specified Graph API endpoint
            HttpResponseMessage response = method switch
            {
                "GET" => await client.GetAsync(endpoint),
                "POST" => await client.PostAsync(endpoint, content!),
                "PUT" => await client.PutAsync(endpoint, content!),
                "PATCH" => await client.PatchAsync(endpoint, content!),
                "DELETE" => await client.DeleteAsync(endpoint),
                _ => throw new ArgumentException($"Unsupported HTTP method: {method}")
            };
            
            var responseContent = await response.Content.ReadAsStringAsync();
            
            if (!response.IsSuccessStatusCode)
            {
                return $"""
                    ❌ Microsoft Graph API request failed
                    Endpoint: {method} {endpoint}
                    Status: {response.StatusCode} - {response.ReasonPhrase}
                    Response: {responseContent}
                    """;
            }

            // Try to format JSON response for better readability
            string formattedResponse;
            try
            {
                using var jsonDocument = JsonDocument.Parse(responseContent);
                formattedResponse = JsonSerializer.Serialize(jsonDocument, new JsonSerializerOptions 
                { 
                    WriteIndented = true 
                });
            }
            catch (JsonException)
            {
                // If not valid JSON, return as-is
                formattedResponse = responseContent;
            }

            return $"""
                === Microsoft Graph API Response (via On-Behalf-Of) ===
                Endpoint: {method} {endpoint}
                Status: {response.StatusCode} {response.ReasonPhrase}
                
                {formattedResponse}
                """;
        }
        catch (HttpRequestException ex)
        {
            return $"❌ HTTP request error for endpoint '{endpoint}': {ex.Message}";
        }
        catch (JsonException ex)
        {
            return $"❌ JSON parsing error for endpoint '{endpoint}': {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            return $"❌ Configuration error: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"❌ Unexpected error calling endpoint '{endpoint}': {ex.Message}";
        }
    }
}
