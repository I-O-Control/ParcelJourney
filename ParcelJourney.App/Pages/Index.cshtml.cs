using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ParcelJourney.App.Pages;

public sealed class IndexModel : PageModel {
    private static readonly string BundledViewer = EmbeddedAssets.Read("ParcelJourney.Viewer");
    public string Viewer => BundledViewer;
    public void OnGet() { }
}
