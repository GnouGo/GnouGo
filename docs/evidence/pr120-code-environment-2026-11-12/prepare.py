"""Prepare an isolated diagnostic without editing the production SDK probe."""
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET

repo = Path(__file__).resolve().parents[3]
original = repo / "tests/GnOuGo.Agent.Planning.Benchmark"
source = (original / "LiveExecutionReadiness.cs").read_text()
assert hashlib.sha256(source.encode()).hexdigest() == "ede3ba875572ea0e370dd4d9802f8b4de710ed843cbfeb886e6ca3e417360e01"
relative = "docs/evidence/pr120-code-environment-2026-11-12"
destination = repo / "artifacts/pr120-v12-20261111/code-environment-probe"
destination.mkdir(parents=True, exist_ok=True)

def replace_once(old, new):
    global source
    assert source.count(old) == 1, old[:90]
    source = source.replace(old, new, 1)

replace_once('        var sdkChatToolsReasoningEffort =', r'''
        if (label != "code-toolchain-environment-20261112-01") throw new InvalidOperationException("This experiment has one independent identity.");
        const string experimentDirectory = "docs/evidence/pr120-code-environment-2026-11-12";
        foreach (var name in new[] { "probe.py", "prepare.py" })
        {
            var relative = experimentDirectory + "/" + name;
            if (SchemaPortabilityCampaign.Git("rev-parse", "HEAD:" + relative) != SchemaPortabilityCampaign.Git("hash-object", relative))
                throw new InvalidOperationException("Commit the exact diagnostic before admission.");
        }
        var experimentScript = await File.ReadAllTextAsync(experimentDirectory + "/probe.py");
        var scriptHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(experimentScript)));
        var sdkChatToolsReasoningEffort =''')
replace_once('["passed"] = false };', r'''["passed"] = false,
            ["purpose"] = "observe actual SDK terminal toolchains without installation or network",
            ["probe_script_sha256"] = scriptHash,
            ["probe_preparation_sha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(experimentDirectory + "/prepare.py"))),
            ["experiment_assembly_sha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(typeof(LiveExecutionReadiness).Assembly.Location))),
            ["pinned_toolchains_verified"] = false, ["full_code_prerequisite_qualified"] = false };''')
start = source.index('        await File.WriteAllTextAsync(Path.Combine(directory, "probe.py"), """')
end = source.index('        var objective = ', start)
source = source[:start] + '        await File.WriteAllTextAsync(Path.Combine(directory, "probe.py"), experimentScript);\n' + source[end:]
replace_once('        var request = new JsonObject { ["projectRoot"]', r'''
        objective += " The committed read-only probe records actual tool versions; report missing versions truthfully and do not install anything.";
        var request = new JsonObject { ["projectRoot"]''')
replace_once('            var exit = commands.Count == 1', r'''
            var scriptUnchanged = File.Exists(Path.Combine(directory, "probe.py")) &&
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(directory, "probe.py")))) == scriptHash;
            var emitted = commands.OfType<JsonObject>().SelectMany(c => (c["output"]?.ToString() ?? "").Split('\n'))
                .Where(line => line.StartsWith("GNOU_GO_CODE_ENVIRONMENT_REPORT=", StringComparison.Ordinal)).ToArray();
            JsonNode? terminalReport = null;
            if (emitted.Length == 1)
                try { terminalReport = JsonNode.Parse(emitted[0]["GNOU_GO_CODE_ENVIRONMENT_REPORT=".Length..]); }
                catch (System.Text.Json.JsonException) { }
            var terminalVerified = JsonNode.DeepEquals(report, terminalReport) && report is not null;
            state["probe_script_unchanged"] = scriptUnchanged; state["terminal_report_verified"] = terminalVerified;
            bool Version(string name, string expected) => report?["toolchains"] is JsonArray versions &&
                versions.OfType<JsonObject>().Any(v => v["name"]?.ToString() == name && v["exit_code"]?.GetValue<int>() == 0 &&
                    v["stdout"]?.ToString().Trim() == expected && v["timed_out"]?.GetValue<bool>() == false);
            state["pinned_toolchains_verified"] = terminalVerified && scriptUnchanged && Version("node", "v24.20.0") &&
                Version("pnpm", "10.34.5") && (Version("python", "Python 3.11.13") || Version("python3", "Python 3.11.13"));
            var exit = commands.Count == 1''')
replace_once('state["passed"] = result.Success && verified && retained && transportVerified && exit && commandCount == 1 &&',
             'state["passed"] = result.Success && verified && retained && transportVerified && exit && commandCount == 1 && scriptUnchanged && terminalVerified &&')
patched = destination / "LiveExecutionReadiness.cs"
patched.write_text(source)

tree = ET.parse(original / "GnOuGo.Agent.Planning.Benchmark.csproj")
root = tree.getroot()
properties = root.find("PropertyGroup")
ET.SubElement(properties, "EnableDefaultCompileItems").text = "false"
ET.SubElement(properties, "AssemblyName").text = "GnOuGo.Agent.Planning.Benchmark"
for node in root.iter():
    attribute = "Project" if node.tag == "Import" else "Include"
    if node.tag in ("ProjectReference", "Compile", "Import") and attribute in node.attrib:
        node.set(attribute, str((original / node.attrib[attribute]).resolve()))
items = ET.SubElement(root, "ItemGroup")
ET.SubElement(items, "Compile", Include=str(original / "*.cs"), Exclude=str(original / "LiveExecutionReadiness.cs"))
ET.SubElement(items, "Compile", Include=str(patched))
tree.write(destination / "Probe.csproj", encoding="unicode")
manifest = {"original_readiness_sha256": "ede3ba875572ea0e370dd4d9802f8b4de710ed843cbfeb886e6ca3e417360e01",
            "generated_source_sha256": hashlib.sha256(source.encode()).hexdigest(),
            "probe_script_sha256": hashlib.sha256((repo / relative / "probe.py").read_bytes()).hexdigest(),
            "preparation_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
            "production_files_changed": False, "provider_calls": 0}
(destination / "prepared.json").write_text(json.dumps(manifest, indent=2) + "\n")
print(json.dumps(manifest))
