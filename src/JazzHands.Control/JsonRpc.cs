using System.Text.Json;
using System.Text.Json.Nodes;
using JazzHands.Core.Serialization;

namespace JazzHands.Control;

/// <summary>JSON-RPC 2.0 as the control server speaks it: the error codes and the message shapes.</summary>
/// <remarks>
/// One JSON value per line. Compact JSON never contains a raw line break (a string holds <c>\n</c>
/// escaped), so a line is a message. A batch is an array of requests on one line and its answer
/// an array of responses. A result larger than the chunk size is sent as <c>rpc.chunk</c>
/// notifications carrying slices of the response's text, which the client joins.
/// </remarks>
public static class JsonRpc
{
    /// <summary>The line could not be read as JSON.</summary>
    public const int ParseError = -32700;

    /// <summary>The JSON is not a request.</summary>
    public const int InvalidRequest = -32600;

    /// <summary>There is no such method.</summary>
    public const int MethodNotFound = -32601;

    /// <summary>The params do not make the command.</summary>
    public const int InvalidParams = -32602;

    /// <summary>Something broke on the server.</summary>
    public const int InternalError = -32603;

    /// <summary>The command was refused; <c>data.code</c> says why, <c>data.path</c> where.</summary>
    public const int CommandError = -32001;

    /// <summary>A media file could not be read or written.</summary>
    public const int MediaError = -32002;

    /// <summary>Another client holds the session.</summary>
    public const int Locked = -32003;

    /// <summary>Too many commands too fast; <c>data.retryAfterMs</c> says when to try again.</summary>
    public const int RateLimited = -32004;

    /// <summary>The connection has not said the token this server asks for.</summary>
    public const int Unauthorized = -32005;

    /// <summary>The notification a large response is sent as, in slices.</summary>
    public const string ChunkMethod = "rpc.chunk";

    /// <summary>How the server and client write JSON: compact, camelCase, times as flicks.</summary>
    public static JsonSerializerOptions Options { get; } = new(JazzJson.Options) { WriteIndented = false };

    /// <summary>A request.</summary>
    public static JsonObject Request(long id, string method, JsonNode? parameters = null) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = method,
        ["params"] = parameters,
    };

    /// <summary>A notification: a request with no id, which is not answered.</summary>
    public static JsonObject Notification(string method, JsonNode? parameters) => new()
    {
        ["jsonrpc"] = "2.0",
        ["method"] = method,
        ["params"] = parameters,
    };

    /// <summary>A successful response.</summary>
    public static JsonObject Result(JsonNode? id, JsonNode? result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result,
    };

    /// <summary>An error response.</summary>
    public static JsonObject Error(JsonNode? id, int code, string message, JsonNode? data = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (data is not null)
        {
            error["data"] = data;
        }

        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = error,
        };
    }

    /// <summary>A value as JSON, the way the project file writes it.</summary>
    public static JsonNode? ToNode(object? value) => value is null ? null : JsonSerializer.SerializeToNode(value, value.GetType(), Options);

    /// <summary>A message as one line of text.</summary>
    public static string Line(JsonNode message) => message.ToJsonString(Options);
}

/// <summary>An error a JSON-RPC server answered with.</summary>
public sealed class JsonRpcException : Exception
{
    /// <summary>Creates one.</summary>
    public JsonRpcException(int code, string message, JsonNode? data = null)
        : base(message)
    {
        Code = code;
        Detail = data;
    }

    /// <summary>Creates one with no code, for the analyzers.</summary>
    public JsonRpcException()
        : this(JsonRpc.InternalError, "The server failed.")
    {
    }

    /// <summary>Creates one with no code, for the analyzers.</summary>
    public JsonRpcException(string message)
        : this(JsonRpc.InternalError, message)
    {
    }

    /// <summary>Creates one with no code, for the analyzers.</summary>
    public JsonRpcException(string message, Exception inner)
        : base(message, inner)
    {
        Code = JsonRpc.InternalError;
    }

    /// <summary>The JSON-RPC error code.</summary>
    public int Code { get; }

    /// <summary>What the server added: for a command error, <c>code</c> and <c>path</c>.</summary>
    public JsonNode? Detail { get; }

    /// <summary>The command error's kebab-case code, or empty for other errors.</summary>
    public string CommandCode => Detail?["code"]?.GetValue<string>() ?? string.Empty;
}
