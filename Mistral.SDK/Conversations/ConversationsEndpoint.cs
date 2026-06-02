using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mistral.SDK.DTOs.Conversations;

namespace Mistral.SDK.Conversations
{
    /// <summary>
    /// Wraps the <c>/v1/conversations</c> endpoint (the Conversations API), which hosts Mistral's built-in
    /// server-side connectors such as web search. Rather than instantiating this yourself, access it through
    /// <see cref="MistralClient.Conversations"/>.
    /// </summary>
    public class ConversationsEndpoint : EndpointBase
    {
        internal ConversationsEndpoint(MistralClient client) : base(client) { }

        protected override string Endpoint => "conversations";

        /// <summary>
        /// Non-streaming call to <c>POST /v1/conversations</c>.
        /// </summary>
        public async Task<ConversationResponse> GetConversationAsync(ConversationRequest request, CancellationToken cancellationToken = default)
        {
            request.Stream = false;

            var response = await HttpRequestRaw(Url, HttpMethod.Post, request, false, cancellationToken).ConfigureAwait(false);

#if NET6_0_OR_GREATER
            string resultAsString = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
#else
            string resultAsString = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
#endif

            var res = await JsonSerializer.DeserializeAsync<ConversationResponse>(
                new MemoryStream(Encoding.UTF8.GetBytes(resultAsString)),
                MistralClient.JsonSerializationOptions,
                cancellationToken).ConfigureAwait(false);

            return res;
        }

        /// <summary>
        /// Streaming call to <c>POST /v1/conversations#stream</c>. Yields one <see cref="ConversationStreamEvent"/>
        /// per SSE frame. The SSE shape is <c>event: &lt;type&gt;\n data: {json}\n\n</c>; we accumulate <c>data:</c>
        /// lines, ignore the <c>event:</c> line (the JSON carries its own <c>type</c>), and parse on the blank line.
        /// </summary>
        public async IAsyncEnumerable<ConversationStreamEvent> StreamConversationAsync(
            ConversationRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            request.Stream = true;

            var response = await HttpRequestRaw(Url, HttpMethod.Post, request, true, cancellationToken).ConfigureAwait(false);

#if NET6_0_OR_GREATER
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#else
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
            using var reader = new StreamReader(stream);

            var data = new StringBuilder();
            string line;
#if NET8_0_OR_GREATER
            while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
#else
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
#endif
            {
                if (line.Length == 0)
                {
                    // Blank line terminates an event.
                    var evt = ParseEvent(data);
                    if (evt != null)
                        yield return evt;
                    continue;
                }

                if (line.StartsWith("data:"))
                {
                    if (data.Length > 0)
                        data.Append('\n');
                    data.Append(line.Substring("data:".Length).TrimStart());
                }
                // "event:" and any other field lines are ignored — we dispatch on the JSON "type".
            }

            // Flush a trailing event with no terminating blank line.
            {
                var evt = ParseEvent(data);
                if (evt != null)
                    yield return evt;
            }
        }

        private static ConversationStreamEvent ParseEvent(StringBuilder data)
        {
            if (data.Length == 0)
                return null;

            string payload = data.ToString();
            data.Clear();

            if (payload == "[DONE]")
                return null;

            return JsonSerializer.Deserialize<ConversationStreamEvent>(payload, MistralClient.JsonSerializationOptions);
        }
    }
}
