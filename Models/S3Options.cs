namespace S3mphony.Models {
    /// <summary>
    /// Represents configuration options for connecting to an Amazon S3 service. This bnids with your appsettings.json.
    /// </summary>
    /// <example>
    /// appsettings.json:
    /// <code>
    /// {
    ///     "AWS": {
    ///         "S3": {
    ///             "AccessKeyId": "",
    ///             "SecretAccessKey: "",
    ///             "Region: "",
    ///             "BucketName: ""
    ///         }
    ///     }
    /// }
    /// </code>
    /// Then, in your Startup.cs or Program.cs:
    /// <code>
    /// builder.Services.Configure<S3Options>(builder.Configuration.GetSection("AWS:S3"));
    /// </code>
    /// </example>
    /// <remarks>This class is used to bind S3-related settings from a configuration source, such as
    /// appsettings.json, for use in S3 client initialization.</remarks>
    public sealed class S3Options {
        public static readonly string SectionName = Constants.S3SettingsSectionName;
        public string AccessKeyId { get; init; } = string.Empty;
        public string SecretAccessKey { get; init; } = string.Empty;
        public string Region { get; init; } = "us-east-2";
        public string? BucketName { get; init; }
    }
}
