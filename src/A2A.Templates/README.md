# RomanAmbinder.A2A.Templates

`dotnet new` templates for the [A2A .NET SDK](https://roman-ambinder.github.io/a2a/): the quickest way to start a new
agent or client project.

```bash
dotnet new install RomanAmbinder.A2A.Templates
dotnet new a2a-agent -n WeatherAgent        # an agent on ASP.NET Core
dotnet new a2a-client -n WeatherClient      # a console app that calls an agent
dotnet new a2a-chat-agent -n ChatAgent      # an agent that hosts an IChatClient (Microsoft.Extensions.AI)
dotnet new a2a-agent -n KeyedAgent --auth apikey   # API keys: hashes in appsettings.json, a2a keys new makes one
```

The agent templates read their Agent Card and limits from the `A2A` section of `appsettings.json`. In the Development
environment they call `UseDevelopmentDefaults()`, so `curl` and anonymous callers work locally. In any other environment
the agents made without `--auth apikey` ask for a bearer token (`AddBearerAuthentication()`, declared on the card) and
refuse to start until you register the scheme, for example `AddJwtBearer()` from
`Microsoft.AspNetCore.Authentication.JwtBearer`. With `--auth apikey`, an API key is required in every environment. The
client template sends the key in the `A2A_API_KEY` environment variable.

See the [getting started guide](https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/getting-started.md).
