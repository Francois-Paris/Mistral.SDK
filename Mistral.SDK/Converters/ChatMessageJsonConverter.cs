using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mistral.SDK.DTOs;

namespace Mistral.SDK.Converters
{
    /// <summary>
    /// JSON converter for <see cref="DTOs.ChatMessage"/> that supports both the legacy and multimodal
    /// representations of the <c>content</c> field used by the Mistral Chat Completions API.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Historically, the SDK modeled <c>content</c> as a simple string. Vision / multimodal requests require
    /// <c>content</c> to be an array of typed chunks (e.g. <c>{"type":"text"}</c>, <c>{"type":"image_url"}</c>).
    /// This converter keeps backward compatibility by serializing <see cref="DTOs.ChatMessage.Content"/> as a string
    /// when no chunks are present, and serializing <see cref="DTOs.ChatMessage.ContentChunks"/> as a JSON array otherwise.
    /// </para>
    /// <para>
    /// On deserialization, it accepts both shapes:
    /// <list type="bullet">
    ///   <item><description><c>"content": "…"</c></description></item>
    ///   <item><description><c>"content": [ { … }, { … } ]</c></description></item>
    /// </list>
    /// </para>
    /// </remarks>
    public sealed class ChatMessageJsonConverter : JsonConverter<ChatMessage>
    {
        public override ChatMessage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;

            var msg = new ChatMessage();

            if (root.TryGetProperty("role", out var roleEl) && roleEl.ValueKind == JsonValueKind.String)
            {
                msg.Role = roleEl.GetString() switch
                {
                    "system" => ChatMessage.RoleEnum.System,
                    "user" => ChatMessage.RoleEnum.User,
                    "assistant" => ChatMessage.RoleEnum.Assistant,
                    "tool" => ChatMessage.RoleEnum.Tool,
                    _ => msg.Role
                };
            }

            if (root.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                msg.Name = nameEl.GetString();

            if (root.TryGetProperty("tool_call_id", out var tcidEl) && tcidEl.ValueKind == JsonValueKind.String)
                msg.ToolCallId = tcidEl.GetString();

            if (root.TryGetProperty("tool_calls", out var toolCallsEl) && toolCallsEl.ValueKind != JsonValueKind.Null)
                msg.ToolCalls = JsonSerializer.Deserialize<List<ToolCall>>(toolCallsEl.GetRawText(), options);

            if (root.TryGetProperty("content", out var contentEl))
            {
                if (contentEl.ValueKind == JsonValueKind.String)
                {
                    msg.Content = contentEl.GetString();
                }
                else if (contentEl.ValueKind == JsonValueKind.Array)
                {
                    msg.ContentChunks = JsonSerializer.Deserialize<List<ChatMessageContentChunk>>(contentEl.GetRawText(), options);
                    msg.Content ??= string.Empty;
                }
            }

            return msg;
        }

        public override void Write(Utf8JsonWriter writer, ChatMessage value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();

            if (value.Role is not null)
            {
                writer.WriteString("role", value.Role switch
                {
                    ChatMessage.RoleEnum.System => "system",
                    ChatMessage.RoleEnum.User => "user",
                    ChatMessage.RoleEnum.Assistant => "assistant",
                    ChatMessage.RoleEnum.Tool => "tool",
                    _ => "user"
                });
            }

            if (!string.IsNullOrEmpty(value.Name))
                writer.WriteString("name", value.Name);

            if (!string.IsNullOrEmpty(value.ToolCallId))
                writer.WriteString("tool_call_id", value.ToolCallId);

            if (value.ToolCalls is { Count: > 0 })
            {
                writer.WritePropertyName("tool_calls");
                JsonSerializer.Serialize(writer, value.ToolCalls, options);
            }

            // Key point: if there are content chunks, we serialize them as an array; otherwise, we serialize the content as a string.
            writer.WritePropertyName("content");
            if (value.ContentChunks is { Count: > 0 })
            {
                JsonSerializer.Serialize(writer, value.ContentChunks, options);
            }
            else
            {
                writer.WriteStringValue(value.Content ?? string.Empty);
            }

            writer.WriteEndObject();
        }
    }
}