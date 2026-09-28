using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace RateMyApi.Api.Security;

/// <summary>Requires <c>X-Api-Key</c> to match <see cref="ApiOptions.Key"/> (when a key is configured).</summary>
public sealed class ApiKeyEndpointFilter(IOptions<ApiOptions> options) : IEndpointFilter
{
    public const string HeaderName = "X-Api-Key";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var expected = options.Value.Key;
        if (string.IsNullOrEmpty(expected))
            return await next(context);

        var provided = context.HttpContext.Request.Headers[HeaderName].ToString();
        if (!KeysMatch(provided, expected))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Unauthorized",
                detail: $"A valid {HeaderName} header is required.");
        }

        return await next(context);
    }

    private static bool KeysMatch(string provided, string expected)
    {
        if (provided.Length == 0)
            return false;

        // Hash first so the comparison is constant-time regardless of length.
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
