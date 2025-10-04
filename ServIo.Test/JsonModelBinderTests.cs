using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Logging.Abstractions;
using ServIo;
using Xunit;

namespace ServIo.Test;

public class JsonModelBinderTests
{
    private sealed class EmptyValueProvider : IValueProvider
    {
        public bool ContainsPrefix(string prefix) => false;
        public ValueProviderResult GetValue(string key) => ValueProviderResult.None;
    }

    private static DefaultModelBindingContext CreateContext<T>(string json, string modelName)
    {
        var http = new DefaultHttpContext();
        http.Request.ContentType = "application/json";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var provider = new EmptyModelMetadataProvider();
        var metadata = provider.GetMetadataForType(typeof(T));

        return new DefaultModelBindingContext
        {
            ModelMetadata = metadata,
            ModelName = modelName,
            FieldName = modelName,
            BindingSource = BindingSource.Body,
            ActionContext = new Microsoft.AspNetCore.Mvc.ActionContext { HttpContext = http },
            ValueProvider = new EmptyValueProvider(),
            ModelState = new ModelStateDictionary(),
        };
    }

    [Fact]
    public async Task Binds_Int_Property()
    {
        var binder = new JsonModelBinder(new NullLogger<JsonModelBinder>());
        var ctx = CreateContext<int>("{ \"Age\": 25 }", "Age");
        await binder.BindModelAsync(ctx);
        ctx.Result.IsModelSet.Should().BeTrue();
        ctx.Result.Model.Should().Be(25);
    }

    [Fact]
    public async Task Missing_Required_Property_Fails()
    {
        var binder = new JsonModelBinder(new NullLogger<JsonModelBinder>());
        var ctx = CreateContext<int>("{ }", "Age");
        await binder.BindModelAsync(ctx);
        ctx.Result.IsModelSet.Should().BeFalse();
        ctx.ModelState.ErrorCount.Should().Be(1);
    }

    [Fact]
    public async Task Invalid_Json_Fails()
    {
        var binder = new JsonModelBinder(new NullLogger<JsonModelBinder>());
        var ctx = CreateContext<int>("{ Age: 25 }", "Age");
        await binder.BindModelAsync(ctx);
        ctx.Result.IsModelSet.Should().BeFalse();
        ctx.ModelState.ErrorCount.Should().Be(1);
    }
}
