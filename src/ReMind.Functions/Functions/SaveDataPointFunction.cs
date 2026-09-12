using System.Data;
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using ReMind.Functions.Models;

namespace ReMind.Functions.Functions;

public sealed class SaveDataPointFunction
{
    private readonly string _connectionString;

    public SaveDataPointFunction(IConfiguration configuration)
    {
        _connectionString = configuration["SqlConnectionString"]
                            ?? Environment.GetEnvironmentVariable("SqlConnectionString")
                            ?? string.Empty;
    }

    [Function("SaveDataPoint")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "datapoints")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        var payload = await req.ReadFromJsonAsync<SaveDataPointRequest>(cancellationToken);
        if (payload is null ||
            string.IsNullOrWhiteSpace(payload.Location) ||
            string.IsNullOrWhiteSpace(payload.Description) ||
            payload.EventDate == default)
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid request payload.", cancellationToken);
            return badRequest;
        }

        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            var serverError = req.CreateResponse(HttpStatusCode.InternalServerError);
            await serverError.WriteStringAsync("SQL connection is not configured.", cancellationToken);
            return serverError;
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandType = CommandType.Text;
        command.CommandText = """
                              INSERT INTO dbo.DataPoints (Location, EventDate, Description)
                              VALUES (@Location, @EventDate, @Description);
                              """;

        command.Parameters.Add(new SqlParameter("@Location", SqlDbType.NVarChar, 200) { Value = payload.Location });
        command.Parameters.Add(new SqlParameter("@EventDate", SqlDbType.DateTime2) { Value = payload.EventDate });
        command.Parameters.Add(new SqlParameter("@Description", SqlDbType.NVarChar, -1) { Value = payload.Description });

        await command.ExecuteNonQueryAsync(cancellationToken);

        var created = req.CreateResponse(HttpStatusCode.Created);
        await created.WriteStringAsync("Saved.", cancellationToken);
        return created;
    }
}
