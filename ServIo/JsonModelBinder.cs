using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Http; // Added for EnableBuffering extension
using PowNet.Extensions;
using System.Text.Json;
using System.Text; // Added for reading request body with encoding
using System.Reflection;
using Microsoft.Extensions.Logging; // Improvement #11: Structured logging
using System.IO;
using System.Buffers;

namespace ServIo
{
	public class JsonModelBinder : IModelBinder
	{
		private const string JsonBodyItemKey = "JsonBody"; // Keeping original key name for compatibility
		private static readonly JsonSerializerOptions _deserializeOptions = new() { PropertyNameCaseInsensitive = true };
		private readonly ILogger<JsonModelBinder>? _logger; // Improvement #11

		public JsonModelBinder(ILogger<JsonModelBinder>? logger = null)
		{
			_logger = logger;
		}

		public async Task BindModelAsync(ModelBindingContext MBC)
		{
			if (MBC == null) throw new ArgumentNullException(nameof(MBC));
			if (MBC.ModelState == null) MBC.ModelState = new ModelStateDictionary();
			if (MBC.BindingSource != null && MBC.BindingSource != BindingSource.Body) return;
			if (MBC.HttpContext.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true) return;

			string? propName = MBC.ModelMetadata.Name;
			if (string.IsNullOrEmpty(propName)) propName = MBC.ModelName; // Fallback to model name if metadata name missing (simple types)
			if (string.IsNullOrEmpty(propName)) return;

			try
			{
				// Improvement #7: Attempt streaming extraction for single property before full parse
				if (!MBC.HttpContext.Items.ContainsKey(JsonBodyItemKey))
				{
					bool streamed = await TryBindStreamingPropertyAsync(MBC, propName);
					if (streamed) return; // success or handled (including default / missing)
				}

				// Fallback / regular path: cache body as JsonDocument (Improvements 1,9)
				if (!MBC.HttpContext.Items.TryGetValue(JsonBodyItemKey, out var jsonBody))
				{
					MBC.HttpContext.Request.EnableBuffering();
					var request = MBC.HttpContext.Request;
					request.Body.Position = 0;
					using var buffer = new MemoryStream();
					await request.Body.CopyToAsync(buffer, MBC.HttpContext.RequestAborted);
					buffer.Position = 0;
					request.Body.Position = 0; // Reset
					JsonDocument doc = await JsonDocument.ParseAsync(buffer, cancellationToken: MBC.HttpContext.RequestAborted);
					MBC.HttpContext.Items[JsonBodyItemKey] = doc;
					jsonBody = doc;
				}

				JsonElement root;
				if (jsonBody is JsonDocument docStored)
					root = docStored.RootElement;
				else if (jsonBody is JsonElement element)
					root = element; // backward compatibility
				else
				{
					MBC.ModelState.AddModelError(propName ?? MBC.ModelName, "Unable to read JSON body.");
					MBC.Result = ModelBindingResult.Failed();
					return;
				}

				if (root.ValueKind != JsonValueKind.Object)
				{
					MBC.ModelState.AddModelError(propName, "JSON root must be an object.");
					MBC.Result = ModelBindingResult.Failed();
					return;
				}

				if (root.TryGetProperty(propName, out var jsonElement) || TryGetPropertyCaseInsensitive(root, propName, out jsonElement))
				{
					object? v = ConvertJsonElement(jsonElement, MBC.ModelMetadata.ModelType);
					MBC.Result = ModelBindingResult.Success(v);
					MBC.ModelState.SetModelValue(propName, v, null);
					_logger?.LogDebug("JsonModelBinder bound property {Property} via full parse", propName);
					return;
				}

				if (MBC.ModelMetadata.IsRequired)
				{
					MBC.ModelState.AddModelError(propName, $"Required property '{propName}' not found in JSON payload.");
					MBC.Result = ModelBindingResult.Failed();
					_logger?.LogWarning("Required JSON property {Property} missing after full parse", propName);
					return;
				}
				else
				{
					object? def = GetDefaultValue(MBC.ModelMetadata.ModelType);
					MBC.Result = ModelBindingResult.Success(def);
					MBC.ModelState.SetModelValue(propName, def, null);
					_logger?.LogDebug("Optional JSON property {Property} missing. Using default after full parse.", propName);
					return;
				}
			}
			catch (JsonException ex)
			{
				if (MBC.ModelState == null) MBC.ModelState = new ModelStateDictionary();
				_logger?.LogError(ex, "Json parsing error during model binding for {Property}", propName ?? MBC.ModelName);
				LogManager.LogError($"Json parsing error during model binding for '{propName ?? MBC.ModelName}': {ex.Message}");
				MBC.ModelState.AddModelError(propName ?? MBC.ModelName, "Invalid JSON payload.");
				MBC.Result = ModelBindingResult.Failed();
			}
			catch (Exception ex)
			{
				if (MBC.ModelState == null) MBC.ModelState = new ModelStateDictionary();
				_logger?.LogError(ex, "Model binding error for {Property}", propName ?? MBC.ModelName);
				LogManager.LogError($"Model binding error for '{propName ?? MBC.ModelName}': {ex.Message}");
				MBC.ModelState.AddModelError(propName ?? MBC.ModelName, "Error during model binding.");
				MBC.Result = ModelBindingResult.Failed();
			}
		}

