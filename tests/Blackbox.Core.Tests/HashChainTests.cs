using System.Text.Json.Nodes;
using Blackbox.Core;
using Blackbox.Tests.Shared;

namespace Blackbox.Core.Tests;

public class HashChainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 10, 15, 2, TimeSpan.Zero);

    private static string WriteChain(TempProject p, int count)
    {
        var store = new SessionStore(p.Path(".blackbox"));
        for (var i = 0; i < count; i++)
        {
            store.Append(new JsonObject { ["v"] = 1, ["session"] = "s1", ["event"] = "PreToolUse", ["data"] = new JsonObject { ["i"] = i } }, Now);
        }
        return store.FindSessionFile("s1")!;
    }

    [Fact]
    public void Compute_MatchesSpecFormula()
    {
        var record = new JsonObject { ["prev"] = HashChain.Genesis, ["seq"] = 0, ["a"] = "b" };
        var expected = "sha256:" + HashChain.Sha256Hex(HashChain.Genesis + "\n" + """{"a":"b","prev":"sha256:GENESIS","seq":0}""");
        Assert.Equal(expected, HashChain.Compute(record));
    }

    [Fact]
    public void Compute_IgnoresExistingHashField()
    {
        var record = new JsonObject { ["prev"] = HashChain.Genesis, ["seq"] = 0 };
        var h = HashChain.Compute(record);
        record["hash"] = "sha256:whatever";
        Assert.Equal(h, HashChain.Compute(record));
    }

    [Fact]
    public void Verify_IntactChain_Ok()
    {
        using var p = new TempProject();
        var result = ChainVerifier.VerifyFile(WriteChain(p, 5));
        Assert.True(result.Ok, result.Error);
        Assert.Equal(5, result.Records);
        Assert.Equal("s1", result.Session);
    }

    [Fact]
    public void Verify_EditedField_HashMismatch()
    {
        using var p = new TempProject();
        var file = WriteChain(p, 5);
        var lines = File.ReadAllLines(file);
        lines[2] = lines[2].Replace("\"i\":2", "\"i\":99");
        File.WriteAllLines(file, lines);
        Assert.Equal("tampered at seq 2 (hash mismatch)", ChainVerifier.VerifyFile(file).Error);
    }

    [Fact]
    public void Verify_DeletedRecord_Detected()
    {
        using var p = new TempProject();
        var file = WriteChain(p, 5);
        var lines = File.ReadAllLines(file).ToList();
        lines.RemoveAt(1);
        File.WriteAllLines(file, lines);
        Assert.Contains("tampered at seq 2 (expected seq 1", ChainVerifier.VerifyFile(file).Error);
    }

    [Fact]
    public void Verify_ReorderedRecords_Detected()
    {
        using var p = new TempProject();
        var file = WriteChain(p, 5);
        var lines = File.ReadAllLines(file);
        (lines[1], lines[2]) = (lines[2], lines[1]);
        File.WriteAllLines(file, lines);
        Assert.False(ChainVerifier.VerifyFile(file).Ok);
    }

    [Fact]
    public void Verify_RecomputedHashWithoutFixingChain_PrevMismatch()
    {
        using var p = new TempProject();
        var file = WriteChain(p, 3);
        var lines = File.ReadAllLines(file);
        var rec = (JsonObject)JsonNode.Parse(lines[1])!;
        rec["data"]!["i"] = 7;
        rec["hash"] = HashChain.Compute(rec); // attacker fixes this record's hash but not the next record's prev
        lines[1] = CanonicalJson.Serialize(rec);
        File.WriteAllLines(file, lines);
        Assert.Equal("tampered at seq 2 (prev hash mismatch)", ChainVerifier.VerifyFile(file).Error);
    }

    [Fact]
    public void Verify_GarbageLine_Invalid()
    {
        using var p = new TempProject();
        var file = WriteChain(p, 2);
        File.AppendAllText(file, "{not json\n");
        Assert.StartsWith("invalid record at line 3", ChainVerifier.VerifyFile(file).Error);
    }
}
