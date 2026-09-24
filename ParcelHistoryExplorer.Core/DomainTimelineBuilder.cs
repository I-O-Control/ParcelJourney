using System.Text.RegularExpressions;

namespace ParcelHistoryExplorer.Core;

internal sealed class DomainTimelineBuilder
{
    private static readonly TimeSpan StageMergeWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PhaseMergeWindow = TimeSpan.FromSeconds(20);

    public IReadOnlyList<ParcelTimelineEvent> Build(
        IReadOnlyList<RawLogHit> rawHits,
        IReadOnlySet<string> normalizedSearchTerms,
        string primarySearchTerm,
        int maxResults)
    {
        var candidates = rawHits
            .Select(Classify)
            .Where(static item => item is not null)
            .Cast<DomainEventCandidate>()
            .Where(item => MatchesRequestedParcel(item, normalizedSearchTerms))
            .ToList();

        if (candidates.Count == 0)
        {
            candidates = rawHits
                .Where(hit => MatchesRequestedParcel(hit.Identifiers, normalizedSearchTerms))
                .Select(CreateFallbackEvent)
                .ToList();
        }

        var stageTimeline = candidates
            .OrderBy(static item => item.Timestamp)
            .GroupBy(CreateStageMergeKey)
            .SelectMany(MergeStageBuckets)
            .ToList();

        var terminalTimestamp = FindTerminalTimestamp(stageTimeline);
        if (terminalTimestamp is not null)
        {
            stageTimeline = stageTimeline
                .Where(item => item.Timestamp <= terminalTimestamp.Value)
                .ToList();
        }

        stageTimeline.AddRange(CreateBoundaryEvents(rawHits, stageTimeline, normalizedSearchTerms, primarySearchTerm, terminalTimestamp));

        return BuildProcessTimeline(stageTimeline)
            .OrderBy(static item => item.Timestamp)
            .ThenBy(static item => GetPhaseOrder(item.Phase))
            .ThenBy(static item => GetStageOrder(item.Stage))
            .Take(maxResults)
            .ToArray();
    }

    private static IReadOnlyList<ParcelTimelineEvent> BuildProcessTimeline(IReadOnlyList<ParcelTimelineEvent> stageTimeline)
    {
        var result = new List<ParcelTimelineEvent>();
        PhaseBucket? bucket = null;

        foreach (var item in stageTimeline.OrderBy(static item => item.Timestamp).ThenBy(static item => GetStageOrder(item.Stage)))
        {
            var phase = MapPhase(item);
            if (bucket is null || !CanMergeIntoPhase(bucket, item, phase))
            {
                if (bucket is not null)
                {
                    result.Add(CreatePhaseEvent(bucket));
                }

                bucket = new PhaseBucket(phase);
            }

            bucket.Events.Add(item);
        }

        if (bucket is not null)
        {
            result.Add(CreatePhaseEvent(bucket));
        }

        return result;
    }

