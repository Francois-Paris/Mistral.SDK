using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Mistral.SDK.DTOs
{
    public class ToolCall
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; }

        // Streaming deltas (OpenAI-style backends of La Plateforme, e.g. GLM 5.2 via vLLM) carry an
        // "index" pairing the argument fragments of one call across chunks. Absent on Mistral's own
        // single-delta tool calls and on non-streaming responses; never serialized back to the API.
        [JsonPropertyName("index")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Index { get; set; }

        [JsonPropertyName("function")]
        public ToolCallParameter Function { get; set; }
    }

    public class ToolCallParameter
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("arguments")]
        public JsonNode Arguments { get; set; }
    }
}
