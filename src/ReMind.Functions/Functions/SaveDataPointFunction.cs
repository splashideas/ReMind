using System.Data;
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using ReMind.Functions.Models;

namespace ReMind.Functions.Functions;

public class SaveDataPointFunction
{
    private readonly string _connectionString;

    public SaveDataPointFunction(IConfiguration configuration)
    {
        _connectionString = configuration["SqlConnectionString"] ?? string.Empty;
    }

    [Function("SaveDataPoint")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "datapoints")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        var payload = await req.ReadFromJsonAsync<SaveDataPointRequest>(cancellationToken);
        var outcome = await HandleAsync(payload, cancellationToken);
        var response = req.CreateResponse(outcome.StatusCode);
        await response.WriteStringAsync(outcome.Message, cancellationToken);
        return response;
    }

    internal async Task<SaveDataPointOutcome> HandleAsync(
        SaveDataPointRequest? payload,
        CancellationToken cancellationToken)
    {
        if (payload is null ||
            string.IsNullOrWhiteSpace(payload.Location) ||
            string.IsNullOrWhiteSpace(payload.Description) ||
            !payload.EventDate.HasValue ||
            payload.EventDate.Value == default)
        {
            return SaveDataPointOutcome.InvalidPayload;
        }

        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return SaveDataPointOutcome.MissingConnectionString;
        }

        try
        {
            await SaveDataPointAsync(payload, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return SaveDataPointOutcome.SaveFailed;
        }

        return SaveDataPointOutcome.Success;
    }

    protected virtual async Task SaveDataPointAsync(
        SaveDataPointRequest payload,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandType = CommandType.Text;
        command.CommandText = """
                              INSERT INTO dbo.DataPoints (Location, EventDate, Description)
                              VALUES (@Location, @EventDate, @Description);
                              """;

        command.Parameters.Add(new SqlParameter("@Location", SqlDbType.NVarChar, 200) { Value = payload.Location });
        command.Parameters.Add(new SqlParameter("@EventDate", SqlDbType.DateTime2) { Value = payload.EventDate!.Value });
        command.Parameters.Add(new SqlParameter("@Description", SqlDbType.NVarChar, -1) { Value = payload.Description });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

internal sealed record SaveDataPointOutcome(HttpStatusCode StatusCode, string Message)
{
    public static readonly SaveDataPointOutcome InvalidPayload = new(HttpStatusCode.BadRequest, "Invalid request payload.");
    public static readonly SaveDataPointOutcome MissingConnectionString = new(HttpStatusCode.InternalServerError, "SQL connection is not configured.");
    public static readonly SaveDataPointOutcome SaveFailed = new(HttpStatusCode.InternalServerError, "Failed to save the data point.");
    public static readonly SaveDataPointOutcome Success = new(HttpStatusCode.Created, "Saved.");
}