    private static bool CanMergeIntoPhase(PhaseBucket bucket, ParcelTimelineEvent item, string phase)
    {
        if (!string.Equals(bucket.Phase, phase, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var last = bucket.Events[^1];
        if (item.Timestamp - last.Timestamp > PhaseMergeWindow)
        {
            return false;
        }

        return SameIdentity(bucket.Events, item);
    }

    private static bool SameIdentity(IReadOnlyList<ParcelTimelineEvent> existing, ParcelTimelineEvent next)
    {
        var parcelId = LatestValue(existing, static item => item.ParcelId);
        var orderId = LatestValue(existing, static item => item.OrderId);
        var trackingId = LatestValue(existing, static item => item.TrackingId);

        return IdentityMatches(parcelId, next.ParcelId)
            || IdentityMatches(orderId, next.OrderId)
            || IdentityMatches(trackingId, next.TrackingId)
            || existing.SelectMany(static item => item.Identifiers).Intersect(next.Identifiers, StringComparer.OrdinalIgnoreCase).Any();
    }

    private static bool IdentityMatches(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(IdentifierExtractor.Normalize(left), IdentifierExtractor.Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static ParcelTimelineEvent CreatePhaseEvent(PhaseBucket bucket)
    {
        var ordered = bucket.Events
            .OrderBy(static item => item.Timestamp)
            .ThenBy(static item => GetStageOrder(item.Stage))
            .ToList();

        var representative = ordered
            .OrderByDescending(static item => GetSeverity(item.Level))
            .ThenByDescending(static item => item.OccurrenceCount)
            .ThenByDescending(static item => GetStageOrder(item.Stage))
            .ThenByDescending(static item => item.Timestamp)
            .First();

        var parcelId = LatestValue(ordered, static item => item.ParcelId);
        var orderId = LatestValue(ordered, static item => item.OrderId);
        var trackingId = LatestValue(ordered, static item => item.TrackingId);
        var scannerName = LatestValue(ordered, static item => item.ScannerName);
        var scannerId = LatestValue(ordered, static item => item.ScannerId);
        var target = LatestValue(ordered, static item => item.Target);
        var stageFlow = string.Join(" -> ", ordered.Select(static item => item.Stage).Distinct(StringComparer.OrdinalIgnoreCase));
        var stageCount = ordered.Count;
        var rawCount = ordered.Sum(static item => item.OccurrenceCount);

        return new ParcelTimelineEvent(
            ordered.Min(static item => item.Timestamp),
            bucket.Phase,
            stageFlow,
            BuildPhaseSummary(bucket.Phase, stageFlow, parcelId, orderId, trackingId, scannerName, target, ordered),
            BuildPhaseDetails(bucket.Phase, stageFlow, rawCount, stageCount, ordered),
            representative.Level,
            representative.Source,
            representative.SourceFile,
            representative.LineNumber,
            parcelId,
            orderId,
            trackingId,
            scannerName,
            scannerId,
            target,
            ordered.SelectMany(static item => item.Identifiers).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            rawCount);
    }

    private static string BuildPhaseSummary(
        string phase,
        string stageFlow,
        string? parcelId,
        string? orderId,
        string? trackingId,
        string? scannerName,
        string? target,
        IReadOnlyList<ParcelTimelineEvent> ordered)
    {
        return phase switch
        {
            "Lifecycle" when stageFlow.Contains("First Appearance", StringComparison.OrdinalIgnoreCase)
                => "First appearance of the parcel in the MFR logs.",
            "Lifecycle" when stageFlow.Contains("OnPlcCloseAcknowledge", StringComparison.OrdinalIgnoreCase)
                => $"Parcel {parcelId ?? trackingId ?? "unknown"} was finalized and exited the system.",
            "Lifecycle" => "Last known appearance of the parcel in the MFR logs.",
            "Induction & Weighing" when stageFlow.Contains("ScanBruttoWaage", StringComparison.OrdinalIgnoreCase)
                => $"Parcel entered at {scannerName ?? "the induction scanner"} and was processed at ScanBruttoWaage.",
            "Induction & Weighing"
                => $"Parcel first conveyor scan was captured at {scannerName ?? "the induction scanner"}.",
            "Labeling" when stageFlow.Contains("SendFileTCP", StringComparison.OrdinalIgnoreCase)
                => $"Labeling started for parcel {parcelId ?? "unknown"} and the print file was sent.",
            "Labeling"
                => $"Parcel {parcelId ?? "unknown"} reached the labeler entry and a labeling task was prepared.",
            "Label Exit & Tracking" when !string.IsNullOrWhiteSpace(trackingId)
                => $"Label exit completed and tracking {trackingId} became associated with the parcel.",
            "Label Exit & Tracking"
                => $"Parcel {parcelId ?? "unknown"} left the labeler and was prepared for routing.",
            "Routing & Divert" when stageFlow.Contains("Ausschleuse Quittung", StringComparison.OrdinalIgnoreCase)
                => string.Equals(target, "AR", StringComparison.OrdinalIgnoreCase)
                    ? $"Parcel {parcelId ?? "unknown"} was acknowledged on the AR exit path."
                    : $"Parcel {parcelId ?? "unknown"} was acknowledged on target {target ?? "unknown"}.",
            "Routing & Divert" when stageFlow.Contains("OnPlcAcknowledge", StringComparison.OrdinalIgnoreCase)
                => string.Equals(target, "AR", StringComparison.OrdinalIgnoreCase)
                    ? $"PLC acknowledged the route for parcel {parcelId ?? "unknown"} toward the AR exit path."
                    : $"PLC acknowledged the route for parcel {parcelId ?? "unknown"} toward {target ?? "unknown"}.",
            "Routing & Divert"
                => string.Equals(target, "AR", StringComparison.OrdinalIgnoreCase)
                    ? $"Routing command assigned parcel {parcelId ?? "unknown"} to the AR exit path."
                    : $"Routing command was issued for parcel {parcelId ?? "unknown"} toward {target ?? "unknown"}.",
            "Parcel State"
                => $"Parcel state was written to the database{FormatSuffix(orderId, " for ZP ")}.",
            "External Reporting"
                => $"Parcel data was reported to LVS/FWMS{FormatSuffix(orderId, " for ZP ")}.",
            "Exception"
                => ordered.Last().Summary,
            _
                => ordered.Last().Summary
        };
    }

    private static string BuildPhaseDetails(
        string phase,
        string stageFlow,
        int rawCount,
        int stageCount,
        IReadOnlyList<ParcelTimelineEvent> ordered)
    {
        var detailParts = new List<string>
        {
            $"Stage flow: {stageFlow}.",
            $"Condensed {stageCount} timeline stages from {rawCount} raw log entries."
        };

        var saveToDb = ordered.LastOrDefault(static item => item.Stage.Contains("SaveToDB", StringComparison.OrdinalIgnoreCase));
        if (saveToDb is not null)
        {
            detailParts.Add(saveToDb.Details);
        }

        var weighing = ordered.LastOrDefault(static item =>
            item.Stage.Contains("ScanBruttoWaage", StringComparison.OrdinalIgnoreCase)
            || item.Stage.Contains("SendeWaagedatenAnsLVS", StringComparison.OrdinalIgnoreCase)
            || item.Stage.Contains("BroadcastWaageDaten", StringComparison.OrdinalIgnoreCase));
        if (weighing is not null && !string.Equals(weighing.Details, saveToDb?.Details, StringComparison.OrdinalIgnoreCase))
        {
            detailParts.Add(weighing.Details);
        }

        var route = ordered.LastOrDefault(static item =>
            item.Stage.Contains("SendTaskToPlc", StringComparison.OrdinalIgnoreCase)
            || item.Stage.Contains("OnPlcAcknowledge", StringComparison.OrdinalIgnoreCase)
            || item.Stage.Contains("OnPlcCloseAcknowledge", StringComparison.OrdinalIgnoreCase)
            || item.Stage.Contains("Ausschleuse Quittung", StringComparison.OrdinalIgnoreCase));
        if (route is not null && !string.Equals(route.Details, saveToDb?.Details, StringComparison.OrdinalIgnoreCase))
        {
            detailParts.Add(route.Details);
        }

        if (string.Equals(phase, "Lifecycle", StringComparison.OrdinalIgnoreCase))
        {
            detailParts.Clear();
            detailParts.Add(ordered.Last().Details);
        }

        return string.Join(" ", detailParts.Where(static part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string? FormatSuffix(string? value, string prefix) =>
        string.IsNullOrWhiteSpace(value) ? null : $"{prefix}{value}";

    private static IEnumerable<ParcelTimelineEvent> CreateBoundaryEvents(
        IReadOnlyList<RawLogHit> rawHits,
        IReadOnlyList<ParcelTimelineEvent> timeline,
        IReadOnlySet<string> normalizedSearchTerms,
        string primarySearchTerm,
        DateTime? terminalTimestamp)
    {
        var filteredRaw = rawHits
            .Where(hit => MatchesRequestedParcel(hit.Identifiers, normalizedSearchTerms))
            .OrderBy(static hit => hit.Timestamp)
            .ToList();

        if (filteredRaw.Count == 0)
        {
            yield break;
        }

        var knownParcel = LatestValue(timeline, static item => item.ParcelId);
        var knownOrder = LatestValue(timeline, static item => item.OrderId);
        var knownTracking = LatestValue(timeline, static item => item.TrackingId);

        var first = filteredRaw.First();
        yield return new ParcelTimelineEvent(
            first.Timestamp,
            "Lifecycle",
            "First Appearance",
            "First appearance in MFR logs",
            $"First parcel-related log line for search '{primarySearchTerm}' came from {SourceClassifier.GetSourceType(first.SourceFile)}: {first.Message}",
            first.Level,
            SourceClassifier.GetSourceType(first.SourceFile),
            first.SourceFile,
            first.LineNumber,
            knownParcel,
            knownOrder,
            knownTracking,
            null,
            null,
            null,
            first.Identifiers,
            1);

        var last = terminalTimestamp is not null
            ? filteredRaw.LastOrDefault(hit => hit.Timestamp <= terminalTimestamp.Value) ?? filteredRaw.Last()
            : filteredRaw.Last();
        var latestKnownState = timeline
            .Where(static item =>
                item.Stage.Contains("SaveToDB", StringComparison.OrdinalIgnoreCase)
                || item.Stage.Contains("OnPlcAcknowledge", StringComparison.OrdinalIgnoreCase)
                || item.Stage.Contains("OnPlcCloseAcknowledge", StringComparison.OrdinalIgnoreCase)
                || item.Stage.Contains("ScanEtiAusgang", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static item => item.Timestamp)
            .LastOrDefault();

        yield return new ParcelTimelineEvent(
            last.Timestamp,
            "Lifecycle",
            "Last Appearance",
            "Last appearance in MFR logs",
            latestKnownState is null
                ? $"Last parcel-related log line for search '{primarySearchTerm}' came from {SourceClassifier.GetSourceType(last.SourceFile)}: {last.Message}"
                : $"Latest known state: {latestKnownState.Summary}. Last parcel-related raw line came from {SourceClassifier.GetSourceType(last.SourceFile)}.",
            last.Level,
            SourceClassifier.GetSourceType(last.SourceFile),
            last.SourceFile,
            last.LineNumber,
            latestKnownState?.ParcelId ?? knownParcel,
            latestKnownState?.OrderId ?? knownOrder,
            latestKnownState?.TrackingId ?? knownTracking,
            latestKnownState?.ScannerName,
            latestKnownState?.ScannerId,
            latestKnownState?.Target,
            last.Identifiers,
            1);
    }

    private static DateTime? FindTerminalTimestamp(IReadOnlyList<ParcelTimelineEvent> stageTimeline)
    {
        var finalClose = stageTimeline
            .Where(static item => IsTerminalEvent(item))
            .OrderBy(static item => item.Timestamp)
            .LastOrDefault();

        return finalClose?.Timestamp;
    }

    private static bool IsTerminalEvent(ParcelTimelineEvent item)
    {
        if (item.Stage.Contains("OnPlcCloseAcknowledge", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!item.Stage.Contains("SaveToDB", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return item.Details.Contains("Status=CLOSED", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<ParcelTimelineEvent> MergeStageBuckets(IGrouping<string, DomainEventCandidate> group)
    {
        var ordered = group.OrderBy(static item => item.Timestamp).ToList();
        var bucket = new List<DomainEventCandidate>();

        foreach (var candidate in ordered)
        {
            if (bucket.Count == 0 || candidate.Timestamp - bucket[^1].Timestamp <= StageMergeWindow)
            {
                bucket.Add(candidate);
                continue;
            }

            yield return MergeStageBucket(bucket);
            bucket.Clear();
            bucket.Add(candidate);
        }

        if (bucket.Count > 0)
        {
            yield return MergeStageBucket(bucket);
        }
    }

    private static ParcelTimelineEvent MergeStageBucket(List<DomainEventCandidate> bucket)
    {
        var representative = bucket
            .OrderByDescending(static item => item.Priority)
            .ThenBy(static item => item.Timestamp)
            .First();

        var details = representative.Details;
        if (bucket.Count > 1)
        {
            var sources = bucket.Select(static item => item.SourceType).Distinct(StringComparer.OrdinalIgnoreCase);
            details = $"{details} Consolidated from {bucket.Count} technical log entries ({string.Join(", ", sources)}).".Trim();
        }

        return new ParcelTimelineEvent(
            bucket.Min(static item => item.Timestamp),
            "Technical",
            representative.Stage,
            representative.Summary,
            details,
            representative.Level,
            representative.SourceType,
            representative.SourceFile,
            representative.LineNumber,
            representative.ParcelId,
            representative.OrderId,
            representative.TrackingId,
            representative.ScannerName,
            representative.ScannerId,
            representative.Target,
            bucket.SelectMany(static item => item.Identifiers).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            bucket.Count);
    }

    private static string CreateStageMergeKey(DomainEventCandidate candidate) =>
        string.Join("|",
            candidate.Stage,
            candidate.ParcelId ?? string.Empty,
            candidate.OrderId ?? string.Empty,
            candidate.TrackingId ?? string.Empty,
            candidate.ScannerName ?? string.Empty,
            candidate.ScannerId ?? string.Empty,
            candidate.Target ?? string.Empty);

    private static string MapPhase(ParcelTimelineEvent item)
    {
        if (item.Stage is "First Appearance" or "Last Appearance")
        {
            return "Lifecycle";
        }

        if (item.Stage is "OnPlcCloseAcknowledge")
        {
            return "Lifecycle";
        }

        if (item.Stage is "Misroute Error")
        {
            return "Exception";
        }

        if (item.Stage is "Missing Dataset" or "Unknown Position Ack" or "NoRead" or "Nio Routing")
        {
            return "Exception";
        }

        if (item.Stage is "MFC->WMF")
        {
            return "External Reporting";
        }

        if (item.Stage is "SendTaskToPlc" or "OnPlcAcknowledge" or "Ausschleuse Quittung")
        {
            return "Routing & Divert";
        }

        if (item.Stage is "ScanEtiAusgang")
        {
            return "Label Exit & Tracking";
        }

        if (item.Stage is "ScanEtiEingang" or "SendTaskToProcEti" or "SendFileTCP")
        {
            return "Labeling";
        }

        if (item.Stage is "ScanBruttoWaage" or "SendeWaagedatenAnsLVS" or "BroadcastWaageDaten" or "OnScannerData" or "SPS->MFR Scanner Daten")
        {
            return "Induction & Weighing";
        }

        if (item.Stage is "SaveToDB")
        {
            return item.ScannerName switch
            {
                not null when item.ScannerName.StartsWith("SC_BW", StringComparison.OrdinalIgnoreCase) => "Induction & Weighing",
                not null when item.ScannerName.StartsWith("SC_ETIIN", StringComparison.OrdinalIgnoreCase) => "Labeling",
                not null when item.ScannerName.StartsWith("SC_ETIO", StringComparison.OrdinalIgnoreCase) => "Label Exit & Tracking",
                _ => "Parcel State"
            };
        }

        return "Parcel State";
    }

    private static bool MatchesRequestedParcel(DomainEventCandidate candidate, IReadOnlySet<string> normalizedSearchTerms)
    {
        if (MatchesRequestedParcel(candidate.Identifiers, normalizedSearchTerms))
        {
            return true;
        }

        return (candidate.ParcelId is not null && normalizedSearchTerms.Contains(IdentifierExtractor.Normalize(candidate.ParcelId)))
            || (candidate.OrderId is not null && normalizedSearchTerms.Contains(IdentifierExtractor.Normalize(candidate.OrderId)))
            || (candidate.TrackingId is not null && normalizedSearchTerms.Contains(IdentifierExtractor.Normalize(candidate.TrackingId)));
    }

    private static bool MatchesRequestedParcel(IReadOnlyList<string> identifiers, IReadOnlySet<string> normalizedSearchTerms) =>
        identifiers.Any(normalizedSearchTerms.Contains);

    private static DomainEventCandidate CreateFallbackEvent(RawLogHit hit) =>
        new(
            hit.Timestamp,
            "Technical",
            hit.Message,
            $"Raw log line from {SourceClassifier.GetSourceType(hit.SourceFile)}.",
            hit.Level,
            SourceClassifier.GetSourceType(hit.SourceFile),
            hit.SourceFile,
            hit.LineNumber,
            null,
            null,
            null,
            null,
            null,
            null,
            hit.Identifiers,
            1);

    private static DomainEventCandidate? Classify(RawLogHit hit)
    {
        var sourceType = SourceClassifier.GetSourceType(hit.SourceFile);

        if (TryParseMisroute(hit, sourceType, out var misroute))
        {
            return misroute;
        }

        if (TryParseOperationalException(hit, sourceType, out var operationalException))
        {
            return operationalException;
        }

        if (TryParseWeighing(hit, sourceType, out var weighing))
        {
            return weighing;
        }

        if (TryParseDatabaseUpdate(hit, sourceType, out var databaseUpdate))
        {
            return databaseUpdate;
        }

        if (TryParseScannerStage(hit, sourceType, out var scannerStage))
        {
            return scannerStage;
        }

        if (TryParseLabelRequest(hit, sourceType, out var labelRequest))
        {
            return labelRequest;
        }

        if (TryParseRouting(hit, sourceType, out var routing))
        {
            return routing;
        }

        if (TryParseDivertAck(hit, sourceType, out var divertAck))
        {
            return divertAck;
        }

        if (TryParseFwms(hit, sourceType, out var fwms))
        {
            return fwms;
        }

        return null;
    }

    private static bool TryParseScannerStage(RawLogHit hit, string sourceType, out DomainEventCandidate? candidate)
    {
        candidate = null;

        if (TryMatch(hit.Message, Patterns.ScanEtiEingang, out var etiIn))
        {
            var barcode = etiIn.Groups["barcode"].Value;
            var parcelId = etiIn.Groups["parcelId"].Value;
            var ids = MergeIdentifiers(hit.Identifiers, barcode, parcelId);
            var scannerName = InferScanner(ids, hit, "SC_ETIIN");
            candidate = new DomainEventCandidate(
                hit.Timestamp,
                "ScanEtiEingang",
                $"ScanEtiEingang at {scannerName}",
                $"Labeler entry scan for parcel {parcelId}.",
                hit.Level,
                sourceType,
                hit.SourceFile,
                hit.LineNumber,
                parcelId,
                ids.OrderId,
                ids.TrackingId,
                scannerName,
                null,
                null,
                ids.Identifiers,
                SourceClassifier.GetPriority(hit.SourceFile, "logic"));
            return true;
        }

        if (TryMatch(hit.Message, Patterns.ScanEtiAusgang, out var etiOut))
        {
            var scannerName = etiOut.Groups["scannerName"].Value;
            var parcelId = etiOut.Groups["parcelId"].Value;
            var target = etiOut.Groups["target"].Value.Trim();
            var ids = MergeIdentifiers(hit.Identifiers, parcelId);
            candidate = new DomainEventCandidate(
                hit.Timestamp,
                "ScanEtiAusgang",
                $"ScanEtiAusgang at {scannerName}",
                string.IsNullOrWhiteSpace(target)
                    ? $"Parcel {parcelId} left the labeler."
                    : $"Parcel {parcelId} left the labeler with target {target}.",
                hit.Level,
                sourceType,
                hit.SourceFile,
                hit.LineNumber,
                parcelId,
                ids.OrderId,
                ids.TrackingId,
                scannerName,
                null,
                string.IsNullOrWhiteSpace(target) ? null : target,
                ids.Identifiers,
                SourceClassifier.GetPriority(hit.SourceFile, "logic"));
            return true;
        }

        if (TryMatch(hit.Message, Patterns.ScannerDataReceived, out var match))
        {
            var scannerId = match.Groups["scannerId"].Value;
            var scannerName = match.Groups["scannerName"].Value;
            var barcode = match.Groups["barcode"].Value;
            var ids = MergeIdentifiers(hit.Identifiers, barcode);
            var normalizedBarcode = IdentifierExtractor.Normalize(barcode);

            if (string.Equals(normalizedBarcode, "NOREAD", StringComparison.OrdinalIgnoreCase))
            {
                candidate = new DomainEventCandidate(
                    hit.Timestamp,
                    "NoRead",
                    $"NoRead at {scannerName}",
                    $"Scanner {scannerId} produced a NoRead barcode.",
                    hit.Level,
                    sourceType,
                    hit.SourceFile,
                    hit.LineNumber,
                    ids.ParcelId,
                    ids.OrderId,
                    ids.TrackingId,
                    scannerName,
                    scannerId,
                    null,
                    ids.Identifiers,
                    SourceClassifier.GetPriority(hit.SourceFile, "error"));
                return true;
            }

            candidate = new DomainEventCandidate(
                hit.Timestamp,
                "OnScannerData",
                $"OnScannerData at {scannerName}",
                $"Scanner {scannerId} read barcode '{barcode.Trim()}'.",
                hit.Level,
                sourceType,
                hit.SourceFile,
                hit.LineNumber,
                ids.ParcelId,
                ids.OrderId,
                ids.TrackingId,
                scannerName,
                scannerId,
                null,
                ids.Identifiers,
                SourceClassifier.GetPriority(hit.SourceFile, "logic"));
            return true;
        }

        if (TryMatch(hit.Message, Patterns.TelegramScannerData, out match))
        {
            var scannerId = match.Groups["scannerId"].Value;
            var barcode = match.Groups["barcode"].Value;
            var ids = MergeIdentifiers(hit.Identifiers, barcode);
            candidate = new DomainEventCandidate(
                hit.Timestamp,
                "SPS->MFR Scanner Daten",
                $"PLC scanner telegram at {scannerId}",
                $"PLC telegram received barcode '{barcode.Trim()}'.",
                hit.Level,
                sourceType,
                hit.SourceFile,
                hit.LineNumber,
                ids.ParcelId,
                ids.OrderId,
                ids.TrackingId,
                SourceClassifier.GetScannerNameFromId(scannerId),
                scannerId,
                null,
                ids.Identifiers,
                SourceClassifier.GetPriority(hit.SourceFile, "plc"));
            return true;
        }

        return false;
    }

    private static bool TryParseWeighing(RawLogHit hit, string sourceType, out DomainEventCandidate? candidate)
    {
        candidate = null;

        if (TryMatch(hit.Message, Patterns.WeightScan, out var match))
        {
            var parcelId = match.Groups["parcelId"].Value;
            var barcode = match.Groups["barcode"].Value;
            var ids = MergeIdentifiers(hit.Identifiers, barcode, parcelId);
            candidate = new DomainEventCandidate(
                hit.Timestamp,
                "ScanBruttoWaage",
                $"ScanBruttoWaage for parcel {parcelId}",
                $"Weight scan accepted for barcode '{barcode.Trim()}'.",
                hit.Level,
                sourceType,
                hit.SourceFile,
                hit.LineNumber,
                parcelId,
                ids.OrderId,
                ids.TrackingId,
                null,
                null,
                null,
                ids.Identifiers,
                SourceClassifier.GetPriority(hit.SourceFile, "logic"));
            return true;
        }

        if (TryMatch(hit.Message, Patterns.WeightBroadcast, out match))
        {
            var parcelId = match.Groups["parcelId"].Value;
            var weight = match.Groups["actualWeight"].Value;
            var ids = MergeIdentifiers(hit.Identifiers, parcelId);
            candidate = new DomainEventCandidate(
                hit.Timestamp,
                "BroadcastWaageDaten",
                $"BroadcastWaageDaten for parcel {parcelId}",
                $"Actual weight {weight} g.",
                hit.Level,
                sourceType,
                hit.SourceFile,
                hit.LineNumber,
                parcelId,
                null,
                null,
                match.Groups["scannerName"].Value,
                null,
                null,
                ids.Identifiers,
                SourceClassifier.GetPriority(hit.SourceFile, "logic"));
            return true;
        }

        if (TryMatch(hit.Message, Patterns.WeightToFwms, out match))
        {
            var parcelId = match.Groups["parcelId"].Value;
            var weight = match.Groups["weight"].Value;
            var orderId = match.Groups["orderId"].Value;
            var ids = MergeIdentifiers(hit.Identifiers, parcelId, orderId);
            candidate = new DomainEventCandidate(
                hit.Timestamp,
                "SendeWaagedatenAnsLVS",
                $"SendeWaagedatenAnsLVS for parcel {parcelId}",
                $"Reported weight {weight} g with order {orderId}.",
                hit.Level,
                sourceType,
                hit.SourceFile,
                hit.LineNumber,
                parcelId,
                orderId,
                null,
                null,
                null,
                null,
                ids.Identifiers,
                SourceClassifier.GetPriority(hit.SourceFile, "logic"));
            return true;
        }

        return false;
    }

    private static bool TryParseLabelRequest(RawLogHit hit, string sourceType, out DomainEventCandidate? candidate)
    {
        candidate = null;

        if (TryMatch(hit.Message, Patterns.LabelRequest, out var match))
        {
            var scannerName = match.Groups["scannerName"].Value;
            var parcelId = match.Groups["parcelId"].Value;
            var ids = MergeIdentifiers(hit.Identifiers, parcelId);
            candidate = new DomainEventCandidate(
                hit.Timestamp,
                "SendTaskToProcEti",
                $"SendTaskToProcEti for parcel {parcelId}",
                $"Label request triggered at {scannerName}.",
                hit.Level,
                sourceType,
                hit.SourceFile,
                hit.LineNumber,
                parcelId,
                ids.OrderId,
                ids.TrackingId,
                scannerName,
                null,
                null,
                ids.Identifiers,
                SourceClassifier.GetPriority(hit.SourceFile, "logic"));
            return true;
        }

        if (TryMatch(hit.Message, Patterns.LabelFileSent, out match))
        {
            var zplName = match.Groups["fileName"].Value;
            var ids = MergeIdentifiers(hit.Identifiers, zplName);
            candidate = new DomainEventCandidate(
                hit.Timestamp,
                "SendFileTCP",
                "SendFileTCP to label printer",
                $"Printer file '{zplName}' transmitted.",
                hit.Level,
                sourceType,
                hit.SourceFile,
                hit.LineNumber,
                ids.ParcelId,
                ids.OrderId,
                ids.TrackingId,
                null,
                null,
                null,
                ids.Identifiers,
                SourceClassifier.GetPriority(hit.SourceFile, "eti"));
            return true;
        }

        if (TryMatch(hit.Message, Patterns.LabelExit, out match))
        {
            var parcelId = match.Groups["parcelId"].Value;
            var trackingId = match.Groups["trackingId"].Value;
            var ids = MergeIdentifiers(hit.Identifiers, parcelId, trackingId);
            candidate = new DomainEventCandidate(
                hit.Timestamp,
                "ScanEtiAusgang",
                $"ScanEtiAusgang for parcel {parcelId}",
                $"Top reading '{trackingId}' captured at labeler exit.",
                hit.Level,
                sourceType,
                hit.SourceFile,
                hit.LineNumber,
                parcelId,
                ids.OrderId,
                ids.TrackingId,
                null,
                null,
                null,
                ids.Identifiers,
                SourceClassifier.GetPriority(hit.SourceFile, "logic"));
            return true;
        }

        return false;
    }

    private static bool TryParseRouting(RawLogHit hit, string sourceType, out DomainEventCandidate? candidate)
    {
        candidate = null;

        if (TryMatch(hit.Message, Patterns.RouteRequest, out var match))
        {
            var scannerId = match.Groups["scannerId"].Value;
            var parcelId = match.Groups["parcelId"].Value;
            var target = match.Groups["target"].Value;
            var ids = MergeIdentifiers(hit.Identifiers, parcelId);
            candidate = new DomainEventCandidate(
                hit.Timestamp,
                "SendTaskToPlc",
                $"SendTaskToPlc for parcel {parcelId}",
                $"Routing command sent at scanner {scannerId}.",
                hit.Level,
                sourceType,
                hit.SourceFile,
                hit.LineNumber,
                parcelId,
                null,
                null,
                SourceClassifier.GetScannerNameFromId(scannerId),
                scannerId,
                target,
                ids.Identifiers,
                SourceClassifier.GetPriority(hit.SourceFile, "logic"));
            return true;
        }

        if (TryMatch(hit.Message, Patterns.RawRouteCommand, out match))
        {
            var scannerId = match.Groups["scannerId"].Value;
            var parcelId = match.Groups["parcelId"].Value;
            var target = match.Groups["target"].Value;
            var ids = MergeIdentifiers(hit.Identifiers, parcelId);
            candidate = new DomainEventCandidate(
                hit.Timestamp,
                "SendTaskToPlc",
                $"PLC command issued for parcel {parcelId}",
                $"Target {target} sent to scanner {scannerId}.",
                hit.Level,
                sourceType,
                hit.SourceFile,
                hit.LineNumber,
                parcelId,
                null,
                null,
                SourceClassifier.GetScannerNameFromId(scannerId),
                scannerId,
                target,
                ids.Identifiers,
                SourceClassifier.GetPriority(hit.SourceFile, "plc"));
            return true;
        }

        return false;
    }

    private static bool TryParseDivertAck(RawLogHit hit, string sourceType, out DomainEventCandidate? candidate)
    {
        candidate = null;

        if (TryMatch(hit.Message, Patterns.AckReceived, out var match))
        {
            var scannerId = match.Groups["scannerId"].Value;
            var scannerName = match.Groups["scannerName"].Value;
            var parcelId = match.Groups["parcelId"].Value;
            var target = match.Groups["target"].Value;
            var ids = MergeIdentifiers(hit.Identifiers, parcelId);
            candidate = new DomainEventCandidate(
                hit.Timestamp,
                "OnPlcAcknowledge",
                $"OnPlcAcknowledge for parcel {parcelId}",
                $"Acknowledgement received from {scannerName}.",
                hit.Level,
                sourceType,
                hit.SourceFile,
                hit.LineNumber,
                parcelId,
                null,
                null,
                scannerName,
                scannerId,
                target,
                ids.Identifiers,
                SourceClassifier.GetPriority(hit.SourceFile, "logic"));
            return true;
        }

        if (TryMatch(hit.Message, Patterns.CloseAckSent, out match))
        {
            var trackingId = match.Groups["trackingId"].Value;
            var target = match.Groups["target"].Value;
            var ids = MergeIdentifiers(hit.Identifiers, trackingId);
            candidate = new DomainEventCandidate(
                hit.Timestamp,
                "OnPlcCloseAcknowledge",
                $"Parcel {ids.ParcelId ?? trackingId} finalized at exit {target}",
                $"FWMS close acknowledgement sent for tracking {trackingId} on target {target}.",
                hit.Level,
                sourceType,
                hit.SourceFile,
                hit.LineNumber,
                ids.ParcelId,
                ids.OrderId,
                ids.TrackingId ?? trackingId,
                null,
                null,
                "AR",
                ids.Identifiers,
                SourceClassifier.GetPriority(hit.SourceFile, "logic"));
            return true;
        }

        if (TryMatch(hit.Message, Patterns.TelegramAck, out match))
        {
            var scannerId = match.Groups["scannerId"].Value;
            var parcelId = match.Groups["parcelId"].Value;
            var target = match.Groups["target"].Value;
            var ids = MergeIdentifiers(hit.Identifiers, parcelId);
            candidate = new DomainEventCandidate(
                hit.Timestamp,
                "Ausschleuse Quittung",
                $"Ausschleuse Quittung for parcel {parcelId}",
                $"Telegram confirms target {target} at scanner {scannerId}.",
                hit.Level,
                sourceType,
                hit.SourceFile,
                hit.LineNumber,
                parcelId,
                null,
                null,
                SourceClassifier.GetScannerNameFromId(scannerId),
                scannerId,
                target,
                ids.Identifiers,
                SourceClassifier.GetPriority(hit.SourceFile, "plc"));
            return true;
        }

        return false;
    }

    private static bool TryParseFwms(RawLogHit hit, string sourceType, out DomainEventCandidate? candidate)
    {
        candidate = null;

        if (!TryMatch(hit.Message, Patterns.FwmsTelegram, out var match))
        {
            return false;
        }

        var payload = match.Groups["payload"].Value;
        var ids = MergeIdentifiers(hit.Identifiers, payload);
        candidate = new DomainEventCandidate(
            hit.Timestamp,
            "MFC->WMF",
            $"MFC->WMF telegram for parcel {ids.ParcelId ?? ids.OrderId ?? "unknown"}",
            $"Telegram payload '{payload.Trim()}'.",
            hit.Level,
            sourceType,
            hit.SourceFile,
            hit.LineNumber,
            ids.ParcelId,
            ids.OrderId,
            ids.TrackingId,
            null,
            null,
            null,
            ids.Identifiers,
            SourceClassifier.GetPriority(hit.SourceFile, "fwms"));
        return true;
    }

    private static bool TryParseMisroute(RawLogHit hit, string sourceType, out DomainEventCandidate? candidate)
    {
        candidate = null;

        if (!TryMatch(hit.Message, Patterns.Misroute, out var match))
        {
            return false;
        }

        var parcelId = match.Groups["parcelId"].Value;
        var expectedTarget = match.Groups["expected"].Value;
        var actualTarget = match.Groups["actual"].Value;
        var scannerId = match.Groups["scannerId"].Value;
        var ids = MergeIdentifiers(hit.Identifiers, parcelId);

        candidate = new DomainEventCandidate(
            hit.Timestamp,
            "Misroute Error",
            $"Parcel {parcelId} diverted to {actualTarget} instead of {expectedTarget}",
            $"Wrong divert detected at scanner {scannerId}.",
            hit.Level,
            sourceType,
            hit.SourceFile,
            hit.LineNumber,
            parcelId,
            null,
            null,
            SourceClassifier.GetScannerNameFromId(scannerId),
            scannerId,
            actualTarget,
            ids.Identifiers,
            SourceClassifier.GetPriority(hit.SourceFile, "error"));
        return true;
    }

    private static bool TryParseOperationalException(RawLogHit hit, string sourceType, out DomainEventCandidate? candidate)
    {
        candidate = null;

        if (TryMatch(hit.Message, Patterns.MissingDataset, out var missingDataset))
        {
            var parcelId = missingDataset.Groups["parcelId"].Value;
            var scannerName = missingDataset.Groups["scannerName"].Value;
            var ids = MergeIdentifiers(hit.Identifiers, parcelId);
            candidate = new DomainEventCandidate(
                hit.Timestamp,
                "Missing Dataset",
                $"Parcel {parcelId} has no tudata record at {scannerName}",
                $"PLC acknowledge arrived for parcel {parcelId}, but no matching tudata record existed at {scannerName}.",
                hit.Level,
                sourceType,
                hit.SourceFile,
                hit.LineNumber,
                parcelId,
                ids.OrderId,
                ids.TrackingId,
                scannerName,
                null,
                null,
                ids.Identifiers,
                SourceClassifier.GetPriority(hit.SourceFile, "error"));
            return true;
        }

        if (TryMatch(hit.Message, Patterns.UnknownPositionAck, out var unknownPosition))
        {
            var parcelId = unknownPosition.Groups["parcelId"].Value;
            var ids = MergeIdentifiers(hit.Identifiers, parcelId);
            candidate = new DomainEventCandidate(
                hit.Timestamp,
                "Unknown Position Ack",
                $"Parcel {parcelId} returned an acknowledge for an unknown position",
                $"A divert acknowledge was received for parcel {parcelId}, but the position could not be mapped.",
                hit.Level,
                sourceType,
                hit.SourceFile,
                hit.LineNumber,
                parcelId,
                ids.OrderId,
                ids.TrackingId,
                null,
                null,
                null,
                ids.Identifiers,
                SourceClassifier.GetPriority(hit.SourceFile, "error"));
            return true;
        }

        if (TryMatch(hit.Message, Patterns.NoReadRouting, out var noReadRouting))
        {
            var scannerId = noReadRouting.Groups["scannerId"].Value;
            var scannerName = SourceClassifier.GetScannerNameFromId(scannerId);
            var target = noReadRouting.Groups["target"].Value;
            var ids = MergeIdentifiers(hit.Identifiers, "NOREAD");
            candidate = new DomainEventCandidate(
                hit.Timestamp,
                "NoRead",
                $"NoRead routed at {scannerName}",
                $"A NoRead parcel was routed to target {target}.",
                hit.Level,
                sourceType,
                hit.SourceFile,
                hit.LineNumber,
                ids.ParcelId,
                ids.OrderId,
                ids.TrackingId,
                scannerName,
                scannerId,
                target,
                ids.Identifiers,
                SourceClassifier.GetPriority(hit.SourceFile, "error"));
            return true;
        }

        if (TryMatch(hit.Message, Patterns.NioRouting, out var nioRouting))
        {
            var scannerName = nioRouting.Groups["scannerName"].Value;
            var trackingId = nioRouting.Groups["trackingId"].Value;
            var target = nioRouting.Groups["target"].Value;
            var reason = nioRouting.Groups["reason"].Value;
            var ids = MergeIdentifiers(hit.Identifiers, trackingId);
            candidate = new DomainEventCandidate(
                hit.Timestamp,
                "Nio Routing",
                $"NIO route at {scannerName}",
                string.IsNullOrWhiteSpace(reason)
                    ? $"Tracking {trackingId} was routed to NIO handling."
                    : $"{reason.Trim()} Tracking {trackingId} was routed to NIO handling.",
                hit.Level,
                sourceType,
                hit.SourceFile,
                hit.LineNumber,
                ids.ParcelId,
                ids.OrderId,
                ids.TrackingId ?? trackingId,
                scannerName,
                null,
                target,
                ids.Identifiers,
                SourceClassifier.GetPriority(hit.SourceFile, "error"));
            return true;
        }

        return false;
    }

    private static bool TryParseDatabaseUpdate(RawLogHit hit, string sourceType, out DomainEventCandidate? candidate)
    {
        candidate = null;

        if (!TryMatch(hit.Message, Patterns.SaveToDb, out var match))
        {
            return false;
        }

        var orderId = match.Groups["orderId"].Value;
        var parcelId = match.Groups["parcelId"].Value;
        var lastScanPos = match.Groups["lastScanPos"].Value;
        var plcTarget = match.Groups["plcTarget"].Value;
        var trackingId = match.Groups["trackingId"].Value;
        var status = match.Groups["status"].Value;
        var ids = MergeIdentifiers(hit.Identifiers, orderId, parcelId, trackingId);

        candidate = new DomainEventCandidate(
            hit.Timestamp,
            "SaveToDB",
            $"SaveToDB at {lastScanPos}",
            $"tudata updated: Status={status}, PlcTarget={plcTarget}, TrackingId={(string.IsNullOrWhiteSpace(trackingId) ? "<empty>" : trackingId)}.",
            hit.Level,
            sourceType,
            hit.SourceFile,
            hit.LineNumber,
            parcelId,
            orderId,
            string.IsNullOrWhiteSpace(trackingId) ? null : trackingId,
            lastScanPos,
            null,
            plcTarget,
            ids.Identifiers,
            SourceClassifier.GetPriority(hit.SourceFile, "logic"));
        return true;
    }

    private static string InferScanner(IdentifierBundle ids, RawLogHit hit, string prefix)
    {
        var known = ids.Identifiers.FirstOrDefault(id => id.StartsWith("SC", StringComparison.OrdinalIgnoreCase));
        return known ?? prefix;
    }

    private static string? LatestValue<T>(IEnumerable<T> source, Func<T, string?> selector) =>
        source.Select(selector).LastOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static int GetPhaseOrder(string phase) =>
        phase switch
        {
            "Lifecycle" => 0,
            "Induction & Weighing" => 10,
            "Labeling" => 20,
            "Label Exit & Tracking" => 30,
            "Routing & Divert" => 40,
            "Parcel State" => 50,
            "External Reporting" => 60,
            "Exception" => 90,
            _ => 100
        };

    private static int GetStageOrder(string stage) =>
        stage switch
        {
            "First Appearance" => 0,
            "OnScannerData" => 10,
            "SPS->MFR Scanner Daten" => 11,
            "ScanBruttoWaage" => 20,
            "SendeWaagedatenAnsLVS" => 30,
            "BroadcastWaageDaten" => 31,
            "ScanEtiEingang" => 40,
            "SendTaskToProcEti" => 45,
            "SendFileTCP" => 50,
            "ScanEtiAusgang" => 60,
            "SaveToDB" => 70,
            "SendTaskToPlc" => 80,
            "OnPlcAcknowledge" => 90,
            "OnPlcCloseAcknowledge" => 92,
            "Ausschleuse Quittung" => 91,
            "MFC->WMF" => 95,
            "NoRead" => 96,
            "Missing Dataset" => 97,
            "Unknown Position Ack" => 98,
            "Nio Routing" => 99,
            "Misroute Error" => 100,
            "Last Appearance" => 999,
            _ => 500
        };

    private static int GetSeverity(string level) =>
        level.ToUpperInvariant() switch
        {
            "ERROR" => 4,
            "WARN" => 3,
            "INFO" => 2,
            "DEBUG" => 1,
            _ => 0
        };

    private static bool TryMatch(string input, Regex regex, out Match match)
    {
        match = regex.Match(input);
        return match.Success;
    }

    private static IdentifierBundle MergeIdentifiers(IReadOnlyList<string> existingIdentifiers, params string?[] texts)
    {
        var identifiers = new HashSet<string>(existingIdentifiers, StringComparer.OrdinalIgnoreCase);
        string? combinedOrderId = null;
        string? combinedParcelId = null;

        foreach (var text in texts)
        {
            if (IdentifierExtractor.TryExtractCombinedZpParts(text, out var extractedOrderId, out var extractedParcelId))
            {
                combinedOrderId ??= extractedOrderId;
                combinedParcelId ??= extractedParcelId;
            }

            foreach (var identifier in IdentifierExtractor.Extract(text ?? string.Empty))
            {
                identifiers.Add(identifier);
            }
        }

        var parcelId = combinedParcelId
            ?? identifiers.FirstOrDefault(static id => id.Length == 10 && id.All(char.IsDigit));
        var orderId = combinedOrderId
            ?? identifiers.FirstOrDefault(static id => id.Length == 20 && id.All(char.IsDigit))
            ?? identifiers.FirstOrDefault(static id => id.Length > 10 && id.All(char.IsDigit));
        var trackingId = identifiers.FirstOrDefault(static id => id.StartsWith("1Z", StringComparison.OrdinalIgnoreCase) || (id.Length >= 12 && id.Any(char.IsLetter)));
        return new IdentifierBundle(parcelId, orderId, trackingId, identifiers.ToArray());
    }

    private sealed record IdentifierBundle(string? ParcelId, string? OrderId, string? TrackingId, IReadOnlyList<string> Identifiers);

    private sealed record DomainEventCandidate(
        DateTime Timestamp,
        string Stage,
        string Summary,
        string Details,
        string Level,
        string SourceType,
        string SourceFile,
        int LineNumber,
        string? ParcelId,
        string? OrderId,
        string? TrackingId,
        string? ScannerName,
        string? ScannerId,
        string? Target,
        IReadOnlyList<string> Identifiers,
        int Priority);

    private sealed record PhaseBucket(string Phase)
    {
        public List<ParcelTimelineEvent> Events { get; } = [];
    }

    internal sealed record RawLogHit(
        DateTime Timestamp,
        string Level,
        string Message,
        string SourceFile,
        int LineNumber,
        IReadOnlyList<string> Identifiers);

    private static class Patterns
    {
        public static readonly Regex ScannerDataReceived = new(@"OnScannerData: received Data: '(?<scannerId>[^|]+)\|(?<scannerName>[^|]+)\|(?<barcode>.+)'", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        public static readonly Regex TelegramScannerData = new(@"Scanner Daten - #'(?<scannerId>[^']+)'/[^ ]+ Barcode '(?<barcode>.+)'", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        public static readonly Regex ScanEtiEingang = new(@"ScanEtiEingang: '(?<barcode>.+)' DS .* ParcelId '(?<parcelId>\d+)'", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        public static readonly Regex ScanEtiAusgang = new(@"ScanEtiAusgang: '(?<scannerName>[^']+)'.*ParcelId='(?<parcelId>\d+)'.*Target:'(?<target>[A-Z0-9 ]*)'", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        public static readonly Regex WeightScan = new(@"ScanBruttoWaage: '(?<barcode>.+)' DS .* ParcelId '(?<parcelId>\d+)'", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        public static readonly Regex WeightBroadcast = new(@"WaageDaten='Waage\|WaageID=.* / (?<scannerName>[^|]+)\|ParcelID=(?<parcelId>\d+)\|IstGewicht=(?<actualWeight>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        public static readonly Regex WeightToFwms = new(@"Parameter='(?<parcelId>\d+)\|(?<weight>\d+)\|(?<orderId>\d+)'\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        public static readonly Regex LabelRequest = new(@"Send job to ProcEtikettierer: .* ScannerId='(?<scannerName>[^']+)' Parceld='(?<parcelId>\d+)'", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        public static readonly Regex LabelFileSent = new(@"Sending file '.*\\(?<fileName>[^\\']+\.zpl)'", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        public static readonly Regex LabelExit = new(@"Seitenlesung: (?<parcelId>\d+), Top-Lesung: (?<trackingId>[A-Za-z0-9%]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        public static readonly Regex RouteRequest = new(@"Parameter='(?<scannerId>[^|]+)\|(?<parcelId>\d+)\|(?<target>[A-Z0-9]+)'\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        public static readonly Regex RawRouteCommand = new(@"Sending data 'S\t[^\t]+\tMFR\s+\tSPS\s+\t02\t[^\t]+\t00\t0043\t(?<scannerId>[^\t]+)\t(?<parcelId>\d+)\s+\t(?<target>[A-Z0-9]+)'", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        public static readonly Regex AckReceived = new(@"OnPlcAcknowledge: received Data: '(?<scannerId>[^|]+)\|(?<scannerName>[^|]+)\|(?<parcelId>\d+)\|(?<target>[A-Z0-9]+)'", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        public static readonly Regex CloseAckSent = new(@"OnPlcAcknowledge:\s*'(?<trackingId>[^']+)'\s*'Ausschleuse Meldung\s*'(?<target>\d+)'\s*an FWMS gesendet => CLOSE\s*'", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        public static readonly Regex TelegramAck = new(@"Ausschleuse Quittung - #'(?<scannerId>[^']+)'/[^ ]+ Barcode '(?<parcelId>\d+)' IstZiel '(?<target>[A-Z0-9]+)'", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        public static readonly Regex FwmsTelegram = new(@"Sent: \d+/\d+ >>.*?(?<payload>ZP\s+\d{8,30}\s+\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        public static readonly Regex Misroute = new(@"Paket '(?<parcelId>\d+)' wurde falsch ausgeschleust! Pos: '(?<scannerId>[^']+)' Soll: '(?<expected>[A-Z0-9]+)' Ist: '(?<actual>[A-Z0-9]+)'", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        public static readonly Regex MissingDataset = new(@"Zu Karton '(?<parcelId>[^']+)' gibt es keinen Datensatz! Pos: (?<scannerName>[A-Z0-9_]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        public static readonly Regex UnknownPositionAck = new(@"Paket '(?<parcelId>\d+)' Ausschleuse-Quittung (?:für|fÃ¼r) unbekannte Position!", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        public static readonly Regex NoReadRouting = new(@"Scan(?:ZuteilungVerschlie.?.?erlinien|AbzweigungArbeitspl.?.?tze): .*NoRead.*Target:'(?<scannerId>[^|']+)\|(?<barcode>[^|']+)\|(?<target>[A-Z0-9]+)'", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        public static readonly Regex NioRouting = new(@"(?<scannerName>SC_[A-Z0-9]+)'.*?(?<trackingId>[A-Z0-9%]+)'.*?(?<reason>WeightErr Paket!\s*Schicke Paket auf NIO!|Ziel NIO => schleuse aus!|Kein Eintrag in der wa_parcel_targets! => Lege neuen an und setze Ziel NIO).*?Target:'(?<target>[A-Z0-9]+)'", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        public static readonly Regex SaveToDb = new(@"SaveToDB - '(?:UPDATE|INSERT) \(tudata\) PrincipalID=[^,]*, OrderID=(?<orderId>[^,]*), ParcelID=(?<parcelId>[^,]*), .*?LastScanPos=(?<lastScanPos>[^,]*), .*?PlcTarget=(?<plcTarget>[^,]*), TrackingId=(?<trackingId>[^,]*), Status=(?<status>[^,]*),", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    }
}

internal static class SourceClassifier
{
    public static string GetSourceType(string sourceFile)
    {
        var fileName = Path.GetFileName(sourceFile);
        if (fileName.StartsWith("ProcLogic", StringComparison.OrdinalIgnoreCase))
        {
            return "Logic";
        }

        if (fileName.StartsWith("ProcPLC", StringComparison.OrdinalIgnoreCase))
        {
            return "PLC Adapter";
        }

        if (fileName.StartsWith("ConPLCtoMFR", StringComparison.OrdinalIgnoreCase) || fileName.StartsWith("ConMFRtoPLC", StringComparison.OrdinalIgnoreCase))
        {
            return "PLC Connection";
        }

        if (fileName.StartsWith("ConMFRtoLVS", StringComparison.OrdinalIgnoreCase))
        {
            return "FWMS Connection";
        }

        if (fileName.StartsWith("ConEti", StringComparison.OrdinalIgnoreCase))
        {
            return "Label Printer";
        }

        if (fileName.StartsWith("ProcCamera", StringComparison.OrdinalIgnoreCase))
        {
            return "Camera";
        }

        return "Technical";
    }

    public static int GetPriority(string sourceFile, string channel)
    {
        var sourceType = GetSourceType(sourceFile);
        return (channel, sourceType) switch
        {
            ("error", _) => 500,
            (_, "Logic") => 400,
            ("eti", "Label Printer") => 360,
            (_, "PLC Adapter") => 300,
            (_, "FWMS Connection") => 220,
            (_, "Camera") => 180,
            (_, "PLC Connection") => 120,
            _ => 100
        };
    }

    public static string? GetScannerNameFromId(string scannerId)
    {
        return scannerId switch
        {
            "3325-1" => "SC_BWL4",
            "3335-1" => "SC_ETIINL4",
            "3345-1" => "SC_ETIOSL4",
            "3060-1" => "SC_BWL5",
            "3070-1" => "SC_ETIINL5",
            "3080-1" => "SC_ETIOSL5",
            "2000-1" => "SC_WAVL",
            "2050-1" => "SC_VL1",
            "2095-1" => "SC_VL2",
            _ => null
        };
    }
}
