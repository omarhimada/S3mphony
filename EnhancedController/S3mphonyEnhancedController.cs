using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Caching.Memory;
using S3mphony.Models;
using S3mphony.Utility;

namespace S3mphony.EnhancedController {
    public class S3mphonyEnhancedController<T>(IMemoryCache cache, S3Channel<T> s3Channel, S3StorageUtility<T> s3StorageUtility) : ControllerBase where T : class {

        public readonly IMemoryCache _cache = cache;
        public readonly S3Channel<T> _s3Channel = s3Channel;
        public readonly S3StorageUtility<T> _s3StorageUtility = s3StorageUtility;

        /// <summary>
        /// Asynchronously retrieves a collection of the most recent structures of type <typeparamref name="T"/> from the cache or the
        /// underlying data source. You may also want to include the [OutputCache] attribute on this method.
        /// </summary>
        /// <remarks>If the data is not present in the cache, the method attempts to retrieve it from the
        /// underlying data source and updates the cache. If no data is found after retrieval, output caching for the
        /// current HTTP response is disabled to prevent caching of empty results.</remarks>
        /// <returns>A task that represents the asynchronous operation. The task result contains an enumerable collection of the
        /// most recent structures of type T. The collection is empty if no structures are available.</returns>
        [HttpGet]
        public async Task<IEnumerable<T>> Get() {
            IEnumerable<T> recentStructures =
                _cache.Get<IEnumerable<T>>(Constants.CacheKey<T>()) ?? [];

            // Local function to avoid code duplication when retrying
            async Task localAsyncFunction() {
                recentStructures = [.. await _s3Channel.GetRecentStructures()];
            }

            if (!recentStructures.Any()) {
                // Try again
                await localAsyncFunction();
            }

            if (recentStructures.Any()) {
                _ = _cache.Set(Constants.CacheKey<T>(), recentStructures, new MemoryCacheEntryOptions {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10),
                    Priority = CacheItemPriority.High
                });
            } else {
                // If still no 'T's, disable output caching for this response
                // This prevents OutputCache from storing the response for this request
                _ = (HttpContext.Features.Get<IOutputCacheFeature>()?.Context.AllowCacheStorage = false);
            }

            return recentStructures ?? [];
        }

        /// <summary>
        /// Creates a new item of type <typeparamref name="T"/> in the storage system and returns a response indicating
        /// the result of the operation.
        /// </summary>
        /// <remarks>This method stores the provided item in the underlying storage system (such as S3)
        /// and invalidates any cached GET results to ensure subsequent reads reflect the new data. The response
        /// includes the location of the newly created item. Output caching is disabled for this operation.</remarks>
        /// <param name="item">The item to create. Cannot be null.</param>
        /// <param name="name">An optional name to assign to the item in storage. If null, a unique name is generated automatically.</param>
        /// <param name="prefix">An optional prefix to prepend to the item's storage key. The default is an empty string.</param>
        /// <param name="contentType">The content type to associate with the stored item. The default is "application/json".</param>
        /// <param name="overwrite">true to overwrite an existing item with the same name; otherwise, false. The default is <see
        /// langword="true"/>.</param>
        /// <param name="ct">A cancellation token that can be used to cancel the operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains an <see cref="ActionResult{T}"/>
        /// indicating the outcome of the creation request. Returns a 201 Created response with the created item if
        /// successful, or a 400 Bad Request if the input is invalid.</returns>
        [HttpPost]
        public async Task<ActionResult<T>> Post([FromBody] T item,
            string name = null,
            string prefix = "",
            string contentType = "application/json",
            bool overwrite = true,
            CancellationToken ct = default) {

            if (item is null)
                return BadRequest("Body cannot be null.");

            // Generate a name if not provided
            name ??= $"{DateTime.Now.ToShortDateString}-{Guid.NewGuid()}";

            // Key of the recently created blob in S3
            string createdKey = await _s3StorageUtility.PutStructureAsync(item, name, prefix, contentType, overwrite, ct);

            // Invalidate the GET cache so new item shows up next read
            _cache.Remove(Constants.CacheKey<T>());

            // POST responses should not be output-cached
            _ = (HttpContext.Features.Get<IOutputCacheFeature>()?.Context.AllowCacheStorage = false);

            // If you have a GET-by-id endpoint, use CreatedAtAction.
            // Without a stable "id" route for T, returning Created(key, item) is fine.
            return Created($"/{typeof(T).Name.ToLowerInvariant()}/{createdKey}", item);
        }
    }
}
