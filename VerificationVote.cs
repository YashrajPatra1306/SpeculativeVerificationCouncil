namespace SpeculativeVerificationCouncil;

/// <summary>
/// Aggregates council votes and computes consensus metrics.
/// </summary>
public sealed class VoteAggregator
{
    private readonly List<CouncilVote> _votes = [];

    public IReadOnlyList<CouncilVote> Votes => _votes;

    public void Add(CouncilVote vote) => _votes.Add(vote);

    /// <summary>Total weighted score across all valid, non-timed-out votes.</summary>
    public double TotalWeightedScore =>
        _votes.Where(v => !v.TimedOut && v.Error is null)
              .Sum(v => v.Weight * v.Result.Confidence);

    /// <summary>Maximum possible weighted score if all models responded with confidence 1.0.</summary>
    public double MaxPossibleScore =>
        _votes.Sum(v => v.Weight);

    /// <summary>Overall confidence as a ratio of achieved to possible score.</summary>
    public double OverallConfidence =>
        MaxPossibleScore > 0 ? TotalWeightedScore / MaxPossibleScore : 0.0;

    /// <summary>How many models returned a valid (non-timeout, non-error) response.</summary>
    public int ValidResponseCount =>
        _votes.Count(v => !v.TimedOut && v.Error is null);

    /// <summary>How many models validated the draft as correct.</summary>
    public int ApprovalCount =>
        _votes.Count(v => !v.TimedOut && v.Error is null && v.Result.Valid);

    /// <summary>How many models rejected the draft.</summary>
    public int RejectionCount =>
        _votes.Count(v => !v.TimedOut && v.Error is null && !v.Result.Valid);

    /// <summary>Detect whether there are contradictions (some approve, some reject).</summary>
    public bool HasContradictions =>
        ApprovalCount > 0 && RejectionCount > 0;

    /// <summary>All unique corrections submitted by rejecting models.</summary>
    public List<string> AllCorrections =>
        _votes.Where(v => !v.TimedOut && v.Error is null && v.Result.Corrections is not null)
              .SelectMany(v => v.Result.Corrections!)
              .Distinct()
              .ToList();

    /// <summary>All facts from all valid responses (not deduplicated).</summary>
    public List<string> AllFacts =>
        _votes.Where(v => !v.TimedOut && v.Error is null && v.Result.Facts is not null)
              .SelectMany(v => v.Result.Facts!)
              .ToList();

    /// <summary>
    /// Facts that appear in ALL valid responses (intersection).
    /// Uses Levenshtein similarity for fuzzy matching.
    /// </summary>
    public List<string> IntersectionFacts(double similarityThreshold = 0.75)
    {
        var validVotes = _votes
            .Where(v => !v.TimedOut && v.Error is null && v.Result.Facts is { Count: > 0 })
            .ToList();

        if (validVotes.Count == 0) return [];
        if (validVotes.Count == 1) return validVotes[0].Result.Facts!.ToList();

        var firstFacts = validVotes[0].Result.Facts!;
        var result = new List<string>();

        foreach (var fact in firstFacts)
        {
            bool inAll = validVotes.Skip(1).All(v =>
                v.Result.Facts!.Any(f => SemanticSimilarity.IsSimilar(fact, f, similarityThreshold)));
            if (inAll) result.Add(fact);
        }

        return result;
    }

    /// <summary>
    /// Facts scored by weighted votes. Returns facts exceeding the threshold.
    /// </summary>
    public List<string> WeightedFacts(double thresholdRatio = 0.5)
    {
        var validVotes = _votes
            .Where(v => !v.TimedOut && v.Error is null && v.Result.Facts is { Count: > 0 })
            .ToList();

        if (validVotes.Count == 0) return [];

        // Collect all unique facts (fuzzy-deduplicated)
        var canonicalFacts = new List<(string Canonical, double Score)>();

        foreach (var vote in validVotes)
        {
            foreach (var fact in vote.Result.Facts!)
            {
                var existing = canonicalFacts.FindIndex(cf =>
                    SemanticSimilarity.IsSimilar(cf.Canonical, fact, 0.75));

                if (existing >= 0)
                {
                    var (c, s) = canonicalFacts[existing];
                    canonicalFacts[existing] = (c, s + vote.Weight * vote.Result.Confidence);
                }
                else
                {
                    canonicalFacts.Add((fact, vote.Weight * vote.Result.Confidence));
                }
            }
        }

        double maxScore = MaxPossibleScore;
        double threshold = maxScore * thresholdRatio;

        return canonicalFacts
            .Where(cf => cf.Score >= threshold)
            .OrderByDescending(cf => cf.Score)
            .Select(cf => cf.Canonical)
            .ToList();
    }

