using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using A2A.Client;
using A2A.Json;
using A2A.Signing;

namespace A2A.Cli;

/// <summary>Builds the output of the commands. Text from the agent always goes through <see cref="Output.One"/> or <see cref="Output.Block"/>.</summary>
internal static class Render
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Writes JSON with <see cref="Utf8JsonWriter"/> and returns it as one line.</summary>
    public static string Json(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions)) write(writer);
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    // ---- card ----

    public static string CardJson(ResolvedAgentCard resolved) => Json(w =>
    {
        w.WriteStartObject();
        w.WriteString("source", resolved.Source.AbsoluteUri);
        w.WriteStartObject("verification");
        w.WriteString("status", resolved.Verification.Status.ToString().ToLowerInvariant());
        w.WriteString("keyId", resolved.Verification.KeyId);
        w.WriteString("reason", resolved.Verification.Reason);
        w.WriteEndObject();
        w.WritePropertyName("card");
        JsonSerializer.Serialize(w, resolved.Card, A2AJson.GetTypeInfo<AgentCard>());
        w.WriteEndObject();
    });

    public static IEnumerable<string> CardText(ResolvedAgentCard r, Output o)
    {
        var card = r.Card;
        yield return $"Name:         {o.One(card.Name)}";
        yield return $"Version:      {o.One(card.Version)}";
        yield return $"Source:       {o.One(r.Source.GetLeftPart(UriPartial.Path))}";
        yield return "Description:";
        foreach (var line in o.Block(r.Description.Raw).Split('\n')) yield return "  " + line.TrimEnd();
        if (card.Provider is { } p) yield return $"Provider:     {o.One(p.Organization)} ({o.One(p.Url)})";

        var v = r.Verification;
        var key = v.KeyId is null ? "" : $", key id {o.One(v.KeyId)}";
        var reason = v.Reason is null ? "" : $": {o.One(v.Reason)}";
        yield return $"Verification: {v.Status}{key}{reason}";
        var id = r.Identity;
        yield return $"Peer:         {o.One(id.Origin)}{(id.KeyId is null ? "" : "#" + o.One(id.KeyId))}, assurance {id.Assurance}";

        yield return "Interfaces:";
        foreach (var i in card.SupportedInterfaces)
            yield return $"  {o.One(i.ProtocolBinding)} {o.One(i.Url.Split('?')[0])} (v{o.One(i.ProtocolVersion)})";
        yield return "Skills:";
        for (var n = 0; n < r.Skills.Count; n++)
            yield return $"  {o.One(card.Skills[n].Id)}: {o.One(r.Skills[n].Name.Raw)}: {o.One(r.Skills[n].Description.Raw, 200)}";
        yield return $"Streaming:    {(card.Capabilities.Streaming == true ? "yes" : "no")}";
    }

    // ---- tasks and messages ----

    public static IEnumerable<string> TaskText(AgentTask t, Output o, bool withHistory = false)
    {
        yield return $"Task:     {o.One(t.Id)}";
        if (t.ContextId.Length > 0) yield return $"Context:  {o.One(t.ContextId)}";
        var message = t.Status.Message is { } m ? ": " + o.One(m.GetText()) : "";
        yield return $"State:    {t.Status.State}{message}";
        if (t.Status.Timestamp is { } ts) yield return $"Updated:  {ts.ToString("u", CultureInfo.InvariantCulture)}";
        foreach (var line in ArtifactLines(t.Artifacts, o)) yield return line;
        if (withHistory && t.History is { Count: > 0 } h) yield return $"History:  {h.Count} message(s)";
    }

    private static IEnumerable<string> ArtifactLines(List<Artifact>? artifacts, Output o)
    {
        if (artifacts is not { Count: > 0 }) yield break;
        yield return "Artifacts:";
        foreach (var a in artifacts)
        {
            var name = string.IsNullOrEmpty(a.Name) ? "" : $" \"{o.One(a.Name, 100)}\"";
            yield return $"  {o.One(a.ArtifactId, 100)}{name}";
            foreach (var part in a.Parts) yield return "    " + PartText(part, o);
        }
    }

    // The part URL is never shown: presigned URLs are secrets.
    public static string PartText(Part part, Output o) => part switch
    {
        TextPart t => $"[text] {o.One(t.Text, 200)}",
        UrlPart u => $"[file] {o.One(u.Filename ?? "(no name)", 100)}{(u.MediaType is null ? "" : " (" + o.One(u.MediaType, 60) + ")")}",
        RawPart r => $"[raw] {r.Raw.Length} bytes",
        DataPart => "[data]",
        _ => "[part]",
    };

    public static IEnumerable<string> MessageText(Message m, Output o)
    {
        yield return $"Message from {m.Role}{(m.TaskId is null ? "" : ", task " + o.One(m.TaskId))}:";
        foreach (var line in o.Block(m.GetText()).Split('\n')) yield return "  " + line.TrimEnd();
        foreach (var part in m.Parts.Where(p => p is not TextPart)) yield return "  " + PartText(part, o);
    }

    public static IEnumerable<string> SendText(SendMessageResponse r, Output o) => r switch
    {
        TaskResponse t => TaskText(t.Task, o),
        MessageResponse m => MessageText(m.Message, o),
        _ => [],
    };

    /// <summary>One line for one stream event.</summary>
    public static string EventLine(StreamResponse e, Output o) => e switch
    {
        TaskEvent t => $"task {o.One(t.Task.Id)} {t.Task.Status.State}",
        MessageEvent m => $"message {m.Message.Role}: {o.One(m.Message.GetText())}",
        StatusUpdateEvent s => $"status {o.One(s.StatusUpdate.TaskId)} {s.StatusUpdate.Status.State}"
            + (s.StatusUpdate.Status.Message is { } sm && sm.GetText().Length > 0 ? ": " + o.One(sm.GetText()) : ""),
        ArtifactUpdateEvent a => $"artifact {o.One(a.ArtifactUpdate.Artifact.ArtifactId, 100)}"
            + (a.ArtifactUpdate.Append ? " (append)" : "") + (a.ArtifactUpdate.LastChunk ? " (last)" : "")
            + ": " + string.Join(" ", a.ArtifactUpdate.Artifact.Parts.Select(p => PartText(p, o))),
        _ => "event",
    };

    public static string TaskLine(AgentTask t, Output o) =>
        $"{o.One(t.Id, 100)}  {t.Status.State}  {o.One(t.ContextId, 100)}"
        + (t.Status.Timestamp is { } ts ? "  " + ts.ToString("u", CultureInfo.InvariantCulture) : "");
}
