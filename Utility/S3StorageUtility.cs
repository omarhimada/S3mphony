using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.ML;
using S3mphony.Models;
using System.Reflection;
using System.Text.Json;

namespace S3mphony.Utility {
    /// <summary>
    /// Provides utility methods for interacting with Amazon S3 storage, including uploading, downloading, listing, and
    /// deleting objects, as well as handling JSON serialization and deserialization for objects of type T.
    /// </summary>
    /// <remarks>This utility encapsulates common S3 operations and abstracts away low-level details such as
    /// key normalization, pagination, and JSON handling. It is designed to simplify working with S3 buckets for
    /// applications that need to store, retrieve, and manage structured data or files. Thread safety depends on the
    /// thread safety of the provided IAmazonS3 client and serializer options.</remarks>
    /// <typeparam name="T">The type of objects to be serialized to or deserialized from JSON when using the utility. Must be a reference
    /// type.</typeparam>
    /// <param name="s3Client">The Amazon S3 client used to perform storage operations. Cannot be null.</param>
    /// <param name="options">The configuration options specifying the S3 bucket and related settings. Cannot be null.</param>
    /// <param name="jsonOptions">The JSON serializer options used for serializing and deserializing objects of type T. Cannot be null.</param>
    public class S3StorageUtility<T>(
        IAmazonS3 s3Client,
        S3Options options,
        JsonSerializerOptions? jsonOptions) where T : class {

        /// <summary>
        /// Retrieves the current <see cref="JsonSerializerOptions"/> instance used for JSON serialization and
        /// deserialization.
        /// </summary>
        /// <returns>The <see cref="JsonSerializerOptions"/> instance to be used. If no custom options are set, returns the
        /// default options defined in <see cref="Constants.DefaultJsonOptions"/>.</returns>
        private JsonSerializerOptions _jsonOptions() => jsonOptions ?? Constants.DefaultJsonOptions;

        /// <summary>
        /// Current application name for tagging in S3.
        /// </summary>
        private readonly string _appName = Assembly.GetExecutingAssembly().GetName()?.Name?.ToLowerInvariant() ?? string.Empty;

        /// <summary>
        /// Checks whether the S3 bucket specified by the current instance exists asynchronously.
        /// </summary>
        /// <param name="ct">A cancellation token that can be used to cancel the operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result is <see langword="true"/> if the bucket
        /// exists; otherwise, <see langword="false"/>.</returns>
        public async Task<bool> EnsureBucketExistsAsync(CancellationToken ct = default) {
            try {
                _ = await s3Client.HeadBucketAsync(new HeadBucketRequest { BucketName = options.BucketName }, ct);
                return true;
            } catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) {
                return false;
            }
        }

        /// <summary>
        /// Asynchronously retrieves a list of directory names (common prefixes) from the S3 bucket, optionally filtered
        /// by a specified prefix.
        /// </summary>
        /// <remarks>Directory names are determined by S3 common prefixes using the '/' delimiter. The
        /// returned directory names include the prefix, if specified. This method may make multiple requests to S3 if
        /// the result set is large.</remarks>
        /// <param name="prefix">An optional prefix to filter the directories. Only directories whose names begin with this prefix are
        /// returned. If null, all directories are listed.</param>
        /// <param name="ct">A cancellation token that can be used to cancel the asynchronous operation.</param>
        /// <returns>A read-only list of strings containing the names of directories found in the bucket. The list is empty if no
        /// directories match the specified prefix.</returns>
        public async Task<IReadOnlyList<string>> ListDirectoriesAsync(string? prefix = null, CancellationToken ct = default) {
            List<string> results = [];

            string? token = null;
            do {
                ListObjectsV2Request req = new() {
                    BucketName = options.BucketName,
                    Prefix = prefix,
                    Delimiter = Constants._fss,
                    ContinuationToken = token,
                };

                ListObjectsV2Response resp = await s3Client.ListObjectsV2Async(req, ct);

                // CommonPrefixes are your "folders"
                if (resp.CommonPrefixes is not null) {
                    results.AddRange(resp.CommonPrefixes);
                }

                token = resp.IsTruncated!.Value ? resp.NextContinuationToken : null;
            } while (token is not null && !ct.IsCancellationRequested);

            return results;
        }

