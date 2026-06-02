using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Mistral.SDK.DTOs.Conversations
{
    /// <summary>
    /// Request body for <c>POST /v1/conversations</c> (the Conversations API).
    /// Used by the IChatClient adapter when a server-side connector (e.g. web search) is requested,
    /// which lives on this endpoint rather than on <c>/v1/chat/completions</c>.
    /// </summary>
    public class ConversationRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; }

        /// <summary>
        /// Conversation inputs. May be a plain string for a single user turn, or — as used here for
        /// stateless replay (<see cref="Store"/> = false) — an array of role/content entries.
        /// </summary>
        [JsonPropertyName("inputs")]
        public List<ConversationInputEntry> Inputs { get; set; } = new List<ConversationInputEntry>();

        /// <summary>System prompt / agent instructions.</summary>
        [JsonPropertyName("instructions")]
        public string Instructions { get; set; }

        /// <summary>Built-in connectors and/or function tools. Here: <c>[{"type":"web_search"}]</c>.</summary>
        [JsonPropertyName("tools")]
        public List<ConversationTool> Tools { get; set; }

        [JsonPropertyName("completion_args")]
        public ConversationCompletionArgs CompletionArgs { get; set; }

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }

        /// <summary>
        /// When false, the conversation is NOT persisted server-side. We manage history ourselves and
        /// replay it via <see cref="Inputs"/>, so this is always false from the IChatClient adapter.
        /// </summary>
        [JsonPropertyName("store")]
        public bool Store { get; set; }
    }

    /// <summary>
    /// A single input entry. The Conversations API accepts a polymorphic union; we use one flat type and
    /// rely on null-omitting serialization so each entry emits only the fields its shape needs:
    /// <list type="bullet">
    /// <item>message: <c>{ "role", "content" }</c> (no <c>type</c>)</item>
    /// <item>function call (replayed): <c>{ "type":"function.call", "tool_call_id", "name", "arguments" }</c></item>
    /// <item>function result: <c>{ "type":"function.result", "tool_call_id", "result" }</c></item>
    /// </list>
    /// </summary>
    public class ConversationInputEntry
    {
        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("role")]
        public string Role { get; set; }

        [JsonPropertyName("content")]
        public string Content { get; set; }

        [JsonPropertyName("tool_call_id")]
        public string ToolCallId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>function.call arguments — a JSON-encoded string, as returned by the model.</summary>
        [JsonPropertyName("arguments")]
        public string Arguments { get; set; }

        /// <summary>function.result payload (the tool's output as a string).</summary>
        [JsonPropertyName("result")]
        public string Result { get; set; }
    }

    /// <summary>
    /// A tool entry. For a built-in connector only <see cref="Type"/> is set (e.g. <c>web_search</c>,
    /// <c>web_search_premium</c>). For a custom function tool, <see cref="Type"/> is <c>function</c> and
    /// <see cref="Function"/> carries its definition.
    /// </summary>
    public class ConversationTool
    {
        [JsonPropertyName("type")]
        public string Type { get; set; }

        /// <summary>Connector configuration (e.g. web_search domain allow/deny lists). Null = unconfigured.</summary>
        [JsonPropertyName("tool_configuration")]
        public ConversationToolConfiguration ToolConfiguration { get; set; }

        [JsonPropertyName("function")]
        public ConversationFunctionTool Function { get; set; }
    }

    /// <summary>
    /// Built-in connector configuration. For web_search, <see cref="Include"/>/<see cref="Exclude"/> restrict the
    /// search to / away from the given domains. (Mistral's connector has no geolocation option.)
    /// </summary>
    public class ConversationToolConfiguration
    {
        [JsonPropertyName("include")]
        public IList<string> Include { get; set; }

        [JsonPropertyName("exclude")]
        public IList<string> Exclude { get; set; }
    }

    public class ConversationFunctionTool
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("parameters")]
        public JsonNode Parameters { get; set; }
    }

    public class ConversationCompletionArgs
    {
        [JsonPropertyName("temperature")]
        public decimal? Temperature { get; set; }

        [JsonPropertyName("top_p")]
        public decimal? TopP { get; set; }

        [JsonPropertyName("max_tokens")]
        public int? MaxTokens { get; set; }
    }
}
