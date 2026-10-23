using System.ComponentModel.DataAnnotations;
using MatTicket.Core.Forms;
using MatTicket.Data.Entities;
using MatTicket.Logic;
using MatTicket.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace MatTicket.Pages.Admin.Questions;

public sealed class EditModel(Biz biz) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib die Frage ein.")]
        public string Label { get; set; } = "";

        public string? Key { get; set; }

        public string Type { get; set; } = BlockTypes.Text;

        public string? Options { get; set; }

        public string? Help { get; set; }

        public string? Placeholder { get; set; }

        public bool Required { get; set; }

        public string? CategoryId { get; set; }
    }

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public Question? Question { get; private set; }

    public List<SelectListItem> Types { get; } = [.. BlockTypes.All.Where(t => BlockTypes.IsQuestion(t.Type)).Select(t => new SelectListItem(t.Label, t.Type))];

    public List<SelectListItem> Categories { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        if (Question is { } q)
        {
            Input = new InputModel { Label = q.Label, Key = q.Key, Type = q.Type, Options = q.Options, Help = q.Help, Placeholder = q.Placeholder, Required = q.Required, CategoryId = q.CategoryId?.ToString() };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        if (ModelState.IsValid)
        {
            try
            {
                var saved = await CategoryLogic.SaveQuestionAsync(biz, Id, new Question
                {
                    Label = Input.Label, Key = Input.Key ?? "", Type = Input.Type, Options = Input.Options, Help = Input.Help, Placeholder = Input.Placeholder,
                    Required = Input.Required, CategoryId = IdList.Parse(Input.CategoryId).Cast<long?>().FirstOrDefault(),
                });
                Flash("Die Frage ist gespeichert.");
                return RedirectToPage(new { id = saved.Id });
            }
            catch (BizException ex)
            {
                ModelState.AddModelError(ex.Field is null ? string.Empty : "Input." + ex.Field, ex.Message);
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        await CategoryLogic.DeleteQuestionAsync(biz, Id ?? 0);
        Flash("Die Frage ist gelöscht. Formulare, die sie enthalten, behalten ihre Kopie.");
        return RedirectToPage("/Admin/Questions/Index");
    }

    private async Task<bool> LoadAsync()
    {
        if (Id is { } id && (Question = await CategoryLogic.QuestionAsync(biz, id)) is null)
        {
            return false;
        }

        Categories = Controls.Options.From(await CategoryLogic.ChoicesAsync(biz));
        var title = Question?.Label ?? "Neue Frage";
        Head(title, null, ("Einstellungen", null), ("Fragenkatalog", "/Admin/Questions"), (title, null));
        ViewData["Width"] = "narrow";
        ViewData["ActiveHref"] = "/Admin/Questions";
        return true;
    }
}
