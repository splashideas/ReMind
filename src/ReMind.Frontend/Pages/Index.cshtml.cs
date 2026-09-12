using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReMind.Frontend.Models;

namespace ReMind.Frontend.Pages;

public class IndexModel(IHttpClientFactory httpClientFactory, IConfiguration configuration) : PageModel
{
    [BindProperty]
    public SaveDataPointRequest Input { get; set; } = new();

    public string? StatusMessage { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var functionUrl = configuration["SaveDataPointFunctionUrl"];
        if (string.IsNullOrWhiteSpace(functionUrl))
        {
            ModelState.AddModelError(string.Empty, "Function endpoint is not configured.");
            return Page();
        }

        var client = httpClientFactory.CreateClient();
        var response = await client.PostAsJsonAsync(functionUrl, Input, HttpContext.RequestAborted);

        if (response.IsSuccessStatusCode)
        {
            StatusMessage = "Data point saved successfully.";
            ModelState.Clear();
            Input = new SaveDataPointRequest();
            return Page();
        }

        ModelState.AddModelError(string.Empty, $"Save failed ({(int)response.StatusCode}).");
        return Page();
    }
}
