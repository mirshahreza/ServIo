using Microsoft.AspNetCore.Mvc.ModelBinding;
using PowNet.Extensions;
using System.Text.Json;

namespace ServIo
{
	public class JsonModelBinder : IModelBinder
	{
		public async Task BindModelAsync(ModelBindingContext MBC)
		{
			if (MBC == null) throw new ArgumentNullException(nameof(MBC));
			if (MBC.HttpContext.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true) return;

			try
			{
				if (!MBC.HttpContext.Items.ContainsKey("JsonBody")) MBC.HttpContext.Items.Add("JsonBody", await MBC.HttpContext.Request.ToJsonAsync());
				var jsonBody = MBC.HttpContext.Items["JsonBody"];

				if (jsonBody is not JsonElement root)
				{
					MBC.Result = ModelBindingResult.Failed();
					return;
				}


				string? propName = MBC.ModelMetadata.Name;
				if (propName is null) return;

				if (root.TryGetProperty(propName, out var jsonElement))
				{
					var v = Convert.ChangeType(jsonElement.ToRealType(), MBC.ModelMetadata.ModelType);
					MBC.Result = ModelBindingResult.Success(v);
					MBC.ModelState.SetModelValue(propName, v, null);
				}

				if (!MBC.Result.IsModelSet) MBC.Result = ModelBindingResult.Failed();
			}
			catch (JsonException)
			{
				MBC.ModelState.AddModelError(MBC.ModelName, "Invalid JSON payload.");
				MBC.Result = ModelBindingResult.Failed();
			}
			catch (Exception ex)
			{
				MBC.ModelState.AddModelError(MBC.ModelName, $"Error during model binding: {ex.Message}");
				MBC.Result = ModelBindingResult.Failed();
			}
		}
	}

	public class JsonModelBinderProvider : IModelBinderProvider
	{
		public IModelBinder? GetBinder(ModelBinderProviderContext context)
		{
			return context == null ? throw new ArgumentNullException(nameof(context)) : (IModelBinder)new JsonModelBinder();
		}
	}
}
