using MatTicket.Core.Forms;

namespace MatTicket.Tests.Core;

public sealed class FormEngineTests
{
    private static FormDefinition Sample() => new()
    {
        Title = "IT",
        Start = "a",
        Steps =
        [
            new FormStep
            {
                Id = "a", Kind = StepKinds.Page, Title = "Bereich",
                Blocks =
                [
                    new FormBlock { Id = "a1", Type = BlockTypes.Radio, Key = "bereich", Label = "Bereich", Required = true, Options = [new() { Value = "drucker", Label = "Drucker" }, new() { Value = "pc", Label = "PC" }] },
                    new FormBlock { Id = "a2", Type = BlockTypes.Email, Key = "email", Label = "E-Mail", MapTo = "requester.email" },
                ],
                Next = [new FormRoute { When = new FormCondition { Field = "bereich", Op = "eq", Value = "drucker" }, To = "b" }, new FormRoute { To = "c" }],
            },
            new FormStep { Id = "b", Kind = StepKinds.End, Title = "Ende" },
            new FormStep { Id = "c", Kind = StepKinds.Ticket, Title = "Ticket" },
        ],
    };

    [Fact]
    public void The_first_route_whose_condition_holds_is_taken()
    {
        var step = Sample().Step("a")!;
        Assert.Equal("b", FormEngine.Next(step, new FormAnswers { ["bereich"] = "drucker" }));
        Assert.Equal("c", FormEngine.Next(step, new FormAnswers { ["bereich"] = "pc" }));
    }

    [Theory]
    [InlineData("eq", "a|b", "b", true)]
    [InlineData("neq", "a", "b", true)]
    [InlineData("contains", "Der Drucker", "drucker", true)]
    [InlineData("in", "pc", "drucker, pc", true)]
    [InlineData("empty", "", null, true)]
    [InlineData("notempty", "", null, false)]
    [InlineData("gt", "12,5", "10", true)]
    [InlineData("lt", "", "10", false)]
    public void Conditions_compare_as_people_expect(string op, string value, string? expected, bool holds)
        => Assert.Equal(holds, FormEngine.Holds(new FormCondition { Field = "x", Op = op, Value = expected }, new FormAnswers { ["x"] = value }));

    [Fact]
    public void Required_questions_and_formats_are_checked()
    {
        var step = Sample().Step("a")!;
        var errors = FormEngine.Validate(step, new FormAnswers { ["bereich"] = "", ["email"] = "keine-mail" }, FormLists.Empty);
        Assert.Contains("a_bereich", errors.Keys);
        Assert.Contains("a_email", errors.Keys);
        Assert.Empty(FormEngine.Validate(step, new FormAnswers { ["bereich"] = "pc", ["email"] = "a@b.de" }, FormLists.Empty));
    }

    [Fact]
    public void A_choice_that_is_not_offered_is_refused()
    {
        var step = Sample().Step("a")!;
        Assert.Contains("a_bereich", FormEngine.Validate(step, new FormAnswers { ["bereich"] = "toaster" }, FormLists.Empty).Keys);
    }

    [Fact]
    public void Several_choices_are_read_joined()
    {
        var step = new FormStep { Id = "s", Blocks = [new FormBlock { Id = "s1", Type = BlockTypes.Checkboxes, Key = "k", Label = "K", Options = [new() { Value = "a", Label = "A" }, new() { Value = "b", Label = "B" }] }] };
        var answers = FormEngine.Read(step, name => name == "a_k" ? ["a", "b", "a"] : []);
        Assert.Equal("a|b", answers["k"]);
    }

    [Fact]
    public void Cleaning_removes_scripts_and_unknown_things()
    {
        var def = Sample();
        def.Steps[0].Blocks.Add(new FormBlock { Id = "x", Type = BlockTypes.RichText, Html = "<p>Hallo</p><script>alert(1)</script><img src=x onerror=alert(1)>" });
        def.Steps[0].Blocks.Add(new FormBlock { Id = "y", Type = "iframe" });
        def.Steps[0].Kind = "evil";
        var clean = FormEngine.Clean(def);
        var text = clean.Steps[0].Blocks.Single(b => b.Type == BlockTypes.RichText).Html!;
        Assert.DoesNotContain("script", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<p>Hallo</p>", text);
        Assert.DoesNotContain(clean.Steps[0].Blocks, b => b.Type == "iframe");
        Assert.Equal(StepKinds.Page, clean.Steps[0].Kind);
    }

    [Fact]
    public void The_check_finds_broken_routes_and_duplicate_keys()
    {
        var def = Sample();
        def.Steps[0].Next.Add(new FormRoute { To = "nirgends" });
        def.Steps[2].Blocks.Add(new FormBlock { Id = "c1", Type = BlockTypes.Text, Key = "bereich", Label = "Doppelt" });
        var check = FormEngine.Check(def);
        Assert.False(check.CanPublish);
        Assert.Contains(check.Errors, e => e.Contains("nicht gibt"));
        Assert.Contains(check.Errors, e => e.Contains("mehreren Fragen"));
    }

    [Fact]
    public void A_good_form_can_be_published()
        => Assert.True(FormEngine.Check(Sample()).CanPublish);

    [Fact]
    public void A_subject_template_shows_the_labels_of_the_answers()
    {
        var def = Sample();
        Assert.Equal("Drucker: kaputt", FormEngine.FillTemplate(def, "{bereich}: {kurz}", new FormAnswers { ["bereich"] = "drucker", ["kurz"] = "kaputt" }, FormLists.Empty));
    }
}

public sealed class FormVisibilityTests
{
    private static readonly FormStep Step = new()
    {
        Id = "s",
        Blocks =
        [
            new FormBlock { Id = "a", Type = BlockTypes.Text, Key = "name", Label = "Name", Required = true, Visibility = Visibilities.Guests },
            new FormBlock { Id = "b", Type = BlockTypes.Customer, Key = "kunde", Label = "Kunde", Required = true, Visibility = Visibilities.Staff },
        ],
    };

    [Fact]
    public void A_block_the_viewer_does_not_see_is_neither_read_nor_required()
    {
        bool Requester(FormBlock b) => Visibilities.Sees(b.Visibility, FormViewer.SignedIn);
        var answers = FormEngine.Read(Step, name => name == "a_kunde" ? ["7"] : [], Requester);
        Assert.False(answers.ContainsKey("kunde"));
        Assert.Empty(FormEngine.Validate(Step, answers, FormLists.Empty, Requester));
    }

    [Fact]
    public void Staff_choose_only_customers_of_the_list()
    {
        var lists = new FormLists([], [new FormOption { Value = "1", Label = "Musterbau" }]);
        bool Staff(FormBlock b) => Visibilities.Sees(b.Visibility, FormViewer.Staff);
        Assert.Empty(FormEngine.Validate(Step, new FormAnswers { ["kunde"] = "1" }, lists, Staff));
        Assert.Contains("a_kunde", FormEngine.Validate(Step, new FormAnswers { ["kunde"] = "99" }, lists, Staff).Keys);
    }

    [Fact]
    public void The_customer_block_is_always_for_staff_only()
    {
        var def = new FormDefinition { Start = "s", Steps = [new FormStep { Id = "s", Blocks = [new FormBlock { Id = "b", Type = BlockTypes.Customer, Key = "kunde", Label = "Kunde" }] }] };
        Assert.Equal(Visibilities.Staff, FormEngine.Clean(def).Steps[0].Blocks[0].Visibility);
    }
}
