# ArtifactStorage sample

An agent writes a 1 MiB file to an object store, and a client downloads it. The store is S3 on MinIO, or Azure Blob on
Azurite, picked by the argument. Both run in Docker, with ports bound to `127.0.0.1` only; the agent listens on
`127.0.0.1:5380`.

| Part | What it shows |
|---|---|
| `AddA2AS3ArtifactStore` / `AddA2AAzureBlobArtifactStore` | the two providers, with a client or connection string the app gives them |
| `ArtifactStorage.PublicBaseUrl`, `MapA2AArtifacts` | the links and the download route, on the agent's origin, with the same authorization as `MapA2A` |
| `AddArtifactFromStreamAsync` | the bytes go straight to the store; the task holds one `url` part with `sizeBytes` and `sha256` |
| `A2AArtifactDownloader` | sends the API key to the agent only, follows the 302 to the presigned URL without it, and checks size and hash |

The sample checks that the downloaded bytes are the bytes the agent wrote, that the agent answered 302 (a presigned
URL, so the bytes did not pass through the agent), and that the bucket or container holds exactly one object for the run.

Run it with `samples/run-samples.sh storage`, or by hand:

```bash
docker compose -f samples/ArtifactStorage/docker-compose.yml up -d --wait
dotnet run --project samples/ArtifactStorage -- s3
dotnet run --project samples/ArtifactStorage -- azure
docker compose -f samples/ArtifactStorage/docker-compose.yml down -v
```

Expected output ends with:

```text
downloaded: 1048576 bytes; the agent answered 302
in the store: run-<id>/_/<taskId>/<objectId> (1048576 bytes)
OK: the file went to S3, and the client downloaded the same bytes through a presigned URL.
```

Notes:
- The API key is made for each run (`A2AApiKey.Create()`). The MinIO user and password are demo values. Azurite uses its well-known development account.
- MinIO and Azurite run on a private address over plain HTTP, so the downloader needs
  `SafeHttpOptions { AllowPrivateNetworks = true, AllowHttp = true }`. A store on a public HTTPS host needs neither.
- Each run writes under a new key prefix and does not delete its object. `down -v` removes them. A real host runs
  `AddA2ARetention()`, whose sweep removes objects of deleted tasks.
- The MinIO image comes from `cgr.dev`, pinned by digest, as in `tests/A2A.Stores.Tests`.

See the [artifact storage guide](../../docs/site/guides/artifact-storage.md).
