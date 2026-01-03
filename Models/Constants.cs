namespace S3mphony.Models {
    public class Constants {

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

        public const char _as = '*';
        public const char _fs = '/';
        public const string _fss = "/";

        public const string S3SettingsSectionName = @"S3";
    }
}
