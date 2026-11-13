using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SsrsPortal.Subscriptions;

namespace SsrsPortal.Pages;

public sealed class SubscriptionsModel(SubscriptionOptions options) : PageModel
{
    public IActionResult OnGet() => options.Enabled ? Page() : NotFound();
}
