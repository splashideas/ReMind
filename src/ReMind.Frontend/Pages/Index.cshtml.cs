using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
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
        var functionKey = configuration["SaveDataPointFunctionKey"];
        if (string.IsNullOrWhiteSpace(functionUrl))
        {
            ModelState.AddModelError(string.Empty, "Function endpoint is not configured.");
            return Page();
        }

        if (string.IsNullOrWhiteSpace(functionKey) &&
            Uri.TryCreate(functionUrl, UriKind.Absolute, out var functionUri) &&
            QueryHelpers.ParseQuery(functionUri.Query).TryGetValue("code", out var codeValues))
        {
            functionKey = codeValues.ToString();
        }

        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, functionUrl)
        {
            Content = JsonContent.Create(Input)
        };

        if (!string.IsNullOrWhiteSpace(functionKey))
        {
            request.Headers.TryAddWithoutValidation("x-functions-key", functionKey);
        }

        using var response = await client.SendAsync(request, HttpContext.RequestAborted);

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
