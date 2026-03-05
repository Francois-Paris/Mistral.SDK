using System.Text.Json.Serialization;

namespace Mistral.SDK.DTOs
{
    /// <summary>
    /// Represents a discrete chunk of content within a chat message, which may be text, an image, or a document.
    /// </summary>
    /// <remarks>Use this class to encapsulate a single type of content for a chat message. The specific
    /// content is determined by the value of the Type property, and only the corresponding content property (Text,
    /// ImageUrl, or DocumentUrl) should be populated for each instance. 
    /// See Mistral cookbook: https://docs.mistral.ai/capabilities/vision</remarks>
    public sealed class ChatMessageContentChunk
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = default!; // "text" | "image_url" | "document_url" ...

        [JsonPropertyName("text")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Text { get; set; }

        [JsonPropertyName("image_url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ImageUrl { get; set; }

        [JsonPropertyName("document_url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DocumentUrl { get; set; }
    }
}