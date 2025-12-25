using System;
using System.Collections.Generic;
using System.Text;

namespace S3mphony.Models {
    /// <summary>
    /// Represents configuration options for connecting to an Amazon S3 service.
    /// </summary>
    /// <remarks>This class is typically used to bind S3-related settings from a configuration source, such as
    /// appsettings.json, for use in S3 client initialization. All properties are immutable after
    /// initialization.</remarks>
    public sealed class S3Options {
        public static string SectionName = @"S3";
        public string AccessKeyId { get; init; } = string.Empty;
        public string SecretAccessKey { get; init; } = string.Empty;
        public string Region { get; init; } = "us-east-2";
        public string? BucketName { get; init; }
    }
}
