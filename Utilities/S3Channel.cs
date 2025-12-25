using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using S3mphony.Models;

namespace S3mphony.Utility {
    public class S3Channel<T> where T : class {
        private readonly S3StorageUtility<T> _s3StorageUtility;
        private readonly IMemoryCache _memoryCache;

        /// <summary>
        /// A semaphore used to control concurrent access to recent structure operations.
        /// </summary>
        /// <remarks>This semaphore ensures that only one thread can access or modify recent structure
        /// data at a time. It should be awaited before performing operations that require exclusive access and released
        /// when the operation is complete.</remarks>
        private static readonly SemaphoreSlim _recentStructuresGate = new(1, 1);

        public S3Channel(S3StorageUtility<T> s3StorageUtility, IMemoryCache memoryCache) {
            _s3StorageUtility = s3StorageUtility ?? throw new ArgumentNullException(nameof(s3StorageUtility));
            _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
        }

        public async Task<List<T>> GetRecentStructures<T>(int takeMostRecent = 100, CancellationToken ct = default, string directory = "") where T : class {
            if (takeMostRecent <= 0) {
                throw new ArgumentOutOfRangeException(nameof(takeMostRecent), "Must be greater than zero.");
            }

            if (_memoryCache.TryGetValue($"{Constants.CacheKey<T>()}:{takeMostRecent}",
                out List<T>? cached) && cached?.Count > 0) {
                return cached;
            }

            IReadOnlyList<(string key, DateTime? lastModified)> mostRecentTKeys = [];
            List<T> response = [];

            await _recentStructuresGate.WaitAsync(ct);
            try {
                // Delegate to refresh listing of keys in the bucket
                Func<Task> refreshListing = async () => {
                    mostRecentTKeys =
                        await _s3StorageUtility.ListBlobsAsync(directory, ct);
                };

                // Delegate to download 'T's from the listing of keys
                Func<Task> downloadFromListing = async () => {
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
                };

                Func<Task> attempt = async () => {
                    // Avoid duplicate response entries
                    response.Clear();
                    await refreshListing();
                    await downloadFromListing();
                };

                // First attempt
                await attempt();

                if (response.Count == 0) {
                    // Wait a moment and try again
                    await Task.Delay(1000, ct);
                    await attempt();
                }

                // Cache only if signal
                if (response.Count > 0) {
                    _memoryCache.Set($"{Constants.CacheKey<T>()}:{takeMostRecent}", response, new MemoryCacheEntryOptions {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10),
                        Priority = CacheItemPriority.High
                    });
                }

                return response;
            }
            finally {
                _recentStructuresGate.Release();
            }
        }
    }
}
