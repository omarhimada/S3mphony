using Microsoft.Extensions.Caching.Memory;
using S3mphony.Models;
using S3mphony.Utility;

namespace S3mphony {
    /// <summary>
    /// Provides generic methods to interact with S3 storage for retrieving strongly typed structures,
    /// deserializing them, and utilizing the in-memory cache of the application to reduce redundant S3 calls.
    /// </summary>
    /// <typeparam name="T">Whatever type of object you're storing in S3.</typeparam>
    /// <param name="s3StorageUtility">Singleton, register this </param>
    /// <param name="memoryCache"></param>
    public class S3Channel<T>(S3StorageUtility<T> s3StorageUtility, IMemoryCache memoryCache) where T : class {
        private readonly S3StorageUtility<T> _s3StorageUtility = s3StorageUtility ?? throw new ArgumentNullException(nameof(s3StorageUtility));
        private readonly IMemoryCache _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));

        /// <summary>
        /// A semaphore used to control concurrent access to recent structure operations.
        /// </summary>
        /// <remarks>This semaphore ensures that only one thread can access or modify recent structure
        /// data at a time. It should be awaited before performing operations that require exclusive access and released
        /// when the operation is complete.</remarks>
        private static readonly SemaphoreSlim _recentStructuresGate = new(1, 1);

        /// <summary>
        /// Retrieves a list of the most recently modified structures of type T from the specified directory.
        /// The 'startAfterKey' and 'takeMostRecent' can be used like the typical SKIP-TAKE pagination pattern.
        /// </summary>
        /// <remarks>Results are cached for improved performance. If no structures are found on the first
        /// attempt, the method will retry once after a short delay. The operation is thread-safe.</remarks>
        /// <typeparam name="T">The type of structure to retrieve. Must be a reference type.</typeparam>
        /// <param name="ct">A cancellation token that can be used to cancel the operation.</param>
        /// <param name="directory">The directory from which to retrieve structures. If empty, the default directory is used.</param>
        /// <returns>A list containing up to the specified number of the most recently modified structures of type T. The list
        /// will be empty if no structures are found.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when takeMostRecent is less than or equal to zero.</exception>
        public async Task<List<T>> GetRecentStructures(
            int takeMostRecent = 100,
            string directory = "",
            CancellationToken ct = default) {

            if (takeMostRecent <= 0) {
                throw new ArgumentOutOfRangeException(nameof(takeMostRecent), "Must be greater than zero.");
            }

            // In-memory cache has a location for each 'page' if you're paginating.
            if (_memoryCache.TryGetValue($"{Constants.CacheKey<T>()}:{takeMostRecent}",
                out List<T>? cached) && cached?.Count > 0) {
                return cached;
            }

            IReadOnlyList<(string key, DateTime? lastModified)> mostRecentTKeys = [];
            List<T> response = [];

            await _recentStructuresGate.WaitAsync(ct);
            try {
                // Local function to refresh listing of keys in the bucket
                async Task refreshListing() {
                    mostRecentTKeys =
                        await _s3StorageUtility.ListBlobsAsync(
                            directory, ct);
                }

                // Local function to download from the listing of keys and orders them
                async Task downloadFromListing() {
                    IEnumerable<(string key, DateTime? lastModified)> orderedQueryable =
                        mostRecentTKeys
                            .OrderByDescending(x => x.lastModified ?? DateTime.MinValue)
                            .Take(takeMostRecent);

                    foreach ((string key, DateTime? lastModified) in orderedQueryable) {
                        ct.ThrowIfCancellationRequested();

                        T? t = await _s3StorageUtility.DownloadJsonAsync<T>(key, ct);
                        if (t is not null)
                            response.Add(t);
                    }
                }

                async Task attempt() {
                    // Avoid duplicate response entries
                    response.Clear();
                    await refreshListing();
                    await downloadFromListing();
                }

                // First attempt
                await attempt();

                if (response.Count == 0) {
                    // Wait a moment and try again
                    await Task.Delay(1000, ct);
                    await attempt();
                }

                // Cache only we receive a non-empty response
                if (response.Count != 0) {
                    _ = _memoryCache.Set($"{Constants.CacheKey<T>()}:{takeMostRecent}", response, new MemoryCacheEntryOptions {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10),
                        Priority = CacheItemPriority.High
                    });
                }

                return response;
            } finally {
                _ = _recentStructuresGate.Release();
            }
        }
    }
}
