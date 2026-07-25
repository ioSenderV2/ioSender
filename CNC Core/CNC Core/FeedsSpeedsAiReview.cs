/*
 * FeedsSpeedsAiReview.cs - part of CNC Core library
 *
 * Optional second-opinion pass over FeedsSpeedsAdvisor's table-driven verdicts, only
 * offered when an API key is configured (Settings > App, registry-backed via SecretStore
 * - see ApiKeySecretName) - no key means this is simply absent, no setup nagging. The
 * table already encodes the domain expert's
 * chip-load math (an LLM has no special physics insight beyond the same chart), so this
 * exists for qualitative judgment the table can't express - e.g. "this axial-over-max
 * flag is fine, it's an adaptive roughing pass" - not to re-derive the arithmetic. Its
 * output is shown to the user before anything is written to the apply file; it never
 * silently substitutes for the table engine.
 *
 * Outbound HTTPS reuses the TLS 1.2 idiom already established in SimulatorManager.cs
 * (this framework targets net462, which does not enable TLS 1.2 by default).
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace CNC.Core
{
    // One parameter's AI opinion: Recommend is null when the AI has nothing to add (agrees with the
    // table, or the parameter isn't applicable) - Comment still carries why, if it said anything.
    public class AiParamRecommendation
    {
        public double? Recommend { get; set; }
        public string Comment { get; set; }
    }

    // One call's per-parameter opinions + token usage (from the API response's own "usage" block - not
    // estimated). Keyed exactly like FeedsSpeedsApplyOp.Set / ParameterVerdict's own field names: "rpm",
    // "cutting_feed", "plunge_feed", "axial_step", "radial_step".
    public class AiReviewResult
    {
        public Dictionary<string, AiParamRecommendation> Parameters { get; set; } = new Dictionary<string, AiParamRecommendation>();
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public string Model { get; set; }
        public double EstimatedCostUsd => FeedsSpeedsAiReview.EstimateCostUsd(InputTokens, OutputTokens, Model);
    }

    public static class FeedsSpeedsAiReview
    {
        // Registry key name under SecretStore (Settings > App > "AI Review Key" sets this) - was the
        // ANTHROPIC_API_KEY env var; kept as a public const since some call sites/comments still refer to
        // it by that name.
        public const string ApiKeySecretName = "AnthropicApiKey";
        private const string ApiUrl = "https://api.anthropic.com/v1/messages";

        // Default model + the picker list a caller (FeedsAndSpeedsView's model dropdown) can offer.
        // Label -> model id, in the order shown; Sonnet 5 first since it's the default.
        public const string DefaultModel = "claude-sonnet-5";
        public static readonly (string Label, string ModelId)[] AvailableModels =
        {
            ("Sonnet 5", "claude-sonnet-5"),
            ("Opus 4.8", "claude-opus-4-8"),
            ("Fable 5", "claude-fable-5"),
            ("Haiku 4.5", "claude-haiku-4-5-20251001"),
        };

        // Per-million-token list price (USD), per model - verify against anthropic.com/pricing if this
        // drifts. Fable 5's isn't confirmed yet, so it defaults to the Sonnet tier as a placeholder.
        private static readonly Dictionary<string, (double In, double Out)> PricingPerMillionTokens = new Dictionary<string, (double, double)>
        {
            ["claude-opus-4-8"] = (15.0, 75.0),
            ["claude-sonnet-5"] = (3.0, 15.0),
            ["claude-haiku-4-5-20251001"] = (0.8, 4.0),
            ["claude-fable-5"] = (3.0, 15.0),   // unconfirmed - Sonnet-tier placeholder
        };

        public static double EstimateCostUsd(int inputTokens, int outputTokens, string model = DefaultModel)
        {
            if (model == null || !PricingPerMillionTokens.TryGetValue(model, out var price))
                price = PricingPerMillionTokens[DefaultModel];
            return inputTokens / 1_000_000.0 * price.In + outputTokens / 1_000_000.0 * price.Out;
        }

        // Registry-backed via SecretStore (Settings > App's "AI Review Key" row sets this) - was an
        // ANTHROPIC_API_KEY env var lookup.
        public static string GetApiKey()
        {
            return SecretStore.Get(ApiKeySecretName);
        }

        public static bool IsAvailable => !string.IsNullOrEmpty(GetApiKey());

        /// <summary>
        /// Sends the export + the table engine's own verdicts for one operation to the Claude API and
        /// asks it to confirm, override, or annotate each parameter individually, as strict JSON so the
        /// caller can show one AI opinion per grid row (see AiReviewResult.Parameters) rather than one
        /// undifferentiated text blob per operation. Never auto-applied - the caller decides per row
        /// whether to prefer the AI's number over the table's. Also returns this call's actual
        /// input/output token counts (from the API response, not estimated), so a caller reviewing many
        /// operations can show a running total. Throws on any failure (no key, network, non-2xx, or a
        /// response that isn't valid JSON) so the caller can show the error inline rather than pretend a
        /// review happened.
        /// </summary>
        public static AiReviewResult Review(FeedsSpeedsOperation op, OperationRecommendation tableResult, string material, string model = DefaultModel, System.Threading.CancellationToken cancellationToken = default)
        {
            string apiKey = GetApiKey();
            if (string.IsNullOrEmpty(apiKey))
                throw new InvalidOperationException("No AI review key set - add one in Settings > App, or AI review is unavailable.");

            string prompt = BuildPrompt(op, tableResult, material);

            try { System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12; } catch { }

            var requestBody = JsonSerializer.Serialize(new
            {
                model = model,
                // Generous headroom: a reasoning-capable model can spend a chunk of this budget on
                // internal thinking before ever emitting the actual "text" content block - 1024 was
                // observed to get fully consumed by that, cutting off before any real reply existed
                // (empty text -> "Could not parse the model's reply as JSON" with an empty body).
                max_tokens = 4096,
                messages = new[] { new { role = "user", content = prompt } },
            });

            var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(ApiUrl);
            req.Method = "POST";
            req.UserAgent = "ioSenderV2";
            req.Accept = "application/json";
            req.ContentType = "application/json";
            req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            req.Timeout = req.ReadWriteTimeout = 60 * 1000;

            // HttpWebRequest has no CancellationToken overload - Abort() is the only way to actually
            // interrupt an in-flight GetRequestStream/GetResponse call. Without this, cancelling only
            // stopped the NEXT operation in a multi-op batch from starting; a review already in flight
            // (a real ~20-30s Anthropic call) ran to completion regardless of Esc/Ctrl+C, with no sign
            // anything had registered until it finally finished.
            using (cancellationToken.Register(() => { try { req.Abort(); } catch { } }))
            {
                var bodyBytes = Encoding.UTF8.GetBytes(requestBody);
                try
                {
                    using (var stream = req.GetRequestStream())
                        stream.Write(bodyBytes, 0, bodyBytes.Length);

                    string responseJson;
                    try
                    {
                        using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                        using (var reader = new System.IO.StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                            responseJson = reader.ReadToEnd();
                    }
                    catch (System.Net.WebException wex) when (wex.Response != null)
                    {
                        // HttpWebRequest throws on any non-2xx before the caller ever sees the body, but
                        // Anthropic's error responses have a real JSON body explaining what went wrong (bad
                        // model name, invalid key, rate limit, ...) - surface that instead of just the generic
                        // "(400) Bad Request" WebException message, which tells you nothing actionable.
                        using (var errStream = wex.Response.GetResponseStream())
                        using (var reader = new System.IO.StreamReader(errStream, Encoding.UTF8))
                        {
                            string body = reader.ReadToEnd();
                            throw new InvalidOperationException($"Anthropic API error: {wex.Message}\n{body}", wex);
                        }
                    }

                    return ParseResponse(responseJson, model);
                }
                catch (System.Net.WebException wex) when (cancellationToken.IsCancellationRequested)
                {
                    // req.Abort() surfaces as a WebException (RequestCanceled), not the framework's own
                    // OperationCanceledException - translate it so callers can catch cancellation uniformly.
                    throw new OperationCanceledException("AI review cancelled.", wex, cancellationToken);
                }
            }
        }

        private static AiReviewResult ParseResponse(string responseJson, string model)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(responseJson); }
            catch (JsonException jex)
            {
                throw new InvalidOperationException(
                    $"Anthropic API returned a non-JSON response: {jex.Message}\nRaw response:\n{responseJson}", jex);
            }

            using (doc)
            {
                var sb = new StringBuilder();
                foreach (var block in doc.RootElement.GetProperty("content").EnumerateArray())
                {
                    if (block.TryGetProperty("text", out var text))
                        sb.Append(text.GetString());
                }

                int inputTokens = 0, outputTokens = 0;
                if (doc.RootElement.TryGetProperty("usage", out var usage))
                {
                    if (usage.TryGetProperty("input_tokens", out var it)) inputTokens = it.GetInt32();
                    if (usage.TryGetProperty("output_tokens", out var ot)) outputTokens = ot.GetInt32();
                }

                string modelText = StripCodeFence(sb.ToString());
                if (string.IsNullOrWhiteSpace(modelText))
                {
                    // Self-diagnosing: include stop_reason + the raw content block types, since an empty
                    // reply usually means the token budget ran out before any "text" block was emitted
                    // (see max_tokens' own comment above) rather than a malformed response.
                    string stopReason = doc.RootElement.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : "(none)";
                    var blockTypes = doc.RootElement.GetProperty("content").EnumerateArray()
                        .Select(b => b.TryGetProperty("type", out var t) ? t.GetString() : "?");
                    throw new InvalidOperationException(
                        $"The model returned no text content (stop_reason={stopReason}, content block types: " +
                        $"{string.Join(", ", blockTypes)}). It likely ran out of tokens before replying.");
                }

                Dictionary<string, AiParamRecommendation> parameters;
                try { parameters = ParseParameters(modelText); }
                catch (JsonException jex)
                {
                    throw new InvalidOperationException(
                        $"Could not parse the model's reply as JSON: {jex.Message}\nModel said:\n{modelText}", jex);
                }
                return new AiReviewResult { Parameters = parameters, InputTokens = inputTokens, OutputTokens = outputTokens, Model = model };
            }
        }

        private static string BuildPrompt(FeedsSpeedsOperation op, OperationRecommendation tableResult, string material)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are reviewing CNC feeds-and-speeds recommendations for one CAM operation. " +
                          "A deterministic chip-load/RPM table already produced the verdicts below. Your job " +
                          "is NOT to redo that arithmetic - it's to catch qualitative judgment calls the table " +
                          "can't express (e.g. an axial-over-max flag that's actually fine because the strategy " +
                          "is adaptive roughing with low radial engagement).");
            sb.AppendLine();
            sb.AppendLine($"Operation: {op.Name} (strategy: {op.Strategy}, tool class: {tableResult.ToolClass})");
            sb.AppendLine($"Tool: {op.Tool?.Name} ({op.Tool?.Type}), diameter {op.Tool?.DiameterMm} mm, {op.Tool?.Flutes} flutes");
            sb.AppendLine($"Material: {material}");
            sb.AppendLine();
            AppendParam(sb, "rpm", "RPM", tableResult.Rpm);
            AppendParam(sb, "cutting_feed", "Cutting feed (mm/min)", tableResult.CuttingFeed);
            AppendParam(sb, "plunge_feed", "Plunge feed (mm/min)", tableResult.PlungeFeed);
            AppendParam(sb, "axial_step", "Axial step (mm)", tableResult.AxialStep);
            AppendParam(sb, "radial_step", "Radial step (mm)", tableResult.RadialStep);
            if (tableResult.Notes.Count > 0)
            {
                sb.AppendLine("Table notes:");
                foreach (var n in tableResult.Notes)
                    sb.AppendLine($"  - {n}");
            }
            sb.AppendLine();
            sb.AppendLine("Respond with ONLY a JSON object (no markdown code fence, no prose before or " +
                          "after) with one entry per parameter listed above, keyed exactly \"rpm\", " +
                          "\"cutting_feed\", \"plunge_feed\", \"axial_step\", \"radial_step\". Each value is " +
                          "{\"recommend\": <number or null>, \"comment\": <short string or null>}. Set " +
                          "\"recommend\" to null when you agree with the table's own recommended value or " +
                          "the parameter isn't applicable to this operation; set it to a number only when " +
                          "you'd override the table. \"comment\" is a short (one sentence) explanation, or " +
                          "null if you have nothing to add.");
            return sb.ToString();
        }

        private static void AppendParam(StringBuilder sb, string key, string label, ParameterVerdict pv)
        {
            sb.AppendLine($"{key} ({label}): current={pv.Current}, recommended={pv.Recommended}, " +
                          $"machine limit={pv.MachineLimit}, verdict={pv.Verdict}");
            foreach (var n in pv.Notes)
                sb.AppendLine($"  - {n}");
        }

        // Claude sometimes wraps JSON in a ```json ... ``` fence despite being asked not to - strip it
        // rather than failing to parse.
        private static string StripCodeFence(string text)
        {
            var t = text.Trim();
            if (t.StartsWith("```"))
            {
                int firstNewline = t.IndexOf('\n');
                if (firstNewline >= 0)
                    t = t.Substring(firstNewline + 1);
                int lastFence = t.LastIndexOf("```", StringComparison.Ordinal);
                if (lastFence >= 0)
                    t = t.Substring(0, lastFence);
            }
            return t.Trim();
        }

        private static Dictionary<string, AiParamRecommendation> ParseParameters(string json)
        {
            var result = new Dictionary<string, AiParamRecommendation>();
            using (var doc = JsonDocument.Parse(json))
            {
                // A model can reply with valid JSON that isn't the requested object shape - e.g. a bare
                // `null` for an operation it has nothing to say about (observed from Fable 5).
                // EnumerateObject() throws InvalidOperationException on anything but ValueKind.Object, so
                // treat that case as "no opinions" instead of failing the whole review.
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return result;

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    double? recommend = null;
                    string comment = null;
                    if (prop.Value.TryGetProperty("recommend", out var r) && r.ValueKind == JsonValueKind.Number)
                        recommend = r.GetDouble();
                    if (prop.Value.TryGetProperty("comment", out var c) && c.ValueKind == JsonValueKind.String)
                        comment = c.GetString();
                    result[prop.Name] = new AiParamRecommendation { Recommend = recommend, Comment = comment };
                }
            }
            return result;
        }
    }
}
