namespace ParcelHistoryExplorer.Core;

public sealed class SearchInputAssistant
{
    public async Task<SearchInputPreparationResult> PrepareAsync(
        string rootPath,
        IReadOnlyList<string> filePatterns,
        bool includeSubdirectories,
        string rawInput,
        CancellationToken cancellationToken = default)
    {
        var normalized = IdentifierExtractor.NormalizeSearchInput(rawInput);
        var expandedTokens = IdentifierExtractor.ExpandSearchTokens(rawInput);
        SearchTraceLog.Info(
            "Prepare",
            $"Input='{rawInput}', Normalized='{normalized}', Expanded=[{string.Join(", ", expandedTokens)}], Root='{rootPath}', IncludeSubdirectories={includeSubdirectories}.");

        if (normalized.Length < 7 && !expandedTokens.Any(IsSpecialFlowToken))
        {
            SearchTraceLog.Info("Prepare", "Rejected input because normalized length is below 7.");
            return SearchInputPreparationResult.Invalid("Enter at least 7 meaningful characters for parcel, order, or tracking search.");
        }

        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            SearchTraceLog.Info("Prepare", "Rejected input because log root does not exist.");
            return SearchInputPreparationResult.Invalid("The selected log folder does not exist.");
        }

        if (expandedTokens.Any(IsSpecialFlowToken))
        {
            SearchTraceLog.Info("Prepare", $"Resolved special flow token '{normalized}'.");
            return SearchInputPreparationResult.Resolved(normalized);
        }

        if (expandedTokens.Any(IsExactParcelOrOrderId) || expandedTokens.Any(LooksLikeExactTrackingId))
        {
            SearchTraceLog.Info("Prepare", "Resolved immediately as exact identifier input.");
            return SearchInputPreparationResult.Resolved(normalized);
        }

        var index = await ParcelIdentityIndex.TryLoadAvailableAsync(rootPath, filePatterns, includeSubdirectories, cancellationToken).ConfigureAwait(false);
        if (index is null)
        {
            SearchTraceLog.Info("Prepare", "Identity index is still warming; continuing without indexed candidate resolution.");
            return SearchInputPreparationResult.Resolved(normalized);
        }

        var exactIdentity = index.Resolve(new HashSet<string>(expandedTokens, StringComparer.OrdinalIgnoreCase));
        if (exactIdentity is not null)
        {
            SearchTraceLog.Info(
                "Prepare",
                $"Resolved via identity index. RelatedTokens={exactIdentity.RelatedTokens.Count}, Files={exactIdentity.Files.Count}, Parcel='{exactIdentity.PreferredParcelId}', Order='{exactIdentity.PreferredOrderId}', Tracking='{exactIdentity.PreferredTrackingId}'.");
            return SearchInputPreparationResult.Resolved(normalized);
        }

        if (LooksLikeTrackingFragment(normalized))
        {
            var trackingCandidates = index.FindCandidatesContaining(normalized, maxCandidates: 25, SearchCandidateKind.TrackingId);
            if (trackingCandidates.Count == 1)
            {
                SearchTraceLog.Info("Prepare", $"Resolved unique tracking fragment candidate '{trackingCandidates[0].Token}'.");
                return SearchInputPreparationResult.Resolved(trackingCandidates[0].Token);
            }

            if (trackingCandidates.Count > 1)
            {
                SearchTraceLog.Info("Prepare", $"Tracking fragment is ambiguous with {trackingCandidates.Count} candidates.");
                return SearchInputPreparationResult.Ambiguous(normalized, trackingCandidates);
            }

            SearchTraceLog.Info("Prepare", "Tracking fragment had no indexed candidates; keeping normalized input.");
            return SearchInputPreparationResult.Resolved(normalized);
        }

        var candidates = index.FindCandidatesContaining(normalized, maxCandidates: 25);
        if (candidates.Count == 1)
        {
            SearchTraceLog.Info("Prepare", $"Resolved unique generic candidate '{candidates[0].Token}'.");
            return SearchInputPreparationResult.Resolved(candidates[0].Token);
        }

        if (candidates.Count > 1)
        {
            SearchTraceLog.Info("Prepare", $"Generic input is ambiguous with {candidates.Count} candidates.");
            return SearchInputPreparationResult.Ambiguous(normalized, candidates);
        }

        SearchTraceLog.Info("Prepare", "Input rejected after index lookup returned no candidates.");
        return SearchInputPreparationResult.Invalid("The input is not a valid parcel/order/tracking identifier, and no matching candidates were found.");
    }

    private static bool IsExactParcelOrOrderId(string normalized) =>
        normalized.Length == 10 && normalized.All(char.IsDigit);

    private static bool LooksLikeExactTrackingId(string normalized)
    {
        if (IdentifierExtractor.LooksLikeRawTrackingIdentifier(normalized))
        {
            return true;
        }

        if (normalized.Length <= 10)
        {
            return false;
        }

        if (normalized.All(char.IsDigit))
        {
            return normalized.Length >= 22;
        }

        return normalized.Length >= 14 && normalized.Any(char.IsLetter) && normalized.Any(char.IsDigit);
    }

    private static bool LooksLikeTrackingFragment(string normalized)
    {
        if (normalized.Contains('%') || normalized.Contains('/') || normalized.Contains('-') || normalized.Contains('_') || normalized.Contains('.'))
        {
            return normalized.Length > 7 && normalized.Any(char.IsDigit);
        }

        if (normalized.Length <= 10)
        {
            return false;
        }

        if (normalized.All(char.IsDigit))
        {
            return normalized.Length < 22;
        }

        return normalized.Length > 10 && normalized.Length < 14 && normalized.Any(char.IsLetter) && normalized.Any(char.IsDigit);
    }

    private static bool IsSpecialFlowToken(string normalized) =>
        normalized is "NOREAD" or "NOVALIDTRACKINGIDPROVIDED";
}

public sealed record SearchInputPreparationResult(
    bool IsValid,
    string? ErrorMessage,
    string? ResolvedSearchTerm,
    string NormalizedInput,
    IReadOnlyList<SearchInputCandidate> Candidates)
{
    public bool RequiresChoice => IsValid && string.IsNullOrWhiteSpace(ResolvedSearchTerm) && Candidates.Count > 1;

    public static SearchInputPreparationResult Invalid(string message) =>
        new(false, message, null, string.Empty, Array.Empty<SearchInputCandidate>());

    public static SearchInputPreparationResult Resolved(string resolvedSearchTerm) =>
        new(true, null, resolvedSearchTerm, resolvedSearchTerm, Array.Empty<SearchInputCandidate>());

    public static SearchInputPreparationResult Ambiguous(string normalizedInput, IReadOnlyList<SearchInputCandidate> candidates) =>
        new(true, null, null, normalizedInput, candidates);
}

public sealed record SearchInputCandidate(
    string Token,
    string Kind,
    string Description);