        /// <summary>
        /// Asynchronously retrieves a list of blob keys and their last modified dates from the storage bucket,
        /// optionally filtered by prefix and paginated using continuation parameters.
        /// </summary>
        /// <remarks>Blob keys that represent S3 folder markers (keys ending with '/') are excluded from
        /// the results. The method may return fewer results than requested if there are not enough matching
        /// blobs.</remarks>
        /// <param name="prefix">
        /// An optional string to filter the results to blobs whose keys begin with the specified prefix. If null, all
        /// blobs are included.
        /// </param>
        /// <param name="ct">
        /// A cancellation token that can be used to cancel the asynchronous operation.</param>
        /// <returns>A read-only list of tuples, each containing the blob key and its last modified date. The list may be empty
        /// if no blobs match the criteria.
        /// </returns>
        public async Task<IReadOnlyList<(string, DateTime?)>>
            ListBlobsAsync(string? prefix = null, CancellationToken ct = default) {
            List<(string, DateTime?)> results = [];

            string? token = null;
            do {
                ListObjectsV2Request req = new() {
                    BucketName = options.BucketName,
                    Prefix = prefix,
                    ContinuationToken = token,
                };

                ListObjectsV2Response resp = await s3Client.ListObjectsV2Async(req, ct);

                // S3 "folder marker" objects can exist; skip keys that end with "/"
                foreach (S3Object? obj in resp.S3Objects) {
                    if (!obj.Key.EndsWith(Constants._fss)) {
                        results.Add((obj.Key, obj.LastModified));
                    }
                }

                token = resp.IsTruncated!.Value ? resp.NextContinuationToken : null;
            } while (token is not null && !ct.IsCancellationRequested);

            return results;
        }

        /// <summary>
        /// Asynchronously uploads a zip file, like a serialized ML.NET model, to an Amazon S3 bucket at the specified key.
        /// Binary ML.NET models are uploaded as raw bytes using application/octet-stream by default.
        /// Override to application/zip only when storing actual ZIP archives, such as ML.NET model bundles or compressed archives.
        /// </summary>
        /// <remarks>The model is serialized in a format compatible with ML.NET and uploaded as a zip file
        /// to the specified S3 location. If a model already exists at the given key, it will be overwritten.</remarks>
        /// <param name="s3Key">The S3 object key under which the model will be stored. Cannot be null or empty.</param>
        /// <param name="ct">A cancellation token that can be used to cancel the upload operation.</param>
        /// <returns>A task that represents the asynchronous upload operation.</returns>
        public async Task UploadModelAsync(
            MLContext mlContext,
            ITransformer model,
            DataViewSchema inputSchema,
            string modelDirectoryPrefix,
            string modelName,
            CancellationToken ct,
            string contentType = Constants.ContentTypeOctetStream) {

            await using MemoryStream memoryStream = new();
            mlContext.Model.Save(model, inputSchema, memoryStream);
            memoryStream.Position = 0;

            PutObjectRequest request = new() {
                // Prevent accidental double slashes
                Key = $"{modelDirectoryPrefix.TrimEnd(Constants._fs)}/{modelName.TrimStart(Constants._fs)}",
                InputStream = memoryStream,
                BucketName = options.BucketName,
                ContentType = Constants.ContentTypeOctetStream,
                TagSet = [
                    new() { Key = Constants.Tags.ObjectDescriptor, Value = Constants.Tags.ModelObjectType },
                    new() { Key = Constants.Tags.Source, Value = $"{_appName}-{Constants.Tags.ObjectSource}"}
                ]
            };

            _ = await s3Client.PutObjectAsync(request, ct);
        }

