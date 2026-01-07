using System.Text.Json;
using System.Text.Json.Serialization;

namespace S3mphony.Models {
    public static class Constants {
        /// <summary>
        /// Provides constant values for commonly used tag names related to S3 object metadata.
        /// </summary>
        public static class Tags {
            public const string ObjectDescriptor = @"description";
            public const string Source = @"source";

            public const string ModelObjectType = @"ml-model";
            public const string ObjectSource = @"s3mphony";
        }

        public static class Messages {
            public const string ErrorGreaterThanZero = @"Must be greater than zero.";
        }

        /// <summary>
        /// Provides default options for JSON serialization and deserialization operations.
        /// </summary>
        /// <remarks>The default options use case-insensitive property name matching, camel case property
        /// naming, and omit properties with null values when writing JSON. Indentation is disabled to reduce output
        /// size, and the encoder allows relaxed escaping for broader character support. These settings are intended to
        /// produce compact, interoperable JSON suitable for storage and transmission.</remarks>
        public static JsonSerializerOptions DefaultJsonOptions = new () { 
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // Save space in S3 storage
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>
        /// Provides constants for standard HTTP header names used in requests and responses.
        /// </summary>
        /// <remarks>Use the fields of this class to avoid hardcoding common HTTP header names when
        /// constructing or parsing HTTP messages. This helps ensure consistency and reduces the risk of typographical
        /// errors.</remarks>
        public static class Headers {
            public const string IfNoneMatch = "If-None-Match";
        }

        /// <summary>
        /// Generates a cache key string for storing or retrieving recent items of the specified type in memory cache.
        /// </summary>
        /// <typeparam name="T">The type of the items for which the cache key is generated. Must be a reference type.</typeparam>
        /// <returns>A string representing the cache key for recent items of type T in memory cache.</returns>
        public static string CacheKey<T>() where T : class => $"memory-cache:recent-{typeof(T).Name.ToLower()}";

        public const string ContentTypeJson = "application/json";
        public const string ContentTypeZip = "application/zip";
        public const string ContentTypeOctetStream = "application/octet-stream";

        public const string IfNoneMatchHeader = "If-None-Match";


        public const string _as = "*";
        public const char _asc = '*';
        public const char _fs = '/';
        public const string _fss = "/";

        public const string S3SettingsSectionName = @"S3";
    }
}
