using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace ReMind.PlaywrightTests;

public class SaveWorkflowTests : IClassFixture<SaveWorkflowFixture>
{
    private readonly SaveWorkflowFixture _fixture;

    public SaveWorkflowTests(SaveWorkflowFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SaveWorkflow_PersistsSubmittedData()
    {
        await using var page = await _fixture.Browser.NewPageAsync(new BrowserNewPageOptions
        {
            IgnoreHTTPSErrors = true
        });

        await page.GotoAsync(_fixture.FrontendBaseUrl);
        await page.GetByLabel("Location").FillAsync("Alexandria");
        await page.GetByLabel("Event date").FillAsync("2024-08-15T13:45");
        await page.GetByLabel("Description").FillAsync("Cataloged a significant archive discovery.");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save data point" }).ClickAsync();

        await Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Data point saved successfully.");

        await using var connection = new SqliteConnection(_fixture.DatabaseConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT Location, EventDate, Description
                              FROM DataPoints
                              ORDER BY DataPointId DESC
                              LIMIT 1;
                              """;

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Alexandria", reader.GetString(0));
        Assert.Equal(
            new DateTime(2024, 8, 15, 13, 45, 0),
            DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        Assert.Equal("Cataloged a significant archive discovery.", reader.GetString(2));
    }

    [Fact]
    public async Task SaveWorkflow_ShowsValidationErrors_WhenRequiredFieldsAreMissing()
    {
        await using var page = await _fixture.Browser.NewPageAsync(new BrowserNewPageOptions
        {
            IgnoreHTTPSErrors = true
        });

        await page.GotoAsync(_fixture.FrontendBaseUrl);
        await page.GetByRole(AriaRole.Button, new() { Name = "Save data point" }).ClickAsync();

        await Expect(page.GetByText("The Location field is required.")).ToBeVisibleAsync();
        await Expect(page.GetByText("The EventDate field is required.")).ToBeVisibleAsync();
        await Expect(page.GetByText("The Description field is required.")).ToBeVisibleAsync();
    }
}

public sealed class SaveWorkflowFixture : IAsyncLifetime
{
    private IHost? _backendHost;
    private Process? _frontendProcess;
    private IPlaywright? _playwright;
    private string? _databasePath;

    public IBrowser Browser { get; private set; } = null!;
    public string FrontendBaseUrl { get; private set; } = string.Empty;
    public string DatabaseConnectionString { get; private set; } = string.Empty;

    public SaveWorkflowFixture()
    {
    }

    public async Task InitializeAsync()
    {
        var backendPort = GetFreePort();
        var frontendPort = GetFreePort();
        _databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        DatabaseConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath
        }.ToString();

        await CreateDatabaseAsync(DatabaseConnectionString);
        _backendHost = await StartBackendAsync(backendPort, DatabaseConnectionString);
        FrontendBaseUrl = $"http://127.0.0.1:{frontendPort}";
        _frontendProcess = StartFrontend(frontendPort, backendPort);
        await WaitForUrlAsync(FrontendBaseUrl);

        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.DisposeAsync();
        }

        _playwright?.Dispose();

        if (_frontendProcess is not null && !_frontendProcess.HasExited)
        {
            _frontendProcess.Kill(entireProcessTree: true);
            await _frontendProcess.WaitForExitAsync();
        }

        if (_backendHost is not null)
        {
            await _backendHost.StopAsync();
            _backendHost.Dispose();
        }

        if (_databasePath is not null && File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    private static async Task CreateDatabaseAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
                              CREATE TABLE DataPoints
                              (
                                  DataPointId INTEGER PRIMARY KEY AUTOINCREMENT,
                                  Location TEXT NOT NULL,
                                  EventDate TEXT NOT NULL,
                                  Description TEXT NOT NULL
                              );
                              """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<IHost> StartBackendAsync(int backendPort, string connectionString)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{backendPort}");

        var app = builder.Build();
        app.MapPost("/api/datapoints", async (HttpContext context) =>
        {
            var payload = await JsonSerializer.DeserializeAsync<SaveRequest>(
                context.Request.Body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web),
                context.RequestAborted);

            if (payload is null ||
                string.IsNullOrWhiteSpace(payload.Location) ||
                string.IsNullOrWhiteSpace(payload.Description) ||
                string.IsNullOrWhiteSpace(payload.EventDate))
            {
                return Results.BadRequest("Invalid request payload.");
            }

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(context.RequestAborted);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                                  INSERT INTO DataPoints (Location, EventDate, Description)
                                  VALUES ($location, $eventDate, $description);
                                  """;
            command.Parameters.AddWithValue("$location", payload.Location);
            command.Parameters.AddWithValue("$eventDate", payload.EventDate);
            command.Parameters.AddWithValue("$description", payload.Description);
            await command.ExecuteNonQueryAsync(context.RequestAborted);

            return Results.Created("/api/datapoints", new { saved = true });
        });

        await app.StartAsync();
        return app;
    }

    private static Process StartFrontend(int frontendPort, int backendPort)
    {
        var projectPath = GetFrontendProjectPath();
        var startInfo = new ProcessStartInfo(
            "dotnet",
            $"run --no-build --no-launch-profile --project \"{projectPath}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{frontendPort}";
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["SaveDataPointFunctionUrl"] = $"http://127.0.0.1:{backendPort}/api/datapoints";

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start frontend process.");
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task WaitForUrlAsync(string url)
    {
        using var client = new HttpClient();

        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(url);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"Timed out waiting for {url}.");
    }

    private static string GetFrontendProjectPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ReMind.sln")))
            {
                return Path.Combine(directory.FullName, "src", "ReMind.Frontend", "ReMind.Frontend.csproj");
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed record SaveRequest(string Location, string EventDate, string Description);
}
