using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Mistral.SDK.DTOs;
using Mistral.SDK.DTOs.Conversations;

namespace Mistral.SDK.Completions
{
    // Web-search path. Mistral's built-in web-search connector lives on /v1/conversations, not on
    // /v1/chat/completions. When ChatOptions carries a HostedWebSearchTool, the IChatClient adapter
    // (CompletionsEndpoint.ChatClient.cs) routes here instead of the normal completion path.
    //
    // Custom function tools coexist with the connector: they are forwarded to the conversation alongside
    // web_search, and a returned function.call is surfaced as a FunctionCallContent so the host's
    // UseFunctionInvocation middleware executes it and re-invokes us with the result — which we replay as a
    // function.result input entry (stateless, store=false). The connector executes server-side; function
    // tools execute client-side, in the same turn.
    public partial class CompletionsEndpoint
    {
        // "web_search" (basic) or "web_search_premium" (adds news sources).
        internal const string WebSearchConnectorType = "web_search";

        // True  -> consume the /v1/conversations SSE stream (token-by-token).
        // False -> single non-streaming call re-emitted as one update (safe fallback if the SSE shape drifts).
        internal const bool UseConversationStreaming = true;

        internal static bool WantsWebSearch(ChatOptions options) =>
            options?.Tools != null && options.Tools.Any(t => t is HostedWebSearchTool);

        internal async Task<ChatResponse> GetWebSearchResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions options, CancellationToken cancellationToken)
        {
            var request = CreateConversationRequest(messages, options);
            var response = await Client.Conversations.GetConversationAsync(request, cancellationToken).ConfigureAwait(false);

            var contents = MapConversationOutputs(response, out _, out bool hasFunctionCalls);
            var message = new Microsoft.Extensions.AI.ChatMessage(ChatRole.Assistant, contents)
            {
                MessageId = response.ConversationId ?? Guid.NewGuid().ToString("N"),
            };

            var completion = new ChatResponse(message)
            {
                ModelId = request.Model,
                ResponseId = response.ConversationId,
                RawRepresentation = response,
                FinishReason = hasFunctionCalls ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop,
            };

            if (response.Usage is { } usage)
                completion.Usage = ToUsageDetails(usage);

            return completion;
        }

