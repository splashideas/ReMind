namespace ReMind.IntegrationTests;

public class ScaffoldTests
{
    [Fact]
    public async Task ApiRootReturnsScaffoldResponse()
    {
        await using var factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ReMind API scaffold", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void ApiAndDataProjectsAreReferenced()
    {
        Assert.Equal("ReMind.Api", typeof(Program).Assembly.GetName().Name);
        Assert.Equal("ReMind.Data.ReMindDbContext", typeof(ReMind.Data.ReMindDbContext).FullName);
    }
}
