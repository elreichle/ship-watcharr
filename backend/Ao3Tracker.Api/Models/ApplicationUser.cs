using Microsoft.AspNetCore.Identity;

namespace Ao3Tracker.Api.Models;

public class ApplicationUser : IdentityUser
{
    public bool IsAdmin { get; set; }

    public Ao3Credential? Ao3Credential { get; set; }

    public ICollection<ScrapeJob> ScrapeJobs { get; set; } = new List<ScrapeJob>();
}