        internal IAsyncEnumerable<ChatResponseUpdate> GetWebSearchStreamingAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions options, CancellationToken cancellationToken) =>
            UseConversationStreaming
                ? StreamWebSearchAsync(messages, options, cancellationToken)
                : BufferedWebSearchAsync(messages, options, cancellationToken);

        // True streaming over the /v1/conversations SSE: text deltas flow token-by-token; web-search calls,
        // citations, function calls and usage are surfaced as they arrive (citations/usage are flushed in the
        // terminal update so the host can attach them as a unit).
        private async IAsyncEnumerable<ChatResponseUpdate> StreamWebSearchAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var request = CreateConversationRequest(messages, options);
            string model = request.Model;
            string conversationId = null;
            var references = new List<AIContent>();
            var seenReferenceUrls = new HashSet<string>();
            bool webSearchAnnounced = false;
            UsageDetails usage = null;
            // function.call.delta streams the arguments incrementally across several events (keyed by
            // output_index), so we accumulate per call and only emit a complete FunctionCallContent at the end.
            var pendingCalls = new Dictionary<int, PartialFunctionCall>();

            await foreach (var evt in Client.Conversations.StreamConversationAsync(request, cancellationToken)
                .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                switch (evt.Type)
                {
                    case "conversation.response.started":
                        conversationId = evt.ConversationId;
                        break;

                    case "tool.execution.started":
                        if (!webSearchAnnounced && (evt.Name == "web_search" || evt.Name == "web_search_premium"))
                        {
                            webSearchAnnounced = true;
                            yield return Update(new WebSearchToolCallContent(conversationId ?? evt.Id ?? string.Empty), conversationId, model);
                        }
                        break;

                    case "message.output.delta":
                        var (deltaText, referenceUrl) = InterpretDeltaContent(evt.Content);
                        if (!string.IsNullOrEmpty(deltaText))
                            yield return Update(new TextContent(deltaText), conversationId, model);
                        TryAddReference(references, seenReferenceUrls, referenceUrl);
                        break;

                    case "function.call.delta":
                        AccumulateFunctionCall(pendingCalls, evt);
                        break;

                    case "conversation.response.done":
                        if (evt.Usage is { } u)
                            usage = ToUsageDetails(u);
                        break;

                    case "conversation.response.error":
                        throw new Exception($"Mistral conversation error ({evt.Code}): {evt.Message}");
                }
            }

            // Terminal update: completed function calls + citations + usage + the finish reason. A function call
            // leaves the turn open (the host's UseFunctionInvocation middleware executes the tool and re-invokes
            // us), so we signal tool_calls; otherwise the turn is complete.
            bool hasFunctionCall = pendingCalls.Count > 0;
            var finalContents = new List<AIContent>();
            foreach (var pc in pendingCalls.Values)
            {
                finalContents.Add(new FunctionCallContent(
                    pc.CallId ?? Guid.NewGuid().ToString("N"),
                    pc.Name,
                    ParseArguments(pc.Arguments)));
            }
            if (references.Count > 0)
                finalContents.Add(new WebSearchToolResultContent(conversationId ?? string.Empty) { Outputs = references });
            if (usage != null)
                finalContents.Add(new UsageContent(usage));

            yield return TerminalUpdate(finalContents, conversationId, model, hasFunctionCall);
        }

        // Fallback: one non-streaming call re-emitted as a single update. Kept behind UseConversationStreaming
        // so we can revert instantly if the SSE event shape ever drifts.
        private async IAsyncEnumerable<ChatResponseUpdate> BufferedWebSearchAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var request = CreateConversationRequest(messages, options);
            var response = await Client.Conversations.GetConversationAsync(request, cancellationToken).ConfigureAwait(false);

            var contents = MapConversationOutputs(response, out _, out bool hasFunctionCalls);
            if (response.Usage is { } usage)
                contents.Add(new UsageContent(ToUsageDetails(usage)));

            yield return TerminalUpdate(contents, response.ConversationId, request.Model, hasFunctionCalls);
        }

        // Intermediate update: no RawRepresentation and no finish reason, so the host keeps consuming.
        private static ChatResponseUpdate Update(AIContent content, string conversationId, string model) =>
            new(ChatRole.Assistant, [content])
            {
                MessageId = conversationId,
                ModelId = model,
                ResponseId = conversationId,
            };

        // Terminal update. The host (Celeste.LLM.ChatWithStreaming) derives the finish reason from the update's
        // RawRepresentation and only recognizes ChatCompletionResponse for Mistral (anything else hits a
        // `default: throw`). Surface a synthetic one so the conversation path is indistinguishable from the
        // normal completion path to the host — no host change required.
        private static ChatResponseUpdate TerminalUpdate(
            List<AIContent> contents, string conversationId, string model, bool hasFunctionCall)
        {
            var raw = new ChatCompletionResponse
            {
                Id = conversationId,
                Model = model,
                Choices =
                [
                    new() {
                        Index = 0,
                        Delta = new DTOs.ChatMessage { Role = DTOs.ChatMessage.RoleEnum.Assistant },
                        FinishReason = hasFunctionCall ? Choice.FinishReasonEnum.ToolCalls : Choice.FinishReasonEnum.Stop,
                    },
                ],
            };

            return new ChatResponseUpdate(ChatRole.Assistant, contents)
            {
                MessageId = conversationId,
                ModelId = model,
                ResponseId = conversationId,
                RawRepresentation = raw,
                FinishReason = hasFunctionCall ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop,
            };
        }

        // message.output.delta `content` is either a plain string (text delta) or a chunk object
        // ({type:"text",text} or {type:"tool_reference",tool,title,url,...}). Returns the text delta and/or a
        // citation URL when present.
        private static (string text, string referenceUrl) InterpretDeltaContent(JsonNode content)
        {
            if (content is null)
                return (null, null);

            if (content is JsonValue value && value.TryGetValue<string>(out var s))
                return (s, null);

            if (content is JsonObject obj)
            {
                string chunkType = obj["type"]?.GetValue<string>();
                if (chunkType == "text")
                    return (obj["text"]?.GetValue<string>(), null);

                if (chunkType == "tool_reference")
                    return (null, obj["url"]?.GetValue<string>());
            }

            return (null, null);
        }

        // function.call.delta streams the call's arguments across several events. Accumulate per output_index,
        // capturing id/name when present. Arguments arrive cumulatively (each delta is the full string so far);
        // we also tolerate the fragment variant by appending when the new value isn't an extension of the old.
        private static void AccumulateFunctionCall(Dictionary<int, PartialFunctionCall> pending, ConversationStreamEvent evt)
        {
            int idx = evt.OutputIndex ?? 0;
            if (!pending.TryGetValue(idx, out var pc))
            {
                pc = new PartialFunctionCall();
                pending[idx] = pc;
            }

            if (!string.IsNullOrEmpty(evt.ToolCallId))
                pc.CallId = evt.ToolCallId;
            if (!string.IsNullOrEmpty(evt.Name))
                pc.Name = evt.Name;

            if (evt.Arguments != null)
            {
                if (pc.Arguments == null || evt.Arguments.StartsWith(pc.Arguments, StringComparison.Ordinal))
                    pc.Arguments = evt.Arguments;   // cumulative (full-so-far)
                else
                    pc.Arguments += evt.Arguments;  // fragment
            }
        }

        private sealed class PartialFunctionCall
        {
            public string CallId;
            public string Name;
            public string Arguments;
        }

        // Adds a citation as a UriContent, skipping blank/relative URLs and de-duplicating exact URLs
        // (Mistral occasionally returns the same source several times in one turn).
        private static void TryAddReference(List<AIContent> references, HashSet<string> seen, string url)
        {
            if (string.IsNullOrEmpty(url))
                return;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return;
            if (!seen.Add(uri.AbsoluteUri))
                return;
            references.Add(new UriContent(uri, "text/html"));
        }

        // Builds the web_search connector's tool_configuration (domain allow/deny lists) from the
        // HostedWebSearchTool's AdditionalProperties — keys "include" / "exclude", each a collection of domains.
        // Null when neither is set. (Mistral's web_search connector has no geolocation option.)
        private static ConversationToolConfiguration BuildWebSearchConfiguration(ChatOptions options)
        {
            var props = options?.Tools?.OfType<HostedWebSearchTool>().FirstOrDefault()?.AdditionalProperties;
            if (props == null)
                return null;

            var include = ReadDomainList(props, "include");
            var exclude = ReadDomainList(props, "exclude");
            if (include == null && exclude == null)
                return null;

            return new ConversationToolConfiguration { Include = include, Exclude = exclude };
        }

        private static List<string> ReadDomainList(IReadOnlyDictionary<string, object?> props, string key)
        {
            if (!props.TryGetValue(key, out var value) || value == null)
                return null;

            var result = new List<string>();
            if (value is string single)
            {
                if (!string.IsNullOrWhiteSpace(single))
                    result.Add(single.Trim());
            }
            else if (value is IEnumerable<string> strings)
            {
                foreach (var s in strings)
                    if (!string.IsNullOrWhiteSpace(s))
                        result.Add(s.Trim());
            }
            else if (value is System.Collections.IEnumerable items)
            {
                foreach (var item in items)
                {
                    var s = item?.ToString();
                    if (!string.IsNullOrWhiteSpace(s))
                        result.Add(s.Trim());
                }
            }

            return result.Count > 0 ? result : null;
        }

        private static ConversationRequest CreateConversationRequest(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions options)
        {
            var request = new ConversationRequest
            {
                Model = options?.ModelId,
                Store = false,
                CompletionArgs = new ConversationCompletionArgs
                {
                    Temperature = (decimal?)options?.Temperature,
                    TopP = (decimal?)options?.TopP,
                    MaxTokens = options?.MaxOutputTokens,
                },
            };

            // Tools = the web_search connector (optionally domain-filtered) + every custom function tool, so the
            // model can search the web AND call functions in the same turn.
            var tools = new List<ConversationTool>
            {
                new ConversationTool
                {
                    Type = WebSearchConnectorType,
                    ToolConfiguration = BuildWebSearchConfiguration(options),
                },
            };
            if (options?.Tools != null)
            {
                foreach (var declaration in options.Tools.OfType<AIFunctionDeclaration>())
                {
                    tools.Add(new ConversationTool
                    {
                        Type = "function",
                        Function = new ConversationFunctionTool
                        {
                            Name = declaration.Name,
                            Description = declaration.Description,
                            // Same JSON-schema normalization as the /chat/completions path (FunctionParameters
                            // is the private nested type shared across this partial class).
                            Parameters = JsonSerializer.SerializeToNode(
                                JsonSerializer.Deserialize<FunctionParameters>(declaration.JsonSchema)),
                        },
                    });
                }
            }
            request.Tools = tools;

            // System content -> `instructions`; user/assistant turns and function call/result -> `inputs`
            // (stateless replay, store=false).
            var systemParts = new List<string>();
            if (!string.IsNullOrEmpty(options?.Instructions))
                systemParts.Add(options.Instructions);

            foreach (var m in messages)
            {
                if (m.Role == ChatRole.System)
                {
                    if (!string.IsNullOrEmpty(m.Text))
                        systemParts.Add(m.Text);
                    continue;
                }

                foreach (var content in m.Contents)
                {
                    switch (content)
                    {
                        case FunctionCallContent call:
                            request.Inputs.Add(new ConversationInputEntry
                            {
                                Type = "function.call",
                                ToolCallId = call.CallId,
                                Name = call.Name,
                                Arguments = JsonSerializer.Serialize(call.Arguments ?? new Dictionary<string, object?>()),
                            });
                            break;

                        case FunctionResultContent result:
                            request.Inputs.Add(new ConversationInputEntry
                            {
                                Type = "function.result",
                                ToolCallId = result.CallId,
                                Result = result.Result?.ToString() ?? string.Empty,
                            });
                            break;
                    }
                }

                // The visible text of the turn (skip empty, e.g. a pure function-call assistant message).
                if (!string.IsNullOrEmpty(m.Text))
                {
                    request.Inputs.Add(new ConversationInputEntry
                    {
                        Role = m.Role == ChatRole.Assistant ? "assistant" : "user",
                        Content = m.Text,
                    });
                }
            }

            if (systemParts.Count > 0)
                request.Instructions = string.Join("\n", systemParts);

            // The endpoint requires at least one input.
            if (request.Inputs.Count == 0)
                request.Inputs.Add(new ConversationInputEntry { Role = "user", Content = "​" });

            return request;
        }

        private static List<AIContent> MapConversationOutputs(
            ConversationResponse response, out bool anySearch, out bool anyFunctionCall)
        {
            anySearch = false;
            anyFunctionCall = false;
            var contents = new List<AIContent>();
            var references = new List<AIContent>();
            var seenReferenceUrls = new HashSet<string>();
            string callId = response.ConversationId ?? Guid.NewGuid().ToString("N");

            foreach (var output in response.Outputs ?? Enumerable.Empty<ConversationOutputEntry>())
            {
                switch (output.Type)
                {
                    case "tool.execution":
                        if (output.Name == "web_search" || output.Name == "web_search_premium")
                            anySearch = true;
                        break;

                    case "function.call":
                        anyFunctionCall = true;
                        contents.Add(new FunctionCallContent(
                            output.ToolCallId ?? Guid.NewGuid().ToString("N"),
                            output.Name,
                            ParseArguments(output.Arguments)));
                        break;

                    case "message.output":
                        if (output.Content == null)
                            break;

                        string text = output.Content.AsText();
                        if (!string.IsNullOrEmpty(text))
                            contents.Add(new TextContent(text));

                        foreach (var reference in output.Content.References)
                            TryAddReference(references, seenReferenceUrls, reference.Url);
                        break;
                }
            }

            // Mirror the AIContent shape Anthropic/OpenAI emit so the host surfaces citations uniformly:
            // a WebSearchToolCallContent for the invocation, then a WebSearchToolResultContent whose Outputs
            // are UriContent (the host reads exactly these).
            if (anySearch)
                contents.Insert(0, new WebSearchToolCallContent(callId));
            if (references.Count > 0)
                contents.Add(new WebSearchToolResultContent(callId) { Outputs = references });

            return contents;
        }

        private static IDictionary<string, object?> ParseArguments(string argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson))
                return new Dictionary<string, object?>();
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson)
                    ?? new Dictionary<string, object?>();
            }
            catch (JsonException)
            {
                return new Dictionary<string, object?>();
            }
        }

        private static UsageDetails ToUsageDetails(Usage usage) => new UsageDetails
        {
            InputTokenCount = usage.PromptTokens,
            OutputTokenCount = usage.CompletionTokens,
            TotalTokenCount = usage.TotalTokens,
        };
    }
}
