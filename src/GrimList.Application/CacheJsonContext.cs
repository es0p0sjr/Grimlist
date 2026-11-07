using System.Text.Json.Serialization;
using GrimList.Core;

namespace GrimList.Application;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(FactionLibrary.Manifest))]
[JsonSerializable(typeof(FactionLibrary.Shard))]
[JsonSerializable(typeof(Dictionary<string, List<AbilityProfile>>))]
[JsonSerializable(typeof(Dictionary<string, ArmySession.CachedSummary>))]
[JsonSerializable(typeof(FactionLibrary.DependencyCache))]
internal partial class CacheJsonContext : JsonSerializerContext;
