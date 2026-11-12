using System.Security.Cryptography;
using System.Text;
using A2A;
using A2A.Client;
using A2A.Client.Transports;
using A2A.Security.Http;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Storage.AzureBlob;
using A2A.Server.Storage.S3;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

// An agent that writes a 1 MiB file to an object store, and a client that downloads it. The store is MinIO (S3) or
// Azurite (Azure Blob), both from docker-compose.yml next to this file:
//   docker compose -f samples/ArtifactStorage/docker-compose.yml up -d --wait
//   dotnet run --project samples/ArtifactStorage -- s3      # or: -- azure
// The agent listens on 127.0.0.1 only. The API key is made for each run; the store credentials are demo values.

if (args.Length == 0 || args[0] is not ("s3" or "azure"))
{
    Console.Error.WriteLine("Usage: dotnet run --project samples/ArtifactStorage -- <s3|azure>");
    return 2;
}

var provider = args[0];
const string agentUrl = "http://127.0.0.1:5380";
// The caller's API key, made for this run. The agent stores only its hash (A2AApiKey.Hash).
var apiKey = A2AApiKey.Create();
const string container = "a2a-sample"; // the S3 bucket or the Azure container
var keyPrefix = $"run-{Guid.NewGuid():N}/"; // a fresh prefix per run, so the check below sees only this run's objects

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(agentUrl);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// Downloads need the caller's A2A credentials: there are no links that work without them. Callers send an API key in
// the X-Api-Key header (AddApiKeyAuthentication below).

var card = new AgentCard
{
    Name = "File Agent",
    Description = "Makes a file and keeps its bytes in an object store.",
    Version = "1.0.0",
    // No SecuritySchemes: AddApiKeyAuthentication declares the X-Api-Key scheme on the card.
    DefaultInputModes = ["text/plain"],
    DefaultOutputModes = ["application/octet-stream"],
    Skills = [new AgentSkill { Id = "file", Name = "File", Description = "Makes a 1 MiB file", Tags = ["demo"] }]
};

builder.Services.AddA2A(a => a.AddAgentExecutor<FileAgent>().AddAgentCard(card)
        .AddApiKeyAuthentication(o => o.Add("file-client", apiKey.Hash)))
    .Configure(o =>
    {
        // The base of every artifact link: the origin clients send their credentials to (the agent's).
        o.ArtifactStorage.PublicBaseUrl = new Uri($"{agentUrl}/a2a");
        // Redirect (the default): the agent answers 302 to a presigned URL of the store, so the bytes do not pass
        // through the agent. Use Proxy when clients cannot reach the store, or when they send header API keys and
        // their HTTP library follows redirects (see the artifact storage guide).
        o.ArtifactStorage.Delivery = ArtifactDelivery.Redirect;
    });

// The store. Both providers need the bucket or container to exist already.
AmazonS3Client? s3 = null;
BlobContainerClient? blobs = null;
if (provider == "s3")
{
    // A client this app owns. Presigned URLs are built from ServiceURL, so it must be an address clients can reach.
    s3 = new AmazonS3Client(new BasicAWSCredentials("a2a-demo", "a2a-demo-secret"),
        new AmazonS3Config { ServiceURL = "http://127.0.0.1:9000", ForcePathStyle = true });
    if (!await AmazonS3Util.DoesS3BucketExistV2Async(s3, container))
        await s3.PutBucketAsync(container);
    builder.Services.AddA2AS3ArtifactStore(o =>
    {
        o.Client = s3;
        o.BucketName = container;
        o.KeyPrefix = keyPrefix;
    });
}
else
{
    // Azurite's well-known development account. The account key lets the store make SAS download links.
    const string connectionString = "UseDevelopmentStorage=true";
    blobs = new BlobContainerClient(connectionString, container);
    await blobs.CreateIfNotExistsAsync();
    builder.Services.AddA2AAzureBlobArtifactStore(o =>
    {
        o.ConnectionString = connectionString;
        o.ContainerName = container;
        o.KeyPrefix = keyPrefix;
    });
}

var app = builder.Build();
var artifactStatus = 0; // the status code of the last artifact download request, to see whether the agent redirected
app.Use(async (http, next) =>
{
    await next(http);
    if (http.Request.Path.StartsWithSegments("/a2a/artifacts")) artifactStatus = http.Response.StatusCode;
});
app.UseAuthentication();
app.UseAuthorization();
#region Map
app.MapA2A("/a2a");
app.MapA2AArtifacts("/a2a"); // the download route: same pattern; the API key protects both
#endregion
await app.StartAsync();
Console.WriteLine($"Agent listening on {agentUrl}/a2a, store: {(provider == "s3" ? "S3 (MinIO)" : "Azure Blob (Azurite)")}");

