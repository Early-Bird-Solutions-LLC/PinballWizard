using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Core.Configuration;

public sealed class BarrelsOfFunOptions
{
    public const string SectionName = "BarrelsOfFun";

    [Required]
    [Url]
    public string BaseUrl { get; set; } = "https://shop.kollectfun.com";

    public int MachineCategoryId { get; set; } = 20;
}