		// Improvement #7: Streaming scan for top-level property without full DOM parse
		private async Task<bool> TryBindStreamingPropertyAsync(ModelBindingContext MBC, string propName)
		{
			try
			{
				var request = MBC.HttpContext.Request;
				request.EnableBuffering();
				request.Body.Position = 0;
				using var buffer = new MemoryStream();
				await request.Body.CopyToAsync(buffer, MBC.HttpContext.RequestAborted);
				buffer.Position = 0;
				request.Body.Position = 0; // reset for others

				ReadOnlySequence<byte> seq = new(buffer.ToArray());
				var reader = new Utf8JsonReader(seq, isFinalBlock: true, state: default);
				bool inRoot = false;
				while (reader.Read())
				{
					if (reader.TokenType == JsonTokenType.StartObject && !inRoot)
					{
						inRoot = true;
						continue;
					}
					if (!inRoot) continue;
					if (reader.TokenType == JsonTokenType.PropertyName)
					{
						string name = reader.GetString() ?? string.Empty;
						if (string.Equals(name, propName, StringComparison.Ordinal) || string.Equals(name, propName, StringComparison.OrdinalIgnoreCase))
						{
							// Move to value
							if (!reader.Read()) break;
							var cloneReader = reader; // copy struct
							using JsonDocument valueDoc = JsonDocument.ParseValue(ref cloneReader);
							JsonElement valueElement = valueDoc.RootElement.Clone();

							object? v = ConvertJsonElement(valueElement, MBC.ModelMetadata.ModelType);
							MBC.Result = ModelBindingResult.Success(v);
							MBC.ModelState.SetModelValue(propName, v, null);
							_logger?.LogDebug("JsonModelBinder bound property {Property} via streaming parse", propName);
							return true; // handled success
						}
						else
						{
							// Skip value token efficiently
							if (!reader.Read()) break; // move to value
							if (reader.TokenType == JsonTokenType.StartObject || reader.TokenType == JsonTokenType.StartArray)
							{
								int depth = 0;
								do
								{
									if (reader.TokenType == JsonTokenType.StartObject || reader.TokenType == JsonTokenType.StartArray) depth++;
									else if (reader.TokenType == JsonTokenType.EndObject || reader.TokenType == JsonTokenType.EndArray) depth--;
									if (!reader.Read()) break;
								} while (depth > 0);
							}
						}
					}
				}

				// Not found: if required we still need full parse for better error messaging downstream, so return false -> fallback
				if (!MBC.ModelMetadata.IsRequired)
				{
					object? def = GetDefaultValue(MBC.ModelMetadata.ModelType);
					MBC.Result = ModelBindingResult.Success(def);
					MBC.ModelState.SetModelValue(propName, def, null);
					_logger?.LogDebug("Streaming parse did not find optional property {Property}. Using default.", propName);
					return true; // handled (no need full parse)
				}
			}
			catch (Exception ex)
			{
				_logger?.LogTrace(ex, "Streaming parse fallback for property {Property}", propName);
				// fall through to full parse
			}
			return false; // proceed with full parsing path
		}

		private static bool TryGetPropertyCaseInsensitive(JsonElement root, string propertyName, out JsonElement value)
		{
			value = default;
			if (root.ValueKind != JsonValueKind.Object) return false;
			foreach (var prop in root.EnumerateObject())
			{
				if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
				{
					value = prop.Value;
					return true;
				}
			}
			return false;
		}

