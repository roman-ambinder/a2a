# RomanAmbinder.A2A.Templates

`dotnet new` templates for the [A2A .NET SDK](https://roman-ambinder.github.io/a2a/).

```bash
dotnet new install RomanAmbinder.A2A.Templates
dotnet new a2a-agent -n WeatherAgent        # an agent on ASP.NET Core
dotnet new a2a-client -n WeatherClient      # a console app that calls an agent
dotnet new a2a-chat-agent -n ChatAgent      # an agent that hosts an IChatClient (Microsoft.Extensions.AI)
```

The agent templates read their Agent Card and limits from the `A2A` section of `appsettings.json`. In the Development
environment they call `UseDevelopmentDefaults()`, so `curl` and anonymous callers work locally. In any other environment
the A2A endpoints require authorization: add your authentication scheme in `Program.cs` before you deploy.
