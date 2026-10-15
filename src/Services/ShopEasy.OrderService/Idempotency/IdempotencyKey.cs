using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ShopEasy.OrderService.Services;

namespace ShopEasy.OrderService.Idempotency;

// The Idempotency-Key request header: a value the client makes up (a UUID is the usual choice)
// and sends again, unchanged, when it repeats a request because it did not see the answer.
public static partial class IdempotencyKey
{
    public const string HeaderName = "Idempotency-Key";
    public const int MaxLength = 128;

    [GeneratedRegex(@"^[A-Za-z0-9._~:\-]{1,128}$")]
    private static partial Regex Allowed();

    // True when the header is acceptable: absent (key is null, unless one is required) or well formed.
    // Otherwise error says what is wrong, for the 400 response.
    public static bool TryParse(string? header, bool required, out string? key, out string? error)
    {
        key = null;
        error = null;

        if (string.IsNullOrWhiteSpace(header))
        {
            if (!required) return true;
            error = $"The {HeaderName} header is required.";
            return false;
        }

        var value = header.Trim();
        if (!Allowed().IsMatch(value))
        {
            error = $"The {HeaderName} header must be 1-{MaxLength} characters: letters, digits and . _ ~ : -";
            return false;
        }

        key = value;
        return true;
    }
}

// Identifies what an order request asks for, so that a key reused for a different request can be
// told apart from a retry of the same one. Built from the validated request, not the raw JSON:
// property order, whitespace, ignored fields (productName, unitPrice) and the order or splitting of
// the lines do not matter, only what would be bought and the note.
public static class OrderRequestFingerprint
{
    public static string Compute(IReadOnlyList<RequestedLine> lines, string? notes)
    {
        var canonical = new StringBuilder("order/v1");
        foreach (var line in lines.OrderBy(l => l.ProductId))
            canonical.Append('|').Append(line.ProductId.ToString("N")).Append('x').Append(line.Quantity);
        canonical.Append("|notes=").Append(notes?.Trim() ?? "");

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }
}