		private static object? ConvertJsonElement(JsonElement element, Type targetType)
		{
			Type nonNullableType = Nullable.GetUnderlyingType(targetType) ?? targetType;

			if (element.ValueKind == JsonValueKind.Null)
			{
				if (Nullable.GetUnderlyingType(targetType) != null) return null;
				return GetDefault(nonNullableType);
			}

			if (nonNullableType == typeof(string)) return element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
			if (nonNullableType == typeof(bool)) return element.ValueKind == JsonValueKind.True || (element.ValueKind == JsonValueKind.False && element.GetBoolean());

			if (nonNullableType.IsPrimitive || nonNullableType == typeof(decimal))
			{
				try
				{
					if (nonNullableType == typeof(int) && element.TryGetInt32(out int i32)) return i32;
					if (nonNullableType == typeof(long) && element.TryGetInt64(out long i64)) return i64;
					if (nonNullableType == typeof(double) && element.TryGetDouble(out double d)) return d;
					if (nonNullableType == typeof(float) && element.TryGetSingle(out float f)) return f;
					if (nonNullableType == typeof(short) && element.TryGetInt16(out short s)) return s;
					if (nonNullableType == typeof(byte) && element.TryGetByte(out byte b)) return b;
					if (nonNullableType == typeof(uint) && element.TryGetUInt32(out uint ui)) return ui;
					if (nonNullableType == typeof(ulong) && element.TryGetUInt64(out ulong ul)) return ul;
					if (nonNullableType == typeof(ushort) && element.TryGetUInt16(out ushort us)) return us;
					if (nonNullableType == typeof(decimal) && element.TryGetDecimal(out decimal dec)) return dec;
				}
				catch { }
			}

			if (nonNullableType == typeof(Guid) && element.ValueKind == JsonValueKind.String && Guid.TryParse(element.GetString(), out Guid g)) return g;
			if (nonNullableType == typeof(DateTime) && element.ValueKind == JsonValueKind.String && DateTime.TryParse(element.GetString(), out DateTime dt)) return dt;
			if (nonNullableType == typeof(DateTimeOffset) && element.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(element.GetString(), out DateTimeOffset dto)) return dto;

			if (nonNullableType.IsEnum)
			{
				try
				{
					if (element.ValueKind == JsonValueKind.String)
					{
						string? name = element.GetString();
						if (name != null && Enum.TryParse(nonNullableType, name, true, out object? enumVal)) return enumVal;
					}
					else if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int enumInt))
					{
						object boxed = Enum.ToObject(nonNullableType, enumInt);
						return boxed;
					}
				}
				catch { }
			}

			try
			{
				string raw = element.GetRawText();
				return JsonSerializer.Deserialize(raw, targetType, _deserializeOptions);
			}
			catch
			{
				try
				{
					return Convert.ChangeType(element.ToString(), nonNullableType);
				}
				catch { return GetDefault(nonNullableType); }
			}
		}

		private static object? GetDefault(Type t) => t.IsValueType ? Activator.CreateInstance(t) : null;
		private static object? GetDefaultValue(Type t) => t.IsValueType ? Activator.CreateInstance(t) : null;
	}

	public class JsonModelBinderProvider : IModelBinderProvider
	{
		public IModelBinder? GetBinder(ModelBinderProviderContext context)
		{
			if (context == null) throw new ArgumentNullException(nameof(context));
			var bindingSource = context.BindingInfo?.BindingSource;
			if (bindingSource != null && bindingSource != BindingSource.Body) return null;

			Type modelType = context.Metadata.ModelType;
			Type underlying = Nullable.GetUnderlyingType(modelType) ?? modelType;
			bool eligible = underlying.IsPrimitive || underlying.IsEnum || underlying == typeof(string) || underlying == typeof(decimal) || underlying == typeof(Guid) || underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset);
			if (!eligible) return null;

			var loggerFactory = (Microsoft.Extensions.Logging.ILoggerFactory?)context.Services.GetService(typeof(Microsoft.Extensions.Logging.ILoggerFactory));
			var logger = loggerFactory?.CreateLogger<JsonModelBinder>();
			return new JsonModelBinder(logger);
		}
	}
}
