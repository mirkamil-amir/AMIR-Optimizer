using System.Security.Cryptography;

namespace PortfolioOptimizer.Infrastructure.Identity;

/// <summary>
/// One-time passwords for administrator resets: four groups of four characters from a cryptographic random source, e.g.
/// <c>Kp7d-Rm3x-…</c> (about 90 bits). Look-alike characters (0/O, 1/l/I) are left out because the value is read aloud or
/// typed from a message; every value has an upper-case letter, a lower-case letter and a digit, as the password policy asks.
/// </summary>
public static class OneTimeCredentials
{
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnopqrstuvwxyz";
    private const string Digits = "23456789";
    private const string Alphabet = Upper + Lower + Digits;

    public static string Generate()
    {
        while (true)
        {
            var chars = new char[16];
            for (var i = 0; i < chars.Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            if (chars.Any(Upper.Contains) && chars.Any(Lower.Contains) && chars.Any(Digits.Contains))
                return string.Join('-', chars.Chunk(4).Select(c => new string(c)));
        }
    }
}
