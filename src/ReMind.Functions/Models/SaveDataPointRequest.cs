namespace ReMind.Functions.Models;

public sealed class SaveDataPointRequest
{
    public string Location { get; set; } = string.Empty;

    public DateTime? EventDate { get; set; }

    public string Description { get; set; } = string.Empty;
}
