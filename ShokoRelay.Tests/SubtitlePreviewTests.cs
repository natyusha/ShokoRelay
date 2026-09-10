using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.Extensions.DependencyInjection;
using ShokoRelay.Controllers;

namespace ShokoRelay.Tests;

public class SubtitlePreviewTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(int.MaxValue, true)]
    public void MvcValidatesThePreviewSeriesId(int seriesId, bool expectedValid)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMvcCore().AddDataAnnotations();
        using var provider = services.BuildServiceProvider();
        var context = new ActionContext { HttpContext = new DefaultHttpContext { RequestServices = provider } };
        var request = new ShokoController.SubtitlePreviewRequest(seriesId, []);
        provider.GetRequiredService<IObjectModelValidator>().Validate(context, null, "", request);
        Assert.Equal(expectedValid, context.ModelState.IsValid);
        if (!expectedValid)
            Assert.Contains(nameof(request.SeriesId), context.ModelState.Keys);
    }
}
