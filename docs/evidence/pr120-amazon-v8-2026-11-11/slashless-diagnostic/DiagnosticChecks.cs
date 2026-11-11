using System.Text.Json.Nodes;

internal static class DiagnosticChecks
{
    private const string Store = "https://www.amazon.fr/stores/page/12345678-1234-1234-1234-123456789abc";
    private const string Entry = "https://sponsored-ads.amazon.fr/clk?opaque=fixture";

    internal static void Run(DiagnosticEvidence evidence)
    {
        var passed = 0;
        void Check(bool condition) { if (!condition) throw new InvalidOperationException("Controlled diagnostic check failed: " + (passed + 1)); passed++; }
        void Reject(Action action) { try { action(); } catch (InvalidOperationException) { passed++; return; } throw new InvalidOperationException("A forbidden diagnostic action was accepted."); }
        Check(DiagnosticEvidence.Entry(Entry)); Check(!evidence.OriginalRoute(Entry)); Check(evidence.Route(Store));
        foreach (var invalid in new[] { Entry.Replace("https:", "http:"), Entry.Replace("amazon.fr/", "amazon.fr:443/"),
            Entry.Replace("https://", "https://user@"), Entry.Replace("/clk?", "/clk/?"), Entry + "#fragment", Entry + "\n", Entry.Replace("amazon.fr", "amazon.com") })
            Check(!DiagnosticEvidence.Entry(invalid));
        var events = Events();
        Check(evidence.Candidates(events[0]).Count == 3);
        Check(evidence.Verify(events)["resolution_evidence_complete"]!.GetValue<bool>());
        Check(!evidence.Verify(events)["source_prerequisite_qualified"]!.GetValue<bool>());
        Check(!evidence.Verify(events)["business_oracle_passed"]!.GetValue<bool>());
        for (var count = 1; count <= 3; count++) evidence.RequireNext(events.Take(count).ToArray(), events[count]["arguments"]!["url"]!.ToString());
        Check(evidence.Verify(events)["resolutions"]!.AsArray().Count == 3); // Duplicate hrefs remain separate.
        Reject(() => evidence.RequireNext([], Entry));
        Reject(() => evidence.RequireNext([events[0]], Store));
        Reject(() => evidence.RequireNext([events[0]], Entry + "&invented=1"));
        Reject(() => evidence.RequireNext(events, Entry));
        foreach (var variant in new[] { "omit", "reorder", "invented", "truncated_source", "bad_reference", "narrow_source", "truncated_destination", "external_hop", "post", "close", "new_search", "failed_read" })
        {
            var changed = Events();
            switch (variant)
            {
                case "omit": changed.RemoveAt(1); break;
                case "reorder": (changed[2], changed[3]) = (changed[3], changed[2]); break;
                case "invented": changed[1]["arguments"]!["url"] += "&invented=1"; break;
                case "truncated_source": changed[0]["result"]!["observationSnapshot"]!["captureTruncated"] = true; break;
                case "bad_reference": changed[0]["result"]!["observationSnapshot"]!["pages"]![0]!["records"]![0]!["reference"] = "wrong:record:0"; break;
                case "narrow_source": changed[0]["arguments"]!["selector"] = "#subset"; break;
                case "truncated_destination": changed[1]["result"]!["observationSnapshot"]!["captureTruncated"] = true; break;
                case "external_hop": changed[1]["result"]!["navigationEvidence"]!["hops"]![1]!["url"] = "https://example.org/"; break;
                case "post": changed[1]["result"]!["navigationEvidence"]!["hops"]![0]!["method"] = "POST"; break;
                case "close": changed[1] = new JsonObject { ["tool"] = "browser_close" }; break;
                case "new_search": changed[1] = changed[0].DeepClone().AsObject(); break;
                case "failed_read": changed[1]["error"] = true; break;
            }
            Check(!evidence.Verify(changed)["resolution_evidence_complete"]!.GetValue<bool>());
        }
        var product = Events(); product[1] = Read(Entry, "https://www.amazon.fr/dp/B000000001", "destination", [new JsonObject { ["text"] = "Observed product" }]);
        var report = evidence.Verify(product);
        Check(report["resolution_evidence_complete"]!.GetValue<bool>());
        Check(report["resolutions"]![0]!["destination_kind"]!.ToString() == "product");
        Check(!report["resolutions"]![0]!["occurrence_verified"]!.GetValue<bool>());
        foreach (var change in new[] { "none", "different_product", "missing_link", "second_heading", "different_context", "borrowed_source_identity", "unresolved_context_link" })
        {
            var association = Events();
            for (var index = 1; index <= 3; index++) association[index] = Read(association[index]["arguments"]!["url"]!.ToString(), "https://www.amazon.fr/dp/B000000001", "destination", [new JsonObject { ["text"] = "Observed product" }]);
            var records = association[0]["result"]!["observationSnapshot"]!["pages"]![0]!["records"]!.AsArray();
            switch (change)
            {
                case "different_product": association[3] = Read(association[3]["arguments"]!["url"]!.ToString(), "https://www.amazon.fr/dp/B000000002", "destination", [new JsonObject { ["text"] = "Other product" }]); break;
                case "missing_link": association.RemoveAt(3); break;
                case "second_heading": records[0]!["heading"] = "other > h2"; break;
                case "different_context": records[0]!["ancestorAttributes"]!["data-asin"]!["index"] = 36; break;
                case "borrowed_source_identity": records[0]!["ancestorAttributes"]!["data-asin"]!["value"] = "B000000001"; break;
                case "unresolved_context_link":
                    var extra = records[0]!.DeepClone(); extra["href"] = Entry.Replace("/clk?", "/clk/?"); extra["reference"] = "source:record:3"; records.Add(extra);
                    association[0]["result"]!["observationSnapshot"]!["recordCount"] = records.Count; break;
            }
            var facts = evidence.ProductAssociationFacts(association);
            Check(facts["association_evidence_complete"]!.GetValue<bool>() == (change == "none"));
            Check(!facts["product_occurrence_claimed"]!.GetValue<bool>());
        }
        Console.WriteLine(new JsonObject { ["controlled_checks_passed"] = passed, ["model_calls"] = 0, ["network_calls"] = 0, ["workflow_qualification"] = false }.ToJsonString());
    }

