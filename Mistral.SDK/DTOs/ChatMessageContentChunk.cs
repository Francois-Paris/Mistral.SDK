using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Mistral.SDK.DTOs
{
    /// <summary>
    /// Represents a discrete chunk of content within a chat message, which may be text, an image, a document,
    /// or a reasoning trace (<c>thinking</c>) for native reasoning models.
    /// </summary>
    /// <remarks>Use this class to encapsulate a single type of content for a chat message. The specific
    /// content is determined by the value of the Type property, and only the corresponding content property (Text,
    /// ImageUrl, DocumentUrl, or Thinking) should be populated for each instance.
    /// See Mistral cookbook: https://docs.mistral.ai/capabilities/vision and reasoning docs:
    /// https://docs.mistral.ai/studio-api/conversations/reasoning</remarks>
    public sealed class ChatMessageContentChunk
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = default!; // "text" | "image_url" | "document_url" | "thinking" ...

        [JsonPropertyName("text")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Text { get; set; }

        [JsonPropertyName("image_url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ImageUrl { get; set; }

        [JsonPropertyName("document_url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DocumentUrl { get; set; }

        /// <summary>
        /// Reasoning trace produced by native reasoning models (e.g. mistral-medium-3-5). Shape attendue :
        /// <c>{"type":"thinking", "thinking":[{"type":"text","text":"..."}]}</c> — un sous-tableau de chunks
        /// (généralement de type <c>"text"</c>) pour permettre des blocs de raisonnement multiples ou rédigés.
        /// La doc Mistral précise qu'il faut REPLAYER ces blocs dans l'historique multi-turn, sinon la qualité chute.
        /// </summary>
        [JsonPropertyName("thinking")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ChatMessageContentChunk>? Thinking { get; set; }
    }
}