        /// <summary>
        /// Asynchronously uploads the specified byte array to the storage bucket at the given key.
        /// </summary>
        /// <remarks>If overwrite is set to false and an object with the specified key already exists, the
        /// upload will not overwrite the existing object and the operation will fail. The method streams the provided
        /// data directly to the storage service without buffering the entire content in memory.</remarks>
        /// <param name="key">The object key under which to store the data. Cannot be null, empty, or whitespace.</param>
        /// <param name="data">The byte array containing the data to upload.</param>
        /// <param name="contentType">The MIME type of the data. Defaults to "application/octet-stream" if not specified.</param>
        /// <param name="overwrite">true to overwrite the object if it already exists; otherwise, false to prevent overwriting. If false and the
        /// object exists, the upload will fail.</param>
        /// <param name="ct">A cancellation token that can be used to cancel the upload operation.</param>
        /// <returns>A task that represents the asynchronous upload operation.</returns>
        /// <exception cref="ArgumentException">Thrown if key is null, empty, or consists only of white-space characters.</exception>
        public async Task UploadBytesAsync(
            string key,
            byte[] data,
            string contentType = Constants.ContentTypeOctetStream,
            bool overwrite = false,
            CancellationToken ct = default) {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("key is required", nameof(key));

            key = NormalizeKey(key);

            using MemoryStream ms = new(data, writable: false);

            PutObjectRequest req = new() {
                BucketName = options.BucketName,
                Key = key,
                InputStream = ms,
                ContentType = contentType,
            };

            if (!overwrite) {
                // Prevent overwrite if object already exists
                req.Headers[Constants.IfNoneMatchHeader] = Constants._as;
            }

            _ = await s3Client.PutObjectAsync(req, ct);
        }

        /// <summary>
        /// Uploads the specified value as a JSON object to the configured S3 bucket using the provided key.
        /// </summary>
        /// <remarks>If <paramref name="overwrite"/> is <see langword="false"/>, the upload will only
        /// succeed if no object with the specified key already exists in the bucket. The value is serialized to JSON
        /// using the configured serializer options before being uploaded.</remarks>
        /// <typeparam name="T">The type of the value to serialize and upload as JSON.</typeparam>
        /// <param name="key">The object key under which the JSON data will be stored in the S3 bucket. Cannot be null, empty, or
        /// whitespace.</param>
        /// <param name="value">The value to serialize to JSON and upload.</param>
        /// <param name="contentType">The MIME type to associate with the uploaded object. Defaults to ContentTypeJson.</param>
        /// <param name="overwrite">If <see langword="true"/>, overwrites any existing object with the same key; otherwise, the upload will fail
        /// if the object already exists.</param>
        /// <param name="ct">A cancellation token that can be used to cancel the upload operation.</param>
        /// <returns>A task that represents the asynchronous upload operation.</returns>
        /// <exception cref="ArgumentException">Thrown if <paramref name="key"/> is null, empty, or consists only of white-space characters.</exception>
        public async Task UploadJsonAsync<T>(
            string key,
            T value,
            string contentType = Constants.ContentTypeJson,
            bool overwrite = false,
            CancellationToken ct = default) {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("key is required", nameof(key));

            key = NormalizeKey(key);

            byte[] utf8 = JsonSerializer.SerializeToUtf8Bytes(value, _jsonOptions());
            using MemoryStream ms = new(utf8, writable: false);

            PutObjectRequest req = new() {
                BucketName = options.BucketName,
                Key = key,
                InputStream = ms,
                ContentType = contentType,
            };

            if (!overwrite)
                req.Headers["If-None-Match"] = "*";
            _ = await s3Client.PutObjectAsync(req, ct);
        }

