using System.Globalization;
using System.Text;
using A2A.Grpc;
using A2A.Server.AspNetCore;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;

namespace A2A.Server.Grpc;

/// <summary>
/// Writes errors raised before the gRPC service runs (the rate limit rejection) as a gRPC trailers-only response:
/// <c>grpc-status</c>, <c>grpc-message</c> and the <c>google.rpc.Status</c> (with ErrorInfo and RetryInfo) in
/// <c>grpc-status-details-bin</c>, the same status the service itself would send.
/// </summary>
internal sealed class GrpcRejectionWriter : IA2ARejectionWriter
{
    public Task? TryWriteAsync(HttpContext http, A2AException error)
    {
        if (http.GetEndpoint()?.DisplayName?.StartsWith(A2AGrpcExtensions.DisplayNamePrefix, StringComparison.Ordinal) != true) return null;
        if (http.Response.HasStarted) return Task.CompletedTask;
        var status = GrpcErrors.ToStatus(error);
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "application/grpc";
        http.Response.Headers["grpc-status"] = status.Code.ToString(CultureInfo.InvariantCulture);
        http.Response.Headers["grpc-message"] = PercentEncode(status.Message);
        http.Response.Headers["grpc-status-details-bin"] = Convert.ToBase64String(status.ToByteArray()).TrimEnd('=');
        return Task.CompletedTask;
    }

    /// <summary>gRPC message encoding: printable ASCII except '%' as is, everything else as %XX of its UTF-8 bytes.</summary>
    private static string PercentEncode(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            if (b is >= 0x20 and <= 0x7E && b != (byte)'%') sb.Append((char)b);
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
