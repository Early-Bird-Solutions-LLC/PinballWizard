using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Core.Configuration;

public sealed class MultimorphicOptions
{
    public const string SectionName = "Multimorphic";

    [Required]
    [Url]
    public string BaseUrl { get; set; } = "https://www.multimorphic.com";

    public int MachineCategoryId { get; set; } = 85;
}
