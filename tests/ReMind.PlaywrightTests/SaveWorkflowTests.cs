using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Data.SqlClient;
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

        await using var connection = new SqlConnection(_fixture.DatabaseConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT TOP (1) Location, EventDate, Description
                              FROM dbo.DataPoints
                              ORDER BY DataPointId DESC;
                              """;

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Alexandria", reader.GetString(0));
        Assert.Equal(new DateTime(2024, 8, 15, 13, 45, 0), reader.GetDateTime(1));
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
    private const string SqlImage = "mcr.microsoft.com/mssql/server:2022-latest";
    private const string AzuriteImage = "mcr.microsoft.com/azure-storage/azurite:latest";
    private const string SqlSaPassword = "ReMind_Test_Password_123";

    private Process? _functionsProcess;
    private Process? _frontendProcess;
    private IPlaywright? _playwright;
    private string? _sqlContainerId;
    private string? _azuriteContainerId;
    private readonly string _containerSuffix = Guid.NewGuid().ToString("N")[..8];

    public IBrowser Browser { get; private set; } = null!;
    public string FrontendBaseUrl { get; private set; } = string.Empty;
    public string DatabaseConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var repoRoot = GetRepositoryRoot();
        var sqlContainerName = $"remind-playwright-sql-{_containerSuffix}";
        var azuriteContainerName = $"remind-playwright-azurite-{_containerSuffix}";

        _sqlContainerId = await StartContainerAsync(
            sqlContainerName,
            [
                "run", "-d", "--rm",
                "--name", sqlContainerName,
                "-e", "ACCEPT_EULA=Y",
                "-e", $"MSSQL_SA_PASSWORD={SqlSaPassword}",
                "-p", "127.0.0.1::1433",
                SqlImage
            ]);

        _azuriteContainerId = await StartContainerAsync(
            azuriteContainerName,
            [
                "run", "-d", "--rm",
                "--name", azuriteContainerName,
                "-p", "127.0.0.1::10000",
                "-p", "127.0.0.1::10001",
                "-p", "127.0.0.1::10002",
                AzuriteImage
            ]);

        var sqlPort = await GetMappedPortAsync(_sqlContainerId, 1433);
        var azuriteBlobPort = await GetMappedPortAsync(_azuriteContainerId, 10000);
        var azuriteQueuePort = await GetMappedPortAsync(_azuriteContainerId, 10001);
        var azuriteTablePort = await GetMappedPortAsync(_azuriteContainerId, 10002);

        DatabaseConnectionString = BuildSqlConnectionString(sqlPort, "ReMindPlaywright");

        await WaitForSqlServerAsync(BuildSqlConnectionString(sqlPort, "master"));
        await CreateDatabaseAsync(DatabaseConnectionString, repoRoot);

        var storageConnectionString =
            "DefaultEndpointsProtocol=http;" +
            "AccountName=devstoreaccount1;" +
            "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;" +
            $"BlobEndpoint=http://127.0.0.1:{azuriteBlobPort}/devstoreaccount1;" +
            $"QueueEndpoint=http://127.0.0.1:{azuriteQueuePort}/devstoreaccount1;" +
            $"TableEndpoint=http://127.0.0.1:{azuriteTablePort}/devstoreaccount1;";

        (_functionsProcess, var functionsBaseUrl) = await StartFunctionsAsync(
            repoRoot,
            DatabaseConnectionString,
            storageConnectionString);

        (_frontendProcess, var frontendBaseUrl) = await StartFrontendAsync(repoRoot, functionsBaseUrl);
        FrontendBaseUrl = frontendBaseUrl;
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

        await StopProcessAsync(_frontendProcess);
        await StopProcessAsync(_functionsProcess);

        await StopContainerAsync(_azuriteContainerId);
        await StopContainerAsync(_sqlContainerId);
    }

    private static string BuildSqlConnectionString(int port, string database) =>
        new SqlConnectionStringBuilder
        {
            DataSource = $"127.0.0.1,{port}",
            InitialCatalog = database,
            UserID = "sa",
            Password = SqlSaPassword,
            TrustServerCertificate = true,
            Encrypt = false
        }.ConnectionString;

    private static async Task CreateDatabaseAsync(string connectionString, string repoRoot)
    {
        var masterConnectionString = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = "master"
        }.ConnectionString;

        await using (var masterConnection = new SqlConnection(masterConnectionString))
        {
            await masterConnection.OpenAsync();
            await using var createDb = masterConnection.CreateCommand();
            createDb.CommandText = """
                                   IF DB_ID(N'ReMindPlaywright') IS NULL
                                   BEGIN
                                       CREATE DATABASE [ReMindPlaywright];
                                   END
                                   """;
            await createDb.ExecuteNonQueryAsync();
        }

        var schemaPath = Path.Join(repoRoot, "database", "ReMind.Database", "Schema", "Tables", "DataPoints.sql");
        var schemaSql = await File.ReadAllTextAsync(schemaPath);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = schemaSql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(Process Process, string BaseUrl)> StartFunctionsAsync(
        string repoRoot,
        string sqlConnectionString,
        string storageConnectionString)
    {
        EnsureCommandAvailable("func", "Azure Functions Core Tools (func) is required to run the Playwright save workflow tests.");

        var projectPath = Path.Join(repoRoot, "src", "ReMind.Functions");
        var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new StringBuilder();
        var startInfo = new ProcessStartInfo("func", "start --port 0")
        {
            WorkingDirectory = projectPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        startInfo.Environment["FUNCTIONS_WORKER_RUNTIME"] = "dotnet-isolated";
        startInfo.Environment["AzureWebJobsStorage"] = storageConnectionString;
        startInfo.Environment["SqlConnectionString"] = sqlConnectionString;
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        void HandleOutput(string? data)
        {
            if (data is null)
            {
                return;
            }

            lock (output)
            {
                output.AppendLine(data);
            }

            const string routeSuffix = "/api/datapoints";
            var routeIndex = data.IndexOf(routeSuffix, StringComparison.OrdinalIgnoreCase);
            var httpIndex = routeIndex > 0
                ? data.LastIndexOf("http://", routeIndex, StringComparison.OrdinalIgnoreCase)
                : -1;
            var httpsIndex = routeIndex > 0
                ? data.LastIndexOf("https://", routeIndex, StringComparison.OrdinalIgnoreCase)
                : -1;
            var baseUrlStartIndex = Math.Max(httpIndex, httpsIndex);
            if (baseUrlStartIndex >= 0 &&
                Uri.TryCreate(data[baseUrlStartIndex..routeIndex], UriKind.Absolute, out var baseUri))
            {
                started.TrySetResult(baseUri.GetLeftPart(UriPartial.Authority));
            }
        }

        process.Exited += (_, _) =>
        {
            started.TrySetException(new InvalidOperationException(
                "Functions process exited before startup completed." + Environment.NewLine + output));
        };
        process.OutputDataReceived += (_, args) => HandleOutput(args.Data);
        process.ErrorDataReceived += (_, args) => HandleOutput(args.Data);

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start Functions process.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            using var registration = timeout.Token.Register(() =>
                started.TrySetException(new TimeoutException(
                    "Timed out waiting for Functions host startup." + Environment.NewLine + output)));

            var baseUrl = await started.Task;
            await WaitForUrlAsync($"{baseUrl}/", allowNonSuccessStatus: true);

            return (process, baseUrl);
        }
        catch
        {
            await StopProcessAsync(process);
            throw;
        }
    }

    private static async Task<(Process Process, string BaseUrl)> StartFrontendAsync(
        string repoRoot,
        string functionsBaseUrl)
    {
        var projectPath = Path.Join(repoRoot, "src", "ReMind.Frontend", "ReMind.Frontend.csproj");
        var configuration = GetBuildConfiguration();
        var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startInfo = new ProcessStartInfo(
            "dotnet",
            $"run --no-build -c {configuration} --no-launch-profile --project \"{projectPath}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        startInfo.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["SaveDataPointFunctionUrl"] = $"{functionsBaseUrl}/api/datapoints";

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        process.Exited += (_, _) =>
        {
            started.TrySetException(new InvalidOperationException("Frontend process exited before startup completed."));
        };
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                return;
            }

            const string prefix = "Now listening on:";
            var index = args.Data.IndexOf(prefix, StringComparison.Ordinal);
            if (index < 0)
            {
                return;
            }

            var url = args.Data[(index + prefix.Length)..].Trim().TrimEnd('/');
            if (url.Length > 0)
            {
                started.TrySetResult(url);
            }
        };
        process.ErrorDataReceived += (_, _) => { };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start frontend process.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var registration = timeout.Token.Register(() => started.TrySetCanceled(timeout.Token));
            var frontendBaseUrl = await started.Task;

            return (process, frontendBaseUrl);
        }
        catch
        {
            await StopProcessAsync(process);
            throw;
        }
    }

    private static async Task WaitForSqlServerAsync(string masterConnectionString)
    {
        Exception? lastException = null;

        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                await using var connection = new SqlConnection(masterConnectionString);
                await connection.OpenAsync();
                return;
            }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException or TimeoutException)
            {
                lastException = ex;
                await Task.Delay(1000);
            }
        }

        throw new TimeoutException("Timed out waiting for SQL Server container.", lastException);
    }

    private static async Task WaitForUrlAsync(string url, bool allowNonSuccessStatus = false)
    {
        using var client = new HttpClient();
        HttpRequestException? lastHttpRequestException = null;

        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(url);
                if (allowNonSuccessStatus || response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException ex)
            {
                lastHttpRequestException = ex;
            }

            await Task.Delay(500);
        }

        throw lastHttpRequestException is null
            ? new TimeoutException($"Timed out waiting for {url}.")
            : new TimeoutException($"Timed out waiting for {url}.", lastHttpRequestException);
    }

    private static async Task<string> StartContainerAsync(string name, IReadOnlyList<string> args)
    {
        EnsureCommandAvailable("docker", "Docker is required to run the Playwright save workflow tests.");

        var containerId = (await RunDockerAsync(args)).Trim();
        if (string.IsNullOrWhiteSpace(containerId))
        {
            throw new InvalidOperationException($"Failed to start Docker container '{name}'.");
        }

        return containerId;
    }

    private static async Task StopContainerAsync(string? containerId)
    {
        if (string.IsNullOrWhiteSpace(containerId))
        {
            return;
        }

        await RunDockerAsync(["rm", "-f", containerId], allowFailure: true);
    }

    private static async Task<int> GetMappedPortAsync(string containerId, int containerPort)
    {
        var mapping = (await RunDockerAsync(["port", containerId, containerPort.ToString()])).Trim();
        if (string.IsNullOrWhiteSpace(mapping))
        {
            throw new InvalidOperationException(
                $"No mapped host port found for container '{containerId}' port {containerPort}.");
        }

        var hostEndpoint = mapping.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        var separatorIndex = hostEndpoint.LastIndexOf(':');
        if (separatorIndex < 0 || !int.TryParse(hostEndpoint[(separatorIndex + 1)..], out var hostPort))
        {
            throw new InvalidOperationException(
                $"Unable to parse mapped host port from Docker output '{hostEndpoint}'.");
        }

        return hostPort;
    }

    private static async Task StopProcessAsync(Process? process)
    {
        if (process is null)
        {
            return;
        }

        using var ownedProcess = process;
        try
        {
            if (!ownedProcess.HasExited)
            {
                ownedProcess.Kill(entireProcessTree: true);
                await ownedProcess.WaitForExitAsync();
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited.
        }
    }

    private static async Task<string> RunDockerAsync(IReadOnlyList<string> args, bool allowFailure = false)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start docker process.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (!allowFailure && process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"docker {string.Join(' ', args)} failed with exit code {process.ExitCode}: {stderr}");
        }

        return stdout;
    }

    private static void EnsureCommandAvailable(string command, string message)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(command, "--version")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            });
            process?.WaitForExit(5000);
            if (process is null || process.ExitCode != 0)
            {
                throw new InvalidOperationException(message);
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(message, ex);
        }
    }

    private static string GetBuildConfiguration()
    {
        var baseDirectory = AppContext.BaseDirectory;
        return baseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || baseDirectory.EndsWith($"{Path.DirectorySeparatorChar}Release", StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Join(directory.FullName, "ReMind.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
