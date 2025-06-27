# Microsoft Graph API MCP Server Setup Guide

This guide will help you set up a Model Context Protocol (MCP) server that provides access to Microsoft Graph API using Entra ID authentication with On-Behalf-Of (OBO) flow.
This sample uses Microsoft Graph API as the API layer for MCP to interface with, as an example.

<img src="https://github.com/jassuri/custom-graph-mcp/blob/main/assets/graph-mcp-demo.gif?raw=true" alt="Graph MCP Demo - list users and apps demo" width="1000"/>


## Table of Contents
1. [Prerequisites](#prerequisites)
2. [Entra ID Application Registration](#azure-ad-application-registration)
3. [Configure the Application](#configure-the-application)
4. [Local Development Setup](#local-development-setup)
5. [Azure Deployment](#azure-deployment)
6. [Testing the Deployment](#testing-the-deployment)
7. [Available MCP Tools](#available-mcp-tools)
8. [Troubleshooting](#troubleshooting)

## Prerequisites

- Azure subscription
- Azure CLI installed ([Download here](https://docs.microsoft.com/en-us/cli/azure/install-azure-cli))
- .NET 9.0 SDK installed
- PowerShell (Windows) or Bash (macOS/Linux)
- An MCP client for testing (VS Code Insiders edition)

## Entra ID Application Registration

### Step 1: Create App Registration

1. **Sign in to Azure Portal**: https://portal.azure.com
2. **Navigate to Azure Active Directory** → **App registrations**
3. **Click "New registration"**
4. **Fill in the details**:
   - **Name**: `MCP Graph API Server`
   - **Supported account types**:
     - `Accounts in this organizational directory only` (Single tenant)
   - **Redirect URI**: Leave blank for now
5. **Click "Register"**

### Step 2: Configure API Permissions

1. **In your app registration**, go to **"API permissions"**
2. **Click "Add a permission"**
3. **Select "Microsoft Graph"** → **"Application permissions"**
4. **Add these permissions**:
   - `Directory.Read.All` (Read directory data)
5. **Click "Add permissions"**
6. **IMPORTANT**: Click **"Grant admin consent"** for your organization

### Step 3: Create Client Secret

1. **Go to "Certificates & secrets"**
2. **Click "New client secret"**
3. **Add description**: `MCP Server Secret`
4. **Set expiration**: Choose appropriate duration (recommended: 12-24 months)
5. **Click "Add"**
6. **⚠️ COPY THE SECRET VALUE IMMEDIATELY** - you won't be able to see it again!

### Step 4: Expose an API (Required for On-Behalf-Of)

1. **Go to "Expose an API"**
2. **Click "Set"** next to Application ID URI
3. **Accept the default URI** (api://your-client-id)
4. **Click "Add a scope"**:
   - **Scope name**: `user.read`
   - **Who can consent**: `Admins and users`
   - **Admin consent display name**: `Access user data`
   - **Admin consent description**: `Allow the application to access user data on behalf of the signed-in user`
   - **User consent display name**: `Access your data`
   - **User consent description**: `Allow the application to access your data`
   - **State**: `Enabled`
5. **Click "Add scope"**

### Step 5: Note Down Important Values

Copy these values - you'll need them later:
- **Tenant ID** (Directory ID): Found in "Overview"
- **Client ID** (Application ID): Found in "Overview"  
- **Client Secret**: The value you copied in Step 3

## Configure the Application

### Step 1: Update appsettings.json

Open `appsettings.json` and replace the placeholder values:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "AzureAd": {
    "Instance": "https://login.microsoftonline.com/",
    "TenantId": "YOUR_TENANT_ID_HERE",
    "ClientId": "YOUR_CLIENT_ID_HERE", 
    "ClientSecret": "YOUR_CLIENT_SECRET_HERE"
  }
}
```

**Replace these values**:
- `YOUR_TENANT_ID_HERE` → Your Entra ID Tenant ID
- `YOUR_CLIENT_ID_HERE` → Your App Registration Client ID  
- `YOUR_CLIENT_SECRET_HERE` → Your Client Secret

### Step 2: Update Program.cs (if needed)

The fallback values in `Program.cs` should also be updated. Find these lines around line 13-15:

```csharp
var tenantId = builder.Configuration["AzureAd:TenantId"] ?? "YOUR_TENANT_ID_HERE";
var clientId = builder.Configuration["AzureAd:ClientId"] ?? "YOUR_CLIENT_ID_HERE";
```

Replace the fallback values with your actual Entra ID values.

## Local Development Setup

### Step 1: Test Locally

1. **Open terminal** in the project directory
2. **Build and run the application**:
   ```bash
   dotnet build
   dotnet run
   ```

3. **Verify it's running**: 
   - Open browser: http://localhost:7071/.well-known/oauth-protected-resource
   - Should return JSON with OAuth configuration

### Step 2: Test with MCP Client

Configure your MCP client to:
- **Server URL**: `http://localhost:7071`
- **Available tools**: `GetCurrentUserFromGraph`, `CallGraphApiEndpoint`, etc.
- **Test prompt**: Using Agent mode in your client, ask the Agent "Get my user profile". It should return the users attributes.

## Azure Deployment

### Step 1: Login to Azure

```bash
az login
```

### Step 2: Create Resource Group

```bash
az group create --name "rg-mcp-server" --location "East US"
```

### Step 3: Create App Service Plan

```bash
az appservice plan create --resource-group rg-mcp-server --name mcpServer --sku B1
```

### Step 4: Create Web App

```bash
az webapp create --resource-group "rg-mcp-server" --plan "mcpServer" --name "graph-mcp"
```

**⚠️ Note**: Replace `"graph-mcp"` with a globally unique name if this one is taken.

### Step 5: Configure App Settings (Important!)

```bash
az webapp config appsettings set --resource-group "rg-mcp-server" --name "graph-mcp" --settings \
  "AzureAd__TenantId=YOUR_TENANT_ID_HERE" \
  "AzureAd__ClientId=YOUR_CLIENT_ID_HERE" \
  "AzureAd__ClientSecret=YOUR_CLIENT_SECRET_HERE" \
  "AzureAd__Instance=https://login.microsoftonline.com/" \
  "ASPNETCORE_ENVIRONMENT=Production"
```

**Replace the placeholder values** with your actual Entra ID values.

### Step 6: Build and Package

```bash
dotnet publish -c Release -o ./publish
```

### Step 7: Create Deployment Package

```bash
Compress-Archive -Path "./publish/*" -DestinationPath "./publish.zip" -Force
```

### Step 8: Deploy to Azure

```bash
az webapp deployment source config-zip --resource-group "rg-mcp-server" --name "graph-mcp" --src "./publish.zip"
```

## Testing the Deployment

### Step 1: Test PRM Endpoint

Open in browser: `https://graph-mcp.azurewebsites.net/.well-known/oauth-protected-resource`

Expected response:
```json
{
  "resource": "https://graph-mcp.azurewebsites.net/sse",
  "authorization_servers": ["https://login.microsoftonline.com/YOUR_TENANT_ID/v2.0"],
  "bearer_methods_supported": ["header"],
  "resource_documentation": "https://docs.example.com/api/mcp"
}
```

### Step 2: Test with MCP Client

Configure your MCP client:
- **Server URL**: `https://graph-mcp.azurewebsites.net`
- **Tools**: Test the available MCP tools

## Available MCP Tools

### 1. GetCurrentUserFromGraph
- **Description**: Get current user information from Microsoft Graph /me endpoint
- **Parameters**: None
- **Returns**: User profile information

### 2. CallGraphApiEndpoint
- **Description**: Universal tool to call any Microsoft Graph API endpoint
- **Parameters**: 
  - `endpoint` (required): Graph API endpoint (e.g., `/v1.0/me`, `/v1.0/users`)
  - `method` (optional): HTTP method (GET, POST, PUT, PATCH, DELETE) - defaults to GET
- **Returns**: JSON response from Graph API

## Troubleshooting

### Common Issues

1. **"Token validation failed"**
   - Check your Entra ID configuration
   - Verify Tenant ID and Client ID are correct
   - Ensure admin consent was granted for API permissions

2. **"On-Behalf-Of flow failed"**
   - Check client secret is correct and not expired
   - Verify the "Expose an API" configuration
   - Ensure the incoming token has the right audience

3. **"No Bearer token found"**
   - Make sure you're sending `Authorization: Bearer <token>` header
   - Verify the token is valid and not expired

4. **"Web app not found" during deployment**
   - The app name must be globally unique
   - Try a different name like `graph-mcp-yourname`

5. **Local development not working on localhost:7071**
   - Run: `dotnet run --urls=http://localhost:7071`
   - Check `Properties/launchSettings.json` has the correct URL

### Debug Commands

**Check web app status**:
```bash
az webapp show --resource-group "rg-mcp-server" --name "graph-mcp" --query "{State:state, HostName:defaultHostName}"
```

**View application logs**:
```bash
az webapp log tail --resource-group "rg-mcp-server" --name "graph-mcp"
```

**Test local server**:
```bash
curl http://localhost:7071/.well-known/oauth-protected-resource
```

## Security Best Practices

1. **Never commit secrets to source control**
2. **Use Azure Key Vault for production secrets**
3. **Regularly rotate client secrets**
4. **Use least-privilege principle for API permissions**
5. **Enable HTTPS only for production**
6. **Monitor and log authentication attempts**

## Support

- **Entra ID issues**: Check Azure Portal → Azure Active Directory → Sign-ins
- **App Service issues**: Check Azure Portal → App Service → Log stream
- **MCP Protocol**: Refer to Model Context Protocol documentation

---

🎉 **Your MCP server with Microsoft Graph API is now ready!**
