using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mistral.SDK.DTOs
{
    public class Usage
    {
        [JsonPropertyName("prompt_tokens")]
        public int PromptTokens { get; set; }

        /// <summary>
        /// Gets or Sets CompletionTokens
        /// </summary>
        /// <example>93</example>
        [JsonPropertyName("completion_tokens")]
        public int CompletionTokens { get; set; }

        /// <summary>
        /// Gets or Sets TotalTokens
        /// </summary>
        /// <example>107</example>
        [JsonPropertyName("total_tokens")]
        public int TotalTokens { get; set; }

        /// <summary>
        /// Reasoning tokens (modèles de reasoning : mistral-medium-3-5, mistral-small-* avec reasoning_effort).
        /// Mistral n'a pas formellement documenté le nom du champ — on cible défensivement la position top-level
        /// la plus probable (côté OpenAI-compat, c'est ici). Si l'API place plutôt l'info dans une sous-structure
        /// (cf. <see cref="CompletionTokensDetails"/>), on lira la valeur via <see cref="GetReasoningTokens"/>.
        /// </summary>
        [JsonPropertyName("reasoning_tokens")]
        public int? ReasoningTokens { get; set; }

        /// <summary>
        /// Forme imbriquée à la OpenAI : <c>{"completion_tokens_details": {"reasoning_tokens": …}}</c>.
        /// Présente pour fallback si Mistral expose le compteur ici plutôt qu'au top-level.
        /// </summary>
        [JsonPropertyName("completion_tokens_details")]
        public CompletionTokensDetails CompletionTokensDetails { get; set; }

        /// <summary>
        /// Détails côté prompt — Mistral expose ici le compteur de tokens en cache
        /// (<c>{"prompt_tokens_details": {"cached_tokens": …}}</c>), mappé sur <c>UsageDetails.CachedInputTokenCount</c>.
        /// </summary>
        [JsonPropertyName("prompt_tokens_details")]
        public PromptTokensDetails PromptTokensDetails { get; set; }

        /// <summary>Retourne le compteur de reasoning tokens en cherchant successivement les deux shapes possibles.</summary>
        public int? GetReasoningTokens() => ReasoningTokens ?? CompletionTokensDetails?.ReasoningTokens;

        /// <summary>Retourne le compteur de tokens en cache côté prompt, si Mistral l'a exposé.</summary>
        public int? GetCachedInputTokens() => PromptTokensDetails?.CachedTokens;

        /// <summary>
        /// Capture tous les champs JSON que Mistral envoie mais qui ne sont pas explicitement mappés ci-dessus.
        /// Utile pour diagnostiquer un nouveau compteur (e.g. <c>thinking_tokens</c>) sans toucher au DTO.
        /// Inspect en debug : <c>usage.ExtensionData</c> → dictionnaire (clé = nom JSON, valeur = JsonElement).
        /// </summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement> ExtensionData { get; set; }
    }

    public class CompletionTokensDetails
    {
        [JsonPropertyName("reasoning_tokens")]
        public int? ReasoningTokens { get; set; }
    }

    public class PromptTokensDetails
    {
        [JsonPropertyName("cached_tokens")]
        public int? CachedTokens { get; set; }
    }
}
