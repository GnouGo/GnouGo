using System.Reflection;
using System.Text.Json.Nodes;

var facts = new ReviewCardFacts(Assembly.Load("GnOuGo.Agent.Planning.Benchmark"));
if (args is ["--inspect", var eventsPath, var output])
{
    if (Path.GetFullPath(eventsPath) == Path.GetFullPath(output) || File.Exists(output))
        throw new InvalidOperationException("A fresh report path distinct from immutable source evidence is required.");
    var events = JsonNode.Parse(await File.ReadAllTextAsync(eventsPath))!.AsArray();
    var original = events.ToJsonString();
    var report = facts.Inspect(events[0]!.AsObject());
    if (original != events.ToJsonString()) throw new InvalidOperationException("Original evidence was modified.");
    await using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    await using (var writer = new StreamWriter(stream))
        await writer.WriteAsync(report.ToJsonString(new() { WriteIndented = true }));
    Console.WriteLine(new JsonObject { ["relations"] = report["relations"]!.AsArray().Count,
        ["unsupported"] = report["relations"]!.AsArray().Count(r => r!["proposed_relationship"]!.ToString() is "unverified" or "unsupported_context_or_occurrence"),
        ["source_prerequisite_qualified"] = false, ["model_calls"] = 0 }.ToJsonString());
    return;
}
if (args is not ["--self-test"]) throw new ArgumentException("Use --self-test or --inspect <retained-events.json> <report.json>.");
const string entry = "https://sponsored-ads.amazon.fr/clk?opaque=fixture";
var checks = 0;
foreach (var count in new[] { 0, 1, 3 })
{
    List<JsonObject> records = [];
    for (var i = 0; i < count; i++)
    {
        records.Add(Link(i, "heading-" + i, "", entry + i, true));
        records.Add(Link(i, "image-" + i, "", entry + i, false));
        records.Add(Link(i, "review-" + i, "", entry + i + "#customerReviews", false));
    }
    var report = facts.Relations(records); var relations = report["relations"]!.AsArray();
    Check(relations.Count == count, "zero/one/many card relations");
    Check(relations.All(r => r!["proposed_relationship"]!.ToString() == "exact_empty_card_redirect_reference_proposal"), "distinct semantic cards under one empty parent");
    Check(relations.All(r => r!["required_own_redirect_source_records"]!.AsArray().Count == 2), "repeated entries retain both own redirect requirements");
    Check(!report["source_prerequisite_qualified"]!.GetValue<bool>() && !report["navigation_authority_added"]!.GetValue<bool>(), "relationships grant no qualification or navigation");
}
foreach (var exact in new[] { false, true })
{
    var baseHref = exact ? entry : "https://www.amazon.fr/dp/B000000001";
    JsonObject[] records = [Link(0, "heading", "B000000001", baseHref, true), Link(0, "review", "B000000001", entry + "#customerReviews", false)];
    var relation = facts.Relations(records)["relations"]!.AsArray().Single()!;
    Check(relation["proposed_relationship"]!.ToString() == "known_source_card_membership_proposal", "known card membership is a different explicit proposal from exact HTTP aliasing");
    Check(!relation["redirect_proof_verified"]!.GetValue<bool>() && !relation["http_call_created"]!.GetValue<bool>(), "known DOM identity is not an HTTP receipt");
}
{
    JsonObject[] records = [Link(0, "title", "B000000001", entry, false), Link(0, "rating", "B000000001", entry + "&tracking=rating#customerReviews", false)];
    Check(facts.Relations(records)["relations"]!.AsArray().Single()!["proposed_relationship"]!.ToString() == "known_source_card_membership_proposal",
        "known-ASIN cards retain the existing no-heading fallback and do not claim exact HTTP alias equality");
    var second = records.Select(r => r.DeepClone().AsObject()).ToArray();
    foreach (var r in second)
    {
        r["reference"] += "-second"; r["selector"] += "-second";
        r["ancestorAttributes"]!["data-asin"]!["index"] = 1;
        r["occurrence"]!["index"] = 1; r["occurrence"]!["selector"] = "#card-1";
    }
    Check(facts.Relations(records.Concat(second).ToArray())["relations"]!.AsArray().Count == 2,
        "same identity in different observed cards retains both relationships");
}
foreach (var change in new[] { "query", "cross_card", "heading", "self_occurrence", "role", "missing_base", "two_headings", "no_heading", "bad_fragment", "unknown_identity", "identity_conflict", "context_conflict", "nonsemantic_div", "missing_known_entry", "bad_tag", "navigation_context", "bad_role_type", "bad_identity_type" })
{
    var known = change is "identity_conflict" or "context_conflict" or "missing_known_entry";
    var first = Link(0, "heading", known ? "B000000001" : "", known ? "https://www.amazon.fr/dp/B000000001" : entry, true);
    var alias = Link(0, "review", known ? "B000000001" : "", entry + "#customerReviews", false);
    List<JsonObject> records = [first, alias];
    switch (change)
    {
        case "query": alias["href"] = entry + "&changed=1#customerReviews"; break;
        case "cross_card": alias["occurrence"]!["index"] = 1; break;
        case "heading": alias["heading"] = "own > h2"; break;
        case "self_occurrence": alias["selector"] = alias["occurrence"]!["selector"]!.DeepClone(); break;
        case "role": alias["role"] = "listitem"; break;
        case "missing_base": records.Remove(first); break;
        case "two_headings": records.Add(Link(0, "second-heading", "", entry, true)); break;
        case "no_heading": first["heading"] = null; break;
        case "bad_fragment": alias["href"] = entry + "#other"; break;
        case "unknown_identity": foreach (var r in records) r["ancestorAttributes"]!["data-asin"]!["value"] = null; break;
        case "identity_conflict": first["href"] = "https://www.amazon.fr/dp/B000000002"; break;
        case "context_conflict": first["ancestorAttributes"]!["data-asin"]!["value"] = "B000000002"; break;
        case "nonsemantic_div": foreach (var r in records) r["occurrence"]!["role"] = null; break;
        case "missing_known_entry": records.Remove(first); break;
        case "bad_tag": foreach (var r in records) r["ancestorAttributes"]!["data-asin"]!["tag"] = "bad<tag"; break;
        case "navigation_context": foreach (var r in records) r["ancestorAttributes"]!["data-asin"]!["tag"] = "nav"; break;
        case "bad_role_type": foreach (var r in records) r["ancestorAttributes"]!["data-asin"]!["role"] = 17; break;
        case "bad_identity_type": foreach (var r in records) r["ancestorAttributes"]!["data-asin"]!["value"] = 17; break;
    }
    var relations = facts.Relations(records)["relations"]!.AsArray();
    Check(relations.Count == 0 || relations.All(r => r!["proposed_relationship"]!.ToString() is "unverified" or "unsupported_context_or_occurrence"), change);
}
Console.WriteLine(new JsonObject { ["controlled_checks"] = checks, ["passed"] = true, ["inactive"] = true,
    ["source_prerequisite_qualified"] = false, ["model_calls"] = 0 }.ToJsonString());

void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks++; }
static JsonObject Link(int card, string reference, string identity, string href, bool heading) => new()
{
    ["kind"] = "link", ["tag"] = "a", ["reference"] = reference, ["selector"] = "#" + reference, ["href"] = href,
    ["heading"] = heading ? "a > h2" : null,
    ["ancestorAttributes"] = new JsonObject { ["data-asin"] = new JsonObject { ["value"] = identity, ["tag"] = "div", ["role"] = null, ["index"] = 0 } },
    ["occurrence"] = new JsonObject { ["selector"] = "#card-" + card, ["tag"] = "div", ["role"] = "listitem", ["index"] = card }
};
