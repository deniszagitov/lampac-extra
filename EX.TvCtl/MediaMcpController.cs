using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace EX.TvCtl;

// Optional companion to tvctl: one paired screen, stateless Streamable HTTP clients.
[AllowAnonymous]
public class MediaMcpController : Controller
{
    const string Protocol = "2025-06-18";
    static readonly object gate = new();
    static readonly Dictionary<string, Operation> ops = new();
    static string screen, selection;
    static DateTime lastSeen;

    sealed class Operation
    {
        public readonly string Id = Guid.NewGuid().ToString("N");
        public readonly DateTime Created = DateTime.UtcNow;
        public string Owner, Session, Action;
        public JObject Args, Result;
        public bool Delivered;

        public JObject Snapshot()
        {
            var result = Result == null ? State("pending") : (JObject)Result.DeepClone();
            result["operation_id"] = Id;
            if ((string)result["state"] == "shown") result["selection_id"] = Id;
            return result;
        }
    }

    static JObject State(string name) => new JObject { ["state"] = name };
    static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    string Caller(string setting)
    {
        string allowed = Environment.GetEnvironmentVariable(setting);
        string header = Request.Headers["Authorization"].ToString();
        if (string.IsNullOrWhiteSpace(allowed) || !header.StartsWith("Bearer ", StringComparison.Ordinal)) return null;
        var supplied = Encoding.UTF8.GetBytes(header.Substring(7));
        foreach (var token in allowed.Split(',').Select(t => t.Trim()).Where(t => t.Length >= 32))
            if (CryptographicOperations.FixedTimeEquals(supplied, Encoding.UTF8.GetBytes(token))) return Digest(token);
        return null;
    }

    async Task<JObject> ReadObject()
    {
        using var reader = new StreamReader(Request.Body);
        try { return JObject.Parse(await reader.ReadToEndAsync()); }
        catch (JsonException) { return null; }
    }

    ActionResult Body(object value) => Content(JsonConvert.SerializeObject(value), "application/json");
    ActionResult Reply(JToken id, object result) => Body(new { jsonrpc = "2.0", id, result });
    ActionResult Error(JToken id, int code, string message) => Body(new { jsonrpc = "2.0", id, error = new { code, message } });

    static void Expire()
    {
        var now = DateTime.UtcNow;
        foreach (var op in ops.Values.ToArray())
        {
            if (now - op.Created > TimeSpan.FromMinutes(30)) ops.Remove(op.Id);
            else if (op.Result == null && now - op.Created > TimeSpan.FromSeconds(60)) op.Result = State("timeout");
        }
    }

    static Operation Add(string action, string owner, JObject args)
    {
        var op = new Operation { Action = action, Owner = owner, Session = screen, Args = args };
        ops.Add(op.Id, op);
        return op;
    }

    static JObject Call(string name, string owner, JObject args)
    {
        lock (gate)
        {
            Expire();
            if (name == "lampa_status")
                return ops.TryGetValue((string)args["operation_id"] ?? "", out var op) && op.Owner == owner
                    ? op.Snapshot() : State("unknown_operation");

            if (name == "lampa_play")
            {
                string id = (string)args["selection_id"];
                if (id == null || !ops.TryGetValue(id, out var shown) || shown.Owner != owner) return State("stale_selection");
                var repeat = ops.Values.FirstOrDefault(o => o.Action == "play" && (string)o.Args["selection_id"] == id);
                if (repeat != null) return repeat.Snapshot();
                if (id != selection || shown.Session != screen || (string)shown.Result?["state"] != "shown")
                    return State("stale_selection");
            }
            else
            {
                string query = (string)args["query"];
                if (string.IsNullOrWhiteSpace(query) || query.Length > 200 ||
                    !new[] { "movie", "series" }.Contains((string)args["kind"]) ||
                    (args["season"] == null) != (args["episode"] == null) ||
                    (args["season"] != null && ((string)args["kind"] != "series" ||
                    args["season"].Type != JTokenType.Integer || args["episode"].Type != JTokenType.Integer ||
                    (int)args["season"] < 0 || (int)args["episode"] < 1))) return State("invalid_arguments");
            }
            if (DateTime.UtcNow - lastSeen > TimeSpan.FromSeconds(12)) return State("screen_offline");
            if (ops.Count >= 500 || ops.Values.Any(o => o.Result == null)) return State("busy");
            var job = Add(name == "lampa_play" ? "play" : "present", owner, args);
            if (job.Action == "present") selection = job.Id;
            return job.Snapshot();
        }
    }