var failures = new List<string>();

// The handler adds the API key to requests for the agent. It must not follow redirects: the downloader follows the
// one redirect itself, without credentials, so the key never goes to the store.
var agentHandler = new ApiKeyHandler(apiKey.Key) { InnerHandler = new SocketsHttpHandler { AllowAutoRedirect = false } };
using var http = new HttpClient(agentHandler, disposeHandler: false);
var options = new A2AClientOptions();
var client = new A2AClient(new JsonRpcTransport(http, new Uri($"{agentUrl}/a2a"), options), options, card);

var reply = await client.SendMessageAsync("make the file");
var task = (reply as TaskResponse)?.Task;
Console.WriteLine($"task: {task?.Status.State}");
var part = task?.Artifacts?.SelectMany(a => a.Parts).OfType<UrlPart>().FirstOrDefault();
if (task?.Status.State != TaskState.Completed || part is null)
{
    failures.Add($"expected a completed task with a url part, got {task?.Status.State}");
}
else
{
    // The task holds a link, not the bytes. The part metadata has the size and the hash of the object.
    Console.WriteLine($"part: {part.Url}");
    Console.WriteLine($"  sizeBytes {part.Metadata?["sizeBytes"]}, sha256 {part.Metadata?["sha256"]}");

    #region PrivateStore
    // MinIO and Azurite run on a private address over plain HTTP. By default the downloader follows the redirect
    // only to a public HTTPS host.
    var downloader = new A2AArtifactDownloader(agentHandler, new Uri(agentUrl),
        new SafeHttpOptions { AllowPrivateNetworks = true, AllowHttp = true });
    #endregion
    using var downloaded = new MemoryStream();
    // It checks sizeBytes and sha256 from the metadata, and throws InvalidDataException on a mismatch.
    await downloader.DownloadAsync(part, downloaded, maxBytes: 10L << 20);
    Console.WriteLine($"downloaded: {downloaded.Length} bytes; the agent answered {artifactStatus}");

    if (!SHA256.HashData(downloaded.ToArray()).AsSpan().SequenceEqual(SHA256.HashData(FileAgent.Content())))
        failures.Add("the downloaded bytes differ from the bytes the agent wrote");
    if (artifactStatus != 302)
        failures.Add($"expected the agent to redirect to a presigned URL (302), got {artifactStatus}");
}

// The bytes are in the store, under this run's prefix, and not in the task.
var stored = new List<(string Key, long Size)>();
if (s3 is not null)
{
    var listed = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = container, Prefix = keyPrefix });
    stored.AddRange((listed.S3Objects ?? []).Select(o => (o.Key, o.Size ?? 0)));
}
else
{
    await foreach (var blob in blobs!.GetBlobsAsync(BlobTraits.None, BlobStates.None, keyPrefix, CancellationToken.None))
        stored.Add((blob.Name, blob.Properties.ContentLength ?? 0));
}

foreach (var (key, size) in stored) Console.WriteLine($"in the store: {key} ({size} bytes)");
if (stored.Count != 1 || stored[0].Size != FileAgent.Size)
    failures.Add($"expected one object of {FileAgent.Size} bytes in the store, found {stored.Count}");

await app.StopAsync();
s3?.Dispose();

if (failures.Count > 0)
{
    foreach (var failure in failures) Console.Error.WriteLine("FAIL: " + failure);
    return 1;
}

Console.WriteLine($"OK: the file went to {(provider == "s3" ? "S3" : "Azure Blob")}, and the client downloaded the same bytes through a presigned URL.");
return 0;

/// <summary>Writes a 1 MiB file with AddArtifactFromStreamAsync. The bytes go straight to the store, never through an event.</summary>
internal sealed class FileAgent : IAgentExecutor
{
    public const int Size = 1 << 20;

    /// <summary>The same bytes on every call, so the client can check what it downloaded.</summary>
    public static byte[] Content()
    {
        var bytes = new byte[Size];
        new Random(42).NextBytes(bytes);
        return bytes;
    }

    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken)
    {
        await updater.StartWorkAsync(cancellationToken);
        await using var file = new MemoryStream(Content());
        // One sealed artifact with one url part. The part metadata gets the object's size and sha256.
        await updater.AddArtifactFromStreamAsync(file, "application/octet-stream", name: "data.bin",
            cancellationToken: cancellationToken);
        await updater.CompleteAsync("Your file is ready.", cancellationToken);
    }
}

/// <summary>Adds the <c>X-Api-Key</c> header to every request.</summary>
internal sealed class ApiKeyHandler(string apiKey) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove("X-Api-Key");
        request.Headers.Add("X-Api-Key", apiKey);
        return base.SendAsync(request, cancellationToken);
    }
}

