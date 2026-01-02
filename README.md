# S3mphony
- S3mphony is a lightweight, generic, developer-friendly C# library that simplifies Amazon S3 interactions for APIs and services.
- Designed for clean architecture and efficient reads, it provides seamless blob listing, JSON serialization, and upload/download utilities through a single fluent channel interface.
- Built-in support for read-through caching and request-level concurrency gating helps prevent redundant bucket calls, avoid cache pollution from empty results, and keep applications responsive under load.
- From a user’s perspective, everything is simplified: intuitive methods, sensible defaults, and a storage model that feels effortless while staying efficient behind the scenes.
- Ideal for APIs, background workers, dashboards, and ML-ops prototypes that need resilient, low-cost, cache-aware access to S3.
- It turns S3 into a database, in an odd way.

![NuGet Version](https://img.shields.io/nuget/v/S3mphony?style=flat)

## S3 Storage Utility Integration

### Example installation and setup:
- Included is `S3Options`, a C# class to hold S3 configuration options.
- Then, in your `Startup.cs` or `Program.cs`, wherever you configure services, add the following:

```csharp
S3Options s3Options = new(builder.Configuration["AWS:S3");

builder.Configuration.Bind(s3Options);

services.Configure<S3Options>(config.GetSection("AWS:S3"));

services.AddSingleton<S3Settings>(s3Options);
services.AddSingleton<S3StorageUtility>(s3Options);
services.AddSingleton<S3Channel>();
```

### Additional service registrations for caching. 
```csharp
builder.Services.AddOutputCache();
builder.Services.AddMemoryCache();
```

- This allows you to inject `S3StorageUtility` wherever needed in your application to interact with S3 storage.
- You can also inject the `S3Settings` directly if you need access to the configuration values. 
- Make sure to replace the placeholder values in `appsettings.json` with your actual AWS S3 credentials and bucket information.

### Example usage of S3StorageUtility:
```csharp
ImportantBusinessDocumentData meetingNotes = new()
{
    Id = Guid.NewGuid(),
    Text = meetingNotes,
    MeetingDate = DateTime.Now
};

var createdKey = await _s3Channel.PutStructureAsync(
    meetingNotes,
    prefix: "meeting-notes/,
    overwrite: false,
    ct: ct);
```

### Example retrieval of data for displaying in an application:
```csharp
IEnumerable<ImportantBusinessDocumentData>? meetingNotes = Cache.Get<IEnumerable<ImportantBusinessDocumentData>>(_recentMeetingNotesCached) ?? null;
if (meetingNotes == null || !meetingNotes.Any()) {
    meetingNotes = await s3Client.GetFromJsonAsync<IEnumerable<ImportantBusinessDocumentData>>("meeting-notes/") ?? null;
    if (meetingNotes != null && meetingNotes.Any())
    {
        Cache.Set(_recentMeetingNotesCached, meetingNotes, _memoryCacheEntryOptions);
    }
}
```

### Example `appsettings.json` configuration for S3 storage:
```json
{
  "AWS": {
    "S3": {
      "BucketName": "ohio-developer-bucket",
      "Region": "us-east-1",
      "AccessKeyId": "FAKE471337942",
      "SecretAccessKey": "DontWorryItIsntARealAccessKey"
    }
  }
} 
```

### Then you've got your cool designed UI with whatever library. A simple example using FluentUI for Blazor:
```csharp
@using Microsoft.Extensions.Caching.Memory
@using Microsoft.FluentUI.AspNetCore.Components
@inject IMemoryCache Cache
@inject IS3Channel S3Channel

<FluentCard style="width: 100%; padding: 16px;">
    <div style="display:flex; align-items:center; gap:12px; justify-content:space-between;">
        <div>
            <FluentText Typography="Typography.PaneHeader">Meeting Notes</FluentText>
            <FluentText Typography="Typography.Body" style="opacity:.8;">
                Recent notes from S3, cached locally when non-empty.
            </FluentText>
        </div>

        <div style="display:flex; gap:8px; align-items:center;">
            <FluentTextField @bind-Value="_query"
                             Placeholder="Search text…"
                             Style="min-width: 260px;"
                             Immediate="true" />
            <FluentButton Appearance="Appearance.Accent" OnClick="RefreshAsync" Disabled="@_busy">
                @(_busy ? "Loading…" : "Refresh")
            </FluentButton>
        </div>
    </div>

    <div style="margin-top: 14px;">
        @if (_busy && _notes.Count == 0)
        {
            <FluentProgressRing />
        }
        else if (_notes.Count == 0)
        {
            <FluentMessageBar Intent="MessageIntent.Info">
                No meeting notes yet. Upload one and it’ll appear here.
            </FluentMessageBar>
        }
        else
        {
            <FluentDataGrid Items="@FilteredNotes"
                            ResizableColumns="true"
                            GridTemplateColumns="1fr 170px 120px"
                            RowClass="row-hover"
                            Style="width: 100%;">

                <PropertyColumn Title="Summary" Property="@(n => Preview(n.Text))" />
                <PropertyColumn Title="Meeting Date" Property="@(n => n.MeetingDate.ToString("yyyy-MM-dd HH:mm"))" />
                <TemplateColumn Title="">
                    <FluentButton Appearance="Appearance.Stealth" OnClick="@(async () => OpenAsync(context))">
                        View
                    </FluentButton>
                </TemplateColumn>
            </FluentDataGrid>
        }
    </div>
</FluentCard>

<FluentDialog @bind-Visible="_dialogOpen" Modal="true" Style="width: min(900px, 92vw);">
    <DialogTitle>
        <FluentText Typography="Typography.Title">Meeting Note</FluentText>
    </DialogTitle>

    <DialogContent>
        @if (_selected is not null)
        {
            <FluentText Typography="Typography.Subtitle">
                @_selected.MeetingDate.ToString("f")
            </FluentText>

            <FluentDivider />

            <div style="white-space: pre-wrap; line-height: 1.35; margin-top: 10px;">
                @_selected.Text
            </div>
        }
    </DialogContent>

    <DialogActions>
        <FluentButton Appearance="Appearance.Outline" OnClick="@(() => _dialogOpen = false)">Close</FluentButton>
    </DialogActions>
</FluentDialog>

@code {
    private const string Prefix = "meeting-notes/";          // no leading slash is usually cleaner for S3 keys
    private const string CacheKey = "recent:meeting-notes";  // local memory cache key
    private static readonly MemoryCacheEntryOptions CacheOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10),
        Priority = CacheItemPriority.High
    };

    private readonly List<ImportantBusinessDocumentData> _notes = new();
    private ImportantBusinessDocumentData? _selected;
    private bool _busy;
    private bool _dialogOpen;
    private string _query = "";

    protected override async Task OnInitializedAsync()
        => await LoadAsync(forceRefresh: false);

    private IEnumerable<ImportantBusinessDocumentData> FilteredNotes =>
        string.IsNullOrWhiteSpace(_query)
            ? _notes
            : _notes.Where(n => (n.Text ?? "").Contains(_query, StringComparison.OrdinalIgnoreCase));

    private async Task RefreshAsync()
        => await LoadAsync(forceRefresh: true);

    private async Task LoadAsync(bool forceRefresh)
    {
        _busy = true;
        try
        {
            if (!forceRefresh &&
                Cache.TryGetValue(CacheKey, out List<ImportantBusinessDocumentData>? cached) &&
                cached is { Count: > 0 })
            {
                _notes.Clear();
                _notes.AddRange(cached.OrderByDescending(n => n.MeetingDate));
                return;
            }

            // Pull most recent notes from S3 (your library)
            var fromS3 = await S3Channel.GetRecentStructuresAsync<ImportantBusinessDocumentData>(
                prefix: Prefix,
                takeMostRecent: 100,
                ct: CancellationToken.None);

            _notes.Clear();
            _notes.AddRange(fromS3.OrderByDescending(n => n.MeetingDate));

            // Cache only if signal
            if (_notes.Count > 0)
                Cache.Set(CacheKey, _notes.ToList(), CacheOptions);
        }
        finally
        {
            _busy = false;
        }
    }

    private Task OpenAsync(ImportantBusinessDocumentData note)
    {
        _selected = note;
        _dialogOpen = true;
        return Task.CompletedTask;
    }

    private static string Preview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(empty)";
        text = text.Trim().Replace("\r", " ").Replace("\n", " ");
        return text.Length <= 90 ? text : text[..90] + "…";
    }
}
```
