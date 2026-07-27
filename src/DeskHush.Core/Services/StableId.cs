using System.Security.Cryptography;
using System.Text;

namespace DeskHush.Core.Services;

public static class StableId
{
    public static string Create(params string?[] parts)
    {
        var input = string.Join('\u001f', parts.Select(part => part?.Trim().ToUpperInvariant() ?? string.Empty));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }
}
