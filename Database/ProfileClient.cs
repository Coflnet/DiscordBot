

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
using System.Text.Encodings.Web;
using RestSharp;

public class ProfileClient
{
    private RestClient profileClient;
    private ILogger<ProfileClient> logger;
    public ProfileClient(IConfiguration config, ILogger<ProfileClient> logger)
    {
        profileClient = new RestClient(config["PROFILE_BASE_URL"]);
        this.logger = logger;
    }

    public async Task<HypixelProfile> GetLookup(string playerId, bool forceRefresh = false)
    {
        try
        {
            return await GetProfile(playerId, forceRefresh);
        }
        catch (System.Exception e)
        {
            logger.LogError(e, $"Failed to get profile for {playerId}");
            await Task.Delay(1000);
            return await GetProfile(playerId, forceRefresh);
        }
    }

    private async Task<HypixelProfile> GetProfile(string playerId, bool forceRefresh)
    {
        var url = $"api/profile/{playerId}/hypixel";
        if(forceRefresh)
        {
            var time = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            // url encode
            url += "?maxAge=" + UrlEncoder.Default.Encode(time);
        }
        logger.LogInformation("Getting profile " + url);
        var request = new RestRequest(url, Method.Get);
        var response = await profileClient.ExecuteAsync<HypixelProfile>(request);
        if (response.IsSuccessful)
        {
            return response.Data;
        }
        throw new Exception("Failed to get profile " + forceRefresh);
    }


    public class HypixelProfile
    {
        public string Displayname { get; set; }
        public long LastLogin { get; set; }
        public SocialMedia SocialMedia { get; set; }
        public HypixelStats Stats { get; set; }
    }

    public class SocialMedia
    {
        public Dictionary<string,string> Links { get; set; }
    }

    public class HypixelStats
    {
        public Skyblock Skyblock { get; set; }

    }
    public class Skyblock
    {
        public Dictionary<string, object> Profiles { get; set; }
    }

}