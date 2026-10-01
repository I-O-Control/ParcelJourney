using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ParcelJourney.App.Pages;

public sealed class IndexModel : PageModel {
    public string Viewer => CaptureReplay.AddLoader(CaptureReplay.Viewer());
    public void OnGet() { }
}
