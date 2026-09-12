using Microsoft.Extensions.Configuration;
using ReMind.Functions.Functions;
using ReMind.Functions.Models;

namespace ReMind.UnitTests;

public class SaveDataPointFunctionTests
{
    [Fact]
    public async Task HandleAsync_ReturnsInvalidPayload_WhenPayloadIsMissingRequiredFields()
    {
        var function = CreateFunction(_ => Task.CompletedTask);

        var outcome = await function.HandleAsync(new SaveDataPointRequest(), CancellationToken.None);

        Assert.Same(SaveDataPointOutcome.InvalidPayload, outcome);
    }

    [Fact]
    public async Task HandleAsync_ReturnsInvalidPayload_WhenPayloadIsNull()
    {
        var function = CreateFunction(_ => Task.CompletedTask);

        var outcome = await function.HandleAsync(null, CancellationToken.None);

        Assert.Same(SaveDataPointOutcome.InvalidPayload, outcome);
    }

    [Fact]
    public async Task HandleAsync_ReturnsInvalidPayload_WhenDescriptionIsBlank()
    {
        var function = CreateFunction(_ => Task.CompletedTask);
        var request = CreateValidRequest();
        request.Description = " ";

        var outcome = await function.HandleAsync(request, CancellationToken.None);

        Assert.Same(SaveDataPointOutcome.InvalidPayload, outcome);
    }

    [Fact]
    public async Task HandleAsync_ReturnsInvalidPayload_WhenEventDateIsNull()
    {
        var function = CreateFunction(_ => Task.CompletedTask);
        var request = CreateValidRequest();
        request.EventDate = null;

        var outcome = await function.HandleAsync(request, CancellationToken.None);

        Assert.Same(SaveDataPointOutcome.InvalidPayload, outcome);
    }

    [Fact]
    public async Task HandleAsync_ReturnsInvalidPayload_WhenEventDateIsDefault()
    {
        var function = CreateFunction(_ => Task.CompletedTask);
        var request = CreateValidRequest();
        request.EventDate = default;

        var outcome = await function.HandleAsync(request, CancellationToken.None);

        Assert.Same(SaveDataPointOutcome.InvalidPayload, outcome);
    }

    [Fact]
    public async Task HandleAsync_ReturnsMissingConnectionString_WhenConfigurationIsMissing()
    {
        var function = CreateFunction(_ => Task.CompletedTask, connectionString: null);

        var outcome = await function.HandleAsync(CreateValidRequest(), CancellationToken.None);

        Assert.Same(SaveDataPointOutcome.MissingConnectionString, outcome);
    }

    [Fact]
    public async Task HandleAsync_ReturnsSuccess_WhenSaveCompletes()
    {
        SaveDataPointRequest? capturedRequest = null;
        var function = CreateFunction(request =>
        {
            capturedRequest = request;
            return Task.CompletedTask;
        });

        var request = CreateValidRequest();
        var outcome = await function.HandleAsync(request, CancellationToken.None);

        Assert.Same(SaveDataPointOutcome.Success, outcome);
        Assert.Same(request, capturedRequest);
    }

    [Fact]
    public async Task HandleAsync_ReturnsSaveFailed_WhenSaveThrows()
    {
        var function = CreateFunction(_ => throw new InvalidOperationException("boom"));

        var outcome = await function.HandleAsync(CreateValidRequest(), CancellationToken.None);

        Assert.Same(SaveDataPointOutcome.SaveFailed, outcome);
    }

    [Fact]
    public async Task HandleAsync_RethrowsCancellation_WhenSaveIsCancelled()
    {
        var function = CreateFunction(_ => throw new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            function.HandleAsync(CreateValidRequest(), CancellationToken.None));
    }

    private static TestableSaveDataPointFunction CreateFunction(
        Func<SaveDataPointRequest, Task> saveAsync,
        string? connectionString = "Server=test;Database=ReMind;Encrypt=False;")
    {
        var values = new Dictionary<string, string?>();
        if (connectionString is not null)
        {
            values["SqlConnectionString"] = connectionString;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        return new TestableSaveDataPointFunction(configuration, saveAsync);
    }

    private static SaveDataPointRequest CreateValidRequest() =>
        new()
        {
            Location = "Athens",
            EventDate = new DateTime(2024, 6, 1, 9, 15, 0, DateTimeKind.Utc),
            Description = "Recorded a historical milestone."
        };

    private sealed class TestableSaveDataPointFunction(
        IConfiguration configuration,
        Func<SaveDataPointRequest, Task> saveAsync) : SaveDataPointFunction(configuration)
    {
        protected override Task SaveDataPointAsync(
            SaveDataPointRequest payload,
            CancellationToken cancellationToken) =>
            saveAsync(payload);
    }
}
