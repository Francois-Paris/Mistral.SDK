using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Mistral.SDK.DTOs.Conversations
{
    /// <summary>
    /// Non-streaming response of <c>POST /v1/conversations</c>.
    /// </summary>
    public class ConversationResponse
    {
        [JsonPropertyName("conversation_id")]
        public string ConversationId { get; set; }

        [JsonPropertyName("object")]
        public string Object { get; set; }

        /// <summary>
        /// Ordered list of produced entries. Relevant types for the web-search path:
        /// <c>message.output</c> (the assistant answer with text + citations) and
        /// <c>tool.execution</c> (a web_search invocation). <c>function.call</c> entries are produced when
        /// custom function tools are supplied — handled in a later pass.
        /// </summary>
        [JsonPropertyName("outputs")]
        public List<ConversationOutputEntry> Outputs { get; set; }

        [JsonPropertyName("usage")]
        public Usage Usage { get; set; }
    }

    public class ConversationOutputEntry
    {
        /// <summary>Discriminator: <c>message.output</c>, <c>tool.execution</c>, <c>function.call</c>, …</summary>
        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("role")]
        public string Role { get; set; }

        /// <summary>Present on <c>message.output</c>. A plain string or an array of typed chunks.</summary>
        [JsonPropertyName("content")]
        public ConversationContent Content { get; set; }

        /// <summary>Present on <c>tool.execution</c> / <c>function.call</c> (e.g. <c>web_search</c>).</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>Present on <c>function.call</c>: correlates the call with its result.</summary>
        [JsonPropertyName("tool_call_id")]
        public string ToolCallId { get; set; }

        /// <summary>Present on <c>function.call</c>: a JSON-encoded string of the call arguments.</summary>
        [JsonPropertyName("arguments")]
        public string Arguments { get; set; }
    }

    /// <summary>
    /// A message-output content. The wire shape is polymorphic — either a bare string, or an array of
    /// chunks (<c>text</c> and <c>tool_reference</c>) — so a custom converter normalizes both into <see cref="Chunks"/>.
    /// </summary>
    [JsonConverter(typeof(ConversationContentConverter))]
    public class ConversationContent
    {
        public List<ConversationContentChunk> Chunks { get; } = new List<ConversationContentChunk>();

        /// <summary>Concatenated text of all <c>text</c> chunks.</summary>
        public string AsText() =>
            string.Concat(Chunks.Where(c => c.Type == "text" && c.Text != null).Select(c => c.Text));

        /// <summary>The <c>tool_reference</c> chunks (web-search citations).</summary>
        public IEnumerable<ConversationContentChunk> References =>
            Chunks.Where(c => c.Type == "tool_reference");
    }

    public class ConversationContentChunk
    {
        /// <summary><c>text</c> or <c>tool_reference</c>.</summary>
        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("text")]
        public string Text { get; set; }

        // tool_reference fields (web-search citation):
        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; }

        [JsonPropertyName("source")]
        public string Source { get; set; }
    }

    /// <summary>
    /// A single Server-Sent Event from <c>POST /v1/conversations#stream</c>. The SSE frame carries an
    /// <c>event:</c> line (the type) and a <c>data:</c> line whose JSON also has a <c>type</c> discriminator;
    /// we dispatch on that. This flat type covers every event we consume — fields absent on a given event are
    /// simply null. Event types (per the OpenAPI spec): <c>conversation.response.started</c>,
    /// <c>message.output.delta</c>, <c>function.call.delta</c>, <c>tool.execution.started|delta|done</c>,
    /// <c>conversation.response.done</c>, <c>conversation.response.error</c>.
    /// </summary>
    public class ConversationStreamEvent
    {
        [JsonPropertyName("type")]
        public string Type { get; set; }

        // conversation.response.started
        [JsonPropertyName("conversation_id")]
        public string ConversationId { get; set; }

        [JsonPropertyName("id")]
        public string Id { get; set; }

        /// <summary>Identifies which output entry a delta belongs to (function-call args stream across several deltas).</summary>
        [JsonPropertyName("output_index")]
        public int? OutputIndex { get; set; }

        // tool.execution.* / function.call.delta
        [JsonPropertyName("name")]
        public string Name { get; set; }

        // function.call.delta
        [JsonPropertyName("tool_call_id")]
        public string ToolCallId { get; set; }

        [JsonPropertyName("arguments")]
        public string Arguments { get; set; }

        /// <summary>message.output.delta payload — a JSON string (text delta) or a chunk object (text / tool_reference).</summary>
        [JsonPropertyName("content")]
        public JsonNode Content { get; set; }

        // conversation.response.done
        [JsonPropertyName("usage")]
        public Usage Usage { get; set; }

        // conversation.response.error
        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("code")]
        public int? Code { get; set; }
    }

    /// <summary>
    /// Reads a message-output <c>content</c> that is either a JSON string or a JSON array of chunk objects,
    /// normalizing both into a <see cref="ConversationContent"/>. Write is not supported (response-only).
    /// </summary>
    public class ConversationContentConverter : JsonConverter<ConversationContent>
    {
        public override ConversationContent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var result = new ConversationContent();

            if (reader.TokenType == JsonTokenType.String)
            {
                result.Chunks.Add(new ConversationContentChunk { Type = "text", Text = reader.GetString() });
            }
            else if (reader.TokenType == JsonTokenType.StartArray)
            {
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray)
                        break;
                    var chunk = JsonSerializer.Deserialize<ConversationContentChunk>(ref reader, options);
                    if (chunk != null)
                        result.Chunks.Add(chunk);
                }
            }
            else if (reader.TokenType != JsonTokenType.Null)
            {
                reader.Skip();
            }

            return result;
        }

        public override void Write(Utf8JsonWriter writer, ConversationContent value, JsonSerializerOptions options) =>
            throw new NotSupportedException($"{nameof(ConversationContent)} is response-only.");
    }
}
