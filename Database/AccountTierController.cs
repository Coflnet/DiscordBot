using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class AccountTierController : ControllerBase
{
    private Persistence persistence;

    public AccountTierController(Persistence persistence)
    {
        this.persistence = persistence;
    }

    [HttpPost]
    public async Task Get(DiscordAccountInfo info)
    {
        await persistence.SaveDiscordAccountInfo(info);
    }
}