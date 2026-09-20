using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Jev.Gameplay.Jev
{
    /// <summary>Secrets never become serialized components, assets, editor preferences or request bodies.</summary>
    public static class JevCredentials
    {
        public static string LocalSettingsDirectory
        {
            get
            {
#if UNITY_EDITOR
                return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "LocalSettings"));
#else
                return Path.Combine(Application.persistentDataPath, "LocalSettings");
#endif
            }
        }

        public static string ReadKey()
        {
            string environment = Environment.GetEnvironmentVariable("JEV_API_KEY");
            if (!string.IsNullOrWhiteSpace(environment)) return environment.Trim();
            string path = Path.Combine(LocalSettingsDirectory, "jev.env");
            try
            {
                if (!File.Exists(path)) return string.Empty;
                foreach (string line in File.ReadLines(path))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("export ", StringComparison.Ordinal)) trimmed = trimmed.Substring(7).TrimStart();
                    int separator = trimmed.IndexOf('=');
                    if (separator < 0 || trimmed.Substring(0, separator).Trim() != "JEV_API_KEY") continue;
                    string value = trimmed.Substring(separator + 1).Trim();
                    if (value.Length > 1 && ((value[0] == '"' && value[value.Length - 1] == '"') ||
                                            (value[0] == '\'' && value[value.Length - 1] == '\'')))
                        value = value.Substring(1, value.Length - 2);
                    return value.Trim();
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return string.Empty;
        }

        public static string Redact(string text, string key)
            => string.IsNullOrEmpty(text) || string.IsNullOrEmpty(key) ? text : text.Replace(key, "[REDACTED]");

        public static JToken RedactedCopy(JToken value, string key)
        {
            if (value is JObject sourceObject)
            {
                var result = new JObject();
                foreach (var property in sourceObject.Properties())
                {
                    string name = property.Name.Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
                    result[Redact(property.Name, key)] = name == "authorization" || name == "apikey" || name == "jevapikey" || name == "secret"
                        ? new JValue("[REDACTED]") : RedactedCopy(property.Value, key);
                }
                return result;
            }
            if (value is JArray sourceArray)
            {
                var result = new JArray();
                foreach (var item in sourceArray) result.Add(RedactedCopy(item, key));
                return result;
            }
            return value?.Type == JTokenType.String ? new JValue(Redact((string)value, key)) : value?.DeepClone();
        }
    }
}
