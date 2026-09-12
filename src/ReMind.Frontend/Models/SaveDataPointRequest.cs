using System.ComponentModel.DataAnnotations;

namespace ReMind.Frontend.Models;

public sealed class SaveDataPointRequest
{
    [Required]
    [StringLength(200)]
    public string Location { get; set; } = string.Empty;

    [Required]
    public DateTime EventDate { get; set; }

    [Required]
    [StringLength(4000)]
    public string Description { get; set; } = string.Empty;
}
