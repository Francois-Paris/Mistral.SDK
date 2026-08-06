using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.AI;
using Mistral.SDK.DTOs;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mistral.SDK.Completions
{
    public partial class CompletionsEndpoint : IChatClient
    {
        private static readonly Regex s_validFunctionCallIdRegex = new("^[a-zA-Z0-9]{9}$");

        /// <summary>
        /// Clé de <c>ChatOptions.AdditionalProperties</c> relayant l'identifiant de prompt caching
        /// (M.E.AI n'a pas de propriété dédiée). Valeur attendue : un identifiant applicatif stable
        /// et non sensible (id de conversation / session) — voir
        /// <see cref="ChatCompletionRequest.PromptCacheKey"/>. Honorée par les deux chemins
        /// (/v1/chat/completions et /v1/conversations).
        /// </summary>
        public const string PromptCacheKeyOption = "prompt_cache_key";

        /// <summary>Lit l'identifiant de prompt caching posé par l'appelant dans les options, ou null.</summary>
        internal static string GetPromptCacheKey(ChatOptions options) =>
            options?.AdditionalProperties?.TryGetValue(PromptCacheKeyOption, out object value) == true
                && value is string key && !string.IsNullOrWhiteSpace(key)
                ? key : null;

        async Task<ChatResponse> IChatClient.GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions options, CancellationToken cancellationToken)
        {
            // Web search lives on /v1/conversations, not /v1/chat/completions — route there when requested.
            if (WantsWebSearch(options))
                return await GetWebSearchResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

            var response = await GetCompletionAsync(CreateRequest(messages, options), cancellationToken).ConfigureAwait(false);

            Microsoft.Extensions.AI.ChatMessage message = new(ChatRole.Assistant, ProcessResponseContent(response))
            {
                MessageId = response.Id ?? Guid.NewGuid().ToString("N"),
            };

            var completion = new ChatResponse(message)
            {
                ModelId = response.Model,
                ResponseId = response.Id,
                RawRepresentation = response,
            };

            if (response.Usage is { } usage)
            {
                completion.Usage = ToUsageDetails(usage);
            }

            return completion;
        }

        async IAsyncEnumerable<ChatResponseUpdate> IChatClient.GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // Web search lives on /v1/conversations, not /v1/chat/completions — route there when requested.
            if (WantsWebSearch(options))
            {
                await foreach (var update in GetWebSearchStreamingAsync(messages, options, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
                    yield return update;
                yield break;
            }

            await foreach (var response in StreamCompletionAsync(CreateRequest(messages, options), cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                foreach (var choice in response.Choices)
                {
                    ChatRole role = choice.Delta?.Role switch
                    {
                        DTOs.ChatMessage.RoleEnum.System => ChatRole.System,
                        DTOs.ChatMessage.RoleEnum.Assistant => ChatRole.Assistant, // formerly (an error or a typo?) ChatRole.User
                        _ => ChatRole.User,
                    };

                    ChatFinishReason? finishReason = choice.FinishReason switch
                    {
                        Choice.FinishReasonEnum.Length => ChatFinishReason.Length,
                        Choice.FinishReasonEnum.ModelLength => ChatFinishReason.Length,
                        _ => ChatFinishReason.Stop
                    };

                    // Delta peut être :
                    //  - une string (path classique non-reasoning)  -> on émet 1 TextContent
                    //  - un tableau de chunks (path reasoning, e.g. mistral-medium-3-5) -> on émet
                    //    TextReasoningContent pour les chunks "thinking" et TextContent pour les "text"
                    var deltaContents = new List<AIContent>();
                    if (choice.Delta?.ContentChunks is { Count: > 0 } deltaChunks)
                    {
                        foreach (var chunk in deltaChunks)
                            AppendChunkAsAIContent(deltaContents, chunk);
                    }
                    else if (!string.IsNullOrEmpty(choice.Delta?.Content))
                    {
                        deltaContents.Add(new Microsoft.Extensions.AI.TextContent(choice.Delta.Content));
                    }

                    var update = new ChatResponseUpdate(role, deltaContents)
                    {
                        MessageId = response.Id,
                        ModelId = response.Model,
                        RawRepresentation = response,
                        ResponseId = response.Id,
                        FinishReason = finishReason,
                    };

                    if (choice.Delta?.ToolCalls is { Count: > 0 })
                    {
                        foreach (var toolCall in choice.Delta.ToolCalls)
                        {
                            Dictionary<string, object> arguments = null;
                            if (toolCall.Function.Arguments is not null)
                            {
                                arguments = JsonSerializer.Deserialize<Dictionary<string, object>>(toolCall.Function.Arguments.ToString());
                            }

                            update.Contents.Add(new FunctionCallContent(
                                toolCall.Id,
                                toolCall.Function.Name,
                                arguments));
                        }
                    }

                    yield return update;
                }

                if (response.Usage is { } usage)
                {
                    yield return new ChatResponseUpdate()
                    {
                        Contents = new List<AIContent>()
                        {
                            new UsageContent(ToUsageDetails(usage))
                        },
                        MessageId = response.Id,
                        ModelId = response.Model,
                        ResponseId = response.Id,
                    };
                }
            }
        }

        private ChatCompletionRequest CreateRequest(IEnumerable<Microsoft.Extensions.AI.ChatMessage> chatMessages, ChatOptions options)
        {
            ChatCompletionRequest request = options?.RawRepresentationFactory?.Invoke(this) as ChatCompletionRequest ?? new();

            request.Messages ??= [];

            if (options?.Instructions is { } instructions)
            {
                request.Messages.Add(new DTOs.ChatMessage(DTOs.ChatMessage.RoleEnum.System, instructions));
            }

            request.Messages.AddRange(chatMessages.SelectMany(m =>
            {
                return ToChatMessageDTO(m);

                static IEnumerable<DTOs.ChatMessage> ToChatMessageDTO(Microsoft.Extensions.AI.ChatMessage m)
                {
                    DTOs.ChatMessage.RoleEnum role =
                        m.Role == ChatRole.System ? DTOs.ChatMessage.RoleEnum.System :
                        m.Role == ChatRole.Assistant ? DTOs.ChatMessage.RoleEnum.Assistant :
                        m.Role == ChatRole.Tool ? DTOs.ChatMessage.RoleEnum.Tool :
                        DTOs.ChatMessage.RoleEnum.User;

                    // We collect all multimodal chunks for this message
                    List<DTOs.ChatMessageContentChunk>? chunks = null;

                    foreach (AIContent content in m.Contents)
                    {
                        switch (content)
                        {
                            case Microsoft.Extensions.AI.TextContent tc:
                                (chunks ??= []).Add(new DTOs.ChatMessageContentChunk
                                {
                                    Type = "text",
                                    Text = tc.Text
                                });
                                break;

                            case Microsoft.Extensions.AI.TextReasoningContent trc when !string.IsNullOrEmpty(trc.Text):
                                // Replay multi-turn : la doc Mistral précise que stripper le bloc thinking dégrade
                                // significativement la qualité, il faut le renvoyer dans l'historique. On le sérialise
                                // dans la shape canonique (sous-tableau avec un chunk text imbriqué).
                                (chunks ??= []).Add(new DTOs.ChatMessageContentChunk
                                {
                                    Type = "thinking",
                                    Thinking = new List<DTOs.ChatMessageContentChunk>
                                    {
                                        new DTOs.ChatMessageContentChunk { Type = "text", Text = trc.Text }
                                    }
                                });
                                break;

                            case Microsoft.Extensions.AI.DataContent dc:
                                // DataContent (Uri) -> chunk image_url or document_url, depending on MediaType
                                string mediaType = dc.MediaType?.ToString() ?? "application/octet-stream";
                                string dataUrl = string.IsNullOrEmpty(dc.Uri) ?
                                    // If dc.Uri, we can reconstruct "data:<mime>;base64,..." from dc.Data bytes here.
                                    $"data:{mediaType};base64,{Convert.ToBase64String(dc.Data.ToArray())}" :
                                    // But as we should receive a DataContent with a valid Uri & MediaType, this is just a fallback.
                                    dc.Uri.ToString();

                                bool isImage = mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

                                (chunks ??= []).Add(new DTOs.ChatMessageContentChunk
                                {
                                    Type = isImage ? "image_url" : "document_url",
                                    ImageUrl = isImage ? dataUrl : null,    
                                    DocumentUrl = !isImage ? dataUrl : null
                                });
                                break;

                            case Microsoft.Extensions.AI.FunctionCallContent fcc:
                                yield return new DTOs.ChatMessage()
                                {
                                    Role = DTOs.ChatMessage.RoleEnum.Assistant,
                                    ToolCalls = new List<ToolCall>()
                                    {
                                        new ToolCall()
                                        {
                                            Id = fcc.CallId,
                                            Function = new ToolCallParameter()
                                            {
                                                Arguments = JsonSerializer.SerializeToNode(fcc.Arguments),
                                                Name = fcc.Name,
                                            }
                                        }
                                    }
                                };
                                break;

                            case Microsoft.Extensions.AI.FunctionResultContent frc:
                                yield return new DTOs.ChatMessage(frc.CallId, frc.CallId, frc.Result?.ToString());
                                break;
                        }
                    }

                    // Send the main message (text or multimodal)
                    if (chunks is { Count: > 0 })
                    {
                        // optimization: if it is only a text chunk, we keep the string format (backward compatible)
                        if (chunks.Count == 1 && chunks[0].Type == "text")
                        {
                            yield return new DTOs.ChatMessage(role, chunks[0].Text ?? string.Empty);
                        }
                        else
                        {
                            yield return new DTOs.ChatMessage()
                            {
                                Role = role,
                                Content = string.Empty, // kept for compatibility
                                ContentChunks = chunks  // will be serialized as an array
                            };
                        }
                    }
                }
            }));

            // Mistral has a bunch of requirements about the structure of messages. Try to avoid the most common issues.
            {
                const string EmptyMessage = "\u200b";

                // There needs to be at least one user or assistant message.
                if (request.Messages.Count == 0 ||
                    request.Messages.All(m => m.Role is not (DTOs.ChatMessage.RoleEnum.User or DTOs.ChatMessage.RoleEnum.Assistant)))
                {
                    request.Messages.Add(new DTOs.ChatMessage(DTOs.ChatMessage.RoleEnum.User, EmptyMessage));
                }

                // System messages should be at the beginning and consolidated into one message.
                string systemMessage = string.Join("\n", request.Messages.Where(m => m.Role == DTOs.ChatMessage.RoleEnum.System).Select(m => m.Content));
                if (!string.IsNullOrWhiteSpace(systemMessage))
                {
                    request.Messages.RemoveAll(m => m.Role == DTOs.ChatMessage.RoleEnum.System);
                    request.Messages.Insert(0, new(DTOs.ChatMessage.RoleEnum.System, systemMessage));
                }

                // Function call IDs must be in a very specific format, nine [a-zA-Z0-9] characters.
                // If any aren't, remove them.
                for (int i = 0; i < request.Messages.Count; i++)
                {
                    var m = request.Messages[i];
                    if (m.ToolCallId is not null && !s_validFunctionCallIdRegex.IsMatch(m.ToolCallId))
                    {
                        request.Messages[i] = null;
                    }
                    else if (m.ToolCalls is { Count: > 0 })
                    {
                        m.ToolCalls.RemoveAll(static tc => !s_validFunctionCallIdRegex.IsMatch(tc.Id));
                        if (m.ToolCalls.Count == 0)
                        {
                            request.Messages[i] = null;
                        }
                    }
                }
                request.Messages.RemoveAll(static m => m is null);

                // User messages should be consolidated so that two user messages don't appear in a row.
                for (int i = 0; i < request.Messages.Count - 1; i++)
                {
                    if (request.Messages[i].Role != DTOs.ChatMessage.RoleEnum.User)
                    {
                        continue;
                    }

                    int next = i + 1;
                    while (next < request.Messages.Count && request.Messages[next].Role == DTOs.ChatMessage.RoleEnum.User) next++;

                    if (i + 1 < next)
                    {
                        // (former code) request.Messages[i].Content = string.Join("\n", request.Messages.Skip(i).Take(next - i).Select(m => m.Content));
                        // When merging consecutive user messages, if one of them has ContentChunks, we merge the chunks.
                        for (int j = i + 1; j < next; j++)
                            MergeUserMessages(request.Messages[i], request.Messages[j]);
                        request.Messages.RemoveRange(i + 1, next - (i + 1));
                    }
                }

                // Tool messages must be followed by an assistant message if there's anything next.
                for (int i = 0; i < request.Messages.Count; i++)
                {
                    if (request.Messages[i].Role == DTOs.ChatMessage.RoleEnum.Tool &&
                        i + 1 < request.Messages.Count &&
                        request.Messages[i + 1].Role != DTOs.ChatMessage.RoleEnum.Assistant)
                    {
                        request.Messages.Insert(i + 1, new DTOs.ChatMessage(DTOs.ChatMessage.RoleEnum.Assistant, EmptyMessage));
                    }
                }

                // The last message must not be Assistant.
                if (request.Messages[request.Messages.Count - 1].Role == DTOs.ChatMessage.RoleEnum.Assistant)
                {
                    request.Messages.Add(new DTOs.ChatMessage(DTOs.ChatMessage.RoleEnum.User, EmptyMessage));
                }
            }

            request.Model ??= options?.ModelId;
            request.Temperature ??= (decimal?)options?.Temperature;
            request.TopP ??= (decimal?)options?.TopP;
            request.MaxTokens ??= options?.MaxOutputTokens;
            request.ParallelToolCalls = options?.AllowMultipleToolCalls ?? request.ParallelToolCalls;
            request.RandomSeed ??= (int?)options?.Seed;
            request.ReasoningEffort ??= ToMistralReasoningEffort(options?.Reasoning?.Effort);
            request.PromptCacheKey ??= GetPromptCacheKey(options);

            if (options?.ResponseFormat is ChatResponseFormatJson)
            {
                request.ResponseFormat ??= new ResponseFormat() { Type = ResponseFormat.ResponseFormatEnum.JSON };
            }

            List<Common.Tool> tools = null;
            if (options?.Tools is not null)
            {
                tools = options
                    .Tools
                    .OfType<AIFunctionDeclaration>()
                    .Select(f => new Common.Tool(new Common.Function(
                        f.Name,
                        f.Description,
                        JsonSerializer.SerializeToNode(JsonSerializer.Deserialize<FunctionParameters>(f.JsonSchema)))))
                    .ToList();
            }

            if (tools is { Count: > 0 })
            {
                if (request.Tools is null)
                {
                    request.Tools = tools;
                }
                else
                {
                    tools.AddRange(request.Tools);
                    request.Tools = tools;
                }

                if (options.ToolMode is RequiredChatToolMode r)
                {
                    request.ToolChoice = ToolChoiceType.Any;
                }
                else if (options.ToolMode is AutoChatToolMode or null)
                {
                    request.ToolChoice = ToolChoiceType.Auto;
                }
                else if (options.ToolMode is NoneChatToolMode)
                {
                    request.ToolChoice = ToolChoiceType.none;
                }
            }

            return request;

            static void MergeUserMessages(DTOs.ChatMessage into, DTOs.ChatMessage other)
            {
                // If no multimodal: former logic
                if ((into.ContentChunks is null || into.ContentChunks.Count == 0) 
                 && (other.ContentChunks is null || other.ContentChunks.Count == 0))
                {
                    into.Content = string.Join("\n", into.Content, other.Content);
                    return;
                }

                into.ContentChunks ??= new List<DTOs.ChatMessageContentChunk>();

                // Converts existing text content into chunks if necessary
                if (!string.IsNullOrEmpty(into.Content) && into.ContentChunks.Count == 0)
                    into.ContentChunks.Add(new DTOs.ChatMessageContentChunk { Type = "text", Text = into.Content });

                // Adds chunks from other
                if (other.ContentChunks is { Count: > 0 })
                    into.ContentChunks.AddRange(other.ContentChunks);
                else if (!string.IsNullOrEmpty(other.Content))
                    into.ContentChunks.Add(new DTOs.ChatMessageContentChunk { Type = "text", Text = other.Content });

                into.Content = string.Empty;
            }
        }

        private static List<AIContent> ProcessResponseContent(ChatCompletionResponse response)
        {
            List<AIContent> contents = new();

            foreach (var content in response.Choices)
            {
                // Pour les modèles de reasoning (mistral-medium-3-5, mistral-small-* avec reasoning_effort),
                // le `content` du message est un tableau de chunks (text + thinking) au lieu d'une string.
                // Le ChatMessageJsonConverter peuple alors ContentChunks et laisse Content vide.
                if (content.Message.ContentChunks is { Count: > 0 } chunks)
                {
                    foreach (var chunk in chunks)
                        AppendChunkAsAIContent(contents, chunk);
                }
                else if (!string.IsNullOrEmpty(content.Message.Content))
                {
                    contents.Add(new Microsoft.Extensions.AI.TextContent(content.Message.Content));
                }

                if (content.Message.ToolCalls is not null)
                {
                    foreach (var toolCall in content.Message.ToolCalls)
                    {
                        Dictionary<string, object> arguments = null;
                        if (toolCall.Function.Arguments is not null)
                        {
                            arguments = JsonSerializer.Deserialize<Dictionary<string, object>>(toolCall.Function.Arguments.ToString());
                        }

                        contents.Add(new FunctionCallContent(
                            toolCall.Id,
                            toolCall.Function.Name,
                            arguments));
                    }
                }
            }

            return contents;
        }

        /// <summary>
        /// Convertit un chunk Mistral en <see cref="AIContent"/> MEAI. Géré : <c>"text"</c> -> <see cref="Microsoft.Extensions.AI.TextContent"/>,
        /// <c>"thinking"</c> -> <see cref="TextReasoningContent"/>. Les autres types (image/document) ne sont
        /// pas réémis ici (la lecture multimodale côté réponse n'est pas pertinente pour l'instant).
        /// </summary>
        internal static void AppendChunkAsAIContent(List<AIContent> contents, ChatMessageContentChunk chunk)
        {
            if (chunk is null) return;
            switch (chunk.Type)
            {
                case "text":
                    if (!string.IsNullOrEmpty(chunk.Text))
                        contents.Add(new Microsoft.Extensions.AI.TextContent(chunk.Text));
                    break;

                case "thinking":
                    // Shape canonique : {"type":"thinking", "thinking":[{"type":"text","text":"…"}]}
                    // Fallback défensif : {"type":"thinking", "text":"…"} (au cas où l'API évolue).
                    string reasoningText = chunk.Thinking is { Count: > 0 }
                        ? string.Concat(System.Linq.Enumerable.Select(chunk.Thinking, c => c?.Text ?? string.Empty))
                        : (chunk.Text ?? string.Empty);
                    if (!string.IsNullOrEmpty(reasoningText))
                        contents.Add(new TextReasoningContent(reasoningText));
                    break;
            }
        }

        /// <summary>
        /// Mapping <see cref="ReasoningEffort"/> MEAI -> string Mistral. L'API ne reconnaît que
        /// <c>"high"</c> ou <c>"none"</c> ; tous les efforts >= Medium remontent à <c>"high"</c>, Low remonte
        /// à <c>"none"</c> (interprétation prudente : Low = "à peine du reasoning"), None / null -> non envoyé.
        /// </summary>
        internal static string? ToMistralReasoningEffort(Microsoft.Extensions.AI.ReasoningEffort? effort) => effort switch
        {
            null or Microsoft.Extensions.AI.ReasoningEffort.None => null,
            Microsoft.Extensions.AI.ReasoningEffort.Low => "none",
            Microsoft.Extensions.AI.ReasoningEffort.Medium
                or Microsoft.Extensions.AI.ReasoningEffort.High
                or Microsoft.Extensions.AI.ReasoningEffort.ExtraHigh => "high",
            _ => null,
        };

        void IDisposable.Dispose() { }

        object IChatClient.GetService(Type serviceType, object serviceKey) =>
            serviceKey is not null ? null :
            serviceType == typeof(ChatClientMetadata) ? (_metadata ??= new ChatClientMetadata(nameof(MistralClient), new Uri(Url))) :
            serviceType?.IsInstanceOfType(this) is true ? this : 
            null;

        private ChatClientMetadata _metadata;

        private sealed class FunctionParameters
        {
            [JsonPropertyName("type")]
            public string Type { get; set; } = "object";

            [JsonPropertyName("required")]
            public List<string> Required { get; set; } = [];

            [JsonPropertyName("properties")]
            public Dictionary<string, JsonElement> Properties { get; set; } = [];
        }
    }

    
}
