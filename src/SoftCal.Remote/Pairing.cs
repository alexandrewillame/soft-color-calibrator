using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SoftCal.Remote;

/// <summary>
/// Pairing: the phone shows a 6-digit code and the user types it on the PC. Neither side sends the code: each proves it
/// knows it with an HMAC over both sides' random nonces (the PC first), keyed by a hash of the code. A phone that sees
/// wrong proofs five times picks a new code, so guessing it on the network gets nowhere.
/// </summary>
public static class Pairing
{
    /// <summary>Wrong proofs a phone takes before it picks a new code.</summary>
    public const int MaxFailures = 5;

    public static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    public static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

    /// <summary>The digits of a code as typed ("482 913", "482-913" → "482913").</summary>
    public static string Normalize(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return new string([.. code.Where(char.IsAsciiDigit)]);
    }

    public static byte[] Key(string code) => SHA256.HashData(Encoding.UTF8.GetBytes("softcal-pair|" + Normalize(code)));

    public static string PcProof(byte[] key, string pcNonce, string phoneNonce) => Mac(key, "pc", pcNonce, phoneNonce);

    public static string PhoneProof(byte[] key, string pcNonce, string phoneNonce) => Mac(key, "phone", pcNonce, phoneNonce);

    /// <summary>Compares two proofs in constant time.</summary>
    public static bool Same(string expected, string? actual) =>
        actual is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));

    private static string Mac(byte[] key, string role, string pcNonce, string phoneNonce) =>
        Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{role}|{pcNonce}|{phoneNonce}")));
}
