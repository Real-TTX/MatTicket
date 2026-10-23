using MatTicket.Configuration;
using MatTicket.Data.Entities;
using MatTicket.Logic;

namespace MatTicket.Tests.Web;

public sealed class SyncQueryTests
{
    [Theory]
    [InlineData("SELECT * FROM Customer")]
    [InlineData("with c as (select 1 as x) select x from c;")]
    public void Reading_queries_are_allowed(string query) => SyncLogic.CheckQuery(query);

    [Theory]
    [InlineData("DELETE FROM Customer")]
    [InlineData("SELECT 1; DROP TABLE Customer")]
    [InlineData("SELECT * INTO Copy FROM Customer")]
    [InlineData("UPDATE Customer SET Name = ''")]
    [InlineData("")]
    public void Anything_else_is_refused(string query) => Assert.Throws<BizException>(() => SyncLogic.CheckQuery(query));
}

public sealed class AiInstructionTests
{
    [Fact]
    public void The_layers_come_in_their_order()
    {
        var op = new OperatorAiConfig { OperatorInstruction = "BETREIBER", ActionInstructions = new() { ["Reply"] = "AKTION" } };
        var text = AiInstructions.SystemText(op, new AiSettings { BaseInstruction = "WEBSITE" }, new SiteSettings(), AiAction.Reply, "SCHRITT");
        var order = new[] { "BETREIBER", "AKTION", "WEBSITE", "SCHRITT", "ANTWORTFORMAT" }.Select(s => text.IndexOf(s, StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.OrderBy(i => i), order);
    }

    [Fact]
    public void An_empty_base_instruction_means_the_built_in_one()
    {
        var text = AiInstructions.SystemText(new OperatorAiConfig(), new AiSettings { BaseInstruction = "" }, new SiteSettings(), AiAction.Assist, null);
        Assert.Contains(AiInstructions.DefaultBase, text);
        Assert.DoesNotContain("Vorgabe des Betreibers", text);
    }
}

public sealed class EntraSettingsTests
{
    [Fact]
    public void A_sign_in_is_offered_only_when_everything_is_filled_in()
    {
        var s = new EntraSettings { Enabled = true, TenantId = "contoso.onmicrosoft.com", ClientId = Guid.NewGuid().ToString(), ClientSecretProtected = "x" };
        Assert.True(s.IsUsable);
        s.ClientId = "kein-guid";
        Assert.False(s.IsUsable);
        s.ClientId = Guid.NewGuid().ToString();
        s.Enabled = false;
        Assert.False(s.IsUsable);
    }
}

public sealed class FormSlugTests
{
    [Theory]
    [InlineData("admin")]
    [InlineData("robots.txt")]
    [InlineData("t")]
    public void The_addresses_of_the_app_are_reserved(string slug) => Assert.Contains(slug, FormLogic.ReservedSlugs);
}