        /// <summary>
        /// Uploads the specified structure to storage as a JSON document, generating a unique key if one is not
        /// provided.
        /// </summary>
        /// <remarks>If no key is provided, the method attempts to generate one using common identifier
        /// properties (such as Id or [Type]Id) from the object. If no suitable identifier is found, a new GUID is used.
        /// The resulting key includes the type name and a ".json" extension. The method always serializes the object as
        /// JSON, regardless of the content type specified.</remarks>
        /// <typeparam name="T">The type of the structure to upload. Must be a reference type.</typeparam>
        /// <param name="value">The object to upload. Cannot be null.</param>
        /// <param name="key">An optional key to use for the stored document. If null or empty, a key is generated based on the object's
        /// type and identifier.</param>
        /// <param name="prefix">An optional prefix to prepend to the storage key. Leading and trailing slashes are trimmed. If empty, no
        /// prefix is applied.</param>
        /// <param name="contentType">The content type to associate with the stored document. Defaults to ContentTypeJson.</param>
        /// <param name="overwrite">true to overwrite any existing document with the same key; otherwise, false.</param>
        /// <param name="ct">A cancellation token that can be used to cancel the operation.</param>
        /// <returns>A normalized key string representing the location of the uploaded document in storage.</returns>
        /// <exception cref="ArgumentNullException">Thrown if value is null.</exception>
        public async Task<string> PutStructureAsync<T>(
            T value,
            string? key = null,
            string prefix = "",
            string contentType = Constants.ContentTypeJson,
            bool overwrite = true,
            CancellationToken ct = default) {
            if (value is null) {
                throw new ArgumentNullException(nameof(value));
            }

            // Normalize prefix (optional)
            prefix = string.IsNullOrWhiteSpace(prefix) ? "" : prefix.Trim().Trim('/');

            // If caller didn't provide a key, generate one:
            // - Prefer value.Id / value.ID if it's a Guid or string
            // - Otherwise use a new Guid
            if (string.IsNullOrWhiteSpace(key)) {
                key = TryGetIdLike(value) ?? Guid.NewGuid().ToString("N");
                key = $"{typeof(T).Name.ToLowerInvariant()}-{key}.json";
            }

            // Apply prefix if present
            string fullKey = string.IsNullOrWhiteSpace(prefix) ? key : $"{prefix}/{key}";

            // Reuse your existing uploader (single source of truth)
            await UploadJsonAsync(
                key: fullKey,
                value: value,
                contentType: contentType,
                overwrite: overwrite,
                ct: ct);

            return NormalizeKey(fullKey);

            static string? TryGetIdLike(object obj) {
                Type t = obj.GetType();

                // Common Id names
                PropertyInfo? prop =
                    t.GetProperty("Id") ??
                    t.GetProperty("ID") ??
                    t.GetProperty($"{t.Name}Id") ??
                    t.GetProperty($"{t.Name}ID");

                if (prop is null)
                    return null;

                object? raw = prop.GetValue(obj);
                if (raw is null)
                    return null;

                // Support Guid and string
                return raw switch {
                    Guid g when g != Guid.Empty => g.ToString("N"),
                    string s when !string.IsNullOrWhiteSpace(s) => s.Trim(),
                    _ => null
                };
            }
        }

