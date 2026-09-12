using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using ReMind.Frontend.Models;
using ReMind.Frontend.Pages;

namespace ReMind.UnitTests;

public class IndexModelTests
{
    [Fact]
    public async Task OnPostAsync_ReturnsPage_WhenModelStateIsInvalid()
    {
        var model = CreateModel(_ => throw new InvalidOperationException("Request should not be sent."));
        model.ModelState.AddModelError("Input.Location", "Required");

        var result = await model.OnPostAsync();

        Assert.IsType<PageResult>(result);
    }

    [Fact]
    public async Task OnPostAsync_AddsModelError_WhenFunctionUrlIsMissing()
    {
        var model = CreateModel(_ => throw new InvalidOperationException("Request should not be sent."), null);

        var result = await model.OnPostAsync();

        Assert.IsType<PageResult>(result);
        var error = Assert.Single(model.ModelState[string.Empty]!.Errors);
        Assert.Equal("Function endpoint is not configured.", error.ErrorMessage);
    }

    [Fact]
    public async Task OnPostAsync_ResetsInputAndShowsSuccess_WhenSaveSucceeds()
    {
        HttpRequestMessage? capturedRequest = null;
        var model = CreateModel(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.Created);
        });

        var result = await model.OnPostAsync();

        Assert.IsType<PageResult>(result);
        Assert.Equal("Data point saved successfully.", model.StatusMessage);
        Assert.Equal(string.Empty, model.Input.Location);
        Assert.Null(model.Input.EventDate);
        Assert.Equal(string.Empty, model.Input.Description);

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest.Method);
        Assert.Equal("https://example.test/api/datapoints", capturedRequest.RequestUri!.ToString());
        var payload = await capturedRequest.Content!.ReadFromJsonAsync<SaveDataPointRequest>();
        Assert.NotNull(payload);
        Assert.Equal("Rome", payload.Location);
        Assert.Equal(new DateTime(2024, 4, 5, 14, 30, 0, DateTimeKind.Utc), payload.EventDate);
        Assert.Equal("Observed a remarkable event.", payload.Description);
    }

    [Fact]
    public async Task OnPostAsync_AddsModelError_WhenSaveFails()
    {
        var model = CreateModel(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var result = await model.OnPostAsync();

        Assert.IsType<PageResult>(result);
        Assert.Null(model.StatusMessage);
        var error = Assert.Single(model.ModelState[string.Empty]!.Errors);
        Assert.Equal("Save failed (502).", error.ErrorMessage);
    }

    private static IndexModel CreateModel(
        Func<HttpRequestMessage, HttpResponseMessage> send,
        string? functionUrl = "https://example.test/api/datapoints")
    {
        var configValues = new Dictionary<string, string?>();
        if (functionUrl is not null)
        {
            configValues["SaveDataPointFunctionUrl"] = functionUrl;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var client = new HttpClient(new StubHttpMessageHandler(send));
        var model = new IndexModel(new StubHttpClientFactory(client), configuration)
        {
            Input = new SaveDataPointRequest
            {
                Location = "Rome",
                EventDate = new DateTime(2024, 4, 5, 14, 30, 0, DateTimeKind.Utc),
                Description = "Observed a remarkable event."
            },
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        return model;
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
}
