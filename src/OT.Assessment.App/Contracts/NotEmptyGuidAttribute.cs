using System.ComponentModel.DataAnnotations;

namespace OT.Assessment.App.Contracts;

/// <summary>
/// <see cref="RequiredAttribute"/> treats <see cref="Guid.Empty"/> as present. An empty id would
/// otherwise be published and then dead-lettered, after the API had already returned 200.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class NotEmptyGuidAttribute : ValidationAttribute
{
    public NotEmptyGuidAttribute() : base("The {0} field must not be an empty guid.")
    {
    }

    public override bool IsValid(object value)
    {
        if (value is null)
        {
            return true;
        }

        return value is Guid guid && guid != Guid.Empty;
    }
}
