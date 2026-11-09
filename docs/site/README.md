# Docs site (contributors)

This folder builds the docs site with [DocFX](https://dotnet.github.io/docfx/) 2.81.0. This file is not part of the site.

## Layout

- `guides/` : hand-written guides. `guides/toc.yml` sets the order.
- `snippets/` : the C# samples, one library project (`A2A.DocSnippets`). It is in `A2A.slnx`, so a normal build compiles it. Warnings are errors.
- `api/` : generated from `src/**/*.csproj`. Only `api/index.md` is committed.
- `filterConfig.yml` : leaves the generated gRPC types (namespace `Lf.A2a.V1`) out of the API pages.
- `check-guides.py` : checks the guides (see below).

## Rules for guides

- No C# fenced blocks. Put the code in `snippets/` inside `#region Name` ... `#endregion` and include it:

  ```text
  [!code-csharp[](../snippets/GettingStarted.cs#Server)]
  ```

  The path is relative to the guide file. The include must be alone on its line. Shell, JSON, YAML and HTTP fences are fine.
- Link types and members with `<xref:A2A.Server.A2AServerOptions>`. A wrong name fails the build.
- Link repo files outside `docs/site/` with `https://github.com/roman-ambinder/a2a/blob/main/<path>`.

## Build and preview locally

From the repo root:

```shell
dotnet tool restore
dotnet build docs/site/snippets/A2A.DocSnippets.csproj
python3 docs/site/check-guides.py
dotnet docfx docs/site/docfx.json --warningsAsErrors
```

The site is written to `docs/site/_site`. To preview it in a browser:

```shell
dotnet docfx serve docs/site/_site
```

Open the address it prints (default `http://localhost:8080`).

Notes:

- DocFX builds the API metadata with `TargetFramework=net10.0` (the `properties` in the `metadata` section of `docfx.json`), so one API set is produced. It also sets `DocfxMetadata=true`, which makes `Directory.Build.props` skip the `PublicAPI.*.txt` `AdditionalFiles` (DocFX lists them twice and warns).
- A missing code snippet file is a DocFX warning. A missing region is not: DocFX renders an empty block. `check-guides.py` catches it.

## CI

The `docs` job in `.github/workflows/ci.yml` runs these steps, then checks links with lychee (`--offline` on the built site; external links only on the guide and home markdown) and uploads `docs/site/_site` as the `docs-site` artifact.

## Publishing

`.github/workflows/docs-pages.yml` runs after CI succeeds on `main`. It takes the `docs-site` artifact from that CI run (no rebuild) and deploys it to https://roman-ambinder.github.io/a2a/.

One-time setup: repo **Settings** > **Pages** > **Build and deployment** > **Source**: choose **GitHub Actions**.

Links in the site are relative, so serving under `/a2a/` needs no base path change.
