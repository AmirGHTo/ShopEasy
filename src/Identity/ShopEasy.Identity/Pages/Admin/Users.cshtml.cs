using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using ShopEasy.Identity.Configuration;
using ShopEasy.Identity.Data;
using ShopEasy.Shared.Security;

namespace ShopEasy.Identity.Pages.Admin;

// The accounts of the system, for administrators only: find one, give it a role, disable or enable it.
// The rules are in AccountAdministration; this page lists, calls it and reports what it answered.
[Authorize(Roles = Roles.Admin)]
public class UsersModel(
    ApplicationDbContext db,
    UserManager<ApplicationUser> users,
    AccountAdministration administration) : PageModel
{
    public const int PageSize = 25;

    public record Row(string Id, string Email, string Name, string Role, bool IsAdmin, string Status, bool IsDisabled, bool IsYou);

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;
    [TempData] public string? Message { get; set; }
    [TempData] public string? Error { get; set; }

    public IReadOnlyList<Row> Rows { get; private set; } = [];
    public int Total { get; private set; }
    public int Pages => Math.Max(1, (Total + PageSize - 1) / PageSize);

    public async Task OnGetAsync()
    {
        var term = (Q ?? "").Trim();
        if (term.Length > 100)
            term = term[..100];
        Q = term;

        var query = db.Users.AsNoTracking();
        if (term.Length > 0)
        {
            var upper = term.ToUpperInvariant();
            query = query.Where(u => u.NormalizedEmail!.Contains(upper));
        }

        Total = await query.CountAsync();
        P = Math.Clamp(P, 1, Pages);
        var page = await query.OrderBy(u => u.NormalizedEmail).Skip((P - 1) * PageSize).Take(PageSize).ToListAsync();

        var ids = page.Select(u => u.Id).ToList();
        var roles = (await (from link in db.UserRoles
                            join role in db.Roles on link.RoleId equals role.Id
                            where ids.Contains(link.UserId)
                            select new { link.UserId, role.Name }).ToListAsync())
            .GroupBy(r => r.UserId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Name!).OrderBy(n => n).ToList());

        var me = users.GetUserId(User);
        Rows = page.Select(u => new Row(
            u.Id, u.Email ?? "", $"{u.FirstName} {u.LastName}".Trim(),
            roles.TryGetValue(u.Id, out var names) ? string.Join(", ", names) : "none",
            names?.Contains(Roles.Admin) == true,
            AccountAdministration.IsDisabled(u) ? "Disabled"
                : AccountAdministration.IsLockedOut(u) ? "Locked out for a while" : "Active",
            AccountAdministration.IsDisabled(u) || AccountAdministration.IsLockedOut(u),
            u.Id == me)).ToList();
    }

    public async Task<IActionResult> OnPostRoleAsync(string id, string role, string? q, int p) =>
        Report(await administration.SetRoleAsync(users.GetUserId(User)!, id, role), q, p);

    public async Task<IActionResult> OnPostEnabledAsync(string id, bool enabled, string? q, int p) =>
        Report(await administration.SetEnabledAsync(users.GetUserId(User)!, id, enabled), q, p);

    private IActionResult Report(AdminOutcome outcome, string? q, int p)
    {
        if (outcome.Succeeded) Message = outcome.Message; else Error = outcome.Message;
        return RedirectToPage(new { q, p });
    }
}
