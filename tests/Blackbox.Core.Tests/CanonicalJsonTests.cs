using System.Text.Json.Nodes;
using Blackbox.Core;

namespace Blackbox.Core.Tests;

public class CanonicalJsonTests
{
    [Fact]
    public void SortsKeysOrdinally_AtEveryLevel()
    {
        var node = JsonNode.Parse("""{"b":1,"a":{"z":true,"B":null,"y":[{"d":1,"c":2}]},"A":"x"}""");
        Assert.Equal("""{"A":"x","a":{"B":null,"y":[{"c":2,"d":1}],"z":true},"b":1}""", CanonicalJson.Serialize(node));
    }

    [Fact]
    public void RemovesWhitespace_KeepsArrayOrder()
    {
        var node = JsonNode.Parse("{ \"k\" : [ 3 , 1 , 2 ] }");
        Assert.Equal("""{"k":[3,1,2]}""", CanonicalJson.Serialize(node));
    }

    [Fact]
    public void KeepsNonAsciiReadable_AndEscapesControlChars()
    {
        var obj = new JsonObject { ["p"] = "Sipariş gönderimine yeniden deneme ekle\n\"tab\"\t" };
        Assert.Equal("{\"p\":\"Sipariş gönderimine yeniden deneme ekle\\n\\\"tab\\\"\\t\"}", CanonicalJson.Serialize(obj));
    }

    [Fact]
    public void NumbersAreCultureInvariant()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            var obj = new JsonObject { ["d"] = 1.5, ["i"] = 42, ["n"] = -7L };
            Assert.Equal("""{"d":1.5,"i":42,"n":-7}""", CanonicalJson.Serialize(obj));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void IsStableAcrossParseAndReserialize()
    {
        var obj = new JsonObject { ["z"] = 1.25, ["a"] = new JsonArray("x", 2, false), ["m"] = new JsonObject { ["ğ"] = "ü", ["emoji"] = "✈" } };
        var once = CanonicalJson.Serialize(obj);
        var twice = CanonicalJson.Serialize(JsonNode.Parse(once));
        Assert.Equal(once, twice);
    }

    [Fact]
    public void SameContent_DifferentInsertionOrder_SameOutput()
    {
        var a = new JsonObject { ["x"] = 1, ["y"] = 2 };
        var b = new JsonObject { ["y"] = 2, ["x"] = 1 };
        Assert.Equal(CanonicalJson.Serialize(a), CanonicalJson.Serialize(b));
    }
}