    /// <summary>
    /// Find contradicted facts between council members using negation and numeric detection.
    /// </summary>
    public List<Contradiction> FindContradictions()
    {
        var contradictions = new List<Contradiction>();
        var validVotes = _votes
            .Where(v => !v.TimedOut && v.Error is null && v.Result.Facts is { Count: > 0 })
            .ToList();

        for (int i = 0; i < validVotes.Count; i++)
        {
            for (int j = i + 1; j < validVotes.Count; j++)
            {
                var voteA = validVotes[i];
                var voteB = validVotes[j];

                foreach (var factA in voteA.Result.Facts!)
                {
                    foreach (var factB in voteB.Result.Facts!)
                    {
                        if (AreContradictory(factA, factB))
                            contradictions.Add(new Contradiction(factA, factB, voteA.ModelName, voteB.ModelName));
                    }
                }
            }
        }

        return contradictions;
    }

    private static bool AreContradictory(string factA, string factB)
    {
        var normA = factA.Trim().ToLowerInvariant();
        var normB = factB.Trim().ToLowerInvariant();

        if (normA.Length < 10 || normB.Length < 10) return false;

        // Negation detection: one fact negates the other
        string[] negations = ["not ", "no ", "never ", "false", "incorrect", "wrong"];
        bool aHasNeg = negations.Any(n => normA.Contains(n));
        bool bHasNeg = negations.Any(n => normB.Contains(n));

        if (aHasNeg != bHasNeg)
        {
            var coreA = negations.Aggregate(normA, (s, n) => s.Replace(n, "")).Trim();
            var coreB = negations.Aggregate(normB, (s, n) => s.Replace(n, "")).Trim();

            if (coreA.Length > 10 && coreB.Length > 10 &&
                SemanticSimilarity.IsSimilar(coreA, coreB, 0.70))
                return true;
        }

        // Numeric contradiction: same context, different values
        return DetectNumericContradiction(normA, normB);
    }

    private static bool DetectNumericContradiction(string factA, string factB)
    {
        var numsA = ExtractNumbers(factA);
        var numsB = ExtractNumbers(factB);

        foreach (var a in numsA)
            foreach (var b in numsB)
                if (a > 0 && b > 0 && Math.Abs(a - b) > Math.Max(a, b) * 0.1)
                    return true;

        return false;
    }

    private static List<double> ExtractNumbers(string text)
    {
        var numbers = new List<double>();
        foreach (var part in text.Split(new[] { ' ', ',', '.', ';', ':' }, StringSplitOptions.RemoveEmptyEntries))
            if (double.TryParse(part, out double n) && n > 0)
                numbers.Add(n);
        return numbers;
    }

    /// <summary>Fastest valid response (for Fast strategy).</summary>
    public CouncilVote? FastestResponse =>
        _votes.Where(v => !v.TimedOut && v.Error is null)
              .OrderBy(v => v.ResponseTime)
              .FirstOrDefault();

    /// <summary>Generate dissent warnings for display.</summary>
    public List<string> DissentWarnings()
    {
        var warnings = new List<string>();

        foreach (var vote in _votes)
        {
            if (vote.TimedOut)
                warnings.Add($"[TIMEOUT] {vote.ModelName} did not respond within deadline");
            else if (vote.Error is not null)
                warnings.Add($"[ERROR] {vote.ModelName}: {vote.Error}");
            else if (!vote.Result.Valid)
                warnings.Add($"[DISSENT] {vote.ModelName} rejected draft (confidence: {vote.Result.Confidence:P0})");
            else if (vote.Result.Confidence < 0.5)
                warnings.Add($"[LOW-CONF] {vote.ModelName} approved with low confidence ({vote.Result.Confidence:P0})");
        }

        return warnings;
    }
}

/// <summary>
/// Levenshtein-based semantic similarity for fact deduplication.
/// </summary>
public static class SemanticSimilarity
{
    public static bool IsSimilar(string a, string b, double threshold = 0.75)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;

        a = a.Trim().ToLowerInvariant();
        b = b.Trim().ToLowerInvariant();

        if (a == b) return true;

        double similarity = 1.0 - (double)LevenshteinDistance(a, b) / Math.Max(a.Length, b.Length);
        return similarity >= threshold;
    }

    private static int LevenshteinDistance(string s, string t)
    {
        int n = s.Length, m = t.Length;
        if (n == 0) return m;
        if (m == 0) return n;

        // Use two rows instead of full matrix to minimize allocations
        var prev = new int[m + 1];
        var curr = new int[m + 1];

        for (int j = 0; j <= m; j++) prev[j] = j;

        for (int i = 1; i <= n; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= m; j++)
            {
                int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }

        return prev[m];
    }
}
