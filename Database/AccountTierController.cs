using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class AccountController : ControllerBase
{
    private Persistence persistence;

    public AccountController(Persistence persistence)
    {
        this.persistence = persistence;
    }

    [HttpPost]
    public async Task Get(DiscordAccountInfo info)
    {
        await persistence.SaveDiscordAccountInfo(info);
    }
}