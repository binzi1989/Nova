using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NovaDesktop.Services;

/// <summary>
/// Deterministic, read-only commerce calculations exposed only while the
/// cross-border commerce Agent Pack is active. Industry Packs remain
/// declarative; this service supplies auditable primitives instead of asking a
/// language model to invent arithmetic or evidence status.
/// </summary>
public sealed class CrossBorderCommerceToolService
{
    public const string PackId = "nova.cross-border-commerce";

    public static bool Supports(string? agentPackId)
        => PackId.Equals(agentPackId, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<JsonObject> CreateDefinitions(string? agentPackId)
        => Supports(agentPackId)
            ?
            [
                Function(
                    "commerce_normalize_product_passport",
                    "Create a structured cross-border SKU Product Passport. Separates confirmed facts, assumptions, and unknowns; calculates readiness without inventing missing specifications.",
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["product_name"] = StringProperty("Human-readable product name."),
                            ["sku"] = StringProperty("Seller SKU, or an empty string when not assigned."),
                            ["category"] = StringProperty("Product category, or an empty string when unknown."),
                            ["brand"] = StringProperty("Brand, or an empty string when unknown."),
                            ["source_country"] = StringProperty("Country of origin, or an empty string when unknown."),
                            ["target_market"] = StringProperty("Target sales country or region."),
                            ["platform"] = StringProperty("Target marketplace or channel."),
                            ["currency"] = StringProperty("ISO-style currency code such as MXN, USD, or CNY."),
                            ["sale_price"] = NumberProperty("Planned customer sale price, greater than zero."),
                            ["unit_product_cost"] = NumberProperty("Unit purchase/manufacturing cost. Use 0 when not confirmed."),
                            ["confirmed_facts"] = StringArray("Facts supported by a source or supplied directly by the user."),
                            ["assumptions"] = StringArray("Working assumptions that still require verification."),
                            ["unknowns"] = StringArray("Known missing facts or decisions."),
                            ["source_refs"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["description"] = "Source references already available for this SKU.",
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject
                                    {
                                        ["url"] = StringProperty("Source URL, or user://provided for user-supplied facts."),
                                        ["observed_at"] = StringProperty("Observation date in YYYY-MM-DD form."),
                                        ["note"] = StringProperty("What this source supports.")
                                    },
                                    ["required"] = new JsonArray("url", "observed_at", "note"),
                                    ["additionalProperties"] = false
                                }
                            }
                        },
                        ["required"] = new JsonArray(
                            "product_name", "sku", "category", "brand", "source_country",
                            "target_market", "platform", "currency", "sale_price",
                            "unit_product_cost", "confirmed_facts", "assumptions", "unknowns",
                            "source_refs"),
                        ["additionalProperties"] = false
                    }),
                Function(
                    "commerce_calculate_landed_profit",
                    "Calculate an auditable per-order landed-profit scenario. All rate inputs are percentages from 0 to 100. Returns every line item, contribution margin, break-even ad rate, break-even ROAS, and missing-input warnings.",
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["currency"] = StringProperty("Currency code shared by all money inputs."),
                            ["sale_price"] = NumberProperty("Customer sale price."),
                            ["unit_product_cost"] = NumberProperty("Product acquisition cost per unit."),
                            ["domestic_shipping"] = NumberProperty("Origin-country transport per unit."),
                            ["international_shipping"] = NumberProperty("International and last-mile transport per unit."),
                            ["packaging"] = NumberProperty("Packaging cost per unit."),
                            ["duty_rate_pct"] = NumberProperty("Duty percentage from 0 to 100."),
                            ["import_tax_rate_pct"] = NumberProperty("Import tax/VAT percentage from 0 to 100."),
                            ["platform_fee_rate_pct"] = NumberProperty("Marketplace fee percentage from 0 to 100."),
                            ["payment_fee_rate_pct"] = NumberProperty("Payment processing percentage from 0 to 100."),
                            ["affiliate_rate_pct"] = NumberProperty("Affiliate/creator commission percentage from 0 to 100."),
                            ["ad_cost_rate_pct"] = NumberProperty("Expected advertising spend as percentage of net sales."),
                            ["return_rate_pct"] = NumberProperty("Expected order return/refund percentage from 0 to 100."),
                            ["return_loss_rate_pct"] = NumberProperty("Share of returned landed inventory that cannot be recovered, from 0 to 100."),
                            ["return_handling_cost"] = NumberProperty("Reverse-logistics and handling cost per returned order."),
                            ["other_variable_cost"] = NumberProperty("Other variable cost per dispatched order.")
                        },
                        ["required"] = new JsonArray(
                            "currency", "sale_price", "unit_product_cost", "domestic_shipping",
                            "international_shipping", "packaging", "duty_rate_pct",
                            "import_tax_rate_pct", "platform_fee_rate_pct", "payment_fee_rate_pct",
                            "affiliate_rate_pct", "ad_cost_rate_pct", "return_rate_pct",
                            "return_loss_rate_pct", "return_handling_cost", "other_variable_cost"),
                        ["additionalProperties"] = false
                    }),
                Function(
                    "commerce_build_evidence_ledger",
                    "Audit market claims into an evidence ledger. Flags missing sources, stale observations, low confidence, and conflicting values. This tool never treats marketplace search counts as sales evidence.",
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["as_of"] = StringProperty("Ledger date in YYYY-MM-DD form."),
                            ["max_age_days"] = IntegerProperty("Maximum evidence age in days, from 1 to 3650."),
                            ["claims"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["minItems"] = 1,
                                ["maxItems"] = 200,
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject
                                    {
                                        ["id"] = StringProperty("Stable claim key. Reuse the key to expose conflicting values."),
                                        ["statement"] = StringProperty("Human-readable claim."),
                                        ["value"] = StringProperty("Observed value or conclusion."),
                                        ["source_url"] = StringProperty("HTTPS source URL, or user://provided for direct user evidence."),
                                        ["source_title"] = StringProperty("Source title or publisher."),
                                        ["observed_at"] = StringProperty("Observation date in YYYY-MM-DD form."),
                                        ["evidence_type"] = StringProperty("One of primary, official, marketplace, secondary, user-provided, or assumption."),
                                        ["confidence"] = NumberProperty("Confidence from 0 to 100."),
                                        ["market"] = StringProperty("Country/platform scope of the claim.")
                                    },
                                    ["required"] = new JsonArray(
                                        "id", "statement", "value", "source_url", "source_title",
                                        "observed_at", "evidence_type", "confidence", "market"),
                                    ["additionalProperties"] = false
                                }
                            }
                        },
                        ["required"] = new JsonArray("as_of", "max_age_days", "claims"),
                        ["additionalProperties"] = false
                    }),
                Function(
                    "commerce_assess_market_demand",
                    "Build an evidence-weighted, non-financial market-demand fit assessment. Scores customer need, usage, market activity, competition, differentiation, demonstrability, local fit and risks; exposes evidence coverage and uncertainty instead of predicting sales.",
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["product_name"] = StringProperty("Product identity for this run. Never inherit it from a previous case."),
                            ["target_market"] = StringProperty("Target country or region for this assessment."),
                            ["platform"] = StringProperty("Primary sales or validation channel."),
                            ["as_of"] = StringProperty("Assessment date in YYYY-MM-DD form."),
                            ["identity_confidence"] = NumberProperty("Confidence that the product has been correctly identified, from 0 to 100."),
                            ["signals"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["description"] = "One entry per evaluated dimension. Supported dimensions are problem-urgency, audience-reach, usage-frequency, market-activity, competition-headroom, differentiation, content-demonstrability, local-fit, trust-barrier, compliance-risk, return-risk, and seasonality-resilience.",
                                ["minItems"] = 1,
                                ["maxItems"] = 12,
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject
                                    {
                                        ["dimension"] = StringProperty("A supported stable dimension key."),
                                        ["score"] = NumberProperty("Observed or estimated dimension score from 0 to 100. For risk dimensions, a higher score means greater risk."),
                                        ["confidence"] = NumberProperty("Confidence in this score from 0 to 100."),
                                        ["evidence_status"] = StringProperty("One of verified, indicative, assumption, or unknown."),
                                        ["rationale"] = StringProperty("Short explanation including contrary interpretations when relevant."),
                                        ["source_refs"] = StringArray("Source URLs, user://provided references, or local artifact references supporting this signal.")
                                    },
                                    ["required"] = new JsonArray(
                                        "dimension", "score", "confidence", "evidence_status", "rationale", "source_refs"),
                                    ["additionalProperties"] = false
                                }
                            }
                        },
                        ["required"] = new JsonArray(
                            "product_name", "target_market", "platform", "as_of",
                            "identity_confidence", "signals"),
                        ["additionalProperties"] = false
                    })
            ]
            : [];

    public string Execute(string toolName, JsonObject arguments)
        => toolName switch
        {
            "commerce_normalize_product_passport" => NormalizeProductPassport(arguments),
            "commerce_calculate_landed_profit" => CalculateLandedProfit(arguments),
            "commerce_build_evidence_ledger" => BuildEvidenceLedger(arguments),
            "commerce_assess_market_demand" => AssessMarketDemand(arguments),
            _ => throw new InvalidOperationException($"Unknown cross-border commerce tool: {toolName}")
        };

    private static string NormalizeProductPassport(JsonObject input)
    {
        var productName = RequireText(input, "product_name", 160);
        var targetMarket = RequireText(input, "target_market", 120);
        var platform = RequireText(input, "platform", 120);
        var currency = RequireText(input, "currency", 12).ToUpperInvariant();
        var salePrice = Money(input, "sale_price", requiredPositive: true);
        var productCost = Money(input, "unit_product_cost");
        var category = Text(input, "category", 160);
        var sourceCountry = Text(input, "source_country", 120);
        var confirmedFacts = Strings(input, "confirmed_facts", 100);
        var assumptions = Strings(input, "assumptions", 100);
        var declaredUnknowns = Strings(input, "unknowns", 100).ToList();
        var sources = input["source_refs"]?.AsArray().Take(100).Select(item =>
        {
            var source = item?.AsObject() ?? new JsonObject();
            return new
            {
                url = Text(source, "url", 500),
                observedAt = Text(source, "observed_at", 32),
                note = Text(source, "note", 500)
            };
        }).ToArray() ?? [];

        AddUnknown(declaredUnknowns, string.IsNullOrWhiteSpace(category), "商品品类与平台类目");
        AddUnknown(declaredUnknowns, string.IsNullOrWhiteSpace(sourceCountry), "原产国");
        AddUnknown(declaredUnknowns, productCost <= 0, "单位采购或制造成本");
        AddUnknown(declaredUnknowns, sources.Length == 0, "至少一个产品事实或市场证据来源");

        var coreChecks = new[]
        {
            !string.IsNullOrWhiteSpace(productName),
            !string.IsNullOrWhiteSpace(category),
            !string.IsNullOrWhiteSpace(sourceCountry),
            !string.IsNullOrWhiteSpace(targetMarket),
            !string.IsNullOrWhiteSpace(platform),
            salePrice > 0,
            productCost > 0,
            confirmedFacts.Count > 0,
            sources.Length > 0
        };
        var readiness = (int)Math.Round(coreChecks.Count(value => value) * 100m / coreChecks.Length);
        var status = readiness >= 85 && declaredUnknowns.Count == 0
            ? "ready-for-launch-gate"
            : readiness >= 55
                ? "conditional"
                : "insufficient-evidence";

        return JsonSerializer.Serialize(new
        {
            schema = "nova.commerce.product-passport.v1",
            generatedAt = DateTimeOffset.UtcNow,
            status,
            readinessScore = readiness,
            identity = new
            {
                productName,
                sku = Text(input, "sku", 120),
                category,
                brand = Text(input, "brand", 120),
                sourceCountry
            },
            commercial = new { targetMarket, platform, currency, salePrice, unitProductCost = productCost },
            factRegistry = new { confirmedFacts, assumptions, unknowns = declaredUnknowns.Distinct().ToArray() },
            sourceRefs = sources,
            nextQuestions = declaredUnknowns.Distinct().Select(value => $"请确认：{value}").ToArray(),
            rules = new[]
            {
                "未进入 confirmedFacts 的规格不得用于广告承诺",
                "价格、政策、库存和平台规则属于易变事实，使用前必须刷新",
                "成本未确认时不得输出确定性盈利结论"
            }
        }, JsonOptions);
    }

    private static string CalculateLandedProfit(JsonObject input)
    {
        var currency = RequireText(input, "currency", 12).ToUpperInvariant();
        var salePrice = Money(input, "sale_price", requiredPositive: true);
        var productCost = Money(input, "unit_product_cost");
        var domesticShipping = Money(input, "domestic_shipping");
        var internationalShipping = Money(input, "international_shipping");
        var packaging = Money(input, "packaging");
        var otherVariable = Money(input, "other_variable_cost");
        var returnHandling = Money(input, "return_handling_cost");

        var dutyRate = Rate(input, "duty_rate_pct");
        var importTaxRate = Rate(input, "import_tax_rate_pct");
        var platformRate = Rate(input, "platform_fee_rate_pct");
        var paymentRate = Rate(input, "payment_fee_rate_pct");
        var affiliateRate = Rate(input, "affiliate_rate_pct");
        var adRate = Rate(input, "ad_cost_rate_pct");
        var returnRate = Rate(input, "return_rate_pct");
        var returnLossRate = Rate(input, "return_loss_rate_pct");

        var customsValue = productCost + domesticShipping + internationalShipping + packaging;
        var duty = customsValue * dutyRate;
        var importTax = (customsValue + duty) * importTaxRate;
        var landedInventoryCost = customsValue + duty + importTax;
        var expectedNetRevenue = salePrice * (1 - returnRate);
        var recoveredInventory = landedInventoryCost * returnRate * (1 - returnLossRate);
        var effectiveInventoryCost = landedInventoryCost - recoveredInventory;
        var platformFee = expectedNetRevenue * platformRate;
        var paymentFee = expectedNetRevenue * paymentRate;
        var affiliateFee = expectedNetRevenue * affiliateRate;
        var advertising = expectedNetRevenue * adRate;
        var expectedReturnHandling = returnHandling * returnRate;
        var contributionBeforeAds = expectedNetRevenue - effectiveInventoryCost - platformFee
                                    - paymentFee - affiliateFee - expectedReturnHandling - otherVariable;
        var contributionProfit = contributionBeforeAds - advertising;
        var contributionMargin = expectedNetRevenue > 0
            ? contributionProfit / expectedNetRevenue
            : 0;
        var breakEvenAdRate = expectedNetRevenue > 0
            ? Math.Max(0, contributionBeforeAds / expectedNetRevenue)
            : 0;
        var breakEvenRoas = breakEvenAdRate > 0 ? 1 / breakEvenAdRate : 0;

        var warnings = new List<string>();
        if (productCost <= 0) warnings.Add("单位商品成本为 0，不能作为可靠利润结论。");
        if (internationalShipping <= 0) warnings.Add("国际/尾程物流为 0，请确认是否遗漏。");
        if (platformRate <= 0) warnings.Add("平台费率为 0，请确认目标平台费用结构。");
        if (returnRate <= 0) warnings.Add("退货率为 0，仅适合作为乐观情景。");
        if (contributionProfit < 0) warnings.Add("当前情景为负贡献利润，应调整价格、成本或投放后再进入市场。");

        return JsonSerializer.Serialize(new
        {
            schema = "nova.commerce.landed-profit.v1",
            currency,
            decision = warnings.Count > 0 || contributionMargin < 0.08m
                ? contributionProfit > 0 ? "conditional" : "no-go"
                : "go",
            inputs = new
            {
                salePrice,
                unitProductCost = productCost,
                domesticShipping,
                internationalShipping,
                packaging,
                ratesPct = new
                {
                    duty = Percent(dutyRate), importTax = Percent(importTaxRate),
                    platform = Percent(platformRate), payment = Percent(paymentRate),
                    affiliate = Percent(affiliateRate), advertising = Percent(adRate),
                    returns = Percent(returnRate), returnedInventoryLoss = Percent(returnLossRate)
                }
            },
            lineItems = new
            {
                expectedNetRevenue = Round(expectedNetRevenue),
                customsValue = Round(customsValue),
                duty = Round(duty),
                importTax = Round(importTax),
                landedInventoryCost = Round(landedInventoryCost),
                recoveredInventoryCredit = Round(recoveredInventory),
                effectiveInventoryCost = Round(effectiveInventoryCost),
                platformFee = Round(platformFee),
                paymentFee = Round(paymentFee),
                affiliateFee = Round(affiliateFee),
                advertising = Round(advertising),
                expectedReturnHandling = Round(expectedReturnHandling),
                otherVariableCost = Round(otherVariable)
            },
            outcome = new
            {
                contributionBeforeAds = Round(contributionBeforeAds),
                contributionProfit = Round(contributionProfit),
                contributionMarginPct = Percent(contributionMargin),
                breakEvenAdRatePct = Percent(breakEvenAdRate),
                breakEvenRoas = Round(breakEvenRoas)
            },
            warnings,
            formulaNotes = new[]
            {
                "expectedNetRevenue = salePrice × (1 - returnRate)",
                "duty = customsValue × dutyRate; importTax = (customsValue + duty) × importTaxRate",
                "returned inventory is credited only for the recoverable share supplied by the user",
                "platform, payment, affiliate and ad rates are applied to expected net revenue"
            }
        }, JsonOptions);
    }

    private static string BuildEvidenceLedger(JsonObject input)
    {
        var asOf = ParseDate(RequireText(input, "as_of", 32), "as_of");
        var maxAgeDays = Math.Clamp(input["max_age_days"]?.GetValue<int>() ?? 30, 1, 3650);
        var claimNodes = input["claims"]?.AsArray()
                         ?? throw new InvalidOperationException("claims is required.");
        if (claimNodes.Count is < 1 or > 200)
        {
            throw new InvalidOperationException("claims must contain between 1 and 200 entries.");
        }

        var claims = new List<EvidenceClaim>();
        foreach (var node in claimNodes)
        {
            var claim = node?.AsObject() ?? throw new InvalidOperationException("Each claim must be an object.");
            var id = RequireText(claim, "id", 120);
            var observedText = Text(claim, "observed_at", 32);
            var sourceUrl = Text(claim, "source_url", 800);
            var evidenceType = Text(claim, "evidence_type", 40).ToLowerInvariant();
            var confidence = Math.Clamp(Number(claim, "confidence"), 0, 100);
            var issues = new List<string>();
            DateOnly? observedAt = null;
            if (DateOnly.TryParseExact(
                    observedText,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsed))
            {
                observedAt = parsed;
            }
            else
            {
                issues.Add("invalid-observed-date");
            }

            var directUserEvidence = sourceUrl.StartsWith("user://", StringComparison.OrdinalIgnoreCase)
                                     && evidenceType == "user-provided";
            var validPublicUrl = Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri)
                                 && uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
            if (!validPublicUrl && !directUserEvidence) issues.Add("missing-or-invalid-source");
            if (evidenceType == "assumption") issues.Add("assumption-not-evidence");
            if (confidence < 60) issues.Add("low-confidence");
            if (observedAt is not null && asOf.DayNumber - observedAt.Value.DayNumber > maxAgeDays)
            {
                issues.Add("stale");
            }
            if (observedAt is not null && observedAt.Value > asOf) issues.Add("future-observation-date");

            claims.Add(new EvidenceClaim(
                id,
                RequireText(claim, "statement", 1000),
                Text(claim, "value", 1000),
                sourceUrl,
                Text(claim, "source_title", 300),
                observedText,
                evidenceType,
                confidence,
                Text(claim, "market", 200),
                issues));
        }

        var conflictIds = claims
            .GroupBy(claim => claim.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(claim => claim.Value.Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Skip(1)
                .Any())
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var claim in claims.Where(claim => conflictIds.Contains(claim.Id)))
        {
            claim.Issues.Add("conflicting-values");
        }

        var verified = claims.Count(claim => claim.Issues.Count == 0);
        var stale = claims.Count(claim => claim.Issues.Contains("stale"));
        var unsourced = claims.Count(claim => claim.Issues.Contains("missing-or-invalid-source"));
        var assumptions = claims.Count(claim => claim.Issues.Contains("assumption-not-evidence"));
        var score = (int)Math.Round(verified * 100m / claims.Count);

        return JsonSerializer.Serialize(new
        {
            schema = "nova.commerce.evidence-ledger.v1",
            asOf = asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            maxAgeDays,
            summary = new
            {
                total = claims.Count,
                verified,
                stale,
                unsourced,
                assumptions,
                conflicts = conflictIds.Count,
                evidenceHealthScore = score,
                launchGateReady = verified > 0 && unsourced == 0 && conflictIds.Count == 0 && score >= 70
            },
            claims = claims.Select(claim => new
            {
                claim.Id,
                claim.Statement,
                claim.Value,
                sourceUrl = claim.SourceUrl,
                sourceTitle = claim.SourceTitle,
                observedAt = claim.ObservedAt,
                evidenceType = claim.EvidenceType,
                claim.Confidence,
                claim.Market,
                status = claim.Issues.Count == 0 ? "verified" : "needs-review",
                issues = claim.Issues
            }),
            conflicts = conflictIds.OrderBy(value => value).ToArray(),
            rules = new[]
            {
                "搜索结果数量、评论数量和页面排序不得直接解释为销量或市场份额",
                "易变价格、政策、库存和平台规则超过 freshness window 后必须重新采集",
                "冲突证据必须保留双方来源，不能静默选择对结论有利的一方"
            }
        }, JsonOptions);
    }

    private static string AssessMarketDemand(JsonObject input)
    {
        var productName = RequireText(input, "product_name", 160);
        var targetMarket = RequireText(input, "target_market", 120);
        var platform = RequireText(input, "platform", 120);
        var asOf = ParseDate(RequireText(input, "as_of", 32), "as_of");
        var identityConfidence = Score(input, "identity_confidence");
        var signalNodes = input["signals"]?.AsArray()
                          ?? throw new InvalidOperationException("signals is required.");
        if (signalNodes.Count is < 1 or > 12)
        {
            throw new InvalidOperationException("signals must contain between 1 and 12 entries.");
        }

        var supported = DemandDimensions.ToDictionary(
            item => item.Key,
            StringComparer.OrdinalIgnoreCase);
        var provided = new Dictionary<string, DemandSignal>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in signalNodes)
        {
            var signal = node?.AsObject()
                         ?? throw new InvalidOperationException("Each demand signal must be an object.");
            var dimension = RequireText(signal, "dimension", 80).ToLowerInvariant();
            if (!supported.ContainsKey(dimension))
            {
                throw new InvalidOperationException($"Unsupported demand dimension: {dimension}.");
            }
            if (provided.ContainsKey(dimension))
            {
                throw new InvalidOperationException($"Demand dimension {dimension} was supplied more than once.");
            }
            var evidenceStatus = RequireText(signal, "evidence_status", 32).ToLowerInvariant();
            if (evidenceStatus is not ("verified" or "indicative" or "assumption" or "unknown"))
            {
                throw new InvalidOperationException(
                    $"evidence_status for {dimension} must be verified, indicative, assumption, or unknown.");
            }
            provided[dimension] = new DemandSignal(
                dimension,
                Score(signal, "score"),
                Score(signal, "confidence"),
                evidenceStatus,
                Text(signal, "rationale", 1200),
                Strings(signal, "source_refs", 20));
        }

        decimal rawWeighted = 0;
        decimal evidenceWeighted = 0;
        decimal coveredWeight = 0;
        var dimensions = new List<object>();
        var blockers = new List<string>();
        var severeRisk = false;
        foreach (var spec in DemandDimensions)
        {
            provided.TryGetValue(spec.Key, out var signal);
            var rawScore = signal?.Score ?? 50m;
            var directionalScore = spec.IsRisk ? 100m - rawScore : rawScore;
            var statusFactor = signal?.EvidenceStatus switch
            {
                "verified" => 1m,
                "indicative" => 0.72m,
                "assumption" => 0.35m,
                _ => 0m
            };
            var confidenceFactor = (signal?.Confidence ?? 0m) / 100m;
            var hasTraceableEvidence = signal is not null
                                       && signal.SourceRefs.Count > 0
                                       && signal.EvidenceStatus is "verified" or "indicative";
            rawWeighted += directionalScore * spec.Weight / 100m;
            evidenceWeighted += spec.Weight * statusFactor * confidenceFactor;
            if (hasTraceableEvidence) coveredWeight += spec.Weight;

            if (spec.IsRisk
                && signal is not null
                && signal.Score >= 75
                && signal.EvidenceStatus is "verified" or "indicative")
            {
                blockers.Add($"{spec.Label} {Round(signal.Score)}/100：{signal.Rationale}");
                severeRisk |= signal.Score >= 85;
            }

            dimensions.Add(new
            {
                key = spec.Key,
                label = spec.Label,
                polarity = spec.IsRisk ? "risk" : "opportunity",
                weight = spec.Weight,
                score = signal is null ? (decimal?)null : Round(signal.Score),
                confidence = signal is null ? (decimal?)null : Round(signal.Confidence),
                evidenceStatus = signal?.EvidenceStatus ?? "missing",
                rationale = signal?.Rationale ?? "本轮尚未提供该维度的判断或证据。",
                sourceRefs = signal?.SourceRefs ?? [],
                directionalScore = signal is null ? (decimal?)null : Round(directionalScore)
            });
        }

        var evidenceQuality = Round(evidenceWeighted);
        var evidenceCoverage = Round(coveredWeight);
        var uncertaintyPenalty = Round(
            (100m - evidenceQuality) * 0.18m
            + (100m - evidenceCoverage) * 0.12m
            + Math.Max(0m, 70m - identityConfidence) * 0.20m);
        var demandFit = Math.Clamp(Round(rawWeighted - uncertaintyPenalty), 0m, 100m);
        var recommendation = identityConfidence < 55m
            ? "IDENTIFY-FIRST"
            : evidenceCoverage < 40m || evidenceQuality < 35m
                ? "VALIDATE-FIRST"
                : severeRisk
                    ? "NO-GO-FOR-NOW"
                    : demandFit >= 72m && blockers.Count == 0
                        ? "GO-TO-TEST"
                        : demandFit >= 52m
                            ? "CONDITIONAL-TEST"
                            : "NO-GO-FOR-NOW";

        var nextValidations = DemandDimensions
            .Where(spec => !provided.TryGetValue(spec.Key, out var signal)
                           || signal.EvidenceStatus is "unknown" or "assumption"
                           || signal.Confidence < 60
                           || signal.SourceRefs.Count == 0)
            .OrderByDescending(spec => spec.Weight)
            .Take(5)
            .Select(spec => new
            {
                dimension = spec.Key,
                label = spec.Label,
                action = DemandValidationAction(spec.Key),
                successSignal = DemandValidationSignal(spec.Key)
            })
            .ToArray();

        return JsonSerializer.Serialize(new
        {
            schema = "nova.commerce.market-demand-fit.v1",
            generatedAt = DateTimeOffset.UtcNow,
            asOf = asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            scope = new { productName, targetMarket, platform, identityConfidence = Round(identityConfidence) },
            recommendation,
            summary = new
            {
                demandFitScore = demandFit,
                evidenceCoveragePct = evidenceCoverage,
                evidenceQualityScore = evidenceQuality,
                uncertaintyPenalty,
                riskBlockerCount = blockers.Count,
                interpretation = recommendation switch
                {
                    "IDENTIFY-FIRST" => "先确认商品身份，当前不适合做确定性市场判断。",
                    "VALIDATE-FIRST" => "存在初步假设，但证据覆盖或质量不足，应先补证。",
                    "GO-TO-TEST" => "现有证据支持进入受控市场测试，不代表已经证明规模化需求。",
                    "CONDITIONAL-TEST" => "存在机会也存在明显不确定性，只适合限额验证。",
                    _ => "当前证据显示需求适配偏弱或风险过高，暂不建议投入。"
                }
            },
            dimensions,
            blockers,
            nextValidations,
            reasoningBoundary = new[]
            {
                "该结果是证据加权的需求适配判断，不是销量、GMV、转化率或市场份额预测",
                "财务可行性必须使用 landed-profit 工具单独评估，再与本结果综合",
                "assumption 和 unknown 会降低证据质量；缺失维度按中性分计入并施加不确定性惩罚",
                "搜索量、评论数、榜单和页面排序只能作为线索，不能单独证明真实购买需求"
            }
        }, JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static JsonObject Function(string name, string description, JsonObject parameters)
        => new()
        {
            ["type"] = "function",
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = parameters,
            ["strict"] = true
        };

    private static JsonObject StringProperty(string description)
        => new() { ["type"] = "string", ["description"] = description };

    private static JsonObject NumberProperty(string description)
        => new() { ["type"] = "number", ["description"] = description };

    private static JsonObject IntegerProperty(string description)
        => new() { ["type"] = "integer", ["description"] = description };

    private static readonly DemandDimensionSpec[] DemandDimensions =
    [
        new("problem-urgency", "消费者问题强度", 12m, false),
        new("audience-reach", "潜在人群范围", 8m, false),
        new("usage-frequency", "使用频率", 8m, false),
        new("market-activity", "市场活动信号", 10m, false),
        new("competition-headroom", "竞争空间", 8m, false),
        new("differentiation", "商品差异化", 10m, false),
        new("content-demonstrability", "内容可演示性", 7m, false),
        new("local-fit", "本地场景适配", 8m, false),
        new("trust-barrier", "信任门槛", 7m, true),
        new("compliance-risk", "合规风险", 7m, true),
        new("return-risk", "退货与售后风险", 7m, true),
        new("seasonality-resilience", "季节稳定性", 8m, false)
    ];

    private static JsonObject StringArray(string description)
        => new()
        {
            ["type"] = "array",
            ["description"] = description,
            ["items"] = new JsonObject { ["type"] = "string" }
        };

    private static string RequireText(JsonObject source, string property, int max)
    {
        var value = Text(source, property, max);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{property} is required.")
            : value;
    }

    private static string Text(JsonObject source, string property, int max)
    {
        var value = source[property]?.GetValue<string>()?.Trim() ?? string.Empty;
        return value.Length <= max ? value : value[..max];
    }

    private static IReadOnlyList<string> Strings(JsonObject source, string property, int maxItems)
        => source[property]?.AsArray()
            .Take(maxItems)
            .Select(item => item?.GetValue<string>()?.Trim() ?? string.Empty)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

    private static decimal Number(JsonObject source, string property)
    {
        if (source[property] is not JsonValue value)
        {
            return 0;
        }
        if (value.TryGetValue<decimal>(out var decimalValue)) return decimalValue;
        if (value.TryGetValue<int>(out var intValue)) return intValue;
        if (value.TryGetValue<long>(out var longValue)) return longValue;
        if (value.TryGetValue<double>(out var doubleValue))
            return Convert.ToDecimal(doubleValue, CultureInfo.InvariantCulture);
        throw new InvalidOperationException($"{property} must be numeric.");
    }

    private static decimal Money(JsonObject source, string property, bool requiredPositive = false)
    {
        var value = Number(source, property);
        if (value < 0 || requiredPositive && value <= 0)
        {
            throw new InvalidOperationException(
                requiredPositive ? $"{property} must be greater than zero." : $"{property} cannot be negative.");
        }
        return value;
    }

    private static decimal Rate(JsonObject source, string property)
    {
        var percentage = Number(source, property);
        if (percentage is < 0 or > 100)
        {
            throw new InvalidOperationException($"{property} must be between 0 and 100.");
        }
        return percentage / 100m;
    }

    private static decimal Score(JsonObject source, string property)
    {
        var value = Number(source, property);
        if (value is < 0 or > 100)
        {
            throw new InvalidOperationException($"{property} must be between 0 and 100.");
        }
        return value;
    }

    private static string DemandValidationAction(string dimension)
        => dimension switch
        {
            "problem-urgency" => "从目标市场评论、问答和访谈中收集消费者反复描述的问题，并记录原话与频次。",
            "audience-reach" => "定义三个可触达的人群切片，估算每类是否真实存在同一任务而不是泛人群标签。",
            "usage-frequency" => "用日记研究、评论或访谈确认真实使用周期，以及现有替代方案是否已经足够。",
            "market-activity" => "交叉检查搜索趋势、平台在售供给、近期开箱/测评内容和评论新鲜度，不把单一指标当销量。",
            "competition-headroom" => "抽样头部、腰部和低价竞品的评价，寻找持续出现但尚未解决的抱怨。",
            "differentiation" => "做盲测或并排演示，验证用户能否在不看品牌文案时说出可感知差异。",
            "content-demonstrability" => "制作三条不同钩子的低成本素材，比较前段留存、有效咨询与落地页行为。",
            "local-fit" => "请目标市场用户或本地运营审查使用场景、语言、尺寸、插头/标准和文化误读。",
            "trust-barrier" => "测试用户在购买前最需要的证明：评价、演示、质保、认证、退换或品牌背书。",
            "compliance-risk" => "核对目标市场类目准入、标签、认证、知识产权和广告承诺边界。",
            "return-risk" => "收集同类商品差评与退货原因，完成包装、易损、误用和售后情景检查。",
            "seasonality-resilience" => "对照至少一年的趋势或类目周期，区分常态需求、节庆需求和短期热点。",
            _ => "补充可以支持或推翻该维度判断的一手或可追溯证据。"
        };

    private static string DemandValidationSignal(string dimension)
        => dimension switch
        {
            "problem-urgency" => "不同来源中出现一致、具体且主动寻求解决的问题表达。",
            "audience-reach" => "至少一个清晰人群能被渠道定向触达，并确认存在共同任务。",
            "usage-frequency" => "用户能回忆近期真实使用或替代行为，而不只是表达泛泛兴趣。",
            "market-activity" => "多个独立来源同时显示新鲜、持续的类目活动。",
            "competition-headroom" => "高频未满足需求可被本商品真实能力覆盖，且不是只靠低价。",
            "differentiation" => "目标用户能稳定感知差异，并愿意因此点击、询价或选择。",
            "content-demonstrability" => "素材带来有效停留、商品页深度行为或购买意向，而非只有播放量。",
            "local-fit" => "本地用户理解用途、认可场景且未发现明显制式或文化冲突。",
            "trust-barrier" => "补充关键证明后，用户的主要购买异议明显下降。",
            "compliance-risk" => "准入、标签、认证和宣传要求有可核验结论且无阻断项。",
            "return-risk" => "样品、包装与说明测试覆盖主要退货原因，剩余风险在可接受阈值内。",
            "seasonality-resilience" => "需求在非热点窗口仍存在，或季节窗口与库存计划明确匹配。",
            _ => "获得能够改变当前分数的可追溯信号。"
        };

    private static DateOnly ParseDate(string value, string property)
        => DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var date)
            ? date
            : throw new InvalidOperationException($"{property} must use YYYY-MM-DD format.");

    private static void AddUnknown(ICollection<string> unknowns, bool condition, string value)
    {
        if (condition && !unknowns.Contains(value, StringComparer.OrdinalIgnoreCase)) unknowns.Add(value);
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
    private static decimal Percent(decimal ratio) => Math.Round(ratio * 100m, 2, MidpointRounding.AwayFromZero);

    private sealed record EvidenceClaim(
        string Id,
        string Statement,
        string Value,
        string SourceUrl,
        string SourceTitle,
        string ObservedAt,
        string EvidenceType,
        decimal Confidence,
        string Market,
        List<string> Issues);

    private sealed record DemandDimensionSpec(
        string Key,
        string Label,
        decimal Weight,
        bool IsRisk);

    private sealed record DemandSignal(
        string Dimension,
        decimal Score,
        decimal Confidence,
        string EvidenceStatus,
        string Rationale,
        IReadOnlyList<string> SourceRefs);
}