    static object Tool(string name, string description, object properties, params string[] required) =>
        new { name, description, inputSchema = new { type = "object", properties, required, additionalProperties = false } };

    static object[] Tools()
    {
        var text = new { type = "string" };
        return new[] {
            Tool("lampa_present", "Find a title and show its card on the paired screen. This does not start playback.",
                new { query = text, kind = new { type = "string", @enum = new[] { "movie", "series" } },
                    year = new { type = "integer" }, season = new { type = "integer", minimum = 0 },
                    episode = new { type = "integer", minimum = 1 }, provider = text }, "query", "kind"),
            Tool("lampa_play", "Play a shown selection after the viewer asks to start. Repeated calls return the same operation.",
                new { selection_id = text }, "selection_id"),
            Tool("lampa_status", "Read the result of a pending operation.", new { operation_id = text }, "operation_id")
        };
    }

    [HttpPost, Route("/tvctl/mcp"), RequestSizeLimit(16384)]
    public async Task<ActionResult> Mcp()
    {
        Response.Headers["Cache-Control"] = "no-store";
        if (Request.Headers.ContainsKey("Origin")) return StatusCode(403);
        var owner = Caller("TVCTL_MCP_TOKENS");
        if (owner == null) return Unauthorized();
        var rpc = await ReadObject();
        if (rpc == null) return Error(null, -32700, "Parse error");
        var id = rpc["id"];
        if ((string)rpc["jsonrpc"] != "2.0" || rpc["method"]?.Type != JTokenType.String)
            return Error(id, -32600, "Invalid request");
        if (id == null) return Accepted();
        switch ((string)rpc["method"])
        {
            case "initialize": return Reply(id, new { protocolVersion = Protocol, capabilities = new { tools = new { } },
                serverInfo = new { name = "tvctl", version = "1.0" } });
            case "ping": return Reply(id, new { });
            case "tools/list": return Reply(id, new { tools = Tools() });
            case "tools/call":
                var p = rpc["params"] as JObject;
                var name = (string)p?["name"];
                if (!new[] { "lampa_present", "lampa_play", "lampa_status" }.Contains(name) || !(p?["arguments"] is JObject args))
                    return Error(id, -32602, "Unknown tool or invalid arguments");
                JObject result;
                try { result = Call(name, owner, args); }
                catch (Exception e) when (e is FormatException || e is OverflowException || e is ArgumentException || e is InvalidCastException)
                { return Error(id, -32602, "Invalid arguments"); }
                return Reply(id, new { structuredContent = result, content = new[] { new { type = "text", text = result.ToString(Formatting.None) } } });
            default: return Error(id, -32601, "Method not found");
        }
    }

    [HttpPost, Route("/tvctl/mcp/device"), RequestSizeLimit(16384)]
    public async Task<ActionResult> Device()
    {
        Response.Headers["Cache-Control"] = "no-store";
        if (Caller("TVCTL_MCP_DEVICE_TOKEN") == null) return Unauthorized();
        var message = await ReadObject();
        var session = (string)message?["session"];
        if (!Guid.TryParseExact(session, "N", out _)) return BadRequest();
        lock (gate)
        {
            Expire();
            if (screen != session) selection = null;
            screen = session;
            lastSeen = DateTime.UtcNow;
            if (message["result"] is JObject r && ops.TryGetValue((string)message["id"] ?? "", out var job)
                && job.Session == session && job.Delivered && job.Result == null) job.Result = r;
            var next = ops.Values.FirstOrDefault(o => o.Session == session && !o.Delivered && o.Result == null);
            if (next == null) return NoContent();
            next.Delivered = true;
            return Body(new { id = next.Id, action = next.Action, args = next.Args });
        }
    }
}
