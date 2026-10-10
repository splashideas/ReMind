namespace ReMind.IntegrationTests;

public class ScaffoldTests
{
    [Fact]
    public void ApiAndDataProjectsAreReferenced()
    {
        Assert.Equal("ReMind.Api", typeof(Program).Assembly.GetName().Name);
        Assert.Equal("ReMind.Data.ReMindDbContext", typeof(ReMind.Data.ReMindDbContext).FullName);
    }
}