        /// <summary>
        /// Asynchronously deletes the object with the specified key from the S3 bucket.
        /// </summary>
        /// <param name="key">The unique identifier of the object to delete. Cannot be null, empty, or consist only of white-space
        /// characters.</param>
        /// <param name="ct">A cancellation token that can be used to cancel the delete operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result is <see langword="true"/> if the object
        /// was successfully deleted; <see langword="false"/> if the object was not found.</returns>
        /// <exception cref="ArgumentException">Thrown if <paramref name="key"/> is null, empty, or consists only of white-space characters.</exception>
        public async Task<bool> DeleteAsync(string key, CancellationToken ct = default) {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException($"{nameof(key)} is required", nameof(key));

            key = NormalizeKey(key);

            try {
                _ = await s3Client.DeleteObjectAsync(new DeleteObjectRequest {
                    BucketName = options.BucketName,
                    Key = key,
                }, ct);
                return true;
            } catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) {
                return false;
            }
        }

        /// <summary>
        /// Deletes all objects in the S3 bucket that have keys starting with the specified prefix asynchronously.
        /// </summary>
        /// <remarks>This method deletes all objects matching the specified prefix, potentially making
        /// multiple requests if the number of objects exceeds the S3 batch delete limit. The operation is idempotent;
        /// if no objects match the prefix, no objects are deleted and the result is 0.</remarks>
        /// <param name="prefix">The key prefix used to identify objects to delete. All objects with keys that begin with this prefix will be
        /// deleted.</param>
        /// <param name="ct">A cancellation token that can be used to cancel the delete operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the number of objects that were
        /// deleted.</returns>
        public async Task<int> DeletePrefixAsync(string prefix, CancellationToken ct = default) {

            int deleted = 0;
            string? token = null;

            do {
                ListObjectsV2Response list = await s3Client.ListObjectsV2Async(new ListObjectsV2Request {
                    BucketName = options.BucketName,
                    Prefix = prefix,
                    ContinuationToken = token,
                }, ct);

                if (list.S3Objects.Count > 0) {
                    // S3 supports batch delete up to 1000 keys per request
                    DeleteObjectsRequest del = new() {
                        BucketName = options.BucketName,
                        Objects = [.. list.S3Objects.Select(o => new KeyVersion { Key = o.Key })]
                    };

                    DeleteObjectsResponse resp = await s3Client.DeleteObjectsAsync(del, ct);
                    deleted += resp.DeletedObjects?.Count ?? 0;
                }

                // This is quite similar to scrolling in Elasticsearch
                token = list.IsTruncated.HasValue ? list.NextContinuationToken : null;
            } while (token is not null && !ct.IsCancellationRequested);

            return deleted;
        }

        /// <summary>
        /// Renames an object in the S3 bucket by copying it to a new key and deleting the original object.
        /// </summary>
        /// <remarks>If overwrite is set to false and an object already exists at destKey, the operation
        /// will fail without modifying either object. The rename operation is performed by copying the object to the
        /// new key and then deleting the original. This method is not atomic; if the operation is interrupted after the
        /// copy but before the delete, both objects may temporarily exist.</remarks>
        /// <param name="sourceKey">The key of the source object to rename. Cannot be null, empty, or whitespace.</param>
        /// <param name="destKey">The destination key for the renamed object. Cannot be null, empty, or whitespace.</param>
        /// <param name="overwrite">true to overwrite the destination object if it already exists; otherwise, false. If false and the
        /// destination exists, the operation fails.</param>
        /// <param name="ct">A cancellation token that can be used to cancel the operation.</param>
        /// <returns>A task that represents the asynchronous rename operation.</returns>
        /// <exception cref="ArgumentException">Thrown if sourceKey or destKey is null, empty, or consists only of white-space characters.</exception>
        /// <exception cref="FileNotFoundException">Thrown if the source object does not exist in the bucket.</exception>
        public async Task RenameAsync(
            string sourceKey,
            string destKey,
            bool overwrite = false,
            CancellationToken ct = default) {
            if (string.IsNullOrWhiteSpace(sourceKey))
                throw new ArgumentException($"{nameof(sourceKey)} is required", nameof(sourceKey));
            if (string.IsNullOrWhiteSpace(destKey))
                throw new ArgumentException($"{nameof(destKey)} is required", nameof(destKey));

            sourceKey = NormalizeKey(sourceKey);
            destKey = NormalizeKey(destKey);

            // Ensure source exists
            try {
                _ = await s3Client.GetObjectMetadataAsync(new GetObjectMetadataRequest {
                    BucketName = options.BucketName,
                    Key = sourceKey
                }, ct);
            } catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) {
                throw new FileNotFoundException($"Source object not found: {sourceKey}");
            }

            CopyObjectRequest copyReq = new() {
                SourceBucket = options.BucketName,
                SourceKey = sourceKey,
                DestinationBucket = options.BucketName,
                DestinationKey = destKey,
            };

            if (!overwrite) {
                // Prevent overwrite at destination if it exists
                copyReq.Headers[Constants.IfNoneMatchHeader] = "*";
            }

            // Copy is synchronous (returns when complete)
            _ = await s3Client.CopyObjectAsync(copyReq, ct);

            // Delete source after copy
            _ = await s3Client.DeleteObjectAsync(new DeleteObjectRequest {
                BucketName = options.BucketName,
                Key = sourceKey
            }, ct);
        }

        /// <summary>
        /// Asynchronously downloads the contents of the specified object from the S3 bucket as a byte array.
        /// </summary>
        /// <param name="key">The key that identifies the object to download from the S3 bucket. Cannot be null, empty, or whitespace.</param>
        /// <param name="ct">A cancellation token that can be used to cancel the download operation.</param>
        /// <returns>A byte array containing the contents of the specified object. The array is empty if the object has no data.</returns>
        /// <exception cref="ArgumentException">Thrown if <paramref name="key"/> is null, empty, or consists only of white-space characters.</exception>
        public async Task<byte[]> DownloadBytesAsync(string key, CancellationToken ct = default) {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException($"{nameof(key)} is required", nameof(key));

            key = NormalizeKey(key);

            using GetObjectResponse resp = await s3Client.GetObjectAsync(new GetObjectRequest {
                BucketName = options.BucketName,
                Key = key,
            }, ct);

            using MemoryStream ms = new();
            await resp.ResponseStream.CopyToAsync(ms, ct);
            return ms.ToArray();
        }

        /// <summary>
        /// Asynchronously downloads JSON data identified by the specified key and deserializes it to an object of type
        /// T.
        /// </summary>
        /// <typeparam name="T">The type to which the downloaded JSON data will be deserialized.</typeparam>
        /// <param name="key">The key that identifies the JSON data to download. Cannot be null or empty.</param>
        /// <param name="ct">A cancellation token that can be used to cancel the download operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the deserialized object of type
        /// T.</returns>
        /// <exception cref="JsonException">Thrown if the downloaded JSON data is null, empty, or cannot be deserialized to type T.</exception>
        public async Task<T> DownloadJsonAsync<T>(string key, CancellationToken ct = default) {
            byte[] data = await DownloadBytesAsync(key, ct);
            T? value = JsonSerializer.Deserialize<T>(data, _jsonOptions());

            return value is null
                ? throw new JsonException($"Object '{key}' contained null/empty JSON for type {typeof(T).Name}")
                : value;
        }

        /// <summary>
        /// Asynchronously downloads the object identified by the specified key and writes its contents to the provided
        /// stream.
        /// </summary>
        /// <param name="key">The key that identifies the object to download. Cannot be null, empty, or consist only of white-space
        /// characters.</param>
        /// <param name="target">The stream to which the downloaded object data will be written. The stream must be writable.</param>
        /// <param name="ct">A cancellation token that can be used to cancel the download operation.</param>
        /// <returns>A task that represents the asynchronous download operation.</returns>
        /// <exception cref="ArgumentException">Thrown if the key is null, empty, or consists only of white-space characters.</exception>
        public async Task DownloadToAsync(string key, Stream target, CancellationToken ct = default) {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException($"{nameof(key)} is required", nameof(key));

            key = NormalizeKey(key);

            using GetObjectResponse resp = await s3Client.GetObjectAsync(new GetObjectRequest {
                BucketName = options.BucketName,
                Key = key,
            }, ct);

            await resp.ResponseStream.CopyToAsync(target, ct);
        }

        /// <summary>
        /// Asynchronously retrieves a list of S3 objects from the specified bucket and prefix whose keys end with the
        /// given suffix. The effectiveness of this method is really dependenant on your personal system's naming strategy
        /// for keys in S3. Directories, prefix dates, etc., can help limit the number of objects that need to be scanned
        /// improving UI/UX with quicker load times.
        /// </summary>
        /// <remarks>This method automatically handles pagination of S3 object listings. The operation may
        /// make multiple requests to S3 if the result set is large. The returned list contains only objects whose keys
        /// match both the specified prefix and suffix.</remarks>
        /// <param name="prefix">The key prefix to filter objects within the bucket. Only objects with keys that start with this prefix are
        /// considered.</param>
        /// <param name="endsWith">The suffix that object keys must end with to be included in the results. The comparison is case-insensitive.</param>
        /// <param name="ct">A cancellation token that can be used to cancel the asynchronous operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains a list of S3 objects whose keys
        /// match the specified prefix and end with the given suffix. The list is empty if no matching objects are
        /// found.</returns>
        public async Task<List<T>> ListFilteredObjectsAsync<T>(
            string prefix,
            string endsWith,
            CancellationToken ct = default) {
            List<T> responses = [];
            string? continuation = null;

            List<T> results = [];

            do {
                // Prefix filtering is built-in
                ListObjectsV2Request request = new() { BucketName = options.BucketName, Prefix = prefix, ContinuationToken = continuation, };

                ListObjectsV2Response response = await s3Client.ListObjectsV2Async(request, ct);
                if (response == null || response.S3Objects.Count == 0) {
                    break;
                }

                // Filter objects by suffix with LINQ
                List<string> filteredKeys = [.. response.S3Objects
                    .Where(obj => obj.Key
                        .EndsWith(endsWith, StringComparison.OrdinalIgnoreCase))
                    .Select(obj => obj.Key)];

                // Concurrent download and deserialize all matching objects
                T?[] values = await Task.WhenAll(
                    filteredKeys.Select(async key =>
                        JsonSerializer.Deserialize<T>(
                            await DownloadBytesAsync(key, ct),
                            _jsonOptions()))
                );

                if (values.Length != 0) {
                    if (response.IsTruncated is null) {
                        continuation = null;
                        break;
                    } else {
                        continuation = response!.NextContinuationToken;
                    }
                } else {
                    break;
                }
            } while (continuation is not null && !ct.IsCancellationRequested);

            return results;
        }

        /// <summary>
        /// Normalizes a key name by replacing backslashes with forward slashes and removing any leading slashes.
        /// </summary>
        /// <param name="name">The key name to normalize. Cannot be null.</param>
        /// <returns>A normalized string with all backslashes replaced by forward slashes and any leading slashes removed.</returns>
        public static string NormalizeKey(string name) => name.Replace('\\', '/').TrimStart('/');
    }
}