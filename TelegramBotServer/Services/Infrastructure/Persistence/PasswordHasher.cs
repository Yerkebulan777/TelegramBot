using System.Security.Cryptography;

namespace TelegramBotServer.Services;

/// <summary>
/// Provides PBKDF2-based password hashing and verification using built-in .NET cryptography.
/// </summary>
public static class PasswordHasher
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 100_000;

    /// <summary>
    /// Hashes a plain-text password using PBKDF2 with a random salt.
    /// Returns a base64-encoded string containing salt + hash.
    /// </summary>
    public static string Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);

        byte[] result = new byte[SaltSize + HashSize];
        salt.CopyTo(result, 0);
        hash.CopyTo(result, SaltSize);

        return Convert.ToBase64String(result);
    }

    /// <summary>
    /// Verifies a plain-text password against a stored hash.
    /// </summary>
    public static bool Verify(string password, string storedHash)
    {
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(storedHash);
        }
        catch (FormatException)
        {
            // Not a valid hash (e.g. plain-text legacy password) — compare directly
            return string.Equals(password, storedHash, StringComparison.Ordinal);
        }

        if (decoded.Length != SaltSize + HashSize)
        {
            // Legacy plain-text password stored in DB
            return string.Equals(password, storedHash, StringComparison.Ordinal);
        }

        byte[] salt = decoded[..SaltSize];
        byte[] expectedHash = decoded[SaltSize..];

        byte[] actualHash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);

        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
}
