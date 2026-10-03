using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace RhinoArcGIS.Core.Profiles
{
    /// <summary>
    /// JSON load/save for <see cref="LayerMappingProfile"/>. Property names and enum values are
    /// emitted as snake_case to line up with the YAML examples in the spec. (A YAML reader can be
    /// added later behind the same model; JSON keeps Wave 1 dependency-free beyond Newtonsoft.)
    /// </summary>
    public static class ProfileJson
    {
        private static JsonSerializerSettings BuildSettings()
        {
            var snake = new SnakeCaseNamingStrategy();
            return new JsonSerializerSettings
            {
                ContractResolver = new DefaultContractResolver { NamingStrategy = snake },
                Converters = { new StringEnumConverter(snake) },
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore
            };
        }

        public static string Serialize(LayerMappingProfile profile)
            => JsonConvert.SerializeObject(profile, BuildSettings());

        public static LayerMappingProfile Deserialize(string json)
            => JsonConvert.DeserializeObject<LayerMappingProfile>(json, BuildSettings());
    }
}