    private static List<JsonObject> Events()
    {
        var records = new JsonArray();
        for (var index = 0; index < 3; index++) records.Add(new JsonObject { ["kind"] = "link", ["href"] = Entry + (index == 2 ? "&third=1" : ""),
            ["reference"] = "source:record:" + index, ["heading"] = index == 1 ? "a > h2" : null,
            ["ancestorAttributes"] = new JsonObject { ["data-asin"] = new JsonObject { ["value"] = "", ["tag"] = "div", ["role"] = null, ["index"] = 35 } } });
        List<JsonObject> events = [Read(DiagnosticEvidence.Search, DiagnosticEvidence.Search, "source", records)];
        foreach (var record in records) events.Add(Read(record!["href"]!.ToString(), Store, "destination", [new JsonObject { ["text"] = "Observed store" }]));
        return events;
    }

    private static JsonObject Read(string requested, string final, string id, JsonArray records)
    {
        var hops = new JsonArray();
        if (requested != final) hops.Add(new JsonObject { ["url"] = requested, ["method"] = "GET", ["statusCode"] = 302, ["location"] = final });
        hops.Add(new JsonObject { ["url"] = final, ["method"] = "GET", ["statusCode"] = 200 });
        return new JsonObject { ["tool"] = "browser_get_content", ["error"] = false,
            ["arguments"] = new JsonObject { ["url"] = requested, ["selector"] = "body", ["format"] = "observation_complete" },
            ["result"] = new JsonObject { ["success"] = true, ["url"] = final, ["truncated"] = false,
                ["navigationEvidence"] = new JsonObject { ["requestedUrl"] = requested, ["finalUrl"] = final, ["complete"] = true, ["hops"] = hops },
                ["observationSnapshot"] = new JsonObject { ["id"] = id, ["captureTruncated"] = false, ["manifestTruncated"] = false, ["recordCount"] = records.Count,
                    ["pages"] = new JsonArray(new JsonObject { ["id"] = id, ["captureTruncated"] = false, ["records"] = records.DeepClone() }) } } };
    }
}
