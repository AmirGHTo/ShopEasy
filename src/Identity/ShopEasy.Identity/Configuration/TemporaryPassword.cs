using System.Security.Cryptography;

namespace ShopEasy.Identity.Configuration;

// The one-time password an administrator hands to a user who cannot sign in. Random, always within
// IdentityPolicy, and written from an alphabet without look-alike characters (no 0 and O, 1 and l
// and I) because it is read from a screen and typed in once.
public static class TemporaryPassword
{
    public const int Length = 16;

    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnpqrstuvwxyz";
    private const string Digits = "23456789";
    private const string Alphabet = Upper + Lower + Digits;

    public static string Generate()
    {
        var chars = new char[Length];
        // one of each kind the policy asks for, the rest from everything, then in random order
        chars[0] = Pick(Upper);
        chars[1] = Pick(Lower);
        chars[2] = Pick(Digits);
        for (var i = 3; i < chars.Length; i++)
            chars[i] = Pick(Alphabet);
        RandomNumberGenerator.Shuffle(chars.AsSpan());
        return new string(chars);
    }

    public static bool UsesOnlyTheAlphabet(string password) => password.All(c => Alphabet.Contains(c));

    private static char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];
}

// What setting a temporary password answered. The password is the one thing here that must never
// reach a log or a message, so the record does not print it.
public sealed record TemporaryPasswordOutcome(bool Succeeded, string Message, string? Email = null, string? Password = null)
{
    public static TemporaryPasswordOutcome Refused(string message) => new(false, message);

    public override string ToString() => $"TemporaryPasswordOutcome {{ Succeeded = {Succeeded}, Message = {Message}, Email = {Email} }}";
}
