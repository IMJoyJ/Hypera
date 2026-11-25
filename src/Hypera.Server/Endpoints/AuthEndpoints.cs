using System.Text.Json;
using Hypera.Platform.Services;
using Hypera.Server.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Hypera.Server.Endpoints;

internal static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/auth/login", HandleLoginAsync)
            .AllowAnonymous()
            .DisableAntiforgery();

        endpoints.MapPost("/api/auth/register", HandleRegisterAsync)
            .AllowAnonymous()
            .DisableAntiforgery();

        endpoints.MapMethods("/api/auth/logout", ["POST", "GET"], (Delegate)HandleLogoutAsync)
            .AllowAnonymous()
            .DisableAntiforgery();

        return endpoints;
    }

    private static async Task<IResult> HandleLoginAsync(
        HttpContext httpContext,
        IAuthService authService,
        CancellationToken cancellationToken)
    {
        (bool isJson, AuthPayload? payload) = await ReadAuthPayloadAsync(httpContext, cancellationToken);
        string redirectTarget = AuthClaimsFactory.SanitizeLocalReturnUrl(payload?.ReturnUrl, "/");

        if (payload is null || string.IsNullOrWhiteSpace(payload.Username) || string.IsNullOrEmpty(payload.Password))
        {
            return FailAuthResponse(isJson, StatusCodes.Status400BadRequest, "missing_credentials", "/login", redirectTarget);
        }

        AuthResult result = await authService.AuthenticateAsync(
            payload.Username.Trim(),
            payload.Password,
            cancellationToken);

        if (!result.Succeeded || result.User is null)
        {
            string code = string.IsNullOrWhiteSpace(result.ErrorCode) ? "invalid_credentials" : result.ErrorCode;
            int status = string.Equals(code, "user_disabled", StringComparison.OrdinalIgnoreCase)
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status401Unauthorized;
            return FailAuthResponse(isJson, status, code, "/login", redirectTarget);
        }

        if (result.User.IsDisabled)
        {
            return FailAuthResponse(isJson, StatusCodes.Status403Forbidden, "user_disabled", "/login", redirectTarget);
        }

        await SignInUserAsync(httpContext, result.User);

        if (isJson)
        {
            return Results.Ok(new
            {
                succeeded = true,
                userId = result.User.Id,
                username = result.User.Username,
                role = result.User.Role.ToString(),
                redirectUrl = redirectTarget,
            });
        }

        return Results.LocalRedirect(redirectTarget);
    }

    private static async Task<IResult> HandleRegisterAsync(
        HttpContext httpContext,
        IAuthService authService,
        CancellationToken cancellationToken)
    {
        (bool isJson, AuthPayload? payload) = await ReadAuthPayloadAsync(httpContext, cancellationToken);
        string redirectTarget = AuthClaimsFactory.SanitizeLocalReturnUrl(payload?.ReturnUrl, "/");

        if (payload is null || string.IsNullOrWhiteSpace(payload.Username) || string.IsNullOrEmpty(payload.Password))
        {
            return FailAuthResponse(isJson, StatusCodes.Status400BadRequest, "missing_credentials", "/register", redirectTarget);
        }

        AuthResult result = await authService.RegisterAsync(
            payload.Username.Trim(),
            payload.Password,
            payload.UiLanguage,
            cancellationToken);

        if (!result.Succeeded || result.User is null)
        {
            string code = string.IsNullOrWhiteSpace(result.ErrorCode) ? "registration_failed" : result.ErrorCode;
            int status = string.Equals(code, "registration_closed", StringComparison.OrdinalIgnoreCase)
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status400BadRequest;
            return FailAuthResponse(isJson, status, code, "/register", redirectTarget);
        }

        if (!result.User.IsDisabled)
        {
            await SignInUserAsync(httpContext, result.User);
        }

        if (isJson)
        {
            return Results.Ok(new
            {
                succeeded = true,
                userId = result.User.Id,
                username = result.User.Username,
                role = result.User.Role.ToString(),
                redirectUrl = redirectTarget,
            });
        }

        return Results.LocalRedirect(redirectTarget);
    }

    private static async Task<IResult> HandleLogoutAsync(HttpContext httpContext)
    {
        await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        string? returnUrl = httpContext.Request.Query["returnUrl"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(returnUrl) && httpContext.Request.HasFormContentType)
        {
            IFormCollection form = await httpContext.Request.ReadFormAsync(httpContext.RequestAborted);
            returnUrl = form["returnUrl"].FirstOrDefault();
        }

        string redirectTarget = AuthClaimsFactory.SanitizeLocalReturnUrl(returnUrl, "/login");

        bool wantsJson = httpContext.Request.HasJsonContentType() ||
                         httpContext.Request.Headers.Accept.Any(static h =>
                             h is not null && h.Contains("application/json", StringComparison.OrdinalIgnoreCase));

        if (wantsJson)
        {
            return Results.Ok(new
            {
                succeeded = true,
                redirectUrl = redirectTarget,
            });
        }

        return Results.LocalRedirect(redirectTarget);
    }

    private static Task SignInUserAsync(HttpContext httpContext, Platform.Domain.AppUser user)
    {
        var props = new AuthenticationProperties
        {
            IsPersistent = true,
            AllowRefresh = true,
        };

        return httpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            AuthClaimsFactory.CreatePrincipal(user),
            props);
    }

    private static IResult FailAuthResponse(
        bool isJson,
        int statusCode,
        string errorCode,
        string formPagePath,
        string returnUrl)
    {
        if (isJson)
        {
            return Results.Json(new
            {
                succeeded = false,
                errorCode,
            }, statusCode: statusCode);
        }

        string url = $"{formPagePath}?error={Uri.EscapeDataString(errorCode)}";
        if (!string.Equals(returnUrl, "/", StringComparison.Ordinal))
        {
            url += $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
        }

        return Results.LocalRedirect(url);
    }

    private static async Task<(bool IsJson, AuthPayload? Payload)> ReadAuthPayloadAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (httpContext.Request.HasJsonContentType())
        {
            try
            {
                AuthPayload? jsonPayload = await httpContext.Request.ReadFromJsonAsync<AuthPayload>(
                    new JsonSerializerOptions(JsonSerializerDefaults.Web),
                    cancellationToken);
                return (true, jsonPayload);
            }
            catch (JsonException)
            {
                return (true, null);
            }
        }

        if (httpContext.Request.HasFormContentType)
        {
            IFormCollection form = await httpContext.Request.ReadFormAsync(cancellationToken);
            var formPayload = new AuthPayload(
                Username: form["username"].FirstOrDefault(),
                Password: form["password"].FirstOrDefault(),
                UiLanguage: form["uiLanguage"].FirstOrDefault(),
                ReturnUrl: form["returnUrl"].FirstOrDefault() ?? httpContext.Request.Query["returnUrl"].FirstOrDefault());
            return (false, formPayload);
        }

        return (false, null);
    }

    private sealed record AuthPayload(
        string? Username,
        string? Password,
        string? UiLanguage,
        string? ReturnUrl);
}
