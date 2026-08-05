using FluentValidation;

namespace AgentHost.Api.Validation;

/// <summary>
/// Shared password rules for every place a password is set (registration, admin user creation,
/// user update). NIST SP 800-63B's guidance is length + a blocklist of known-bad choices rather
/// than composition rules, so that is what this implements — with no external service or
/// network dependency.
/// </summary>
public static class PasswordPolicy
{
    public const int MinimumLength = 12;

    /// <summary>
    /// Small embedded blocklist of the passwords that dominate every credential-stuffing list.
    /// Compared case-insensitively. This is deliberately short: it is a sanity floor, not a
    /// substitute for a full breached-password corpus (which would need a network dependency).
    /// </summary>
    private static readonly HashSet<string> CommonPasswords = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "password1", "password12", "password123", "password1234",
        "passw0rd123", "123456789012", "1234567890", "12345678", "123456789",
        "qwertyuiop", "qwerty123456", "letmein12345", "iloveyou1234",
        "administrator", "adminadmin", "welcome12345", "welcome123",
        "changeme123", "p@ssw0rd123", "trustno1234", "monkey123456",
        "football1234", "dragon123456", "baseball1234", "sunshine1234",
        "princess1234", "superman1234", "abc123456789", "1qaz2wsx3edc",
    };

    public static bool IsAllSameCharacter(string value) =>
        value.Length > 0 && value.All(c => c == value[0]);

    public static bool IsCommon(string value) => CommonPasswords.Contains(value);

    /// <summary>Applies the full policy to a password property on any validator.</summary>
    public static IRuleBuilderOptions<T, string> Password<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty()
            .MinimumLength(MinimumLength)
                .WithMessage($"Password must be at least {MinimumLength} characters")
            .Must(p => !IsAllSameCharacter(p))
                .WithMessage("Password must not be a single repeated character")
            .Must(p => !IsCommon(p))
                .WithMessage("Password is too common; choose something less guessable");
}
