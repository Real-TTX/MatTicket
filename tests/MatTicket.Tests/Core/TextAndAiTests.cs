using MatTicket.Core.Ai;
using MatTicket.Core.Text;

namespace MatTicket.Tests.Core;

public sealed class HtmlSafeTests
{
    [Fact]
    public void Safe_markup_stays_and_links_open_safely()
    {
        var html = HtmlSafe.Clean("<p><strong>Hallo</strong> <a href=\"https://example.com\">Link</a></p>");
        Assert.Contains("<strong>Hallo</strong>", html);
        Assert.Contains("rel=\"noopener noreferrer nofollow\"", html);
        Assert.Contains("target=\"_blank\"", html);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("<iframe src=\"https://evil\"></iframe>")]
    public void Scripts_are_recognised_and_removed(string attack)
    {
        Assert.True(HtmlSafe.ContainsScript(attack));
        var clean = HtmlSafe.Clean("<p>ok</p>" + attack);
        Assert.False(HtmlSafe.ContainsScript(clean));
    }

    [Fact]
    public void Text_becomes_paragraphs_and_back()
    {
        var html = HtmlSafe.FromText("Zeile 1\nZeile 2\n\n<b>kein Tag</b>");
        Assert.Equal("<p>Zeile 1<br>Zeile 2</p><p>&lt;b&gt;kein Tag&lt;/b&gt;</p>", html);
        Assert.Equal("Zeile 1\nZeile 2\n\n<b>kein Tag</b>", HtmlSafe.ToText(html));
    }
}

public sealed class TextToolsTests
{
    [Fact]
    public void Placeholders_are_filled_and_encoded_for_addresses()
    {
        var url = Placeholders.Fill("https://crm/kunde/{CustomerNumber}?q={x.ort}", k => k == "CustomerNumber" ? "10 01" : "Köln", urlEncode: true);
        Assert.Equal("https://crm/kunde/10%2001?q=K%C3%B6ln", url);
        Assert.Equal(["CustomerNumber", "x.ort"], Placeholders.Names("{CustomerNumber} {x.ort} {CustomerNumber}"));
    }

    [Theory]
    [InlineData("IT-Support für Größe", "it-support-fuer-groesse")]
    [InlineData("  ", "")]
    public void Slugs_are_made_from_names(string name, string slug) => Assert.Equal(slug, Slug.From(name));

    [Fact]
    public void Personal_data_is_masked()
    {
        var masked = Pii.Mask("Bitte an max.muster@example.com oder +49 171 1234567 schreiben, Max Muster.", ["Max Muster"]);
        Assert.Equal("Bitte an [E-Mail] oder [Telefon] schreiben, [Name].", masked);
    }
}

public sealed class LenientJsonTests
{
    [Theory]
    [InlineData("```json\n{\"reply\": \"<p>Hi</p>\",}\n```")]
    [InlineData("Gern, hier ist die Antwort: {\"reply\": \"<p>Hi</p>\"} Viel Erfolg!")]
    [InlineData("{ /* Kommentar */ \"reply\": \"<p>Hi</p>\" }")]
    public void The_object_is_found_in_what_models_write(string text)
    {
        var o = LenientJson.FirstObject(text);
        Assert.NotNull(o);
        Assert.Equal("<p>Hi</p>", o.Value.Text("reply"));
    }

    [Fact]
    public void Braces_inside_strings_do_not_confuse_the_reader()
        => Assert.Equal("a } b", LenientJson.FirstObject("x {\"t\": \"a } b\"}")!.Value.Text("t"));

    [Fact]
    public void Nothing_is_nothing() => Assert.Null(LenientJson.FirstObject("Ich schaue mir das an …"));

    [Fact]
    public void Values_are_read_forgivingly()
    {
        var o = LenientJson.FirstObject("{\"id\": \"12\", \"ok\": \"ja\", \"n\": 3}")!.Value;
        Assert.Equal(12, o.Long("id"));
        Assert.True(o.Bool("ok"));
        Assert.Equal(3, o.Long("n"));
    }

    [Fact]
    public void Tokens_are_estimated_from_characters() => Assert.Equal(3, TokenEstimate.Of("123456789"));
}
