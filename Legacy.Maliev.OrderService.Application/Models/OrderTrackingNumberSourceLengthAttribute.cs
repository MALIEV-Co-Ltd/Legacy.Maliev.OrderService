using System.ComponentModel.DataAnnotations;

namespace Legacy.Maliev.OrderService.Application.Models;

/// <summary>Preserves the source tracking bound while allowing only excess ASCII padding.</summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
public sealed class OrderTrackingNumberSourceLengthAttribute() : ValidationAttribute("The {0} field is too long.")
{
    /// <summary>The source storage capacity in UTF-16 code units.</summary>
    public const int MaximumLength = 250;

    /// <summary>Accepts null and literals whose excess consists entirely of U+0020.</summary>
    public static bool IsAllowed(string? value)
    {
        if (value is null || value.Length <= MaximumLength) return true;
        for (var index = MaximumLength; index < value.Length; index++)
            if (value[index] != ' ') return false;
        return true;
    }

    /// <inheritdoc />
    public override bool IsValid(object? value) => value is null || value is string text && IsAllowed(text);
}
